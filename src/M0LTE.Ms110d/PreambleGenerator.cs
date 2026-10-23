namespace M0LTE.Ms110d;

/// <summary>
/// Appendix D autobaud synchronization preamble (D.5.2.1, doc pp. 165-170): a TLC section of
/// N 32-chip blocks, then M super-frames of [Fixed (9 Walsh channel symbols, or 1 when M=1)]
/// [4 downcount symbols c3…c0][5 WID symbols w4…w0], all 8PSK chips at 2400 Bd. Walsh
/// expansion and PN scrambling per D.5.2.1.1 (Tables D-XIV, D-XVIII/XIX/XX); prose anchored in
/// <c>docs/dev/ms110d/tables/preamble-fixed-tlc-prose.md</c>.
/// </summary>
/// <remarks>
/// Open point O-1 (design §2.3): the 3 kHz Fixed subsection is 288 chips against the 256-entry
/// fixedPN. The wrap-around reading (<c>fixedPN[chip mod 256]</c>) is implemented - and at
/// 3 kHz the per-channel-symbol-restart reading coincides with it, because 8 channel symbols =
/// exactly 256 chips, so symbol 9 starts at fixedPN[0] under either reading. The two readings
/// only diverge at bandwidths whose chips-per-symbol × 8 ≠ 256 (none of Table D-XIII's), so no
/// runtime switch is carried.
/// </remarks>
public sealed class PreambleGenerator
{
    private readonly int _tlcBlocks;
    private readonly int _superframes;

