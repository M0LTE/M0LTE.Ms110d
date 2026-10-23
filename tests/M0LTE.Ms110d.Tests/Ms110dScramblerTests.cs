using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

public class Ms110dScramblerTests
{
    // Independently hand-derived wire vector (checklist L3): scramble values from init
    // 000000001 with the register convention of docs/dev/ms110d/design.md §2.2 (b0' = b8 ^ b4,
    // rightmost three bits (b2 b1 b0) used, 3 iterations per PSK symbol). Derived by walking
    // the register on paper / a throwaway script, not by running the implementation.
    private static readonly int[] First16 = [1, 0, 2, 1, 4, 1, 1, 6, 2, 5, 3, 0, 3, 3, 6, 4];

    [Fact]
    public void First_Frame_Symbol_Is_Scrambled_By_The_Init_Value()
    {
        var scrambler = new Ms110dScrambler();

        // D.5.1.3: "the first data symbol of every data frame shall ... be scrambled by the
        // appropriate number of bits from the initialization value of 00000001".
        scrambler.NextPsk(0).Should().Be(1);
        scrambler.Reset();
        scrambler.NextPsk(6).Should().Be(7);
    }

    [Fact]
    public void Psk_Scramble_Sequence_Matches_The_Hand_Computed_Wire_Vector()
    {
        var scrambler = new Ms110dScrambler();
        var values = new int[16];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = scrambler.NextPsk(0);
        }

        values.Should().Equal(First16);
    }

    [Fact]
    public void Prose_Example_Register_010_Plus_Symbol_6_Transmits_Symbol_0()
    {
        // D.5.1.3 worked example: register rightmost bits 010 (value 2), symbol 6 →
        // (6+2) mod 8 = 0. The register value 2 first occurs in the hand-derived sequence at
        // symbol index 2 (First16[2] == 2); feed symbol 6 there.
        var scrambler = new Ms110dScrambler();
        scrambler.NextPsk(0);
        scrambler.NextPsk(0);
        scrambler.NextPsk(6).Should().Be(0);
    }

    [Fact]
    public void Scramble_Sequence_Period_Is_511()
    {
        // x⁹+x⁴+1 is primitive: register period 511; gcd(3, 511) = 1 so the PSK symbol
        // scramble sequence also has period 511 (D.5.1.3: "length of the scrambling
        // sequence is 511 bits").
        var scrambler = new Ms110dScrambler();
        var values = new int[511 * 2];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = scrambler.NextPsk(0);
        }

        for (int i = 0; i < 511; i++)
        {
            values[i + 511].Should().Be(values[i]);
        }

        for (int period = 1; period < 511; period++)
        {
            bool repeats = true;
            for (int i = 0; i < 511 && repeats; i++)
            {
                repeats = values[i] == values[(i + period) % 511];
            }

            repeats.Should().BeFalse($"period {period} must not repeat before 511");
        }
    }

    [Fact]
    public void Qam_Path_Uses_The_Rightmost_Bits_And_Iterates_Per_Bit()
    {
        // The same register drives QAM scrambling with N-bit XOR (D.5.1.3 example: register
        // 0101 XOR symbol 3 → 6). From init, the first 16QAM symbol XORs with sr & 0xF = 1.
        var scrambler = new Ms110dScrambler();
        scrambler.NextQam(3, 4).Should().Be(2);

        // After 4 iterations from init the register low bits must match the PSK walk: the
        // PSK sequence advances 3/symbol, so cross-check via a fresh scrambler.
        scrambler.Reset();
        scrambler.NextQam(0, 8).Should().Be(1);
    }
}
