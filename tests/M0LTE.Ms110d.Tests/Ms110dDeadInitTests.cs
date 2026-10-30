using System.Globalization;
using M0LTE.Ms110d;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Regression rig for issue #101 - the WN2 (BPSK r1/4, K=48) DFE dead-init edge and the input
/// signal-level AGC that fixes it. There is no AGC upstream of this modem, so a globally-low
/// receive level (the real-RF Poor capture, ~20 dB below the sim's nominal) leaves the K=48
/// cold-restart LS solves over-regularised → the equalizer initialises dead → SignalLost. The
/// fix normalizes the global receive level (estimated from a fade-averaged preamble SIGNAL
/// correlation) ahead of the equalizer. Reproduced by the issue's own method - scale a
/// clean-sim WN2 Poor burst down ~−20 dB (init gain 0.081 → 0.014, matching the OTA capture's
/// ref≈0.014). #101 first shipped as a boost for globally-low bursts only (a dead-zone above
/// a level of 0.04); since issues #7 (WN1/WN2) and #17 (every other waveform with
/// mini-probes) every burst is normalized to a fixed level in both directions, and
/// <see cref="Ms110dReceiveLevelTests"/> pins the level independence that buys.
/// </summary>
public class Ms110dDeadInitTests
{
    // A deterministic Poor burst (WN2 unless told otherwise), built exactly as Ms110dMaskTests.RunPointWorker builds
    // theirs (Long interleaver, K=7, 20-super-frame preamble). Bespoke seed, outside the gated
    // mask families - a robustness fixture, not a mask point.
    private const int FixtureSeed = 424_242;
    private const double Wn2PoorSnrDb = 5.0;

