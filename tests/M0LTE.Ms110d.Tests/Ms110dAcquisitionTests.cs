using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Acquisition and the lock state machine across more than one burst: the re-read of a
/// rejected preamble at the strongest peak (issue #8), a receiver that keeps going after a
/// lock whose waveform changes the block geometry (issue #9), and the background preamble
/// search that lets go of a burst that will never reach its EOM (issue #10).
/// </summary>
public class Ms110dAcquisitionTests
{
    private static byte[] RandomBits(int count, int seed)
    {
        var random = new Random(seed);
        var bits = new byte[count];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }

        return bits;
    }

    private static float[] Gaussian(int count, double sigma, int seed)
    {
        var random = new Random(seed);
        var noise = new float[count];
        for (int i = 0; i < count; i += 2)
        {
            double u1 = 1.0 - random.NextDouble(), u2 = random.NextDouble();
            double r = Math.Sqrt(-2 * Math.Log(u1));
            noise[i] = (float)(sigma * r * Math.Cos(2 * Math.PI * u2));
            if (i + 1 < count)
            {
                noise[i + 1] = (float)(sigma * r * Math.Sin(2 * Math.PI * u2));
            }
        }

        return noise;
    }

    private static double Rms(float[] x)
    {
        double sum = 0;
        int n = 0;
        foreach (float v in x)
        {
            if (v != 0)
            {
                sum += v * v;
                n++;
            }
        }

        return Math.Sqrt(sum / Math.Max(1, n));
    }

    private static void AddNoise(float[] audio, double sigma, int seed)
    {
        float[] noise = Gaussian(audio.Length, sigma, seed);
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] += noise[i];
        }
    }

    private static float[] Concat(params float[][] parts)
    {
        var result = new float[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (float[] part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }

    private static (List<Ms110dBurst> Bursts, List<string> Diagnostics) Receive(float[] audio)
    {
        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        var diagnostics = new List<string>();
        demod.BurstCompleted += bursts.Add;
        demod.FrameDiagnostics += diagnostics.Add;

        // Fed in 20 ms blocks, as a live modem would.
        for (int i = 0; i < audio.Length; i += 192)
        {
            demod.Process(audio.AsSpan(i, Math.Min(192, audio.Length - i)));
        }

        return (bursts, diagnostics);
    }

    [Fact]
    public void A_Rejected_Earliest_Echo_Is_Read_Again_At_The_Strongest_Peak()
    {
        // Issue #8. Two static paths 4.2 ms apart, the earliest one 4.2 dB weaker: within the
        // walkback's reach, so acquisition times the preamble on it. At 2 dB SNR, with a
        // two-super-frame preamble, this seed's reads at the earliest echo fail at both
        // super-frames, and the receiver before the fix lost the whole burst. Read once more
        // at the strongest peak, the preamble locks, and the burst decodes from the earliest
        // echo as before.
        const int seed = 3;
        const double early = 0.62;
        const int delay = 40; // samples at 9600 Hz
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 5, PreambleSuperframes = 2 });
        byte[] payload = RandomBits(800, 77 + seed);
        float[] x = tx.Modulate(payload);
        var twoPath = new float[x.Length + delay];
        for (int i = 0; i < x.Length; i++)
        {
            twoPath[i] += (float)(early * x[i]);
            twoPath[i + delay] += x[i];
        }

        var audio = new float[3000 + twoPath.Length + 9600];
        twoPath.CopyTo(audio, 3000);
        AddNoise(audio, Rms(twoPath) * Math.Pow(10, -2.0 / 20), 1000 + seed);

        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        var diagnostics = new List<string>();
        demod.BurstCompleted += bursts.Add;
        demod.FrameDiagnostics += diagnostics.Add;
        demod.Process(audio);

        diagnostics.Should().Contain(l => l.StartsWith("retry@", StringComparison.Ordinal),
            "the read at the earliest echo is rejected and retried at the strongest peak");
        bursts.Should().ContainSingle(b => b.Reason == Ms110dBurstEndReason.Eom)
            .Which.PayloadBits.Should().Equal(payload);
    }

    [Fact]
    public void A_Long_Interleaver_Burst_After_A_Short_One_Decodes_On_The_Same_Receiver()
    {
        // Issue #9. The 8PSK block ensemble kept its candidate buffer from one burst to the
        // next, so the first WN 7 block after a lock with a longer interleaver threw "Destination
        // array was not long enough" out of Process. On air the second lock came from a WN 13
        // preamble whose WID was misread as WN 7 Long; any longer 8PSK block does it.
        byte[] first = RandomBits(1200, 9);
        byte[] second = RandomBits(1200, 10);
        float[] audio = Concat(
            new float[3000],
            new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 7 }).Modulate(first),
            new float[9600],
            new Ms110dModulator(new Ms110dTxSettings
            {
                WaveformNumber = 7,
                Interleaver = Ms110dInterleaverKind.Long,
            }).Modulate(second),
            new float[9600]);

        (List<Ms110dBurst> bursts, _) = Receive(audio);

        bursts.Should().HaveCount(2);
        bursts[0].Reason.Should().Be(Ms110dBurstEndReason.Eom);
        bursts[0].PayloadBits.Should().Equal(first);
        bursts[1].Reason.Should().Be(Ms110dBurstEndReason.Eom);
        bursts[1].PayloadBits.Should().Equal(second);
    }

    [Fact]
    public void A_Wrong_Waveform_Id_Does_Not_Stop_The_Receiver()
    {
        // Issue #9, the on-air shape: a WN 7 burst, then a WN 13 burst whose preamble says
        // WN 7 Long, then an ordinary burst. Whatever the receiver makes of the middle one,
        // it must not throw, and the burst after it must decode.
        const int preambleSamples = 3 * 576 * 4; // three super-frames at 4 samples a chip
        byte[] first = RandomBits(1200, 21);
        byte[] last = RandomBits(800, 22);
        float[] wn13 = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 13 })
            .Modulate(RandomBits(30000, 23));
        float[] wrongPreamble = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = 7,
            Interleaver = Ms110dInterleaverKind.Long,
        }).Modulate(RandomBits(64, 24));
        Array.Copy(wrongPreamble, wn13, preambleSamples);

        float[] audio = Concat(
            new float[3000],
            new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 7 }).Modulate(first),
            new float[9600],
            wn13,
            new float[9600],
            new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4 }).Modulate(last),
            new float[9600]);
        AddNoise(audio, 0.01, 5);

        List<Ms110dBurst> bursts = null!;
        FluentActions.Invoking(() => bursts = Receive(audio).Bursts).Should().NotThrow();

        bursts.Should().Contain(b => b.Reason == Ms110dBurstEndReason.Eom && b.PayloadBits.SequenceEqual(first));
        bursts.Should().Contain(b => b.Reason == Ms110dBurstEndReason.Eom && b.PayloadBits.SequenceEqual(last));
    }

    [Fact]
    public void A_New_Preamble_Ends_A_Burst_That_Lost_Its_Eom()
    {
        // Issue #10. The first burst is cut off part way, so its EOM never arrives, and the next
        // burst starts 1 s later. A receiver deaf while locked held the dead burst for seconds
        // and missed the second one entirely; the background search finds the new preamble,
        // lets go of the dead burst and decodes the new one.
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4 });
        float[] cut = tx.Modulate(RandomBits(4000, 31));
        Array.Resize(ref cut, cut.Length / 2);
        byte[] second = RandomBits(800, 32);

        float[] audio = Concat(
            new float[3000],
            cut,
            new float[9600],
            tx.Modulate(second),
            new float[9600]);
        AddNoise(audio, 0.005, 6);

        (List<Ms110dBurst> bursts, List<string> diagnostics) = Receive(audio);

        bursts.Should().HaveCount(2);
        bursts[0].Reason.Should().Be(Ms110dBurstEndReason.NewPreamble);
        bursts[1].Reason.Should().Be(Ms110dBurstEndReason.Eom);
        bursts[1].PayloadBits.Should().Equal(second);
        diagnostics.Should().ContainSingle(l => l.StartsWith("new-preamble@", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Burst_Without_A_New_Preamble_Is_Not_Let_Go()
    {
        // Issue #10, the other side: the background search runs over every data symbol of a
        // long burst, and nothing in the data may pass for a new preamble.
        foreach (int wn in new[] { 0, 1, 3, 5, 6, 7, 8, 13 })
        {
            var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn });
            byte[] payload = RandomBits(wn == 0 ? 600 : 6000, 40 + wn);
            float[] audio = Concat(new float[3000], tx.Modulate(payload), new float[9600]);
            AddNoise(audio, 0.003, 50 + wn);

            (List<Ms110dBurst> bursts, _) = Receive(audio);

            bursts.Should().ContainSingle($"WN {wn} is one burst");
            bursts[0].Reason.Should().Be(Ms110dBurstEndReason.Eom, $"WN {wn} runs to its EOM");
            bursts[0].PayloadBits.Should().Equal(payload, $"WN {wn} decodes");
        }
    }
}
