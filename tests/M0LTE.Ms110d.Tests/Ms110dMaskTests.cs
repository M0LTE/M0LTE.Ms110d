using M0LTE.Fec;
using M0LTE.Ms110d;
using M0LTE.Ms110d.Fec;
using M0LTE.HfChannel;


namespace M0LTE.Ms110d.Tests;

/// <summary>
/// The design §5.3/§6 statistical mask runs - environment-gated (<c>MS110D_MASKS=1</c> for
/// the AWGN gates, <c>MS110D_MASKS_POOR=1</c> for the measured-not-gated Poor channel),
/// because a full point runs minutes of simulated signal and the suite hours; per §5.3 these
/// are nightly/rotating runs, not per-PR CI. Conditions per D.6.1: coded BER ≤ 1.0E-5, Long
/// interleaver, 20-super-frame preamble; SNR in 3 kHz noise bandwidth. Accept rule per point
/// (the implemented form of design §5.3, restated 2026-07-23): a fixed sample of ≥ 3×10⁶
/// payload bits (fading gate points additionally ≥ 600 s simulated per D-LXV duration logic);
/// with ≥ 30 errors the direct BER must be ≤ 1e-5, else the 97.5 % Poisson upper bound must
/// clear 1e-5. Since §B4/§B4.1/§B3.5b (2026-07-26) the at-mask Poor points (WN0-WN6, WN13)
/// are hard-gated by default; the Poor-gate successor program (2026-08-20) added WN7 (G1d,
/// the 8PSK per-block ensemble) and WN8 (G2, the MFB decoder's MMSE cold rung and schedule),
/// so every Poor point is hard-gated and <see cref="MeasuredOnlyBank"/> is empty - it stays
/// as the mechanism a future measured-only point re-banks into. <c>MS110D_POOR_GATED=1</c>
/// still forces the 1E-5 gate everywhere (now a no-op, kept for chasing legs).
/// Override the bit budget with <c>MS110D_MASK_BITS</c> for smoke runs (reports are then
/// labelled SMOKE, not gate evidence).
/// </summary>
public class Ms110dMaskTests(ITestOutputHelper output)
{
    private const double TargetBer = 1e-5;

    private sealed record MaskRun(
        long Bits, long Errors, int Bursts, int AcquisitionFailures, double SimSeconds,
        long UncodedBits = 0, long UncodedErrors = 0, long DeepFadeBits = 0, long DeepFadeErrors = 0,
        int TurboConverged = 0, int TurboReverted = 0, int TurboAborted = 0, int TurboSkipped = 0,
        int AgcResolves = 0)
    {
        public double Ber => Bits == 0 ? double.NaN : (double)Errors / Bits;
    }

    // Table D-LXIV 3 kHz rows (docs/dev/ms110d/tables/d6x-ber-masks.csv): WN → (AWGN, Poor) SNR dB.
    public static TheoryData<int, double> AwgnMasks() => new()
    {
        { 0, -6 }, { 1, -3 }, { 2, 0 }, { 3, 3 }, { 4, 5 }, { 5, 6 }, { 6, 9 },
        { 7, 13 }, { 8, 16 }, { 13, 6 },
    };

    public static TheoryData<int, double> PoorMasks() => new()
    {
        { 0, -1 }, { 1, 3 }, { 2, 5 }, { 3, 7 }, { 4, 10 }, { 5, 11 }, { 6, 14 },
        { 7, 19 }, { 8, 23 }, { 13, 11 },
    };

    // The measured-only ceiling bank (docs/dev/archive/ms110d/poor-gate-successor-plan.md, leg G0).
    // A point that does not clear the 1E-5 Poor mask has what it DOES measure banked here
    // as the exact closing counts of the battery that closed it. (WN7 sat here at 83/48
    // from the W0 re-baseline until G1d's ensemble took it to 0/0 and into the gated set;
    // WN8 at 1,254 / 75,713 then 31 / 77 until G2f took it to 12 / 18 and into the gated
    // set - both 2026-08-20.) The simulation is seed-deterministic, so in the
    // battery configuration (in-code budget, 4 workers, both seed families) the count is
    // a byte-identity pin, not a statistic: any other digit, in either direction, is
    // drift in the demodulator and must be explained and re-banked (the Phase B closeout
    // §6 merge rule). A chase leg that intends to move these numbers runs with
    // MS110D_POOR_GATED=1, which asserts the mask instead of the bank.
    private sealed record MeasuredOnlyPin(long Bits, long Errors, string Evidence);

    // Empty since 2026-08-20: WN8 left for the gated set at G2f (12 / 18 on 4,325,120 bits per
    // family, evidence/2026-08-20-poorgate-g2/battery-g2f/; it sat here at 31 / 77 after
    // G2d and at 1,254 / 75,713 from the WN8 program's W6).
    private static readonly Dictionary<(int Wn, int SeedOffset), MeasuredOnlyPin> MeasuredOnlyBank = new();

