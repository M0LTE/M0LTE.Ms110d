using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Which mini-probes are cyclically shifted follows D.5.2.2 at both ends: the probe after
/// frame g (counted across the burst) is shifted only when g % L == L - 2, L the waveform's
/// Long interleaver frame count, whatever interleaver is selected. Up to 0.2.x the library
/// shifted at the selected interleaver's boundary instead; that pattern is gone.
/// </summary>
public class Ms110dProbePatternTests
{
    private static readonly int[] Waveforms = [1, 2, 3, 4, 5, 6, 7, 8, 13];

    private static readonly Ms110dInterleaverKind[] Interleavers =
    [
        Ms110dInterleaverKind.UltraShort, Ms110dInterleaverKind.Short,
        Ms110dInterleaverKind.Medium, Ms110dInterleaverKind.Long,
    ];

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

    public static TheoryData<int, Ms110dInterleaverKind, bool> Matrix()
    {
        var data = new TheoryData<int, Ms110dInterleaverKind, bool>();
        foreach (int wn in Waveforms)
        {
            foreach (Ms110dInterleaverKind kind in Interleavers)
            {
                foreach (bool awgn in new[] { false, true })
                {
                    data.Add(wn, kind, awgn);
                }
            }
        }

        return data;
    }

    public static TheoryData<int, Ms110dInterleaverKind> Modes()
    {
        var data = new TheoryData<int, Ms110dInterleaverKind>();
        foreach (int wn in Waveforms)
        {
            foreach (Ms110dInterleaverKind kind in Interleavers)
            {
                data.Add(wn, kind);
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
        int wn, Ms110dInterleaverKind kind, byte[] payload, bool awgn, int seed, bool eot = true)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = kind,
            AppendEot = eot,
        });
        float[] audio = tx.Modulate(payload);
        if (!awgn)
        {
            return audio;
        }

