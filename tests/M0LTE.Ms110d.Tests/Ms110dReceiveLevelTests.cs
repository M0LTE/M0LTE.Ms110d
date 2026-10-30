using System.Globalization;
using M0LTE.Ms110d;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Issue #7: decoding must not depend on the absolute receive level. The K=48 waveforms
/// (WN1/WN2) start their equaliser with ridges sized for the level the masks run at, so
/// before the fix a burst arriving at a moderate level (preamble level about 0.05 to 0.15,
/// above the old boost threshold of 0.04) started at about a tenth of the right gain and
/// lost interleaver block 0. These tests replay one burst per waveform and channel at
/// receive levels from about 0.02 to 1.0 and require the same clean decode at every level.
/// <para>
/// The bursts use the Short interleaver, as on air: a Short block is short enough that a
/// slow equaliser start ruins block 0 outright, which is what the on-air test showed.
/// </para>
/// </summary>
public class Ms110dReceiveLevelTests
{
    // The simulated channel's output sits at a preamble level of about 0.25 (the level the
    // AWGN masks run at), so these scales cover preamble levels from about 0.02 to 1.0. The
    // band the old WN1/WN2 start failed in (0.05 to 0.15) is scales 0.2 to 0.6.
    private static readonly double[] Scales = [0.08, 0.2, 0.4, 0.6, 1.0, 4.0];

    // One burst per waveform and channel. Seeds are picked so the burst decodes cleanly at
    // the masks' level; for WN1/WN2 each one also failed at one or more of these levels
    // before the fix.
    private static readonly (int Wn, string Channel, int Seed)[] Bursts =
    [
        (1, "awgn", 7003), (1, "poor", 7014),
        (2, "awgn", 7000), (2, "poor", 7002),
        (3, "awgn", 7000), (3, "poor", 7000),
    ];

    public static TheoryData<int, string, int, double> Cases()
    {
        var data = new TheoryData<int, string, int, double>();
        foreach ((int wn, string channel, int seed) in Bursts)
        {
            foreach (double scale in Scales)
            {
                data.Add(wn, channel, seed, scale);
            }
        }

        return data;
    }

    private static (float[] Rx, byte[] Payload) BuildBurst(int wn, string channel, int seed, double scale)
    {
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Short,
            ConstraintLength = 7,
        };
        var tx = new Ms110dModulator(settings);
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Short);

        // Four interleaver blocks: block 0 is the one a slow equaliser start loses.
        var random = new Random(seed + wn);
        var payload = new byte[(4 * il.InputBits) - 32];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio = tx.Modulate(payload);
        (WattersonPath[] paths, double snrDb) = channel switch
        {
            "awgn" => (Array.Empty<WattersonPath>(), 5.0),
            "poor" => (WattersonChannel.Poor, 8.0),
            _ => throw new ArgumentOutOfRangeException(nameof(channel)),
        };
        var ch = new WattersonChannel(9600, seed + (10 * wn) + (channel == "poor" ? 1 : 0), paths);
        float[] rx = ch.Apply(audio, snrDb, leadInSamples: 9600, leadOutSamples: 9600);
        for (int i = 0; i < rx.Length; i++)
        {
            rx[i] = (float)(rx[i] * scale);
        }

        return (rx, payload);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Decodes_Cleanly_At_Every_Receive_Level(int wn, string channel, int seed, double scale)
    {
        (float[] rx, byte[] payload) = BuildBurst(wn, channel, seed, scale);

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

        reason.Should().Be(Ms110dBurstEndReason.Eom, $"WN{wn} {channel} at scale {scale} (preamble level {level:F3})");
        errors.Should().Be(0, $"WN{wn} {channel} at scale {scale} (preamble level {level:F3}) must decode cleanly");
    }

    // Stronger than a clean decode: for WN1/WN2 the receiver normalizes the level before the
    // equalizer, so the first-pass soft values should be the same at every input scale, up to
    // float rounding. Compared with the masks' level (scale 1).
    [Theory]
    [InlineData(1, "poor", 7014)]
    [InlineData(2, "poor", 7002)]
    [InlineData(2, "awgn", 7000)]
    public void First_Pass_Soft_Values_Do_Not_Depend_On_Receive_Level(int wn, string channel, int seed)
    {
        float[] reference = FirstPassLlrs(wn, channel, seed, 1.0);
        reference.Should().NotBeEmpty();
        foreach (double scale in new[] { 0.08, 0.2, 0.6, 4.0 })
        {
            float[] llrs = FirstPassLlrs(wn, channel, seed, scale);
            llrs.Length.Should().Be(reference.Length, $"WN{wn} {channel} at scale {scale}");
            double maxDiff = 0;
            for (int i = 0; i < llrs.Length; i++)
            {
                maxDiff = Math.Max(maxDiff, Math.Abs(llrs[i] - reference[i]));
            }

            maxDiff.Should().BeLessThan(1e-4, $"WN{wn} {channel} soft values at scale {scale}");
        }
    }

    private static float[] FirstPassLlrs(int wn, string channel, int seed, double scale)
    {
        (float[] rx, _) = BuildBurst(wn, channel, seed, scale);
        var demod = new Ms110dDemodulator(new Ms110dDemodOptions());
        var llrs = new List<float>();
        demod.FirstPassBlockLlrs += (_, block) => llrs.AddRange(block);
        demod.Process(rx);
        demod.Process(new float[9600]);
        demod.Terminate();
        return [.. llrs];
    }
}
