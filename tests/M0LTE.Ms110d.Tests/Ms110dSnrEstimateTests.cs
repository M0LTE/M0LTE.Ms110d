using System.Collections.Concurrent;
using System.Globalization;
using M0LTE.HfChannel;
using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// The SNR estimator (docs/design-5066-support.md section 2): the per-block, per-burst and
/// preamble figures against the channel simulator's set SNR in 3 kHz. The quick tests here run
/// in CI. The section 2.5 sweep runs behind environment variables:
/// <c>MS110D_SNR_SWEEP=1</c> for the AWGN points and <c>MS110D_MASKS_POOR=1</c> for the Poor
/// and Moderate points, with <c>MS110D_SNR_WN</c> (comma list), <c>MS110D_SNR_IL</c>
/// (Short, Long or both, comma list), <c>MS110D_SNR_SEED_OFFSET</c> (added to every seed, for
/// a realisation set disjoint from the one the equaliser loss table was fitted on),
/// <c>MS110D_SNR_BURSTS</c> (AWGN bursts per point, default 20), <c>MS110D_SNR_SECONDS</c> (fading signal seconds per point, default 60) and
/// <c>MS110D_MASK_WORKERS</c> (parallel workers). Report lines go to the test output and, with
/// <c>MS110D_SNR_LOG=path</c>, are appended to that file.
/// </summary>
public class Ms110dSnrEstimateTests(ITestOutputHelper output)
{
    // Table D-LXIV 3 kHz rows, as Ms110dMaskTests: WN to (AWGN, Poor) SNR dB.
    private static readonly Dictionary<int, (double Awgn, double Poor)> MaskSnr = new()
    {
        [0] = (-6, -1), [1] = (-3, 3), [2] = (0, 5), [3] = (3, 7), [4] = (5, 10), [5] = (6, 11),
        [6] = (9, 14), [7] = (13, 19), [8] = (16, 23), [13] = (6, 11),
    };

    // ITU-R F.1487 mid-latitude Moderate: two equal Rayleigh paths, 1 ms apart, 0.5 Hz spread.
    private static readonly WattersonPath[] Moderate =
    [
        new(0, Fading: true, DopplerSpreadHz: 0.5),
        new(1.0, Fading: true, DopplerSpreadHz: 0.5),
    ];

    internal sealed record BlockSample(double? SnrDb, int ProbesUsed, int ProbesBad, bool Ok);

    internal sealed record BurstSample(
        double? SnrDb, int ProbesUsed, int ProbesBad, double? PreambleSnrDb, Ms110dBurstEndReason Reason,
        List<BlockSample> Blocks, long DecodeErrors);

    private static byte[] RandomBits(int count, Random random)
    {
        var bits = new byte[count];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }

