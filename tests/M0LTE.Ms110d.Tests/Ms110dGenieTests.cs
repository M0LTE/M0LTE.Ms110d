using M0LTE.Ms110d;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Validation of the channel-truth genie (phase-b-plan §B0): the diagnostic seam that feeds
/// estimation a noise-free copy of the same channel realization while detection stays on
/// the noisy stream. The genie must be provably inert when its stream carries no extra
/// information, and the rig must provably reproduce the identical realization noise-free.
/// </summary>
public class Ms110dGenieTests
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

    private static List<byte> Decode(float[] rx, float[]? genieStream)
    {
        var decoded = new List<byte>();
        var demod = new Ms110dDemodulator();
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        if (genieStream is null)
        {
            demod.Process(rx);
            return decoded;
        }

        // Interleaved chunk feeding: genie ahead of every read but never far enough
        // ahead to wrap the ring (the WriteGenie contract).
        const int chunk = 4800;
        for (int i = 0; i < rx.Length; i += chunk)
        {
            int length = Math.Min(chunk, rx.Length - i);
            demod.WriteGenie(genieStream.AsSpan(i, length));
            demod.Process(rx.AsSpan(i, length));
        }

        return decoded;
    }

    [Fact]
    public void Genie_Fed_The_Noisy_Stream_Itself_Is_Bit_Identical_To_A_Normal_Run()
    {
        // The strongest seam check: with the genie ring holding EXACTLY the noisy samples,
        // every redirected estimation read returns the same values a normal run reads, so
        // the decode must be bit-identical - the plumbing changes data sourcing, nothing else.
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4, PreambleSuperframes = 5 });
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(4, Ms110dInterleaverKind.Long);
        byte[] payload = RandomBits(il.InputBits * 2, seed: 21);
        float[] rx = new WattersonChannel(9600, seed: 31).Apply(
            tx.Modulate(payload), snrDb: 5, leadInSamples: 2400, leadOutSamples: 4800);

        List<byte> normal = Decode(rx, genieStream: null);
        List<byte> genie = Decode(rx, genieStream: rx);

        normal.Count.Should().BeGreaterThan(0, "the reference run must decode");
        genie.Should().Equal(normal, "a genie fed the noisy stream itself must change nothing");
    }

    [Fact]
    public void Genie_With_The_True_Clean_Stream_Decodes_Bit_Exact_On_Awgn_At_Mask()
    {
        // AWGN at the WN4 mask point with estimation on the true noise-free signal:
        // must decode bit-exact (the genie bound can only improve on the normal run,
        // which is itself 0-error here).
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4, PreambleSuperframes = 5 });
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(4, Ms110dInterleaverKind.Long);
        byte[] payload = RandomBits(il.InputBits * 2, seed: 22);
        float[] audio = tx.Modulate(payload);
        var channel = new WattersonChannel(9600, seed: 32);
        float[] rx = channel.Apply(audio, snrDb: 5, leadInSamples: 2400, leadOutSamples: 4800);
        float[] clean = new WattersonChannel(9600, seed: 32).Apply(
            audio, double.PositiveInfinity, leadInSamples: 2400, leadOutSamples: 4800);

        List<byte> decoded = Decode(rx, clean);

        decoded.Count.Should().BeGreaterThanOrEqualTo(payload.Length);
        for (int i = 0; i < payload.Length; i++)
        {
            if (decoded[i] != payload[i])
            {
                Assert.Fail($"genie AWGN decode differs at bit {i}");
            }
        }
    }

    [Fact]
    public void Genie_Fed_Far_Ahead_Of_The_Noisy_Stream_Throws()
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4, PreambleSuperframes = 5 });
        byte[] payload = RandomBits(128, seed: 26);
        float[] rx = new WattersonChannel(9600, seed: 34).Apply(
            tx.Modulate(payload), snrDb: 20, leadInSamples: 2400, leadOutSamples: 2400);

        var demod = new Ms110dDemodulator();
        Action feedAll = () => demod.WriteGenie(rx);
        feedAll.Should().Throw<InvalidOperationException>(
            "feeding the whole burst upfront would wrap the genie ring and silently corrupt estimation");
    }

    [Fact]
    public void Genie_Stream_Falling_Behind_Throws()
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4, PreambleSuperframes = 5 });
        byte[] payload = RandomBits(128, seed: 23);
        float[] rx = new WattersonChannel(9600, seed: 33).Apply(
            tx.Modulate(payload), snrDb: 20, leadInSamples: 2400, leadOutSamples: 2400);

        var demod = new Ms110dDemodulator();
        demod.WriteGenie(rx.AsSpan(0, 512)); // genie enabled but covering almost nothing
        Action process = () => demod.Process(rx);
        process.Should().Throw<InvalidOperationException>(
            "a genie stream behind the noisy stream would silently corrupt estimation");
    }

    [Fact]
    public void Watterson_Same_Seed_Noiseless_Run_Reproduces_The_Identical_Realization()
    {
        // The genie's foundation: the rig draws fading gains BEFORE noise, so the same seed
        // at SNR = ∞ replays the identical channel realization. The noisy−clean difference
        // must then be pure calibrated noise: its RMS matches the AddNoise sigma, far below
        // signal scale.
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4, PreambleSuperframes = 2 });
        float[] audio = tx.Modulate(RandomBits(512, seed: 24));
        const double snrDb = 20;

        float[] noisy = new WattersonChannel(9600, 44, WattersonChannel.Poor).Apply(audio, snrDb);
        float[] clean = new WattersonChannel(9600, 44, WattersonChannel.Poor).Apply(
            audio, double.PositiveInfinity);
        float[] clean2 = new WattersonChannel(9600, 44, WattersonChannel.Poor).Apply(
            audio, double.PositiveInfinity);

        clean2.Should().Equal(clean, "the noiseless run must be deterministic per seed");

        double signalPower = 0, diffPower = 0;
        for (int i = 0; i < clean.Length; i++)
        {
            signalPower += (double)audio[i] * audio[i];
            double d = (double)noisy[i] - clean[i];
            diffPower += d * d;
        }

        signalPower /= audio.Length;
        diffPower /= clean.Length;
        double expectedNoisePower = signalPower / Math.Pow(10, snrDb / 10.0) * (9600.0 / 2.0) / 3000.0;
        (diffPower / expectedNoisePower).Should().BeInRange(0.9, 1.1,
            "noisy − clean must be exactly the calibrated additive noise (identical realization)");
    }

    [Fact]
    public void Watterson_Records_The_Fading_Gain_Trajectory_When_Asked()
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4, PreambleSuperframes = 2 });
        float[] audio = tx.Modulate(RandomBits(2048, seed: 25));
        var channel = new WattersonChannel(9600, 45, WattersonChannel.Poor) { RecordGains = true };
        channel.Apply(audio, snrDb: 20);

        var gains = channel.PathGains();
        gains.Should().NotBeNull();
        gains!.Count.Should().Be(2, "Poor has two fading paths");
        foreach (Complex32[] trajectory in gains)
        {
            // ~96 Hz coverage of the burst span.
            trajectory.Length.Should().BeGreaterThan(
                (int)(0.9 * audio.Length / 9600.0 * WattersonChannel.GainSampleRateHz));

            double meanSquare = 0;
            foreach (Complex32 g in trajectory)
            {
                meanSquare += g.Cnorm();
            }

            meanSquare /= trajectory.Length;
            meanSquare.Should().BeInRange(0.2, 5.0,
                "gain trajectories are unit-mean-square processes (loose bound: short spans wander)");
        }
    }
}
