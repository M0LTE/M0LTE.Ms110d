using M0LTE.Ms110d;
using M0LTE.Ms110d.Fec;

namespace M0LTE.Ms110d.Tests;

public class Ms110dInterleaverTests
{
    [Fact]
    public void Wire_Side_Sequence_Matches_The_D5332_Worked_Example()
    {
        // D.5.3.3.2 (docs/dev/ms110d/tables/d5332-interleaver-load-example.md): WID 1 / 3 kHz /
        // UltraShort - 192 bits, increment 25 - loads B(0…8) at 0, 25, 50, 75, 100, 125,
        // 150, 175 and 8. Loopback-blind checklist L1: this asserts the WIRE side (after the
        // linear fetch, wire[(25·n) mod 192] == B(n)), not just a round-trip.
        var interleaver = new Ms110dInterleaver(192, 25);
        var random = new Random(2025);
        var punctured = new byte[192];
        for (int i = 0; i < punctured.Length; i++)
        {
            punctured[i] = (byte)random.Next(2);
        }

        var wire = new byte[192];
        interleaver.Interleave(punctured, wire);

        int[] printedLocations = [0, 25, 50, 75, 100, 125, 150, 175, 8];
        for (int n = 0; n < printedLocations.Length; n++)
        {
            wire[printedLocations[n]].Should().Be(punctured[n], $"B({n}) loads at {printedLocations[n]}");
        }

        for (int n = 0; n < 192; n++)
        {
            wire[n * 25 % 192].Should().Be(punctured[n]);
        }
    }

    [Fact]
    public void Deinterleave_Inverts_Interleave()
    {
        // Secondary property only (L1): the wire-side test above carries the direction.
        Ms110dInterleaverParams p = Ms110dInterleaverParams.Get3k(6, Ms110dInterleaverKind.Short);
        var interleaver = new Ms110dInterleaver(p.SizeBits, p.Increment);
        var random = new Random(77);
        var punctured = new byte[p.SizeBits];
        for (int i = 0; i < punctured.Length; i++)
        {
            punctured[i] = (byte)random.Next(2);
        }

        var wire = new byte[p.SizeBits];
        interleaver.Interleave(punctured, wire);

        var rxLlrs = new float[p.SizeBits];
        for (int i = 0; i < wire.Length; i++)
        {
            rxLlrs[i] = wire[i] == 0 ? 1f : -1f;
        }

        var llrs = new float[p.SizeBits];
        interleaver.Deinterleave(rxLlrs, llrs);
        for (int n = 0; n < p.SizeBits; n++)
        {
            (llrs[n] > 0 ? 0 : 1).Should().Be(punctured[n]);
        }
    }

    [Fact]
    public void Every_Implemented_Increment_Is_A_Permutation()
    {
        // WN 7/8 (Phase B, PR #60) included since issue #67 - the Table D-LI rows for
        // 8PSK/16QAM must be permutations exactly like the Phase A rows.
        foreach (int wn in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 13 })
        {
            foreach (Ms110dInterleaverKind kind in Enum.GetValues<Ms110dInterleaverKind>())
            {
                if (wn == 0 && kind == Ms110dInterleaverKind.UltraShort)
                {
                    continue; // Table D-XXXVII dash
                }

                Ms110dInterleaverParams p = Ms110dInterleaverParams.Get3k(wn, kind);
                var interleaver = new Ms110dInterleaver(p.SizeBits, p.Increment);
                var punctured = new byte[p.SizeBits];
                punctured.AsSpan().Fill(1);
                var wire = new byte[p.SizeBits];
                interleaver.Interleave(punctured, wire);
                wire.Should().AllBeEquivalentTo(1, $"WID {wn} {kind}: (n·{p.Increment}) mod {p.SizeBits} must be a permutation");
            }
        }
    }
}
