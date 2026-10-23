using M0LTE.Dsp;
using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// The transmit roll-off option (<see cref="Ms110dTxSettings.RollOff"/>) and the receiver's
/// matched-filter roll-off (<see cref="Ms110dDemodOptions.RollOff"/>): the default is the
/// standard's 0.35 and unchanged, a narrower roll-off really is narrower, and the receiver
/// decodes it whether or not its own filter is matched. The filter-study evidence in
/// docs/dev/ms110d/evidence/2026-10-04-filter-width/ measures what the choice is worth.
/// </summary>
public class Ms110dRollOffTests
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

    [Fact]
    public void Default_Roll_Off_Is_The_Standard_And_Leaves_The_Waveform_Unchanged()
    {
        new Ms110dTxSettings().RollOff.Should().Be(0.35);
        new Ms110dDemodOptions().RollOff.Should().Be(0.35);

        byte[] payload = RandomBits(400, 77);
        float[] byDefault = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 4 }).Modulate(payload);
        float[] explicitly = new Ms110dModulator(
            new Ms110dTxSettings { WaveformNumber = 4, RollOff = Ms110dModulator.RollOff }).Modulate(payload);
        explicitly.Should().Equal(byDefault);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.10)]
    [InlineData(0.05)]
    public void A_Narrower_Roll_Off_Stays_Inside_Its_Own_Band(double rollOff)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = 4, RollOff = rollOff, PreambleSuperframes = 2,
        });
        float[] audio = tx.Modulate(RandomBits(4000, 313));
        int skip = (2 * 576 * 4) + 512;
        float[] steady = audio.AsSpan(skip, audio.Length - skip - 1024).ToArray();

        (double lo, double hi, double width, _) = OccupiedBandwidth.Measure(steady, 9600);
        double edge = 2400 * (1 + rollOff);
        width.Should().BeLessThan(edge, "99 % of the power sits inside the nominal edges");
        width.Should().BeLessThan(2796, "narrower than the 0.35 waveform's measured 99 % width");
        lo.Should().BeGreaterThan(1800 - (edge / 2));
        hi.Should().BeLessThan(1800 + (edge / 2));
    }

    [Theory]
    [InlineData(4, 0.10, 0.35)]
    [InlineData(4, 0.10, 0.10)]
    [InlineData(3, 0.05, 0.35)]
    [InlineData(3, 0.05, 0.05)]
    public void A_Narrow_Transmitter_Loops_Back_Bit_Exact_Matched_Or_Not(int wn, double txRollOff, double rxRollOff)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn, RollOff = txRollOff });
        byte[] payload = RandomBits(400, 1000 + wn);
        var demod = new Ms110dDemodulator(new Ms110dDemodOptions { RollOff = rxRollOff });
        Ms110dBurst? burst = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.Process(new float[1500]);
        demod.Process(tx.Modulate(payload));
        demod.Process(new float[6000]);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.04)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void An_Out_Of_Range_Roll_Off_Is_Refused_At_Both_Ends(double rollOff)
    {
        Action transmit = () => _ = new Ms110dModulator(new Ms110dTxSettings { RollOff = rollOff });
        Action receive = () => _ = new Ms110dDemodulator(new Ms110dDemodOptions { RollOff = rollOff });
        transmit.Should().Throw<ArgumentException>();
        receive.Should().Throw<ArgumentException>();
    }
}
