using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

public class Ms110dModeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(13)]
    public void Bps_Arithmetic_Reproduces_Table_DII(int wn)
    {
        // 2400 · U/(U+K) · bits/symbol · code rate must equal the Table D-II user rate -
        // the cross-check that ties D-XI/D-XII, D-XLIX and D-II together (design §2.4).
        Ms110dMode mode = Ms110dMode.Mode3k(wn);
        (int p, int q) = ParseRate(mode.CodeRate);
        double bps = 2400.0 * mode.U / (mode.U + mode.K) * mode.BitsPerSymbol * p / q;
        bps.Should().BeApproximately(mode.Bps, 1e-9);
    }

    [Fact]
    public void Wn0_Walsh_Arithmetic_Gives_75_Bps()
    {
        Ms110dMode mode = Ms110dMode.Mode3k(0);
        // 2400 chips/s ÷ 32 chips/symbol × 2 coded bits/symbol × rate 1/2 = 75 bps.
        (2400.0 / 32 * mode.BitsPerSymbol / 2).Should().Be(mode.Bps);
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
    public void Interleaver_Geometry_Is_Self_Consistent(int wn)
    {
        Ms110dMode mode = Ms110dMode.Mode3k(wn);
        (int p, int q) = ParseRate(mode.CodeRate);
        foreach (Ms110dInterleaverKind kind in Enum.GetValues<Ms110dInterleaverKind>())
        {
            if (wn == 0 && kind == Ms110dInterleaverKind.UltraShort)
            {
                Assert.Throws<ArgumentException>(() => Ms110dInterleaverParams.Get3k(wn, kind));
                continue;
            }

            Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, kind);

            // Coded bits fill the interleaver exactly (D.5.3.2 "shall still fit exactly").
            (il.InputBits * q).Should().Be(il.SizeBits * p, $"WID {wn} {kind}");

            // Frames × symbols-per-frame × bits-per-symbol == interleaver size.
            int symbolsPerFrame = wn == 0 ? 1 : mode.U;
            (il.Frames * symbolsPerFrame * mode.BitsPerSymbol).Should().Be(il.SizeBits, $"WID {wn} {kind}");
        }
    }

    private static (int P, int Q) ParseRate(string rate)
    {
        string[] parts = rate.Split('/');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }
}