        return bits;
    }

    /// <summary>One burst through the channel and a fresh demodulator; every SNR figure and
    /// each block's decode against the known payload.</summary>
    internal static BurstSample RunBurst(
        int wn, Ms110dInterleaverKind il, int blocks, double snrDb, WattersonPath[] paths, int seed,
        int preambleSuperframes = 3, Ms110dDemodOptions? options = null, bool genie = false,
        int truncateAfterSamples = 0)
    {
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = il,
            ConstraintLength = 7,
            PreambleSuperframes = preambleSuperframes,
        };
        var tx = new Ms110dModulator(settings);
        Ms110dInterleaverParams ilParams = Ms110dInterleaverParams.Get3k(wn, il);
        var random = new Random(seed);
        byte[] payload = RandomBits((blocks * ilParams.InputBits) - 32, random);
        byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, ilParams.InputBits);
        float[] audio = tx.Modulate(payload);
        if (truncateAfterSamples > 0 && truncateAfterSamples < audio.Length)
        {
            // The transmitter stops mid-burst: what follows is the channel's noise only.
            Array.Clear(audio, truncateAfterSamples, audio.Length - truncateAfterSamples);
        }

        float[] rx = new WattersonChannel(9600, seed + 1, paths).Apply(
            audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400);

        var demod = new Ms110dDemodulator(options ?? new Ms110dDemodOptions());
        double? preamble = null;
        Ms110dBurst? burst = null;
        var blockSamples = new List<BlockSample>();
        long decodeErrors = 0;
        demod.BurstStarted += s => preamble ??= s.PreambleSnrDb;
        demod.BlockDecoded += b =>
        {
            bool ok = false;
            if (b.Index < txBits.Length / ilParams.InputBits)
            {
                ok = b.Bits.AsSpan().SequenceEqual(txBits.AsSpan(b.Index * ilParams.InputBits, ilParams.InputBits));
            }

            blockSamples.Add(new BlockSample(b.SnrDb, b.ProbesUsed, b.ProbesBad, ok));
        };
        demod.BurstCompleted += b => burst ??= b;

        if (genie)
        {
            float[] clean = new WattersonChannel(9600, seed + 1, paths).Apply(
                audio, double.PositiveInfinity, leadInSamples: 2400, leadOutSamples: 2400);
            for (int i = 0; i < rx.Length; i += 4800)
            {
                int length = Math.Min(4800, rx.Length - i);
                demod.WriteGenie(clean.AsSpan(i, length));
                demod.Process(rx.AsSpan(i, length));
            }
        }
        else
        {
            demod.Process(rx);
            if (burst is null)
            {
                // Let a burst still decoding run out on noise so it completes (SignalLost).
                var tail = new WattersonChannel(9600, seed + 2, []).Apply(
                    new float[9600 * 8], snrDb, leadInSamples: 0, leadOutSamples: 0);
                demod.Process(tail);
            }
        }

        if (burst is not null)
        {
            int compared = Math.Min(burst.PayloadBits.Length, payload.Length);
            for (int i = 0; i < compared; i++)
            {
                decodeErrors += burst.PayloadBits[i] != payload[i] ? 1 : 0;
            }

            decodeErrors += payload.Length - compared;
        }
        else
        {
            decodeErrors = payload.Length;
        }

        return new BurstSample(
            burst?.SnrDb, burst?.ProbesUsed ?? 0, burst?.ProbesBad ?? 0, preamble,
            burst?.Reason ?? Ms110dBurstEndReason.Terminated, blockSamples, decodeErrors);
    }

    private static (double Mean, double Std) Stats(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return (double.NaN, double.NaN);
        }

        double mean = values.Average();
        double var = values.Count > 1 ? values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1) : 0;
        return (mean, Math.Sqrt(var));
    }

    // ---------------------------------------------------------------- quick tests (CI)

    [Fact]
    public void Wn8ModulationCorrectionMatchesTheDesignFigure()
    {
        // (256 x 0.783 + 32) / 288 = 0.807, about -0.93 dB (design section 2.1).
        Ms110dDemodulator.ModulationCorrectionDb(Ms110dMode.Mode3k(8)).Should().BeApproximately(-0.93, 0.02);
        foreach (int wn in new[] { 1, 2, 3, 4, 5, 6, 7, 13 })
        {
            Ms110dDemodulator.ModulationCorrectionDb(Ms110dMode.Mode3k(wn)).Should().Be(0);
        }
    }

    [Theory]
    [InlineData(9.0)]
    [InlineData(19.0)]
    public void Wn6AwgnBurstEstimateIsWithinOneDbOfTheSetSnr(double snrDb)
    {
        var bursts = new List<double>();
        var preambles = new List<double>();
        for (int b = 0; b < 3; b++)
        {
            BurstSample s = RunBurst(6, Ms110dInterleaverKind.Short, 4, snrDb, [], seed: 4100 + (b * 10));
            s.Reason.Should().Be(Ms110dBurstEndReason.Eom);
            s.SnrDb.Should().NotBeNull();
            s.Blocks.Should().HaveCount(4);
            s.Blocks.Should().AllSatisfy(blk =>
            {
                blk.SnrDb.Should().NotBeNull();
                blk.ProbesUsed.Should().Be(4);
            });
            s.ProbesUsed.Should().Be(16);
            bursts.Add(s.SnrDb!.Value);
            preambles.Add(s.PreambleSnrDb!.Value);
        }

        output.WriteLine(
            $"WN6 Short AWGN {snrDb} dB: burst {string.Join(" ", bursts.Select(v => v.ToString("F2", CultureInfo.InvariantCulture)))}, " +
            $"preamble {string.Join(" ", preambles.Select(v => v.ToString("F2", CultureInfo.InvariantCulture)))}");
        bursts.Average().Should().BeApproximately(snrDb, 1.0);
        preambles.Average().Should().BeApproximately(snrDb, 2.0);
    }

    [Fact]
    public void Wn6AwgnEstimateRisesWithTheSetSnr()
    {
        double previous = double.NegativeInfinity;
        foreach (double snrDb in new[] { 9.0, 14.0, 19.0, 29.0 })
        {
            BurstSample s = RunBurst(6, Ms110dInterleaverKind.Short, 2, snrDb, [], seed: 4200);
            s.SnrDb.Should().NotBeNull();
            output.WriteLine($"WN6 AWGN {snrDb} dB -> {s.SnrDb:F2}");
            s.SnrDb!.Value.Should().BeGreaterThan(previous);
            previous = s.SnrDb.Value;
        }
    }

    [Fact]
    public void Wn0ReportsNullInBurstAndAPreambleEstimate()
    {
        BurstSample s = RunBurst(0, Ms110dInterleaverKind.Short, 2, 4.0, [], seed: 4300);
        s.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        s.SnrDb.Should().BeNull();
        s.ProbesUsed.Should().Be(0);
        s.Blocks.Should().AllSatisfy(blk =>
        {
            blk.SnrDb.Should().BeNull();
            blk.ProbesUsed.Should().Be(0);
        });
        s.PreambleSnrDb.Should().NotBeNull();
        output.WriteLine($"WN0 AWGN 4 dB: preamble {s.PreambleSnrDb:F2}");
        s.PreambleSnrDb!.Value.Should().BeApproximately(4.0, 2.0);
    }

    [Fact]
    public void GenieModeReportsNull()
    {
        BurstSample s = RunBurst(6, Ms110dInterleaverKind.Short, 2, 15.0, [], seed: 4400, genie: true);
        s.Blocks.Should().NotBeEmpty();
        s.SnrDb.Should().BeNull();
        s.PreambleSnrDb.Should().BeNull();
        s.Blocks.Should().AllSatisfy(blk => blk.SnrDb.Should().BeNull());
    }

    [Fact]
    public void DisableTurboLeavesTheSnrUnchanged()
    {
        // WN 7 at a modest SNR, where the turbo pass runs on the shipped path.
        BurstSample on = RunBurst(7, Ms110dInterleaverKind.Short, 3, 16.0, [], seed: 4500);
        BurstSample off = RunBurst(7, Ms110dInterleaverKind.Short, 3, 16.0, [], seed: 4500,
            options: new Ms110dDemodOptions { DisableTurbo = true });
        on.SnrDb.Should().NotBeNull();
        output.WriteLine($"turbo on {on.SnrDb:F4}, off {off.SnrDb:F4}");
        off.SnrDb.Should().Be(on.SnrDb);
        off.Blocks.Select(b => b.SnrDb).Should().Equal(on.Blocks.Select(b => b.SnrDb));
    }

    [Fact]
    public void SignalLostBurstLeavesOutItsTrailingBadRun()
    {
        // WN 6 Short (4 frames of 288 symbols a block): stop the transmitter after about two
        // seconds of data, so the receiver runs on into noise until SignalLost.
        const double snrDb = 15.0;
        int preambleSamples = 3 * 18 * 32 * 4;
        int stopAt = preambleSamples + (2 * 9600);
        BurstSample s = RunBurst(6, Ms110dInterleaverKind.Short, 12, snrDb, [], seed: 4600,
            truncateAfterSamples: stopAt);
        s.Reason.Should().Be(Ms110dBurstEndReason.SignalLost);
        int blockProbes = s.Blocks.Sum(b => b.ProbesUsed);
        int blockBad = s.Blocks.Sum(b => b.ProbesBad);
        output.WriteLine(
            $"burst {s.SnrDb:F2} dB over {s.ProbesUsed} probes ({s.ProbesBad} bad); blocks {blockProbes} probes " +
            $"({blockBad} bad): {string.Join(" ", s.Blocks.Select(b => $"{b.SnrDb:F1}/{b.ProbesBad}"))}");

        // The noise-only blocks read at the floor and carry bad probes...
        blockBad.Should().BeGreaterThan(0);
        s.Blocks.Where(b => b.ProbesBad > 0).Should().AllSatisfy(b => b.SnrDb.Should().BeLessThan(0));
        // ...the burst figure leaves the trailing run out, so it reads what the signal blocks read
        // (the simulator sets its SNR over the whole array, silence included, so the local SNR
        // while the signal is on is higher than the set figure; compare with the blocks instead),
        double signalBlocks = s.Blocks.Where(b => b.ProbesBad == 0).Average(b => b.SnrDb!.Value);
        s.SnrDb.Should().NotBeNull();
        s.SnrDb!.Value.Should().BeApproximately(signalBlocks, 0.5);
        // and its probe count stops short of the probes processed (about 4 s of probes dropped).
        int signalProbes = (int)Math.Ceiling(2.0 * 2400 / 288);
        s.ProbesUsed.Should().BeInRange(signalProbes - 1, signalProbes + 2);
    }

    // ---------------------------------------------------------------- section 2.5 sweep

    private static int Workers()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("MS110D_MASK_WORKERS"), out int w) ? Math.Max(1, w) : 1;
    }

    // A disjoint realisation set for an out-of-sample check of the equaliser loss table,
    // which was fitted on the default seeds (as MS110D_MASK_SEED_OFFSET in the mask tests).
    private static int SeedOffset()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("MS110D_SNR_SEED_OFFSET"), out int o) ? o : 0;
    }

    private static int[] SweepWns()
    {
        string? s = Environment.GetEnvironmentVariable("MS110D_SNR_WN");
        return s is null ? [1, 2, 3, 4, 5, 6, 7, 8, 13] : s.Split(',').Select(int.Parse).ToArray();
    }

    private static Ms110dInterleaverKind[] SweepInterleavers()
    {
        string? s = Environment.GetEnvironmentVariable("MS110D_SNR_IL");
        return s is null
            ? [Ms110dInterleaverKind.Short, Ms110dInterleaverKind.Long]
            : s.Split(',').Select(Enum.Parse<Ms110dInterleaverKind>).ToArray();
    }

    private void Report(string line)
    {
        output.WriteLine(line);
        Console.Error.WriteLine(line);
        string? log = Environment.GetEnvironmentVariable("MS110D_SNR_LOG");
        if (log is not null)
        {
            File.AppendAllText(log, line + Environment.NewLine);
        }
    }

    private sealed record PointResult(
        string Channel, int Wn, Ms110dInterleaverKind Il, double SetSnr, List<BurstSample> Bursts)
    {
        public List<double> BurstSnr => Bursts.Where(b => b.SnrDb is not null).Select(b => b.SnrDb!.Value).ToList();

        public List<double> PreambleSnr =>
            Bursts.Where(b => b.PreambleSnrDb is not null).Select(b => b.PreambleSnrDb!.Value).ToList();

        public List<BlockSample> Blocks => Bursts.SelectMany(b => b.Blocks).ToList();
    }

    /// <summary>Runs <paramref name="bursts"/> bursts of <paramref name="blocks"/> blocks at one
    /// point, split over the workers on disjoint seeds.</summary>
    private static PointResult RunSweepPoint(
        string channel, WattersonPath[] paths, int wn, Ms110dInterleaverKind il, double snrDb, int bursts, int blocks,
        int seed)
    {
        var results = new ConcurrentBag<(int Index, BurstSample Sample)>();
        Parallel.For(0, bursts, new ParallelOptions { MaxDegreeOfParallelism = Workers() }, b =>
        {
            results.Add((b, RunBurst(wn, il, blocks, snrDb, paths, seed + SeedOffset() + (b * 1000))));
        });

        return new PointResult(channel, wn, il, snrDb, results.OrderBy(r => r.Index).Select(r => r.Sample).ToList());
    }

    /// <summary>Block spread about each burst's own mean: the estimator's noise, not the
    /// burst-to-burst spread (which on fading is the channel).</summary>
    private static double BlockSd(PointResult p)
    {
        var blockDev = new List<double>();
        foreach (BurstSample burst in p.Bursts)
        {
            List<double> v = burst.Blocks.Where(b => b.SnrDb is not null).Select(b => b.SnrDb!.Value).ToList();
            if (v.Count > 1)
            {
                double m = v.Average();
                blockDev.AddRange(v.Select(x => x - m));
            }
        }

        return blockDev.Count > 1 ? Math.Sqrt(blockDev.Sum(d => d * d) / (blockDev.Count - 1)) : double.NaN;
    }

    private static string Signed(double v)
    {
        string text = v.ToString("F2", CultureInfo.InvariantCulture);
        return text.StartsWith('-') ? text : "+" + text;
    }

    private string Describe(PointResult p)
    {
        (double bm, double bs) = Stats(p.BurstSnr);
        (double pm, double ps) = Stats(p.PreambleSnr);
        double blockStd = BlockSd(p);
        List<BlockSample> blocks = p.Blocks;
        int bad = blocks.Sum(b => b.ProbesBad), used = blocks.Sum(b => b.ProbesUsed);
        int blockErrors = blocks.Count(b => !b.Ok);
        return string.Create(CultureInfo.InvariantCulture,
            $"[snr] {p.Channel} WN{p.Wn} {p.Il} set {p.SetSnr:F1} dB: burst mean {bm:F2} (bias {Signed(bm - p.SetSnr)}) " +
            $"sd {bs:F2} n={p.BurstSnr.Count}; block sd {blockStd:F2} n={blocks.Count}; preamble mean {pm:F2} " +
            $"(bias {Signed(pm - p.SetSnr)}) sd {ps:F2}; probes bad {bad}/{used}; block errors {blockErrors}/{blocks.Count}; " +
            $"bursts not EOM {p.Bursts.Count(b => b.Reason != Ms110dBurstEndReason.Eom)}");
    }

    /// <summary>The estimated SNR at which the block error rate crosses 10%, from the per-block
    /// (estimate, decoded) pairs pooled over a WN's points: blocks sorted by estimate, the lowest
    /// estimate above which no 1 dB-wide bin exceeds 10% errors. NaN when there are no
    /// errors at all (the crossing is below every point measured).</summary>
    private static double BlerCrossing(IEnumerable<BlockSample> blocks)
    {
        var bins = blocks.Where(b => b.SnrDb is not null)
            .GroupBy(b => Math.Floor(b.SnrDb!.Value))
            .OrderBy(g => g.Key)
            .Select(g => (Bin: g.Key, Bler: g.Count(b => !b.Ok) / (double)g.Count()))
            .ToList();
        if (bins.All(b => b.Bler <= 0.10))
        {
            return double.NaN;
        }

        int last = bins.FindLastIndex(b => b.Bler > 0.10);
        return last + 1 < bins.Count ? bins[last + 1].Bin : bins[last].Bin + 1;
    }

    [Fact]
    public void Sweep_Awgn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_SNR_SWEEP") != "1",
            "set MS110D_SNR_SWEEP=1 for the section 2.5 AWGN sweep");
        int bursts = int.TryParse(Environment.GetEnvironmentVariable("MS110D_SNR_BURSTS"), out int nb) ? nb : 20;
        var failures = new List<string>();
        foreach (int wn in SweepWns())
        {
            foreach (Ms110dInterleaverKind il in SweepInterleavers())
            {
                double mask = MaskSnr[wn].Awgn;
                double[] points = [mask, mask + 5, mask + 10, mask + 20, 30];
                points = points.Distinct().Order().ToArray();
                // The bias and block-spread gates run from the mask SNR up to the lower of
                // mask + 20 dB and 30 dB. WN 1 and 2 at the Short interleaver stop at mask + 5 dB:
                // their K = 48 equaliser converges over seconds at high SNR, longer than a Short
                // burst, so the figure there is the lower effective SNR the equaliser really
                // delivers, not an estimator error (docs/snr-calibration.md).
                double gateTop = wn is 1 or 2 && il == Ms110dInterleaverKind.Short ? mask + 5 : Math.Min(mask + 20, 30);
                double blockSdLimit = il == Ms110dInterleaverKind.Long ? 0.5 : 1.0;
                var results = new List<PointResult>();
                foreach (double snr in points)
                {
                    PointResult p = RunSweepPoint("AWGN", [], wn, il, snr, bursts, 4, 70_000 + (wn * 100) + (int)il);
                    results.Add(p);
                    Report(Describe(p));
                    (double mean, _) = Stats(p.BurstSnr);
                    if (snr <= gateTop && Math.Abs(mean - snr) > 1.0)
                    {
                        failures.Add($"WN{wn} {il} {snr} dB: burst bias {mean - snr:F2}");
                    }

                    if (snr <= gateTop && BlockSd(p) > blockSdLimit)
                    {
                        failures.Add($"WN{wn} {il} {snr} dB: block sd {BlockSd(p):F2}");
                    }

                    (double pmean, _) = Stats(p.PreambleSnr);
                    if (Math.Abs(pmean - snr) > 2.0 && snr <= 30)
                    {
                        failures.Add($"WN{wn} {il} {snr} dB: preamble bias {pmean - snr:F2}");
                    }
                }

                for (int i = 1; i < results.Count; i++)
                {
                    if (Stats(results[i].BurstSnr).Mean <= Stats(results[i - 1].BurstSnr).Mean)
                    {
                        failures.Add($"WN{wn} {il}: not monotonic at {results[i].SetSnr} dB");
                    }
                }

                Report(string.Create(CultureInfo.InvariantCulture,
                    $"[snr] AWGN WN{wn} {il}: 10% block-error crossing at estimated {BlerCrossing(results.SelectMany(r => r.Blocks)):F0} dB"));
            }
        }

        failures.Should().BeEmpty();
    }

    [Fact]
    public void Sweep_PoorAndModerate()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS_POOR") != "1",
            "set MS110D_MASKS_POOR=1 for the section 2.5 Poor and Moderate points");
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("MS110D_SNR_SECONDS"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) ? sec : 60;
        var failures = new List<string>();
        foreach (int wn in SweepWns())
        {
            foreach (Ms110dInterleaverKind il in SweepInterleavers())
            {
                Ms110dMode mode = Ms110dMode.Mode3k(wn);
                Ms110dInterleaverParams ilp = Ms110dInterleaverParams.Get3k(wn, il);
                double blockSeconds = ilp.Frames * (mode.U + mode.K) / 2400.0;
                // Bursts of about 10 s (at least one block), enough of them for the signal time.
                int blocks = Math.Max(1, (int)Math.Round(10 / blockSeconds));
                int bursts = Math.Max(3, (int)Math.Ceiling(seconds / (blocks * blockSeconds)));
                double mask = MaskSnr[wn].Poor;
                foreach ((string name, WattersonPath[] paths) in new[] { ("Poor", WattersonChannel.Poor), ("Moderate", Moderate) })
                {
                    var results = new List<PointResult>();
                    foreach (double snr in new[] { mask, mask + 6, mask + 12 })
                    {
                        PointResult p = RunSweepPoint(name, paths, wn, il, snr, bursts, blocks,
                            90_000 + (wn * 100) + (int)il + (name == "Poor" ? 0 : 50));
                        results.Add(p);
                        Report(Describe(p));
                        // Section 2.5: on Poor the value is recorded, the repeatability gated.
                        if (name == "Poor" && Stats(p.BurstSnr).Std >= 2.0)
                        {
                            failures.Add($"Poor WN{wn} {il} {snr} dB: burst sd {Stats(p.BurstSnr).Std:F2}");
                        }
                    }

                    for (int i = 1; i < results.Count; i++)
                    {
                        if (name == "Poor" && Stats(results[i].BurstSnr).Mean <= Stats(results[i - 1].BurstSnr).Mean)
                        {
                            failures.Add($"Poor WN{wn} {il}: not monotonic at {results[i].SetSnr} dB");
                        }
                    }

                    Report(string.Create(CultureInfo.InvariantCulture,
                        $"[snr] {name} WN{wn} {il}: 10% block-error crossing at estimated {BlerCrossing(results.SelectMany(r => r.Blocks)):F0} dB"));
                }
            }
        }

        failures.Should().BeEmpty();
    }
}