    private static (float[] Rx, byte[] Payload) BuildPoorBurst(int payloadSeed, int channelSeed, double scale, int wn = 2)
    {
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Long,
            ConstraintLength = 7,
            PreambleSuperframes = 20,
        };
        var tx = new Ms110dModulator(settings);
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long);
        double blockSeconds = il.Frames * (tx.Mode.U + tx.Mode.K) / 2400.0;
        int blocksPerBurst = Math.Max(1, (int)(90 / blockSeconds));
        int payloadBitsPerBurst = (blocksPerBurst * il.InputBits) - 32;

        var random = new Random(payloadSeed);
        var payload = new byte[payloadBitsPerBurst];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio = tx.Modulate(payload);
        var channel = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor);
        float[] rx = channel.Apply(audio, Wn2PoorSnrDb, leadInSamples: 2400, leadOutSamples: 2400);
        if (scale != 1.0)
        {
            for (int i = 0; i < rx.Length; i++)
            {
                rx[i] = (float)(rx[i] * scale);
            }
        }

        return (rx, payload);
    }

    private sealed record DecodeResult(
        Ms110dBurstEndReason? Reason, int Decoded, long CodedErrors,
        double InitRefGain, double AgcLevel, double AgcGain);

    private static double Field(string line, string key)
    {
        int at = line.IndexOf(key, StringComparison.Ordinal);
        if (at < 0)
        {
            return double.NaN;
        }

        int start = at + key.Length;
        int end = line.IndexOf(' ', start);
        return double.Parse(line[start..(end < 0 ? line.Length : end)], CultureInfo.InvariantCulture);
    }

    private static DecodeResult RunBurst(float[] rx, byte[] payload)
    {
        var demod = new Ms110dDemodulator(new Ms110dDemodOptions());
        var decoded = new List<byte>(payload.Length + 64);
        Ms110dBurstEndReason? reason = null;
        double initRefGain = double.NaN, agcLevel = double.NaN, agcGain = double.NaN;
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        demod.BurstCompleted += bu => reason = bu.Reason;
        demod.FrameDiagnostics += line =>
        {
            if (double.IsNaN(agcGain) && line.StartsWith("agc@", StringComparison.Ordinal))
            {
                agcLevel = Field(line, "level=");
                agcGain = Field(line, "gain=");
            }
            else if (double.IsNaN(initRefGain) && line.StartsWith("frame@", StringComparison.Ordinal))
            {
                initRefGain = Field(line, "ref=");
            }
        };
        demod.Process(rx);

        long codedErrors = 0;
        int compared = Math.Min(decoded.Count, payload.Length);
        for (int i = 0; i < compared; i++)
        {
            if (decoded[i] != payload[i])
            {
                codedErrors++;
            }
        }

        codedErrors += payload.Length - compared; // truncated decode counts as errors
        return new DecodeResult(reason, decoded.Count, codedErrors, initRefGain, agcLevel, agcGain);
    }

    // The red test (issue #101): a WN2 Poor burst that decodes cleanly at full receive level
    // dies SignalLost once the level is scaled down ~−20 dB - the K=48 init solve initialises
    // dead. RED on `main` (SignalLost, init gain ≈ 0.014); GREEN with the input AGC (the global
    // level-normalization lifts it back to nominal so the equalizer initialises and tracks
    // normally - same SNR as full level, so it decodes clean).
    [Fact]
    public void Wn2_Poor_Dead_Init_Recovers_At_Low_Receive_Level()
    {
        // −20 dB - the issue's own controlled reproduction (real path level offset).
        (float[] rx, byte[] payload) = BuildPoorBurst(FixtureSeed, FixtureSeed + 1, scale: 0.1);
        DecodeResult r = RunBurst(rx, payload);

        r.Reason.Should().Be(
            Ms110dBurstEndReason.Eom,
            "the input AGC must normalize the globally-low level so the burst decodes to EOM " +
            "instead of dying at a dead init");
        r.AgcGain.Should().BeGreaterThan(
            2.0, "the AGC must detect the low level and boost it (≈10× at −20 dB)");
        r.CodedErrors.Should().Be(
            0, "scaling is level-only (SNR unchanged), so a normalized burst decodes cleanly");
    }

    // The full-level control: the SAME burst at nominal level decodes cleanly. WN1/WN2
    // always normalize to a fixed level (issue #7), so the gain is whatever brings the
    // measured level to that target (Poor reads below the AWGN level the target matches, so
    // this burst is boosted a little).
    [Fact]
    public void Wn2_Poor_Full_Level_Normalizes_To_Target()
    {
        (float[] rx, byte[] payload) = BuildPoorBurst(FixtureSeed, FixtureSeed + 1, scale: 1.0);
        DecodeResult r = RunBurst(rx, payload);

        r.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        r.CodedErrors.Should().Be(0);
        (r.AgcLevel * r.AgcGain).Should().BeApproximately(
            0.25, 0.001, "WN2 normalizes its measured receive level to the K=48 target");
    }

    // Issue #17: the other waveforms with mini-probes normalize too, to their own target
    // (0.5), in both directions.
    [Theory]
    [InlineData(1.0)]
    [InlineData(4.0)]
    public void Wn3_Poor_Normalizes_To_Target(double scale)
    {
        (float[] rx, byte[] payload) = BuildPoorBurst(FixtureSeed, FixtureSeed + 1, scale, wn: 3);
        DecodeResult r = RunBurst(rx, payload);

        r.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        (r.AgcLevel * r.AgcGain).Should().BeApproximately(
            0.5, 0.002, "WN3 normalizes its measured receive level to the shared target");
    }

    // Calibration (env-gated): sweep the receive-level scale and print reason / init gain /
    // measured AGC level+gain so the dead-init flip point and the AGC response are visible.
    [Fact]
    public void Dead_Init_Scale_Calibration()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_DEADINIT_CAL") != "1",
            "set MS110D_DEADINIT_CAL=1 for the dead-init scale calibration");

        foreach (double scale in new[] { 1.0, 0.5, 0.25, 0.1, 0.05, 0.03, 0.02, 0.01 })
        {
            (float[] rx, byte[] payload) = BuildPoorBurst(FixtureSeed, FixtureSeed + 1, scale);
            DecodeResult r = RunBurst(rx, payload);
            Console.Error.WriteLine(
                $"scale={scale:F3} ({20 * Math.Log10(scale):+0.0;-0.0} dB): reason={r.Reason} " +
                $"decoded={r.Decoded}/{payload.Length} codedErrs={r.CodedErrors} " +
                $"initRef={r.InitRefGain:F4} agcLevel={r.AgcLevel:F4} agcGain={r.AgcGain:F3}");
        }
    }
}