        var channel = new WattersonChannel(9600, seed);
        return channel.Apply(audio, snrDb: MaskSnr(wn) + 8, leadInSamples: 1500, leadOutSamples: 4000);
    }

    // Feeds a clean burst (no channel lead-out) up to just before its last block completes,
    // so the receiver is still locked on it (the end of a burst resets the burst state).
    private static Ms110dDemodulator ReceiveAllButLastBlock(int wn, Ms110dInterleaverKind kind, float[] audio)
    {
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        Ms110dMode mode = Ms110dMode.Mode3k(wn);
        int lastBlockSamples = il.Frames * (mode.U + mode.K) * 4;
        var demod = new Ms110dDemodulator();
        demod.Process(new float[1500]);
        demod.Process(audio.AsSpan(0, audio.Length - lastBlockSamples - 400).ToArray());
        return demod;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Every_Mode_And_Interleaver_Loops_Back(int wn, Ms110dInterleaverKind kind, bool awgn)
    {
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        // Two interleaver blocks, so the stream carries more than one block boundary.
        byte[] payload = RandomBits((2 * il.InputBits) - 32 - 8, 7000 + wn + (int)kind);
        float[] audio = Transmit(wn, kind, payload, awgn, seed: 31 + wn);

        (Ms110dBurst? burst, _) = Receive(audio);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Transmitter_Shifts_Only_The_Probe_At_The_Long_Boundary(int wn, Ms110dInterleaverKind kind)
    {
        // Enough blocks to pass frame L - 2 at least once, whatever the interleaver.
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        Ms110dMode mode = Ms110dMode.Mode3k(wn);
        int longFrames = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long).Frames;
        int blocks = Math.Max(2, (longFrames + il.Frames - 1) / il.Frames);
        byte[] payload = RandomBits((blocks * il.InputBits) - 32, 7400 + wn + (int)kind);
        var settings = new Ms110dTxSettings { WaveformNumber = wn, Interleaver = kind };
        Complex32[] symbols = new Ms110dModulator(settings).BuildSymbols(payload);
        int preamble = new PreambleGenerator(settings.TlcBlocks, settings.PreambleSuperframes)
            .Generate(wn, kind, settings.ConstraintLength).Length;

        Complex32[] unshifted = MiniProbe.Get(mode.K, boundary: false);
        Complex32[] shifted = MiniProbe.Get(mode.K, boundary: true);
        symbols.AsSpan(preamble, mode.K).ToArray().Should().Equal(unshifted, "the probe ending the preamble is never shifted");

        int shiftedCount = 0;
        for (long g = 0; g < (long)blocks * il.Frames; g++)
        {
            int start = preamble + mode.K + (int)(g * (mode.U + mode.K)) + mode.U;
            bool expectShifted = g % longFrames == longFrames - 2;
            symbols.AsSpan(start, mode.K).ToArray().Should().Equal(
                expectShifted ? shifted : unshifted, $"the probe after frame {g}");
            shiftedCount += expectShifted ? 1 : 0;
        }

        shiftedCount.Should().BeGreaterThan(0);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Receiver_Expects_The_Standard_Pattern_At_Every_Probe(int wn, Ms110dInterleaverKind kind)
    {
        // The receiver's view of which probe is shifted is the same rule, configured from the
        // waveform the burst announces; the Long interleaver's own frame count is the period.
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        int longFrames = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long).Frames;
        byte[] payload = RandomBits((2 * il.InputBits) - 40, 8000 + wn + (int)kind);
        Ms110dDemodulator demod = ReceiveAllButLastBlock(wn, kind, Transmit(wn, kind, payload, awgn: false, seed: 1));

        demod.Lock.Should().NotBeNull();
        for (long g = -1; g < 3L * longFrames; g++)
        {
            demod.ProbeShiftedAfter(g).Should().Be(g >= 0 && g % longFrames == longFrames - 2, $"the probe after frame {g}");
        }
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(13)]
    public void Preamble_End_Probe_Is_Unshifted_Before_The_First_UltraShort_Block(int wn)
    {
        // The turbo paths ask for the probe preceding frame 0 of block 0 (frame number -1).
        // It is the probe that ends the preamble and is never shifted, even with one frame
        // per block.
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.UltraShort);
        byte[] payload = RandomBits((4 * il.InputBits) - 40, 8500 + wn);
        float[] audio = Transmit(wn, Ms110dInterleaverKind.UltraShort, payload, awgn: false, seed: 1);
        Ms110dDemodulator demod = ReceiveAllButLastBlock(wn, Ms110dInterleaverKind.UltraShort, audio);

        demod.Lock.Should().NotBeNull();
        demod.ProbeShiftedAfter(-1).Should().BeFalse();
    }

    [Theory]
    [InlineData(5, Ms110dInterleaverKind.UltraShort)]
    [InlineData(5, Ms110dInterleaverKind.Short)]
    public void Shifted_Probe_At_The_Long_Boundary_Is_Decoded_There(int wn, Ms110dInterleaverKind kind)
    {
        // Long enough to reach frame L - 2 = 62, the only frame after which a probe is
        // shifted at WN5: 64 UltraShort blocks, 16 Short ones.
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);
        int blocks = 64 / il.Frames;
        byte[] payload = RandomBits((blocks * il.InputBits) - 32 - 8, 7200 + (int)kind);
        float[] audio = Transmit(wn, kind, payload, awgn: true, seed: 17);

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
        // One frame per block, 63 blocks: the burst's last probe (after frame 62 = L - 2 at
        // WN5 to WN13) is a shifted one, so the 32-symbol EOT extension continues the
        // shifted sequence.
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.UltraShort);
        byte[] payload = RandomBits((63 * il.InputBits) - 32, 7300 + wn);
        float[] audio = Transmit(wn, Ms110dInterleaverKind.UltraShort, payload, awgn: true, seed: 3, eot);

        (Ms110dBurst? burst, _) = Receive(audio);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }

    [Fact]
    public void Rule_Shifts_Once_Per_Long_Block_And_Never_The_Preamble_End_Probe()
    {
        foreach (int wn in Waveforms)
        {
            int longFrames = ProbePattern.LongFrames(wn);
            longFrames.Should().Be(Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long).Frames);
            ProbePattern.ShiftedAfter(-1, longFrames).Should().BeFalse();
            for (long g = 0; g < 3L * longFrames; g++)
            {
                // For the Long interleaver this is the probe after the second-to-last frame
                // of each block; every other interleaver uses the same frame numbers.
                ProbePattern.ShiftedAfter(g, longFrames).Should().Be((g + 2) % longFrames == 0, $"WN{wn} frame {g}");
            }
        }
    }
}
