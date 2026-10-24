using M0LTE.Ms110d;

namespace M0LTE.Ms110d.RefGen;

/// <summary>The default case list written by <c>standard</c>.</summary>
public static class StandardSet
{
    private static readonly int[] Waveforms = [0, 1, 2, 3, 4, 5, 6, 7, 8, 13];

    /// <summary>Every waveform x {short, long} x {K7, K9} with a 100 bit payload (seed 1);
    /// for the short interleaver also seeds 2 and 3 with a payload of two and a half
    /// interleaver blocks (several blocks, so scrambler and probe resets are exercised),
    /// plus three cases with non-default preamble settings.</summary>
    public static IReadOnlyList<RefCase> Cases()
    {
        var cases = new List<RefCase>();
        foreach (int wn in Waveforms)
        {
            foreach (var il in new[] { Ms110dInterleaverKind.Short, Ms110dInterleaverKind.Long })
            {
                foreach (int k in new[] { 7, 9 })
                {
                    cases.Add(new RefCase(wn, il, k, 1, 100));
                    if (il == Ms110dInterleaverKind.Short)
                    {
                        int block = Ms110dInterleaverParams.Get3k(wn, il).InputBits;
                        int bits = (block * 5 / 2) - 32; // with the 32 bit EOM: exactly 2.5 blocks used
                        cases.Add(new RefCase(wn, il, k, 2, bits));
                        cases.Add(new RefCase(wn, il, k, 3, bits));
                    }
                }
            }
        }

        cases.Add(new RefCase(6, Ms110dInterleaverKind.Short, 7, 4, 100, PreambleSuperframes: 1, TlcBlocks: 2));
        cases.Add(new RefCase(0, Ms110dInterleaverKind.Short, 7, 4, 100, PreambleSuperframes: 5, TlcBlocks: 1));
        cases.Add(new RefCase(8, Ms110dInterleaverKind.Short, 9, 4, 100, PreambleSuperframes: 2, TlcBlocks: 3));
        return cases;
    }
}
