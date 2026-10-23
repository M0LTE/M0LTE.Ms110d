using M0LTE.Ms110d;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Rung 1 (design §5.1): hermetic TX→RX loopback at native 9600 Hz. Bit-exact payload, EOM
/// detection, autobaud from cold start, adversarial CFO. Statistical mask runs live in
/// <see cref="Ms110dMaskTests"/> (environment-gated).
/// </summary>
public class Ms110dLoopbackTests
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

    private static (Ms110dBurst? Burst, Ms110dDemodulator Demod) RunLoopback(
        float[] audio, Ms110dDemodOptions? options = null, int silence = 1500)
    {
        var demod = new Ms110dDemodulator(options);
        Ms110dBurst? burst = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.Process(new float[silence]);
        demod.Process(audio);
        demod.Process(new float[6000]);
        return (burst, demod);
    }

    private static void AssertExact(Ms110dBurst? burst, byte[] payload)
    {
        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    public void Every_Waveform_Loops_Back_Bit_Exact(int wn)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn });
        byte[] payload = RandomBits(400, 1000 + wn);
        (Ms110dBurst? burst, Ms110dDemodulator demod) = RunLoopback(tx.Modulate(payload));

        AssertExact(burst, payload);
        demod.State.Should().Be(Ms110dRxState.Searching, "the receiver returns to acquisition after EOM");
    }

    [Theory]
    [InlineData(6, Ms110dInterleaverKind.UltraShort, 7)]
    [InlineData(6, Ms110dInterleaverKind.Medium, 7)]
    [InlineData(6, Ms110dInterleaverKind.Long, 7)]
    [InlineData(2, Ms110dInterleaverKind.UltraShort, 9)]
    [InlineData(5, Ms110dInterleaverKind.Short, 9)]
    [InlineData(0, Ms110dInterleaverKind.Medium, 9)]
    [InlineData(1, Ms110dInterleaverKind.Long, 7)]
    [InlineData(7, Ms110dInterleaverKind.UltraShort, 7)]
    [InlineData(7, Ms110dInterleaverKind.Long, 9)]
    [InlineData(8, Ms110dInterleaverKind.Medium, 7)]
    [InlineData(8, Ms110dInterleaverKind.Short, 9)]
    // Issue #67 matrix fill-in (restated §5.1): with the defaults covered by
    // Every_Waveform_Loops_Back_Bit_Exact and the rows above, the rows below take every
    // distinct (interleaver size, increment) cell of Tables D-XXXVII/D-LI to a decode,
    // WN3/WN4/WN13 at every non-default interleaver, and every WN but 1 at K=9 with its
    // default interleaver. Eight combos are left out purely for runtime, each adding only
    // a code-rate or constraint-length pairing to a covered cell: (1,US,7)/(2,US,7)
    // [≡ (2,US,9)], (1,M,7)/(2,M,7) [≡ (3,M,7)/(4,M,7)], (2,L,7) [≡ (1,L,7)], (7,L,7)
    // [≡ (7,L,9)], (8,US,7) [≡ (5,S,7)], and (1,S,9) [K=9 at this geometry ≡ (2,US,9);
    // the rate-1/8 repeat stage is K-independent and covered by (1,S,7)/(1,L,7)]. All 31
    // missing combos were verified decoding bit-exact during calibration on 2026-07-23
    // against the Phase A closeout equalizer (ff1d832).
    [InlineData(0, Ms110dInterleaverKind.Medium, 7)]
    [InlineData(0, Ms110dInterleaverKind.Long, 7)]
    [InlineData(3, Ms110dInterleaverKind.UltraShort, 7)]
    [InlineData(3, Ms110dInterleaverKind.Medium, 7)]
    [InlineData(3, Ms110dInterleaverKind.Long, 7)]
    [InlineData(4, Ms110dInterleaverKind.UltraShort, 7)]
    [InlineData(4, Ms110dInterleaverKind.Medium, 7)]
    [InlineData(4, Ms110dInterleaverKind.Long, 7)]
    [InlineData(5, Ms110dInterleaverKind.UltraShort, 7)]
    [InlineData(5, Ms110dInterleaverKind.Medium, 7)]
    [InlineData(5, Ms110dInterleaverKind.Long, 7)]
    [InlineData(7, Ms110dInterleaverKind.Medium, 7)]
    [InlineData(8, Ms110dInterleaverKind.Long, 7)]
    [InlineData(13, Ms110dInterleaverKind.UltraShort, 7)]
    [InlineData(13, Ms110dInterleaverKind.Medium, 7)]
    [InlineData(13, Ms110dInterleaverKind.Long, 7)]
    // K=9 at the default (Short) interleaver - WN13's 9/16 puncture with K=9 especially.
    // (5,S,9) and (8,S,9) already sit in the hand-picked rows above; WN2's K=9 default
    // slot is carried by (2,US,9) plus (2,S,9) here.
    [InlineData(0, Ms110dInterleaverKind.Short, 9)]
    [InlineData(2, Ms110dInterleaverKind.Short, 9)]
    [InlineData(3, Ms110dInterleaverKind.Short, 9)]
    [InlineData(4, Ms110dInterleaverKind.Short, 9)]
    [InlineData(6, Ms110dInterleaverKind.Short, 9)]
    [InlineData(7, Ms110dInterleaverKind.Short, 9)]
    [InlineData(13, Ms110dInterleaverKind.Short, 9)]
    public void Interleaver_And_Constraint_Length_Variants_Loop_Back(
        int wn, Ms110dInterleaverKind interleaver, int k)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = interleaver,
            ConstraintLength = k,
        });
        int bits = wn == 1 && interleaver == Ms110dInterleaverKind.Long ? 1500 : 400;
        byte[] payload = RandomBits(bits, 2000 + wn + ((int)interleaver * 17) + k);
        (Ms110dBurst? burst, _) = RunLoopback(tx.Modulate(payload));

        AssertExact(burst, payload);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    [InlineData(20)]
    public void Preamble_Superframe_Counts_All_Acquire(int m)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6, PreambleSuperframes = m });
        byte[] payload = RandomBits(400, 3000 + m);
        (Ms110dBurst? burst, _) = RunLoopback(tx.Modulate(payload));

        AssertExact(burst, payload);
    }

    [Theory]
    [InlineData(0.30)]  // into superframe 0
    [InlineData(1.60)]  // into superframe 1
    [InlineData(2.75)]  // into superframe 2
    public void Cold_Start_Mid_Preamble_Still_Acquires(double superframesMissed)
    {
        // Late entry into the repeated preamble: chop whole+fractional super-frames off the
        // front - the receiver must lock on a later repeat and count down correctly.
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6, PreambleSuperframes = 5 });
        byte[] payload = RandomBits(400, 40);
        float[] audio = tx.Modulate(payload);
        int skip = (int)(superframesMissed * 576 * 4);
        (Ms110dBurst? burst, _) = RunLoopback(audio.Skip(skip).ToArray());

        AssertExact(burst, payload);
    }

    [Theory]
    [InlineData(-60)]
    [InlineData(25)]
    [InlineData(60)]
    public void Carrier_Frequency_Offset_Within_The_Grid_Is_Acquired(double cfoHz)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6, PreambleSuperframes = 4 });
        byte[] payload = RandomBits(400, 50 + (int)cfoHz);
        var channel = new WattersonChannel(9600, seed: 9);
        float[] audio = channel.Apply(
            tx.Modulate(payload), snrDb: 25, leadInSamples: 1500, leadOutSamples: 4000,
            frequencyOffsetHz: cfoHz);

        var demod = new Ms110dDemodulator();
        Ms110dBurst? burst = null;
        Ms110dLockInfo? seen = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.BlockDecoded += _ => seen ??= demod.Lock;
        demod.Process(audio);

        AssertExact(burst, payload);
        seen.Should().NotBeNull();
        seen!.CfoHz.Should().BeApproximately(cfoHz, 3.0, "the refined CFO estimate tracks the offset");
    }

    [Theory]
    [InlineData(7, -60)]
    [InlineData(7, 60)]
    [InlineData(8, -60)]
    [InlineData(8, 60)]
    public void Carrier_Frequency_Offset_Phase_B_Waveforms(int wn, double cfoHz)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn, PreambleSuperframes = 4 });
        byte[] payload = RandomBits(400, 100 + wn + (int)cfoHz);
        var channel = new WattersonChannel(9600, seed: 11);
        float[] audio = channel.Apply(
            tx.Modulate(payload), snrDb: 25, leadInSamples: 1500, leadOutSamples: 4000,
            frequencyOffsetHz: cfoHz);

        (Ms110dBurst? burst, _) = RunLoopback(audio, silence: 0);
        AssertExact(burst, payload);
    }

    [Theory]
    [InlineData(2, -75)]
    [InlineData(2, 75)]
    [InlineData(6, -75)]
    [InlineData(6, 75)]
    [InlineData(7, -75)]
    [InlineData(7, 75)]
    [InlineData(8, -75)]
    [InlineData(8, 75)]
    public void Carrier_Frequency_Offset_At_The_Grid_Edge_Is_Acquired(int wn, double cfoHz)
    {
        // ±75 Hz is the D.6.4 Doppler figure and the outermost acquisition search bin;
        // the shipped suite previously stopped at ±60 Hz (restated §5.1, issue #67).
        // One WN per modulation family: WN2 BPSK, WN6 QPSK, WN7 8PSK, WN8 16QAM.
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn, PreambleSuperframes = 4 });
        byte[] payload = RandomBits(400, 150 + wn + (int)cfoHz);
        var channel = new WattersonChannel(9600, seed: 13);
        float[] audio = channel.Apply(
            tx.Modulate(payload), snrDb: 25, leadInSamples: 1500, leadOutSamples: 4000,
            frequencyOffsetHz: cfoHz);

        (Ms110dBurst? burst, _) = RunLoopback(audio, silence: 0);
        AssertExact(burst, payload);
    }

    [Fact]
    public void Moderate_Awgn_Well_Above_Mask_Decodes_Cleanly()
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6, PreambleSuperframes = 3 });
        byte[] payload = RandomBits(800, 60);
        var channel = new WattersonChannel(9600, seed: 10);
        float[] audio = channel.Apply(
            tx.Modulate(payload), snrDb: 15, leadInSamples: 2000, leadOutSamples: 4000);

        (Ms110dBurst? burst, _) = RunLoopback(audio, silence: 0);
        AssertExact(burst, payload);
    }

    [Fact]
    public void Max_Input_Data_Blocks_Limit_Ends_The_Burst()
    {
        // WN6 Short = 1536 info bits per block: a 4000-bit payload spans 3 blocks; a 1-block
        // limit must end the burst after the first (D.5.4.5.3).
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6 });
        byte[] payload = RandomBits(4000, 70);
        (Ms110dBurst? burst, _) = RunLoopback(
            tx.Modulate(payload), new Ms110dDemodOptions { MaxInputDataBlocks = 1 });

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.MaxInputDataBlocks);
        burst.Blocks.Should().Be(1);
        burst.PayloadBits.Should().Equal(payload.Take(1536));
    }

    [Fact]
    public void Terminate_Command_Ends_The_Burst()
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6 });
        byte[] payload = RandomBits(4000, 80);
        float[] audio = tx.Modulate(payload);

        var demod = new Ms110dDemodulator();
        Ms110dBurst? burst = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.BlockDecoded += _ => demod.Terminate();
        demod.Process(audio);
        demod.Process(new float[4000]);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Terminated);
    }

    [Fact]
    public void Truncated_Transmission_Does_Not_Produce_A_False_Payload()
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6 });
        byte[] payload = RandomBits(400, 90);
        float[] audio = tx.Modulate(payload);
        float[] truncated = audio.Take(audio.Length - 2500).ToArray();

        (Ms110dBurst? burst, _) = RunLoopback(truncated, silence: 0);
        (burst?.Reason == Ms110dBurstEndReason.Eom).Should().BeFalse(
            "the EOM block was cut off - a clean EOM burst would be a false decode");
    }

    [Fact]
    public void Eot_Disabled_Still_Decodes()
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 5, AppendEot = false });
        byte[] payload = RandomBits(600, 91);
        (Ms110dBurst? burst, _) = RunLoopback(tx.Modulate(payload));
        AssertExact(burst, payload);
    }

    [Fact]
    public void Back_To_Back_Bursts_Both_Decode()
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6 });
        byte[] first = RandomBits(300, 92);
        byte[] second = RandomBits(500, 93);

        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        demod.BurstCompleted += bursts.Add;
        demod.Process(new float[1000]);
        demod.Process(tx.Modulate(first));
        demod.Process(new float[3000]);
        demod.Process(tx.Modulate(second));
        demod.Process(new float[6000]);

        bursts.Should().HaveCount(2);
        bursts[0].PayloadBits.Should().Equal(first);
        bursts[1].PayloadBits.Should().Equal(second);
    }
}
