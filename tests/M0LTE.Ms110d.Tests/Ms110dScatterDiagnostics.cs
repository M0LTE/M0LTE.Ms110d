using M0LTE.Ms110d;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Env-gated per-symbol scatter dump for the B1 autopsies (phase-b-plan §B1): one Poor
/// burst at the mask SNR with every equalized data symbol, the per-frame tracking
/// diagnostics, and the rig's tap trajectory written to files for offline analysis
/// (rotation clustering, fade alignment, error localization). Not a gate - an instrument.
/// <c>MS110D_SCATTER=1</c>, <c>MS110D_SCATTER_WN</c> (default 7), <c>MS110D_SCATTER_SNR</c>
/// (default the WN's Poor mask), <c>MS110D_SCATTER_SEED</c> (default 500+WN),
/// <c>MS110D_SCATTER_OUT</c> (directory, default the current directory),
/// <c>MS110D_SCATTER_GENIE=1</c> for a genie-aided dump.
/// </summary>
public class Ms110dScatterDiagnostics
{
    private static readonly Dictionary<int, double> PoorSnr = new()
    {
        { 0, -1 }, { 1, 3 }, { 2, 5 }, { 3, 7 }, { 4, 10 }, { 5, 11 }, { 6, 14 },
        { 7, 19 }, { 8, 23 }, { 13, 11 },
    };

    [Fact]
    public void Poor_Burst_Scatter_Dump()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_SCATTER") != "1",
            "set MS110D_SCATTER=1 for the diagnostic scatter dump");

        int wn = int.TryParse(Environment.GetEnvironmentVariable("MS110D_SCATTER_WN"), out int w) ? w : 7;
        double snrDb = double.TryParse(Environment.GetEnvironmentVariable("MS110D_SCATTER_SNR"), out double s)
            ? s : PoorSnr[wn];
        int seed = int.TryParse(Environment.GetEnvironmentVariable("MS110D_SCATTER_SEED"), out int sd)
            ? sd : 500 + wn;
        string outDir = Environment.GetEnvironmentVariable("MS110D_SCATTER_OUT") ?? ".";
        bool genie = Environment.GetEnvironmentVariable("MS110D_SCATTER_GENIE") == "1";

        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Long,
            ConstraintLength = 7,
            PreambleSuperframes = 20,
        };
        var tx = new Ms110dModulator(settings);
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long);

        var random = new Random(seed);
        var payload = new byte[(2 * il.InputBits) - 32];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio = tx.Modulate(payload);
        var channel = new WattersonChannel(9600, seed + 1, WattersonChannel.Poor) { RecordGains = true };
        float[] rx = channel.Apply(audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400);

        string tag = $"wn{wn}-seed{seed}{(genie ? "-genie" : "")}";
        using var symbols = new StreamWriter(Path.Combine(outDir, $"scatter-{tag}.csv"));
        symbols.WriteLine("index,re,im");
        using var frames = new StreamWriter(Path.Combine(outDir, $"frames-{tag}.log"));
        using var gainsOut = new StreamWriter(Path.Combine(outDir, $"gains-{tag}.csv"));

        int symbolIndex = 0;
        var decoded = new List<byte>();
        var demod = new Ms110dDemodulator();
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        demod.DataSymbolEqualized += y => symbols.WriteLine(
            $"{symbolIndex++},{y.Re:F5},{y.Im:F5}");
        demod.FrameDiagnostics += line => frames.WriteLine(line);
        if (genie)
        {
            float[] clean = new WattersonChannel(9600, seed + 1, WattersonChannel.Poor).Apply(
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
        }

        gainsOut.WriteLine("index,p0re,p0im,p1re,p1im");
        IReadOnlyList<Complex32[]> gains = channel.PathGains()!;
        int count = Math.Max(gains[0].Length, gains[1].Length);
        for (int i = 0; i < count; i++)
        {
            Complex32 g0 = gains[0][Math.Min(i, gains[0].Length - 1)];
            Complex32 g1 = gains[1][Math.Min(i, gains[1].Length - 1)];
            gainsOut.WriteLine($"{i},{g0.Re:F5},{g0.Im:F5},{g1.Re:F5},{g1.Im:F5}");
        }

        int errors = 0;
        int compared = Math.Min(decoded.Count, payload.Length);
        for (int i = 0; i < compared; i++)
        {
            if (decoded[i] != payload[i])
            {
                errors++;
            }
        }

        // Instrument, not a gate: record the outcome in the dump directory.
        File.WriteAllText(
            Path.Combine(outDir, $"summary-{tag}.txt"),
            $"WN{wn} @ {snrDb} dB seed {seed} genie={genie}: {compared}/{payload.Length} bits compared, " +
            $"{errors} errors, {decoded.Count} decoded, {symbolIndex} symbols dumped, " +
            $"turbo {demod.TurboConverged}c/{demod.TurboReverted}r/{demod.TurboAborted}a/{demod.TurboSkipped}s\n");
        decoded.Count.Should().BeGreaterThan(0, "the burst must at least acquire for the dump to mean anything");
    }
}
