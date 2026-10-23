using M0LTE.Fec;
using M0LTE.Ms110d.Fec;

namespace M0LTE.Ms110d.Tests;

public class Ms110dPunctureTests
{
    public static TheoryData<string, int, int> AllRates()
    {
        // rate string, numerator p, denominator q - every Table D-L row.
        return new TheoryData<string, int, int>
        {
            { "9/10", 9, 10 }, { "8/9", 8, 9 }, { "5/6", 5, 6 }, { "4/5", 4, 5 },
            { "3/4", 3, 4 }, { "2/3", 2, 3 }, { "4/7", 4, 7 }, { "9/16", 9, 16 },
            { "1/2", 1, 2 }, { "2/5", 2, 5 }, { "1/3", 1, 3 }, { "2/7", 2, 7 },
            { "1/4", 1, 4 }, { "1/6", 1, 6 }, { "1/8", 1, 8 }, { "1/12", 1, 12 },
            { "1/16", 1, 16 },
        };
    }

    [Theory]
    [MemberData(nameof(AllRates))]
    public void Every_Mask_Reproduces_Its_Table_D49_Rate_For_Both_Constraint_Lengths(
        string rate, int p, int q)
    {
        foreach (ConvolutionalCode code in new[] { ConvolutionalCode.K7, ConvolutionalCode.K9 })
        {
            PunctureSpec spec = Ms110dPuncture.Get(code, rate);
            int infoBits = p * spec.KeepT1.Length; // integral mask cycles
            spec.OutputLength(infoBits).Should().Be(infoBits * q / p, $"rate {rate}, K={code.K}");
        }
    }

    [Theory]
    [MemberData(nameof(AllRates))]
    public void Depuncture_Restores_The_Mother_Lattice_With_Erasures_Where_Punctured(
        string rate, int p, int q)
    {
        _ = q;
        foreach (ConvolutionalCode code in new[] { ConvolutionalCode.K7, ConvolutionalCode.K9 })
        {
            PunctureSpec spec = Ms110dPuncture.Get(code, rate);
            int infoBits = p * spec.KeepT1.Length * 2;
            var random = new Random(31 + code.K);
            var coded = new byte[2 * infoBits];
            for (int i = 0; i < coded.Length; i++)
            {
                coded[i] = (byte)random.Next(2);
            }

            var punctured = new byte[spec.OutputLength(infoBits)];
            Ms110dPuncture.Apply(spec, coded, punctured).Should().Be(punctured.Length);

            var rxLlrs = new float[punctured.Length];
            for (int i = 0; i < punctured.Length; i++)
            {
                rxLlrs[i] = punctured[i] == 0 ? 1f : -1f;
            }

            var mother = new float[2 * infoBits];
            Ms110dPuncture.Depuncture(spec, rxLlrs, mother);

            for (int i = 0; i < mother.Length; i++)
            {
                if (mother[i] == 0f)
                {
                    continue; // erased position - puncture removed it
                }

                // Repetition sums same-sign copies, so magnitude ≥ 1 and the sign carries.
                (mother[i] > 0 ? 0 : 1).Should().Be(coded[i]);
            }

            // Erasure count matches the mask exactly.
            int expectedErasures = (2 * infoBits) - Distinct(spec, infoBits);
            mother.Count(v => v == 0f).Should().Be(expectedErasures);
        }
    }

    [Fact]
    public void Repetition_Rates_Sum_Repeated_Llr_Copies()
    {
        // Rate 1/8 = the rate-1/2 pair stream repeated 4×: each mother position collects
        // exactly 4 copies.
        PunctureSpec spec = Ms110dPuncture.Get(ConvolutionalCode.K7, "1/8");
        var coded = new byte[] { 0, 1 };
        var punctured = new byte[8];
        Ms110dPuncture.Apply(spec, coded, punctured);
        punctured.Should().Equal(0, 1, 0, 1, 0, 1, 0, 1);

        var rxLlrs = new float[] { 1f, -1f, 1f, -1f, 1f, -1f, 1f, -1f };
        var mother = new float[2];
        Ms110dPuncture.Depuncture(spec, rxLlrs, mother);
        mother.Should().Equal(4f, -4f);
    }

    [Fact]
    public void K7_And_K9_Masks_Differ_Where_Table_DL_Says_They_Do()
    {
        Ms110dPuncture.Get(ConvolutionalCode.K7, "3/4").KeepT1.Should().Equal(1, 1, 0);
        Ms110dPuncture.Get(ConvolutionalCode.K7, "3/4").KeepT2.Should().Equal(1, 0, 1);
        Ms110dPuncture.Get(ConvolutionalCode.K9, "3/4").KeepT1.Should().Equal(1, 1, 1);
        Ms110dPuncture.Get(ConvolutionalCode.K9, "3/4").KeepT2.Should().Equal(1, 0, 0);

        // 9/16 (WN13) is identical for both constraint lengths.
        Ms110dPuncture.Get(ConvolutionalCode.K9, "9/16").Should().BeEquivalentTo(
            Ms110dPuncture.Get(ConvolutionalCode.K7, "9/16"));
    }

    private static int Distinct(PunctureSpec spec, int infoBits)
    {
        // Number of distinct mother-lattice positions that receive at least one copy.
        var seen = new HashSet<int>();
        int pairs = infoBits * spec.RepeatFactor;
        for (int p = 0; p < pairs; p++)
        {
            int n = p / spec.RepeatFactor;
            int col = p % spec.KeepT1.Length;
            if (spec.KeepT1[col] != 0)
            {
                seen.Add(2 * n);
            }

            if (spec.KeepT2[col] != 0)
            {
                seen.Add((2 * n) + 1);
            }
        }

        return seen.Count;
    }
}
