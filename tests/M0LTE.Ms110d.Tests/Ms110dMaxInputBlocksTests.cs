using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>The run-time maximum input data blocks (docs/design-5066-support.md section 4).</summary>
public class Ms110dMaxInputBlocksTests
{
    private static readonly Ms110dTxSettings Settings =
        new() { WaveformNumber = 4, Interleaver = Ms110dInterleaverKind.Short };

    private static float[] Burst(int blocks, int seed)
    {
        Ms110dBurstPlan plan = Ms110dBurstPlan.ForBlocks(Settings, blocks);
        var random = new Random(seed);
        var bits = new byte[plan.PayloadCapacityBits];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }

        var tx = new Ms110dModulator(Settings).Modulate(bits);
        var audio = new float[3000 + tx.Length + 9600];
        tx.CopyTo(audio, 3000);
        return audio;
    }

    private static void Feed(Ms110dDemodulator demod, float[] audio)
    {
        for (int i = 0; i < audio.Length; i += 192)
        {
            demod.Process(audio.AsSpan(i, Math.Min(192, audio.Length - i)));
        }
    }

    [Fact]
    public void Starts_at_the_options_value_and_zero_is_unlimited()
    {
        new Ms110dDemodulator().MaxInputDataBlocks.Should().Be(0);
        new Ms110dDemodulator(new Ms110dDemodOptions { MaxInputDataBlocks = 3 }).MaxInputDataBlocks.Should().Be(3);

        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        demod.BurstCompleted += bursts.Add;
        demod.MaxInputDataBlocks = 1;
        demod.MaxInputDataBlocks = 0;
        Feed(demod, Burst(4, 1));
        bursts.Should().ContainSingle().Which.Blocks.Should().Be(4);
        bursts[0].Reason.Should().Be(Ms110dBurstEndReason.Eom);
    }

    [Fact]
    public void Negative_values_throw()
    {
        var demod = new Ms110dDemodulator();
        FluentActions.Invoking(() => demod.MaxInputDataBlocks = -1).Should().Throw<ArgumentOutOfRangeException>();
        demod.MaxInputDataBlocks.Should().Be(0);
    }

    [Fact]
    public void Set_before_a_longer_burst_ends_it_after_that_many_blocks()
    {
        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        demod.BurstCompleted += bursts.Add;
        demod.MaxInputDataBlocks = 2;
        Feed(demod, Burst(4, 2));
        bursts.Should().ContainSingle();
        bursts[0].Reason.Should().Be(Ms110dBurstEndReason.MaxInputDataBlocks);
        bursts[0].Blocks.Should().Be(2);
    }

    [Fact]
    public void The_value_applies_to_later_bursts_and_survives_reset_and_burst_end()
    {
        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        demod.BurstCompleted += bursts.Add;
        demod.MaxInputDataBlocks = 2;
        Feed(demod, Burst(4, 3));
        demod.Reset();
        demod.MaxInputDataBlocks.Should().Be(2);
        Feed(demod, Burst(4, 4));

        bursts.Should().HaveCount(2);
        bursts.Should().OnlyContain(b => b.Reason == Ms110dBurstEndReason.MaxInputDataBlocks && b.Blocks == 2);
    }

    [Fact]
    public void Lowering_the_limit_mid_burst_ends_it_in_the_next_Process_call_not_in_the_setter()
    {
        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        var decoded = new List<Ms110dRxBlock>();
        demod.BurstCompleted += bursts.Add;
        demod.BlockDecoded += decoded.Add;
        float[] audio = Burst(6, 5);

        int at = 0;
        while (decoded.Count < 3)
        {
            demod.Process(audio.AsSpan(at, 192));
            at += 192;
        }

        bursts.Should().BeEmpty();
        demod.MaxInputDataBlocks = 2; // below the three already decoded
        bursts.Should().BeEmpty("the setter only stores the value");
        demod.State.Should().Be(Ms110dRxState.Tracking);

        long before = demod.SamplesProcessed;
        demod.Process(Array.Empty<float>());
        bursts.Should().ContainSingle();
        bursts[0].Reason.Should().Be(Ms110dBurstEndReason.MaxInputDataBlocks);
        bursts[0].Blocks.Should().Be(3);
        bursts[0].EndSample.Should().Be(before);
        demod.State.Should().Be(Ms110dRxState.Searching);
    }

    [Fact]
    public void Setting_the_limit_to_the_number_already_decoded_also_ends_the_burst()
    {
        var demod = new Ms110dDemodulator();
        var bursts = new List<Ms110dBurst>();
        var decoded = new List<Ms110dRxBlock>();
        demod.BurstCompleted += bursts.Add;
        demod.BlockDecoded += decoded.Add;
        float[] audio = Burst(6, 6);

        int at = 0;
        while (decoded.Count < 2)
        {
            demod.Process(audio.AsSpan(at, 192));
            at += 192;
        }

        demod.MaxInputDataBlocks = 2;
        demod.Process(audio.AsSpan(at, 192));
        bursts.Should().ContainSingle().Which.Reason.Should().Be(Ms110dBurstEndReason.MaxInputDataBlocks);
        bursts[0].Blocks.Should().Be(2);
    }

    [Fact]
    public void A_negative_option_still_means_unlimited_and_reads_back_as_zero()
    {
        var demod = new Ms110dDemodulator(new Ms110dDemodOptions { MaxInputDataBlocks = -1 });
        demod.MaxInputDataBlocks.Should().Be(0);
        var bursts = new List<Ms110dBurst>();
        demod.BurstCompleted += bursts.Add;
        Feed(demod, Burst(3, 7));
        bursts.Should().ContainSingle().Which.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        bursts[0].Blocks.Should().Be(3);
    }
}
