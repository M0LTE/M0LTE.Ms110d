using System.Collections.Concurrent;
using System.Globalization;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Issue #17 receive-level sweep (env-gated, writes a CSV): the same bursts decoded at several
/// receive levels, so a level-dependent receiver shows up as seeds whose outcome changes with
/// level. One line per burst and level: wn, channel, interleaver, seed, scale, measured
/// preamble level, clean (0/1), bit errors. Each waveform runs at its own mask SNR.
/// <para>
/// Env: MS110D_SWEEP_OUT (CSV path, required), MS110D_SWEEP_WN, MS110D_SWEEP_CHANNEL
/// (awgn or poor), MS110D_SWEEP_IL (Short or Long), MS110D_SWEEP_SEEDS (default 100),
/// MS110D_SWEEP_SEED_BASE, MS110D_SWEEP_BLOCKS, MS110D_SWEEP_SCALES (comma list, default
/// eight log-spaced scales from 0.04 to 4, which on Poor is a preamble level of about 0.007
/// to 0.7), MS110D_SWEEP_THREADS.
/// </para>
/// </summary>
public class Ms110dLevelSweep
{
    private static readonly Dictionary<int, (double Awgn, double Poor)> MaskSnr = new()
    {
        [0] = (-6, -1), [1] = (-3, 3), [2] = (0, 5), [3] = (3, 7), [4] = (5, 10), [5] = (6, 11),
        [6] = (9, 14), [7] = (13, 19), [8] = (16, 23), [13] = (6, 11),
    };

    [Fact]
    public void Sweep()
    {
        string? outPath = Environment.GetEnvironmentVariable("MS110D_SWEEP_OUT");
        Assert.SkipWhen(outPath is null, "set MS110D_SWEEP_OUT");
        int wn = int.Parse(Environment.GetEnvironmentVariable("MS110D_SWEEP_WN")!, CultureInfo.InvariantCulture);
        string channel = Environment.GetEnvironmentVariable("MS110D_SWEEP_CHANNEL") ?? "poor";
        var kind = Enum.Parse<Ms110dInterleaverKind>(Environment.GetEnvironmentVariable("MS110D_SWEEP_IL") ?? "Short");
        int seeds = int.Parse(Environment.GetEnvironmentVariable("MS110D_SWEEP_SEEDS") ?? "100", CultureInfo.InvariantCulture);
        int seedBase = int.Parse(Environment.GetEnvironmentVariable("MS110D_SWEEP_SEED_BASE") ?? "20000", CultureInfo.InvariantCulture);
        int blocks = int.Parse(Environment.GetEnvironmentVariable("MS110D_SWEEP_BLOCKS") ?? (kind == Ms110dInterleaverKind.Long ? "2" : "4"), CultureInfo.InvariantCulture);
        double[] scales = (Environment.GetEnvironmentVariable("MS110D_SWEEP_SCALES") ?? "0.04,0.0772,0.149,0.288,0.556,1.07,2.07,4.0")
            .Split(',').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        double snrDb = channel == "poor" ? MaskSnr[wn].Poor : MaskSnr[wn].Awgn;

        var lines = new ConcurrentBag<string>();
        var work = (from s in Enumerable.Range(0, seeds) from sc in scales select (Seed: seedBase + s, Scale: sc)).ToArray();
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = int.TryParse(Environment.GetEnvironmentVariable("MS110D_SWEEP_THREADS"), out int th) ? th : Environment.ProcessorCount }, w =>
        {
            (bool clean, int errors, double level) = RunOne(wn, channel, kind, blocks, snrDb, w.Seed, w.Scale);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"{wn},{channel},{kind},{w.Seed},{w.Scale},{level:F4},{(clean ? 1 : 0)},{errors}"));
        });
        File.WriteAllLines(outPath!, lines.OrderBy(l => l, StringComparer.Ordinal));
    }

    private static (bool Clean, int Errors, double Level) RunOne(
        int wn, string channel, Ms110dInterleaverKind kind, int blocks, double snrDb, int seed, double scale)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn, Interleaver = kind, ConstraintLength = 7,
        });
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        var random = new Random(seed + wn);
        var payload = new byte[(blocks * il.InputBits) - 32];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio = tx.Modulate(payload);
        WattersonPath[] paths = channel == "poor" ? WattersonChannel.Poor : [];
        var ch = new WattersonChannel(9600, seed + (10 * wn) + (channel == "poor" ? 1 : 0), paths);
        float[] rx = ch.Apply(audio, snrDb, leadInSamples: 9600, leadOutSamples: 9600);
        for (int i = 0; i < rx.Length; i++)
        {
            rx[i] = (float)(rx[i] * scale);
        }

        var demod = new Ms110dDemodulator(new Ms110dDemodOptions());
        var decoded = new List<byte>(payload.Length + 64);
        Ms110dBurstEndReason? reason = null;
        double level = double.NaN;
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        demod.BurstCompleted += b => reason ??= b.Reason;
        demod.FrameDiagnostics += line =>
        {
            if (double.IsNaN(level) && line.StartsWith("agc@", StringComparison.Ordinal))
            {
                int at = line.IndexOf("level=", StringComparison.Ordinal) + 6;
                level = double.Parse(line[at..line.IndexOf(' ', at)], CultureInfo.InvariantCulture);
            }
        };
        demod.Process(rx);
        demod.Process(new float[9600]);
        demod.Terminate();

        int errors = 0;
        for (int i = 0; i < payload.Length; i++)
        {
            if (i >= decoded.Count || decoded[i] != payload[i])
            {
                errors++;
            }
        }

        return (reason == Ms110dBurstEndReason.Eom && errors == 0, errors, level);
    }
}