    // The bank only speaks for the configuration it was measured in: the in-code budget,
    // the battery's 4-worker seed split, no instrument knobs (genie, lambda, ridge A/B
    // runs are report evidence, never pins). Anything else is a measured run with no pin.
    private static bool BankApplies(int wn, out MeasuredOnlyPin pin)
    {
        pin = null!;
        bool batteryForm = Environment.GetEnvironmentVariable("MS110D_MASK_BITS") is null
            && Workers() == 4
            && !GenieMode()
            && Environment.GetEnvironmentVariable("MS110D_MASK_RLS_LAMBDA") is null
            && Environment.GetEnvironmentVariable("MS110D_MASK_TRACK_RIDGE") is null;
        return batteryForm && MeasuredOnlyBank.TryGetValue((wn, SeedOffset()), out pin!);
    }

    [Theory]
    [MemberData(nameof(AwgnMasks))]
    public void Awgn_Mask_Gate(int wn, double snrDb)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS") != "1",
            "set MS110D_MASKS=1 for the statistical mask runs");
        string? wnFilter = Environment.GetEnvironmentVariable("MS110D_MASK_WN");
        Assert.SkipWhen(wnFilter is not null && wnFilter != wn.ToString(),
            $"MS110D_MASK_WN={wnFilter} - skipping WN{wn}");

        snrDb += double.TryParse(Environment.GetEnvironmentVariable("MS110D_MASK_SNR_DELTA"), out double delta) ? delta : 0;
        MaskRun run = RunPoint(wn, snrDb, [], TargetBits(), seed: 100 + wn + SeedOffset());
        Report($"AWGN WN{wn} @ {snrDb:+0;-0;0} dB{SeedTag()}", run);
        AssertMask(run);
    }

    [Fact]
    public void Static_Wid2_Gate()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS") != "1",
            "set MS110D_MASKS=1 for the statistical mask runs");

        // Table D-LXV, 3 kHz: WID 2 → 3-path static (0.0, 3.0, 9.0 ms), equal power - the rig
        // whose purpose (design §6) is to prove the K=48 DFE's 9 ms feedback span + probe-
        // training convergence WITHOUT fade tracking, not to hit a spec SNR.
        //
        // House bar RESTATED (2026-07-17): the gate ran at 5 dB - a number BORROWED from the
        // WN2 "Poor" (2-path/2 ms/1 Hz fading) mask, never a spec requirement (the D-LXV SNR
        // column was not transcribed; D.6.3 is "Not yet standardized"). 5 dB is unjustifiably
        // optimistic for this HARDER static channel (3 equal paths spread over 9 ms → deeper
        // spectral nulls than the 2-path Poor rig). Measured waterfall after the K=48 MMSE-ridge
        // fix (docs/dev/ms110d/design.md §5.1; MS110D_MASK_BITS=500000): 5 dB → 8.3E-5, 7 dB → 7.8E-6
        // (knee), 9 dB → clean. The equalizer demonstrably spans the echo and reaches the 1E-5
        // mask by ~9 dB; the full FF span is load-bearing (shrinking FF 32→12 wrecks this
        // channel to 3.2E-3). Gate restated to 9 dB - the lowest robustly-passing SNR - proving
        // span+convergence with margin. Better-than-9 dB on this channel is Phase-B RLS scope
        // (design §2.5/§6), deliberately not chased here. Sweep via MS110D_STATIC_SNR.
        WattersonPath[] paths = [new(0), new(3.0), new(9.0)];
        double snrDb = double.TryParse(
            Environment.GetEnvironmentVariable("MS110D_STATIC_SNR"), out double s) ? s : 9;
        MaskRun run = RunPoint(2, snrDb, paths, TargetBits(), seed: 900 + SeedOffset());
        Report($"Static WID2 (0/3/9 ms) @ {snrDb:+0;-0;0} dB (restated house bar)", run);
        AssertMask(run);
    }

    [Theory]
    [InlineData(2, -75)]
    [InlineData(2, 75)]
    [InlineData(6, 75)]
    public void Doppler_Offset_Engineering_Check(int wn, double offsetHz)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS") != "1",
            "set MS110D_MASKS=1 for the statistical mask runs");

        // D.6.4 specifies ±75 Hz at 24 dB for WID 10 (a Phase C gate); run it at the Phase A
        // waveforms as an engineering check (design §5.1).
        MaskRun run = RunPoint(wn, 24, [], 200_000, seed: 700 + wn, frequencyOffsetHz: offsetHz);
        Report($"Doppler offset WN{wn} @ {offsetHz:+0;-0} Hz, 24 dB", run);
        run.AcquisitionFailures.Should().Be(0);
        run.Errors.Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(PoorMasks))]
    public void Poor_Channel_Mask_Gate(int wn, double snrDb)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS_POOR") != "1",
            "set MS110D_MASKS_POOR=1 for the Poor-channel mask runs");
        string? wnFilter = Environment.GetEnvironmentVariable("MS110D_MASK_WN");
        Assert.SkipWhen(wnFilter is not null && wnFilter != wn.ToString(),
            $"MS110D_MASK_WN={wnFilter} - skipping WN{wn}");

        // §B4 (2026-07-26): WN2/WN5/WN6's rates sit close enough to the mask that a 3M
        // sample is not §5.3-decidable (WN6 ≈8.8E-6 clears only the ≥30-error direct
        // path, and 3M expects ~28; WN2 ≈4.9E-6 trips the <30-error Poisson bound from
        // k=20). The default is the 6M budget the B4 gate evidence used.
        long targetBits = Environment.GetEnvironmentVariable("MS110D_MASK_BITS") is null && wn is 2 or 5 or 6
            ? 6_000_000 : TargetBits();

        snrDb += double.TryParse(Environment.GetEnvironmentVariable("MS110D_MASK_SNR_DELTA"), out double poorDelta) ? poorDelta : 0;
        MaskRun run = RunPoint(wn, snrDb, WattersonChannel.Poor, targetBits, seed: 500 + wn + SeedOffset(),
            minSimSeconds: 600);
        Report($"POOR WN{wn} @ {snrDb:+0;-0;0} dB{SeedTag()}", run);

        // §B4 (2026-07-26): the at-mask set is hard-gated by default - flip criterion and
        // both-family gate-run evidence in evidence/2026-07-26-phase-b4-gate/. The two
        // measured-only points both left the bank for the gated set on 2026-08-20 - WN7 at
        // G1d, WN8 at G2f (below); the bank stays as the mechanism. §B4.1 (same
        // day): WN6 joins the gated set - the SPIKE-UP per-segment pricing margin lever
        // moved it from 57/57 (pooled bound 1.14E-5, AT THE LINE) to 35/39 both families
        // (pooled 74/12.97M, bound 7.15E-6 - passes the B4 flip criterion;
        // evidence/2026-07-26-phase-b41-wn6floor/). §B3.5b (same day): WN0 joins - the
        // symmetric RAKE finger window ended the echo-lock lottery and took the point
        // from 5.99E-3/6.40E-3 to 0 and 3 errors per 3M family (bounds 1.22E-6/2.92E-6,
        // pooled 1.46E-6 - passes all three B4 flip conditions;
        // evidence/2026-07-26-phase-b35b-wn0genie/). G1d (2026-08-20, the Poor-gate
        // successor program): WN7 joins - the 8PSK per-block ensemble (DFE-chain beside
        // the MFB-form decoder, each receiver's evidence weighed in log-likelihood units)
        // took the point from 83/48 to 0/0 on both families at full budget with every
        // other census byte-identical (evidence/2026-08-20-poorgate-g1d/).
        // G2 (2026-08-20): WN8 joins - the MFB decoder's MMSE cold rung and its schedule
        // (soft floor, hard-phase cycle members, hard-first after a cycle, plateau
        // handover) took the point from 1,254 / 75,713 to 12 / 18 per 4,325,120 bits on
        // the two families (bounds 4.85E-6 / 6.58E-6), every other census byte-identical
        // (evidence/2026-08-20-poorgate-g2/battery-g2f/). Every Poor point is now gated;
        // MS110D_POOR_GATED=1 stays as the chasing-leg tool.
        bool gated = wn is 0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 13
            || Environment.GetEnvironmentVariable("MS110D_POOR_GATED") == "1";
        if (gated)
        {
            AssertMask(run);
        }
        else if (BankApplies(wn, out MeasuredOnlyPin pin))
        {
            run.AcquisitionFailures.Should().Be(0, "the banked batteries acquired every burst");
            run.Bits.Should().Be(pin.Bits,
                $"the measured-only bank for WN{wn}{SeedTag()} was taken at this budget ({pin.Evidence})");
            run.Errors.Should().Be(pin.Errors,
                $"WN{wn}{SeedTag()} is measured-only and pinned to its banked closing count " +
                $"({pin.Evidence}); a different digit is demodulator drift to explain and " +
                "re-bank, and a chase leg runs with MS110D_POOR_GATED=1");
        }
        else
        {
            run.Bits.Should().BeGreaterThan(0);
        }
    }

    // Off-rig discipline harness (phase-b-plan §B0): fading geometries the D.6.1 Poor rig
    // does NOT use, run at the Poor mask SNRs as direction checks - measured, never gated -
    // so no tuned constant can quietly encode the rig's exact 2 ms / 1 Hz numbers again
    // (the BCJR delay-5 lesson from the Phase A audit).
    public static TheoryData<string, double, double, int, double> OffRigPoints()
    {
        var data = new TheoryData<string, double, double, int, double>();
        foreach ((int wn, double snr) in new (int, double)[]
                 { (0, -1), (1, 3), (2, 5), (3, 7), (4, 10), (5, 11), (6, 14), (7, 19), (8, 23), (13, 11) })
        {
            data.Add("1ms-0.5Hz", 1.0, 0.5, wn, snr);
            data.Add("3ms-2Hz", 3.0, 2.0, wn, snr);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OffRigPoints))]
    public void OffRig_Direction_Check(string label, double delayMs, double dopplerHz, int wn, double snrDb)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS_OFFRIG") != "1",
            "set MS110D_MASKS_OFFRIG=1 for the off-rig direction checks");
        string? wnFilter = Environment.GetEnvironmentVariable("MS110D_MASK_WN");
        Assert.SkipWhen(wnFilter is not null && wnFilter != wn.ToString(),
            $"MS110D_MASK_WN={wnFilter} - skipping WN{wn}");

        WattersonPath[] paths =
        [
            new(0, Fading: true, DopplerSpreadHz: dopplerHz),
            new(delayMs, Fading: true, DopplerSpreadHz: dopplerHz),
        ];
        MaskRun run = RunPoint(wn, snrDb, paths, TargetBits(), seed: 800 + wn + SeedOffset());
        Report($"OFFRIG[{label}] WN{wn} @ {snrDb:+0;-0;0} dB{SeedTag()}", run);
        run.Bits.Should().BeGreaterThan(0); // direction check: measured, never gated
    }

    [Theory]
    [InlineData(4, 10)]
    [InlineData(6, 14)]
    [InlineData(8, 23)]
    public void Poor_Channel_Smoke(int wn, double snrDb)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS_POOR") != "1",
            "set MS110D_MASKS_POOR=1 for the Poor-channel mask runs");

        long bits = long.TryParse(
            Environment.GetEnvironmentVariable("MS110D_MASK_BITS"), out long b) ? b : 100_000;
        MaskRun run = RunPoint(wn, snrDb, WattersonChannel.Poor, bits, seed: 500 + wn);
        Report($"POOR (smoke) WN{wn} @ {snrDb:+0;-0;0} dB", run);
        // Smoke = quick indicative number, not a gate: the default 100k bits cannot clear
        // the 97.5 % Poisson bound even at zero errors (3.7/1e5 > 1e-5), so AssertMask here
        // would be unpassable by construction. Report + acquisition sanity only.
        run.AcquisitionFailures.Should().Be(0);
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(4, 10)]
    [InlineData(6, 14)]
    [InlineData(7, 19)]
    [InlineData(8, 23)]
    public void Static_2Path_Diagnostic(int wn, double snrDb)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS_POOR") != "1",
            "set MS110D_MASKS_POOR=1");

        // Same geometry as Poor (0ms, 2ms) but no fading - isolates multipath from fading.
        WattersonPath[] staticPoor =
        [
            new(0, Fading: false),
            new(2, Fading: false),
        ];
        MaskRun run = RunPoint(wn, snrDb, staticPoor, 100_000, seed: 500 + wn);
        Report($"STATIC-2PATH WN{wn} @ {snrDb:+0;-0;0} dB", run);
        run.AcquisitionFailures.Should().Be(0);
        run.Ber.Should().BeLessThanOrEqualTo(1e-5);
    }

    // The complement of Static_2Path_Diagnostic: ONE Rayleigh path, no echo - isolates
    // fade/carrier tracking from ISI (B1 autopsy instrument; measured, not gated: the
    // whole point is reading the number for broken-tier points).
    [Theory]
    [InlineData(0, -1)]
    [InlineData(7, 19)]
    public void Flat_Rayleigh_Diagnostic(int wn, double snrDb)
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MASKS_POOR") != "1",
            "set MS110D_MASKS_POOR=1");

        WattersonPath[] flatFading = [new(0, Fading: true, DopplerSpreadHz: 1)];
        MaskRun run = RunPoint(wn, snrDb, flatFading, 100_000, seed: 500 + wn);
        Report($"FLAT-RAYLEIGH WN{wn} @ {snrDb:+0;-0;0} dB", run);
        run.AcquisitionFailures.Should().Be(0);
        run.Bits.Should().BeGreaterThan(0); // measured, never gated
    }

    // Disjoint-seed verification (issue #67): the equalizer thresholds were iterated
    // against the default seeds, so gate claims can be cross-checked on a disjoint
    // realization set with MS110D_MASK_SEED_OFFSET (reports carry the offset).
    private static int SeedOffset()
    {
        return int.TryParse(
            Environment.GetEnvironmentVariable("MS110D_MASK_SEED_OFFSET"), out int o) ? o : 0;
    }

    // Not a mask run (the D-LXIV/D-LXV points are specified at the Long interleaver):
    // MS110D_MASK_INTERLEAVER=Short|Medium|UltraShort runs the point at that interleaver.
    // Reports are labelled by the caller, never gate evidence.
    private static Ms110dInterleaverKind MaskInterleaver()
    {
        string? name = Environment.GetEnvironmentVariable("MS110D_MASK_INTERLEAVER");
        return name is null ? Ms110dInterleaverKind.Long : Enum.Parse<Ms110dInterleaverKind>(name);
    }

    private static bool GenieMode()
    {
        return Environment.GetEnvironmentVariable("MS110D_MASK_GENIE") == "1";
    }

    private static long TargetBits()
    {
        string? overrideBits = Environment.GetEnvironmentVariable("MS110D_MASK_BITS");
        return overrideBits is null ? 3_000_000 : long.Parse(overrideBits);
    }

    // Intra-point parallelism: bursts are statistically independent (fresh demodulator,
    // fresh per-burst channel/noise realization), so a point's bit budget can split
    // across MS110D_MASK_WORKERS workers on disjoint seed subspaces (+1e6 per worker -
    // far beyond the per-burst stride of 1000) and the counts summed; the fading
    // sim-time floor divides likewise (the statistical intent is ~600 independent fades
    // TOTAL, which N disjoint realizations of 600/N s each provide). Point wall-clock
    // scales ~1/N - this is what keeps the low-rate points (WN0/1/2: 10-40 ks of
    // simulated audio per 3M bits) from dominating a sweep on an otherwise idle box.
    private static int Workers()
    {
        return int.TryParse(
            Environment.GetEnvironmentVariable("MS110D_MASK_WORKERS"), out int w)
            ? Math.Max(1, w) : 1;
    }

    private MaskRun RunPoint(
        int wn,
        double snrDb,
        WattersonPath[] paths,
        long targetBits,
        int seed,
        double minSimSeconds = 0,
        double frequencyOffsetHz = 0)
    {
        int workers = Workers();
        if (workers <= 1)
        {
            return RunPointWorker(wn, snrDb, paths, targetBits, seed, minSimSeconds, frequencyOffsetHz, 0);
        }

        long bitsPerWorker = (targetBits + workers - 1) / workers;
        double simPerWorker = minSimSeconds / workers;
        var runs = new MaskRun[workers];
        Parallel.For(0, workers, w =>
        {
            runs[w] = RunPointWorker(
                wn, snrDb, paths, bitsPerWorker, seed + (w * 1_000_000),
                simPerWorker, frequencyOffsetHz, w);
        });

        MaskRun total = new(0, 0, 0, 0, 0);
        foreach (MaskRun r in runs)
        {
            total = new MaskRun(
                total.Bits + r.Bits, total.Errors + r.Errors, total.Bursts + r.Bursts,
                total.AcquisitionFailures + r.AcquisitionFailures, total.SimSeconds + r.SimSeconds,
                total.UncodedBits + r.UncodedBits, total.UncodedErrors + r.UncodedErrors,
                total.DeepFadeBits + r.DeepFadeBits, total.DeepFadeErrors + r.DeepFadeErrors,
                total.TurboConverged + r.TurboConverged, total.TurboReverted + r.TurboReverted,
                total.TurboAborted + r.TurboAborted, total.TurboSkipped + r.TurboSkipped,
                total.AgcResolves + r.AgcResolves);
        }

        return total;
    }

    private MaskRun RunPointWorker(
        int wn,
        double snrDb,
        WattersonPath[] paths,
        long targetBits,
        int seed,
        double minSimSeconds,
        double frequencyOffsetHz,
        int worker)
    {
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = MaskInterleaver(),
            ConstraintLength = 7,
            PreambleSuperframes = 20,
        };
        var tx = new Ms110dModulator(settings);
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, MaskInterleaver());

        // Uncoded-vs-coded telemetry (§5.3, phase-b-plan §B0): re-encode the known TX
        // stream per block and compare against sign(first-pass LLR) in wire order - the
        // uncoded channel-bit error rate. Wire positions map to air time, so with the
        // rig's recorded gain trajectory each uncoded error is classified against the
        // composite channel power at its own air time (deep fade = composite < −6 dB).
        ConvolutionalCode telemetryCode = ConvolutionalCode.K7;
        PunctureSpec telemetryPuncture = Ms110dPuncture.Get(telemetryCode, tx.Mode.CodeRate);
        var telemetryInterleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);
        int preambleChips = settings.PreambleSuperframes * 18 * 32; // TlcBlocks = 0, M > 1
        int bitsPerSymbol = tx.Mode.BitsPerSymbol;
        bool fadingPaths = paths.Any(p => p.Fading);

        double blockSeconds = wn == 0
            ? il.Frames * 32.0 / 2400
            : il.Frames * (tx.Mode.U + tx.Mode.K) / 2400.0;
        int blocksPerBurst = Math.Max(1, (int)(90 / blockSeconds));
        int payloadBitsPerBurst = (blocksPerBurst * il.InputBits) - 32;

        var random = new Random(seed);
        long bits = 0, errors = 0;
        int bursts = 0, acquisitionFailures = 0;
        double simSeconds = 0;
        long uncodedBits = 0, uncodedErrors = 0, deepFadeBits = 0, deepFadeErrors = 0;
        int turboConverged = 0, turboReverted = 0, turboAborted = 0, turboSkipped = 0, agcResolves = 0;
        // MS110D_DEBUG moved host-side: the library fires FrameDiagnostics, the harness prints.
        bool debugTrace = Environment.GetEnvironmentVariable("MS110D_DEBUG") == "1";
        // MS110D_MASK_GENIE=1: feed the demodulator the SAME channel realization noise-free
        // (identical seed at SNR = ∞ - the rig draws fading gains before noise, so the
        // realization is reproduced exactly). Estimation then runs on channel truth while
        // detection stays noisy: the perfect-channel-observation bound of the current
        // detector (phase-b-plan §B0). Always labelled, never performance evidence.
        bool genie = GenieMode();

        // §B3 tail-autopsy census (MS110D_MASK_BURST_LOG=prefix): one CSV line per burst,
        // one file per (wn, worker). The B3-entry re-baseline showed the sub-8PSK points
        // are tail-dominated - a minority of bursts hold essentially all the errors - so
        // the first job is localizing WHICH bursts die. Every line carries the burst's
        // channel seed: with the worker seed and burst index the exact corpse is
        // reproducible in the autopsy rig (payload = sequential draws of Random(seed)).
        string? burstLogPrefix = Environment.GetEnvironmentVariable("MS110D_MASK_BURST_LOG");
        using StreamWriter? burstLog = burstLogPrefix is null
            ? null
            : new StreamWriter($"{burstLogPrefix}-wn{wn}-w{worker}.csv") { AutoFlush = true };
        burstLog?.WriteLine(
            "burst,channelSeed,bits,decoded,errors,firstErr,lastErr,collapses," +
            "uncodedErrs,deepFadeErrs,turboC,turboR,turboA,turboS,reason");

        // MS110D_MASK_BURSTS=N: N bursts in total (split over the workers) instead of a bit budget.
        int fixedBursts = int.TryParse(Environment.GetEnvironmentVariable("MS110D_MASK_BURSTS"), out int nb)
            ? (nb + Workers() - 1) / Workers() : 0;
        while (fixedBursts > 0 ? bursts < fixedBursts : (bits < targetBits || simSeconds < minSimSeconds))
        {
            long uncodedErrsBefore = uncodedErrors;
            long deepFadeErrsBefore = deepFadeErrors;
            var payload = new byte[payloadBitsPerBurst];
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)random.Next(2);
            }

            float[] audio = tx.Modulate(payload);
            var channel = new WattersonChannel(9600, seed + (1000 * bursts) + 1, paths)
            {
                RecordGains = fadingPaths,
            };
            float[] rx = channel.Apply(
                audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400,
                frequencyOffsetHz: frequencyOffsetHz);
            IReadOnlyList<Complex32[]>? gains = channel.PathGains();

            byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, il.InputBits);
            int txBlocks = txBits.Length / il.InputBits;
            var fetchedBlocks = new byte[txBlocks][];
            for (int b = 0; b < txBlocks; b++)
            {
                fetchedBlocks[b] = Ms110dFraming.EncodeBlock(
                    telemetryCode, telemetryPuncture, telemetryInterleaver,
                    txBits.AsSpan(b * il.InputBits, il.InputBits));
            }

            var decoded = new List<byte>(payload.Length + 64);
            // §B2.4 RLS λ A/B knob: MS110D_MASK_RLS_LAMBDA=0.995 runs the design-§2.5
            // coherence-set memory instead of the frame-tied default. Evidence lines from
            // such runs are for the RLS-vs-NLMS report, never the gate table.
            float? rlsLambda = float.TryParse(
                Environment.GetEnvironmentVariable("MS110D_MASK_RLS_LAMBDA"), out float lam)
                ? lam : null;
            // §B3.2 anchored-ridge A/B knob: the anchor ridge is the solve's cross-frame
            // memory; the WN2 genie pair measured a flat estimation-noise tax this knob
            // probes. Same rule as the λ knob: report evidence only, never the gate table.
            float? trackRidge = float.TryParse(
                Environment.GetEnvironmentVariable("MS110D_MASK_TRACK_RIDGE"), out float tr)
                ? tr : null;
            var demod = new Ms110dDemodulator(new Ms110dDemodOptions
            {
                RlsForgettingFactor = rlsLambda,
                TrackRidge = trackRidge,
                // §B4.1 per-segment pricing variant ("spikeup"/"spike2s"); unset = shipped.
                TurboNsegMode = Environment.GetEnvironmentVariable("MS110D_TURBO_NSEG"),
            });
            Ms110dBurstEndReason? endReason = null;
            demod.BurstCompleted += bu => endReason = bu.Reason;
            demod.BlockDecoded += b => decoded.AddRange(b.Bits);
            demod.FirstPassBlockLlrs += (blockIndex, llrs) =>
            {
                if (blockIndex >= txBlocks)
                {
                    return; // surplus block (post-EOM noise decode) - no TX reference
                }

                byte[] fetched = fetchedBlocks[blockIndex];
                int compareBits = Math.Min(llrs.Length, fetched.Length);
                uncodedBits += compareBits;
                for (int i = 0; i < compareBits; i++)
                {
                    bool bitError = (llrs[i] > 0 ? 0 : 1) != fetched[i];
                    if (bitError)
                    {
                        uncodedErrors++;
                    }

                    if (gains is null || gains.Count == 0)
                    {
                        continue;
                    }

                    long symbol = i / bitsPerSymbol;
                    long chip = wn == 0
                        ? (((long)blockIndex * (il.SizeBits / 2)) + symbol) * 32
                        : ((((long)blockIndex * il.Frames) + (symbol / tx.Mode.U)) * (tx.Mode.U + tx.Mode.K))
                            + (symbol % tx.Mode.U);
                    int gainIndex = (int)((preambleChips + chip) * 4 / 100);
                    if (CompositeGain(gains, gainIndex) < 0.25)
                    {
                        deepFadeBits++;
                        if (bitError)
                        {
                            deepFadeErrors++;
                        }
                    }
                }
            };
            if (debugTrace)
            {
                demod.FrameDiagnostics += line => Console.Error.WriteLine(line);
            }

            if (genie)
            {
                float[] clean = new WattersonChannel(9600, seed + (1000 * bursts) + 1, paths).Apply(
                    audio, double.PositiveInfinity, leadInSamples: 2400, leadOutSamples: 2400,
                    frequencyOffsetHz: frequencyOffsetHz);
                // Interleaved chunks: the genie must stay ahead of every read but never
                // far enough ahead to wrap the ~13.7 s ring (WriteGenie contract).
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
            }

            long burstErrors = 0;
            long firstErr = -1, lastErr = -1;
            if (decoded.Count == 0)
            {
                acquisitionFailures++;
                burstErrors = payload.Length;
                firstErr = 0;
                lastErr = payload.Length - 1;
            }
            else
            {
                int compared = Math.Min(decoded.Count, payload.Length);
                for (int i = 0; i < compared; i++)
                {
                    if (decoded[i] != payload[i])
                    {
                        burstErrors++;
                        if (firstErr < 0)
                        {
                            firstErr = i;
                        }

                        lastErr = i;
                    }
                }

                burstErrors += payload.Length - compared; // truncated decode counts as errors
                if (compared < payload.Length)
                {
                    if (firstErr < 0)
                    {
                        firstErr = compared;
                    }

                    lastErr = payload.Length - 1;
                }

                // Decode beyond the whole TX stream (payload + EOM + fill) means post-EOM
                // false blocks - previously free, now errors (#67).
                burstErrors += Math.Max(0, decoded.Count - txBits.Length);
            }

            burstLog?.WriteLine(
                $"{bursts},{seed + (1000 * bursts) + 1},{payload.Length},{decoded.Count}," +
                $"{burstErrors},{firstErr},{lastErr},{demod.CollapseResolves}," +
                $"{uncodedErrors - uncodedErrsBefore},{deepFadeErrors - deepFadeErrsBefore}," +
                $"{demod.TurboConverged},{demod.TurboReverted},{demod.TurboAborted}," +
                $"{demod.TurboSkipped},{endReason}");

            bits += payload.Length;
            errors += burstErrors;
            bursts++;
            simSeconds += audio.Length / 9600.0;
            turboConverged += demod.TurboConverged;
            turboReverted += demod.TurboReverted;
            turboAborted += demod.TurboAborted;
            turboSkipped += demod.TurboSkipped;
            agcResolves += demod.AgcResolves;

            if (bursts % 10 == 0)
            {
                string progress =
                    $"  … [w{worker}] {bursts} bursts, {bits:N0} bits, {errors} errors, {simSeconds:F0} s simulated";
                output.WriteLine(progress);
                Console.Error.WriteLine(progress);
            }
        }

        return new MaskRun(
            bits, errors, bursts, acquisitionFailures, simSeconds,
            uncodedBits, uncodedErrors, deepFadeBits, deepFadeErrors,
            turboConverged, turboReverted, turboAborted, turboSkipped, agcResolves);
    }

    /// <summary>Composite channel power (equal-power-normalized) at a gain-trajectory
    /// index; static paths contribute their constant unit gain.</summary>
    private static double CompositeGain(IReadOnlyList<Complex32[]> gains, int index)
    {
        double sum = 0;
        foreach (Complex32[] path in gains)
        {
            sum += path[Math.Min(index, path.Length - 1)].MagnitudeSquared();
        }

        return sum / gains.Count;
    }

    private static string SeedTag()
    {
        int o = SeedOffset();
        return o == 0 ? "" : $" (seed+{o})";
    }

    private void Report(string label, MaskRun run)
    {
        double upper = PoissonUpper975(run.Errors) / run.Bits;
        string verdict = run.Errors >= 30
            ? $"BER {run.Ber:E2} (direct, ≥30 errors)"
            : $"BER {run.Ber:E2}, 97.5 % upper bound {upper:E2}";
        string workersTag = Workers() > 1 ? $", {Workers()} workers" : "";
        string line =
            $"[mask] {label}: {run.Bits:N0} bits, {run.Errors} errors, {run.Bursts} bursts " +
            $"({run.AcquisitionFailures} acquisition failures), {run.SimSeconds:F0} s simulated{workersTag} - {verdict}";
        if (run.UncodedBits > 0)
        {
            line += $" | uncoded {(double)run.UncodedErrors / run.UncodedBits:E2}";
            if (run.DeepFadeBits > 0 && run.UncodedErrors > 0)
            {
                line += $", deep-fade(<-6 dB) holds {100.0 * run.DeepFadeErrors / run.UncodedErrors:F0}% of " +
                    $"uncoded errors in {100.0 * run.DeepFadeBits / run.UncodedBits:F0}% of bits";
            }

            line += $", turbo {run.TurboConverged}c/{run.TurboReverted}r/{run.TurboAborted}a/{run.TurboSkipped}s";
            line += $", agc {run.AgcResolves}"; // #101: 0 = AGC no-op in-family (masks byte-identical)
        }

        if (run.Bits < 3_000_000)
        {
            line += " [SMOKE - below the §5.3 budget; not gate evidence]";
        }

        if (GenieMode())
        {
            line += " [GENIE - perfect-channel-observation bound; never performance evidence]";
        }

        string? lambda = Environment.GetEnvironmentVariable("MS110D_MASK_RLS_LAMBDA");
        if (lambda is not null)
        {
            line += $" [RLS λ={lambda} - §B2.4 A/B run; report evidence only, never the gate table]";
        }

        string? trackRidgeLabel = Environment.GetEnvironmentVariable("MS110D_MASK_TRACK_RIDGE");
        if (trackRidgeLabel is not null)
        {
            line += $" [track ridge={trackRidgeLabel} - §B3.2 A/B run; report evidence only, never the gate table]";
        }

        output.WriteLine(line);

        string? log = Environment.GetEnvironmentVariable("MS110D_MASK_LOG");
        if (log is not null)
        {
            File.AppendAllText(log, $"{DateTime.UtcNow:o} {line}{Environment.NewLine}");
        }
    }

    private static void AssertMask(MaskRun run)
    {
        run.AcquisitionFailures.Should().Be(0, "every burst must acquire (≤3 attempts rule, house-tightened to 1)");
        if (run.Errors >= 30)
        {
            run.Ber.Should().BeLessThanOrEqualTo(TargetBer);
        }
        else
        {
            (PoissonUpper975(run.Errors) / run.Bits).Should().BeLessThanOrEqualTo(
                TargetBer, "the 95 % CI upper bound must clear the mask (design §5.3)");
        }
    }

    /// <summary>97.5 % upper confidence bound on a Poisson mean given k observed events
    /// (χ²(0.975, 2k+2)/2, Wilson-Hilferty approximation - ≤1 % error here).</summary>
    private static double PoissonUpper975(long k)
    {
        double nu = (2 * k) + 2;
        const double z = 1.959964;
        double h = 2.0 / (9.0 * nu);
        double chi = nu * Math.Pow(1 - h + (z * Math.Sqrt(h)), 3);
        return chi / 2.0;
    }
}
