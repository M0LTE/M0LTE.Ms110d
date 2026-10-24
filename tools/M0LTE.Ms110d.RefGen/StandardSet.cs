using M0LTE.Ms110d;

namespace M0LTE.Ms110d.RefGen;

/// <summary>The default case list written by <c>standard</c>.</summary>
public static class StandardSet
{
    private static readonly int[] Waveforms = [0, 1, 2, 3, 4, 5, 6, 7, 8, 13];

    private static readonly Ms110dInterleaverKind[] Interleavers =
    [
        Ms110dInterleaverKind.UltraShort, Ms110dInterleaverKind.Short,
        Ms110dInterleaverKind.Medium, Ms110dInterleaverKind.Long,
    ];

    /// <summary>The standard set:
    /// <list type="bullet">
    /// <item>every valid waveform x interleaver (ultra-short, short, medium, long; WN0 has no
    /// ultra-short) x {K7, K9} (long: one code per waveform, alternating) with seed 1 and a 100 bit payload;</item>
    /// <item>short interleaver also with seeds 2 and 3 and a payload that, with the EOM,
    /// fills exactly two and a half blocks;</item>
    /// <item>two multi-block medium cases;</item>
    /// <item>edge cases: EOM off, EOT off, amplitude 0.25, roll-off 0.10, EOM ending exactly
    /// on a block end, payload exactly one block, empty payload;</item>
    /// <item>three non-default preamble settings.</item>
    /// </list></summary>
    public static IReadOnlyList<RefCase> Cases()
    {
        var cases = new List<RefCase>();
        foreach (int wn in Waveforms)
        {
            foreach (var il in Interleavers)
            {
                if (!Ms110dInterleaverParams.Has3k(wn, il))
                {
                    continue;
                }

                // Long cases are large, so each waveform gets one code (alternating by position);
                // the other interleavers get both.
                int[] codes = il == Ms110dInterleaverKind.Long
                    ? [Array.IndexOf(Waveforms, wn) % 2 == 0 ? 7 : 9]
                    : [7, 9];
                foreach (int k in codes)
                {
                    cases.Add(new RefCase(wn, il, k, 1, 100));
                    if (il == Ms110dInterleaverKind.Short)
                    {
                        int bits = Block(wn, il) * 5 / 2 - 32; // with the 32 bit EOM: exactly 2.5 blocks used
                        cases.Add(new RefCase(wn, il, k, 2, bits));
                        cases.Add(new RefCase(wn, il, k, 3, bits));
                    }
                }
            }
        }

        // Multi-block on the medium interleaver (two and a half blocks used).
        cases.Add(new RefCase(6, Ms110dInterleaverKind.Medium, 7, 5, Block(6, Ms110dInterleaverKind.Medium) * 5 / 2 - 32));
        cases.Add(new RefCase(3, Ms110dInterleaverKind.Medium, 9, 5, Block(3, Ms110dInterleaverKind.Medium) * 5 / 2 - 32));

        // Edge cases, all on the short interleaver.
        var s = Ms110dInterleaverKind.Short;
        cases.Add(new RefCase(6, s, 7, 6, 100, AppendEom: false));
        cases.Add(new RefCase(6, s, 7, 6, 100, AppendEot: false));
        cases.Add(new RefCase(6, s, 7, 6, 100, Amplitude: 0.25f));
        cases.Add(new RefCase(6, s, 7, 6, 100, RollOff: 0.10));
        cases.Add(new RefCase(6, s, 7, 6, Block(6, s) - 32));  // EOM ends exactly on the block end
        cases.Add(new RefCase(1, s, 9, 6, Block(1, s) - 32));
        cases.Add(new RefCase(6, s, 7, 6, Block(6, s)));       // payload fills one block; EOM spills into a second
        cases.Add(new RefCase(6, s, 7, 6, 0));                 // empty payload, EOM only
        cases.Add(new RefCase(6, s, 7, 6, 0, AppendEom: false)); // empty and no EOM: one zero block

        cases.Add(new RefCase(6, s, 7, 4, 100, PreambleSuperframes: 1, TlcBlocks: 2));
        cases.Add(new RefCase(0, s, 7, 4, 100, PreambleSuperframes: 5, TlcBlocks: 1));
        cases.Add(new RefCase(8, s, 9, 4, 100, PreambleSuperframes: 2, TlcBlocks: 3));
        return cases;
    }

    private static int Block(int wn, Ms110dInterleaverKind il) => Ms110dInterleaverParams.Get3k(wn, il).InputBits;
}
