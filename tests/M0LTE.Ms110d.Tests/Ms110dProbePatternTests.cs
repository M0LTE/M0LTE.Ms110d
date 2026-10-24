using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// The receiver accepts both conventions for which mini-probes are cyclically shifted: the one
/// this library has always sent (shifted at the selected interleaver's boundary) and the
/// D.5.2.2 one (shifted only at the Long interleaver's boundary). The standard pattern is
/// produced here through the modulator's internal test switch; the default stays the old one.
/// </summary>
public class Ms110dProbePatternTests(ITestOutputHelper output)
{
    private static readonly int[] Waveforms = [1, 2, 3, 4, 5, 6, 7, 8, 13];

    // Table D-LXIV AWGN mask SNR per waveform (dB); the loopbacks run a modest margin above it.
    private static double MaskSnr(int wn) => wn switch
    {
        1 => -3, 2 => 0, 3 => 3, 4 => 5, 5 => 6, 6 => 9, 7 => 13, 8 => 16, 13 => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(wn)),
    };

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

    public static TheoryData<int, Ms110dInterleaverKind, bool, bool> Matrix()
    {
        var data = new TheoryData<int, Ms110dInterleaverKind, bool, bool>();
        foreach (int wn in Waveforms)
        {
            // Long: both rules coincide, covered by Long_Interleaver_Sends_Identical_Audio_Under_Both_Patterns.
            foreach (Ms110dInterleaverKind kind in new[]
            {
                Ms110dInterleaverKind.UltraShort, Ms110dInterleaverKind.Short, Ms110dInterleaverKind.Medium,
            })
            {
                foreach (bool standard in new[] { false, true })
                {
                    foreach (bool awgn in new[] { false, true })
                    {
                        data.Add(wn, kind, standard, awgn);
                    }
                }
            }
        }

        return data;
    }

    private static (Ms110dBurst? Burst, Ms110dDemodulator Demod) Receive(float[] audio)
    {
        var demod = new Ms110dDemodulator();
        Ms110dBurst? burst = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.Process(new float[1500]);
        demod.Process(audio);
        demod.Process(new float[6000]);
        return (burst, demod);
    }

    private static float[] Transmit(
        int wn, Ms110dInterleaverKind kind, bool standard, byte[] payload, bool awgn, int seed, bool eot = true)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = kind,
            AppendEot = eot,
        })
        {
            StandardProbePattern = standard,
        };
        float[] audio = tx.Modulate(payload);
        if (!awgn)
        {
            return audio;
        }

        var channel = new WattersonChannel(9600, seed);
        return channel.Apply(audio, snrDb: MaskSnr(wn) + 8, leadInSamples: 1500, leadOutSamples: 4000);
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_Mode_And_Interleaver_Loops_Back_Under_Both_Patterns(
        int wn, Ms110dInterleaverKind kind, bool standard, bool awgn)
    {
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        // Two interleaver blocks, so the stream carries more than one boundary.
        byte[] payload = RandomBits((2 * il.InputBits) - 32 - 8, 7000 + wn + (int)kind);
        float[] audio = Transmit(wn, kind, standard, payload, awgn, seed: 31 + wn);

        (Ms110dBurst? burst, _) = Receive(audio);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }

    [Theory]
    [InlineData(5, Ms110dInterleaverKind.UltraShort)]
    [InlineData(1, Ms110dInterleaverKind.Short)]
    [InlineData(7, Ms110dInterleaverKind.Medium)]
    public void Pattern_Is_Decided_From_The_Probes(int wn, Ms110dInterleaverKind kind)
    {
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        foreach (bool standard in new[] { false, true })
        {
            byte[] payload = RandomBits((6 * il.InputBits) - 32 - 8, 7100 + wn);
            float[] audio = Transmit(wn, kind, standard, payload, awgn: true, seed: 5);

            // Stop before the last block completes: the end of a burst resets the receiver's
            // burst state.
            Ms110dMode mode = Ms110dMode.Mode3k(wn);
            int lastBlockSamples = il.Frames * (mode.U + mode.K) * 4;
            var demod = new Ms110dDemodulator();
            demod.Process(new float[1500]);
            demod.Process(audio.AsSpan(0, audio.Length - 4000 - lastBlockSamples - 400).ToArray());

            // The first probe on which the two rules disagree.
            long g = il.Frames - 2 >= 0 && kind != Ms110dInterleaverKind.UltraShort ? il.Frames - 2 : 0;
            demod.ProbePatternForBlocks.Boundary(g).Should().Be(
                !standard, "the receiver follows the pattern the probes were sent with");
            output.WriteLine($"WN{wn} {kind} standard={standard}: {demod.ProbePatternForBlocks.Scored} probes scored, locked={demod.ProbePatternForBlocks.Locked}");
        }
    }

    [Theory]
    [InlineData(6, Ms110dInterleaverKind.Short, 14)]
    [InlineData(6, Ms110dInterleaverKind.Short, 11)]
    [InlineData(13, Ms110dInterleaverKind.Short, 5)]
    [InlineData(13, Ms110dInterleaverKind.Short, 8)]
    [InlineData(3, Ms110dInterleaverKind.UltraShort, 7)]
    public void Faded_Current_Pattern_Bursts_Never_Leave_The_Current_Pattern(
        int wn, Ms110dInterleaverKind kind, double snrDb)
    {
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        Ms110dMode mode = Ms110dMode.Mode3k(wn);
        for (int seed = 1; seed <= 8; seed++)
        {
            var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn, Interleaver = kind });
            byte[] payload = RandomBits((20 * il.InputBits) - 32 - 8, 8000 + seed);
            float[] audio = new WattersonChannel(9600, 900 + seed, WattersonChannel.Poor)
                .Apply(tx.Modulate(payload), snrDb, leadInSamples: 1500, leadOutSamples: 4000);

            // Stop before the last block completes (the end of a burst resets the pattern).
            int lastBlockSamples = il.Frames * (mode.U + mode.K) * 4;
            var demod = new Ms110dDemodulator();
            demod.Process(new float[1500]);
            demod.Process(audio.AsSpan(0, audio.Length - 4000 - lastBlockSamples - 400).ToArray());

            for (long g = 0; g < 20L * il.Frames; g++)
            {
                if (ProbePattern.CurrentRule(g, il.Frames))
                {
                    demod.ProbePatternForBlocks.Boundary(g).Should().BeTrue(
                        $"a faded current-pattern burst (seed {seed}) must stay on the current pattern at frame {g}");
                }
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Long_Interleaver_Sends_Identical_Audio_Under_Both_Patterns(int wn)
    {
        var settings = new Ms110dTxSettings { WaveformNumber = wn, Interleaver = Ms110dInterleaverKind.Long };
        byte[] payload = RandomBits(300, 77);
        float[] current = new Ms110dModulator(settings).Modulate(payload);
        float[] standard = new Ms110dModulator(settings) { StandardProbePattern = true }.Modulate(payload);
        standard.Should().Equal(current);
    }

    [Theory]
    [InlineData(5, Ms110dInterleaverKind.UltraShort)]
    [InlineData(5, Ms110dInterleaverKind.Short)]
    public void Standard_Pattern_Shifts_Only_At_The_Long_Boundary_And_Is_Decoded_There(
        int wn, Ms110dInterleaverKind kind)
    {
        // Long enough to reach frame L - 2 = 62, the only frame after which the standard
        // pattern shifts a probe at WN5: 64 UltraShort blocks, 16 Short ones.
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        int blocks = 64 / il.Frames;
        byte[] payload = RandomBits((blocks * il.InputBits) - 32 - 8, 7200 + (int)kind);
        float[] audio = Transmit(wn, kind, standard: true, payload, awgn: true, seed: 17);

        (Ms110dBurst? burst, _) = Receive(audio);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    public void Last_Probe_Being_A_Boundary_Probe_With_The_Eot_Extension_Loops_Back(int wn, bool eot)
    {
        // One frame per block, 63 blocks: under the standard rule the burst's last probe
        // (after frame 62 = L - 2 at WN5-13) is a shifted one, so the 32-symbol EOT
        // extension continues the shifted sequence. (The current rule shifts every probe.)
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.UltraShort);
        byte[] payload = RandomBits((63 * il.InputBits) - 32, 7300 + wn);
        foreach (bool standard in new[] { false, true })
        {
            float[] audio = Transmit(wn, Ms110dInterleaverKind.UltraShort, standard, payload, awgn: true, seed: 3, eot);

            (Ms110dBurst? burst, _) = Receive(audio);

            burst.Should().NotBeNull();
            burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
            burst.PayloadBits.Should().Equal(payload);
        }
    }

    [Fact]
    public void Rules_Agree_For_The_Long_Interleaver_And_Standard_Is_A_Subset_Otherwise()
    {
        foreach (int wn in Waveforms)
        {
            int longFrames = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long).Frames;
            foreach (Ms110dInterleaverKind kind in Enum.GetValues<Ms110dInterleaverKind>())
            {
                int frames = Ms110dInterleaverParams.Get3k(wn, kind).Frames;
                for (long g = -1; g < 3L * longFrames; g++)
                {
                    bool current = ProbePattern.CurrentRule(g, frames);
                    bool standard = ProbePattern.StandardRule(g, longFrames);
                    if (kind == Ms110dInterleaverKind.Long)
                    {
                        current.Should().Be(standard);
                    }

                    if (standard)
                    {
                        current.Should().BeTrue();
                    }
                }
            }
        }
    }
}
