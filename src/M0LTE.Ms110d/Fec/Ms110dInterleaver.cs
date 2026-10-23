namespace M0LTE.Ms110d.Fec;

/// <summary>
/// Appendix D block interleaver (D.5.3.3.1-.3, doc pp. 222-223): a single 1-D array of
/// "Interleaver Size in Bits". TX <b>loads</b> punctured bit B(n) at
/// (n × increment) mod size and <b>fetches</b> linearly 0,1,2,…; RX inverts with
/// llr[n] = rx[(n·increment) mod size]. Sizes and increments for 3 kHz come from Tables
/// D-XXXVII / D-LI (<c>docs/dev/ms110d/tables/d37-interleaver-3khz.csv</c>,
/// <c>d51-interleaver-increments-3khz.csv</c>).
/// </summary>
/// <remarks>
/// Load-direction is loopback-blind (checklist L1): the unit test asserts the wire-side
/// D.5.3.3.2 worked example (WID 1 / 3 kHz / UltraShort: size 192, increment 25 ⇒ load
/// locations 0, 25, 50, 75, 100, 125, 150, 175, 8), i.e. after linear fetch
/// wire[(25·n) mod 192] == B(n). Round-trip is a secondary property only.
/// </remarks>
public sealed class Ms110dInterleaver
{
    private readonly int[] _perm;

    /// <summary>Creates the interleaver; <paramref name="increment"/> must be coprime with
    /// <paramref name="sizeBits"/> for the load map to be a permutation.</summary>
    public Ms110dInterleaver(int sizeBits, int increment)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sizeBits, 1);
        _perm = new int[sizeBits];
        long loc = 0;
        for (int n = 0; n < sizeBits; n++)
        {
            _perm[n] = (int)loc;
            loc += increment;
            if (loc >= sizeBits)
            {
                loc -= sizeBits;
            }
        }
    }

    /// <summary>Interleaver size in bits.</summary>
    public int SizeBits => _perm.Length;

    /// <summary>TX: loads <paramref name="punctured"/> at the permuted locations; the
    /// linear fetch is then just <paramref name="fetched"/> read in order.</summary>
    public void Interleave(ReadOnlySpan<byte> punctured, Span<byte> fetched)
    {
        CheckLengths(punctured.Length, fetched.Length);
        for (int n = 0; n < _perm.Length; n++)
        {
            fetched[_perm[n]] = punctured[n];
        }
    }

    /// <summary>Soft mirror of the byte <see cref="Interleave(ReadOnlySpan{byte}, Span{byte})"/>
    /// for the §B3.3 soft-feedback turbo: punctured-order posterior LLRs → wire order.</summary>
    public void Interleave(ReadOnlySpan<float> punctured, Span<float> fetched)
    {
        CheckLengths(punctured.Length, fetched.Length);
        for (int n = 0; n < _perm.Length; n++)
        {
            fetched[_perm[n]] = punctured[n];
        }
    }

    /// <summary>RX: recovers punctured-order LLRs from wire-order LLRs.</summary>
    public void Deinterleave(ReadOnlySpan<float> rxLlrs, Span<float> llrs)
    {
        CheckLengths(rxLlrs.Length, llrs.Length);
        for (int n = 0; n < _perm.Length; n++)
        {
            llrs[n] = rxLlrs[_perm[n]];
        }
    }

    private void CheckLengths(int a, int b)
    {
        if (a != _perm.Length || b != _perm.Length)
        {
            throw new ArgumentException(
                $"buffers must be exactly the interleaver size ({_perm.Length} bits) - " +
                "the punctured block shall fit exactly within the interleaver (D.5.3.2)");
        }
    }
}