    /// <summary>Creates a generator for N TLC blocks (0-255; 0 omits the section) and
    /// M super-frames (1-32).</summary>
    public PreambleGenerator(int tlcBlocks, int superframes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(tlcBlocks, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tlcBlocks, 255);
        ArgumentOutOfRangeException.ThrowIfLessThan(superframes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(superframes, 32);
        _tlcBlocks = tlcBlocks;
        _superframes = superframes;
    }

    /// <summary>Chips in one super-frame: (9 fixed + 4 count + 5 WID) × 32 for M ≥ 2,
    /// (1 + 4 + 5) × 32 for the single-Walsh-symbol M = 1 case.</summary>
    public int SuperframeChips => (FixedSymbolCount + 9) * Ms110dTables.PreambleChipsPerSymbol;

    /// <summary>Fixed-subsection channel symbols: 9, or 1 when M = 1 (D.5.2.1.3).</summary>
    public int FixedSymbolCount => _superframes == 1 ? 1 : 9;

    /// <summary>Generates the whole preamble as 8PSK chip numbers 0-7.</summary>
    public byte[] Generate(int wn, Ms110dInterleaverKind interleaver, int constraintLength)
    {
        byte[] wid = EncodeWid(wn, interleaver, constraintLength);
        int chipsPerSym = Ms110dTables.PreambleChipsPerSymbol;
        var chips = new List<byte>((_tlcBlocks * chipsPerSym) + (_superframes * SuperframeChips));

        // TLC: N 32-chip blocks, complex conjugate of the Fixed-section Table D-XVIII
        // sequence (D.5.2.1.2) - conjugate of 8PSK symbol s is (8 − s) mod 8.
        for (int i = 0; i < _tlcBlocks * chipsPerSym; i++)
        {
            chips.Add((byte)((8 - Ms110dTables.FixedPn[i % 256]) & 7));
        }

        for (int rep = 0; rep < _superframes; rep++)
        {
            int count = _superframes - 1 - rep;
            AppendFixed(chips);
            AppendWalshSection(chips, EncodeCount(count), Ms110dTables.CntPn);
            AppendWalshSection(chips, wid, Ms110dTables.WidPn);
        }

        return [.. chips];
    }

    /// <summary>The scrambled Fixed-subsection chips (the receiver's matched-filter
    /// reference) for this generator's M.</summary>
    public byte[] FixedSectionChips()
    {
        var chips = new List<byte>(FixedSymbolCount * Ms110dTables.PreambleChipsPerSymbol);
        AppendFixed(chips);
        return [.. chips];
    }

    /// <summary>Scrambled chips of a count section conveying <paramref name="count"/>.</summary>
    public static byte[] CountSectionChips(int count)
    {
        var chips = new List<byte>(4 * Ms110dTables.PreambleChipsPerSymbol);
        AppendWalshSection(chips, EncodeCount(count), Ms110dTables.CntPn);
        return [.. chips];
    }

    /// <summary>Scrambled chips of the WID section for a waveform configuration.</summary>
    public static byte[] WidSectionChips(int wn, Ms110dInterleaverKind interleaver, int constraintLength)
    {
        var chips = new List<byte>(5 * Ms110dTables.PreambleChipsPerSymbol);
        AppendWalshSection(chips, EncodeWid(wn, interleaver, constraintLength), Ms110dTables.WidPn);
        return [.. chips];
    }

    /// <summary>Encodes the five WID di-bits w4…w0 (D.5.2.1.3.2 + Tables D-XV/XVI/XVII):
    /// d9d8d7d6 = WN, d5d4 = interleaver, d3 = 0 for K=7 / 1 for K=9,
    /// d2 = d9^d8^d7, d1 = d7^d6^d5, d0 = d5^d4^d3. The explicit checksum mapping is
    /// implemented; the D-XVII "lsb of w1 shall be 0" prose oddity (O-3) is recorded in
    /// <c>docs/dev/ms110d/README.md</c>, not silently resolved.</summary>
    internal static byte[] EncodeWid(int wn, Ms110dInterleaverKind interleaver, int constraintLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(wn, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wn, 13);
        if (constraintLength is not (7 or 9))
        {
            throw new ArgumentOutOfRangeException(nameof(constraintLength), constraintLength, "K must be 7 or 9");
        }

        int d9 = (wn >> 3) & 1, d8 = (wn >> 2) & 1, d7 = (wn >> 1) & 1, d6 = wn & 1;
        int il = (int)interleaver;
        int d5 = (il >> 1) & 1, d4 = il & 1;
        int d3 = constraintLength == 9 ? 1 : 0;
        int d2 = d9 ^ d8 ^ d7;
        int d1 = d7 ^ d6 ^ d5;
        int d0 = d5 ^ d4 ^ d3;
        return
        [
            (byte)((d9 << 1) | d8), (byte)((d7 << 1) | d6), (byte)((d5 << 1) | d4),
            (byte)((d3 << 1) | d2), (byte)((d1 << 1) | d0),
        ];
    }

    /// <summary>Decodes w4…w0 back to (WN, interleaver, K); false if the 3-bit checksum
    /// fails or the WN is reserved (14-15).</summary>
    internal static bool TryDecodeWid(
        ReadOnlySpan<byte> dibits, out int wn, out Ms110dInterleaverKind interleaver, out int constraintLength)
    {
        wn = (dibits[0] << 2) | dibits[1];
        interleaver = (Ms110dInterleaverKind)dibits[2];
        constraintLength = (dibits[3] & 2) != 0 ? 9 : 7;
        if (wn >= 14)
        {
            return false;
        }

        byte[] expected = EncodeWid(wn, interleaver, constraintLength);
        return dibits[3] == expected[3] && dibits[4] == expected[4];
    }

    /// <summary>Encodes the four downcount di-bits c3…c0 (D.5.2.1.3.1): 5-bit count b4…b0
    /// plus parity b7 = b1^b2^b3, b6 = b2^b3^b4, b5 = b0^b1^b2.</summary>
    internal static byte[] EncodeCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, 31);
        int b0 = count & 1, b1 = (count >> 1) & 1, b2 = (count >> 2) & 1, b3 = (count >> 3) & 1, b4 = (count >> 4) & 1;
        int b7 = b1 ^ b2 ^ b3;
        int b6 = b2 ^ b3 ^ b4;
        int b5 = b0 ^ b1 ^ b2;
        return [(byte)((b7 << 1) | b6), (byte)((b5 << 1) | b4), (byte)((b3 << 1) | b2), (byte)((b1 << 1) | b0)];
    }

    /// <summary>Decodes c3…c0 to the downcount value; false if parity fails.</summary>
    internal static bool TryDecodeCount(ReadOnlySpan<byte> dibits, out int count)
    {
        count = (((dibits[1] & 1) << 4) | (dibits[2] << 2) | dibits[3]) & 31;
        byte[] expected = EncodeCount(count);
        return dibits[0] == expected[0] && (dibits[1] >> 1) == (expected[1] >> 1);
    }

    private void AppendFixed(List<byte> chips)
    {
        // 9 Walsh symbols with di-bits {0,0,2,1,2,1,0,2,3}, or a single di-bit-3 symbol when
        // M = 1 (D.5.2.1.3). PN index runs across the section; see the O-1 remark above.
        ReadOnlySpan<byte> dibits = _superframes == 1 ? [3] : Ms110dTables.FixedDibits;
        int pos = 0;
        foreach (byte dibit in dibits)
        {
            byte[] walsh = Ms110dTables.Walsh[dibit];
            for (int i = 0; i < Ms110dTables.PreambleChipsPerSymbol; i++)
            {
                chips.Add((byte)((walsh[i & 3] + Ms110dTables.FixedPn[pos % 256]) & 7));
                pos++;
            }
        }
    }

    private static void AppendWalshSection(List<byte> chips, byte[] dibits, ReadOnlySpan<byte> pn)
    {
        int pos = 0;
        foreach (byte dibit in dibits)
        {
            byte[] walsh = Ms110dTables.Walsh[dibit];
            for (int i = 0; i < Ms110dTables.PreambleChipsPerSymbol; i++)
            {
                chips.Add((byte)((walsh[i & 3] + pn[pos]) & 7));
                pos++;
            }
        }
    }
}
