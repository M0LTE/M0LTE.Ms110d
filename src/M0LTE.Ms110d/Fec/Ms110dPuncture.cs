using M0LTE.Fec;
namespace M0LTE.Ms110d.Fec;

/// <summary>
/// One Table D-L puncture/repetition rule (MIL-STD-188-110D doc p. 222, transcribed in
/// <c>docs/dev/ms110d/tables/transcription-notes.md</c> § Table D-L). <see cref="KeepT1"/> /
/// <see cref="KeepT2"/> are the two mask rows applied column-wise to the (repeated) pair
/// stream; <see cref="RepeatFactor"/> repeats the rate-1/2 pair adjacently
/// (T1,T2,T1,T2 per input bit for 2×) <b>before</b> the mask is applied (the worked 1/3
/// example, doc p. 221 - repeat first, then puncture).
/// </summary>
/// <param name="KeepT1">Mask row for T1 (b0) bits, 1 = keep.</param>
/// <param name="KeepT2">Mask row for T2 (b1) bits, 1 = keep.</param>
/// <param name="RepeatFactor">Pair repetition factor (1 = no repetition).</param>
public sealed record PunctureSpec(byte[] KeepT1, byte[] KeepT2, int RepeatFactor)
{
    /// <summary>Punctured output length for an <paramref name="infoBits"/>-bit block.</summary>
    public int OutputLength(int infoBits)
    {
        int pairs = infoBits * RepeatFactor;
        if (pairs % KeepT1.Length != 0)
        {
            throw new ArgumentException(
                $"block of {infoBits} bits is not an integral number of {KeepT1.Length}-column mask cycles",
                nameof(infoBits));
        }

        int perCycle = 0;
        for (int i = 0; i < KeepT1.Length; i++)
        {
            perCycle += KeepT1[i] + KeepT2[i];
        }

        return pairs / KeepT1.Length * perCycle;
    }
}

/// <summary>
/// Appendix D puncturing/repetition (D.5.3.2.4 + Table D-L). Puncturing happens after the
/// tail-biting encoder and <b>before</b> interleaving (D.5.3.2). K=7 and K=9 masks differ for
/// some rates (e.g. 3/4: 110/101 vs 111/100) - the tables are keyed by constraint length.
/// </summary>
public static class Ms110dPuncture
{
    /// <summary>Looks up the Table D-L spec for <paramref name="code"/> at
    /// <paramref name="rate"/> (e.g. "3/4", "1/8"). Throws for unknown rates.</summary>
    public static PunctureSpec Get(ConvolutionalCode code, string rate)
    {
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(rate);
        bool k9 = code.K == 9;
        return rate switch
        {
            // Rates above 1/2: mask only. K=7 / K=9 rows verbatim from Table D-L.
            "9/10" => k9 ? Spec("111000101", "100111010", 1) : Spec("111101110", "100010001", 1),
            "8/9" => k9 ? Spec("11100000", "10011111", 1) : Spec("11110100", "10001011", 1),
            "5/6" => k9 ? Spec("10110", "11001", 1) : Spec("11010", "10101", 1),
            "4/5" => k9 ? Spec("1101", "1010", 1) : Spec("1111", "1000", 1),
            "3/4" => k9 ? Spec("111", "100", 1) : Spec("110", "101", 1),
            "2/3" => Spec("11", "10", 1),
            "4/7" => Spec("1111", "0111", 1),
            "9/16" => Spec("111101111", "111111011", 1),
            "1/2" => Spec("1", "1", 1),

            // Rates below 1/2: repeat the rate-1/2 pair stream, then mask (D-L "Repeated" rows).
            "2/5" => Spec("1110", "1010", 2),
            "1/3" => Spec("11", "10", 2),  // "2/3 Repeated 2x"
            "2/7" => Spec("1111", "0111", 2),
            "1/4" => Spec("1", "1", 2),
            "1/6" => Spec("1", "1", 3),
            "1/8" => Spec("1", "1", 4),
            "1/12" => Spec("1", "1", 6),
            "1/16" => Spec("1", "1", 8),
            _ => throw new ArgumentException($"no Table D-L entry for rate {rate}", nameof(rate)),
        };
    }

    /// <summary>TX: repeats and punctures the 2N-bit mother stream
    /// (<paramref name="coded"/> = T1,T2 pairs) into <paramref name="punctured"/>.
    /// Returns the number of bits written.</summary>
    public static int Apply(PunctureSpec spec, ReadOnlySpan<byte> coded, Span<byte> punctured)
    {
        ArgumentNullException.ThrowIfNull(spec);
        int pairs = coded.Length / 2 * spec.RepeatFactor;
        int len = spec.KeepT1.Length;
        int o = 0;
        for (int p = 0; p < pairs; p++)
        {
            int n = p / spec.RepeatFactor;
            int col = p % len;
            if (spec.KeepT1[col] != 0)
            {
                punctured[o++] = coded[2 * n];
            }

            if (spec.KeepT2[col] != 0)
            {
                punctured[o++] = coded[(2 * n) + 1];
            }
        }

        return o;
    }

    /// <summary>Soft mirror of <see cref="Apply(PunctureSpec, ReadOnlySpan{byte}, Span{byte})"/>
    /// for the §B3.3 soft-feedback turbo: walks the mother-lattice posterior LLRs through the
    /// same repeat/mask schedule, so each transmitted copy of a repeated bit carries the same
    /// (block-wide) posterior. Returns the number of values written.</summary>
    public static int Apply(PunctureSpec spec, ReadOnlySpan<float> mother, Span<float> punctured)
    {
        ArgumentNullException.ThrowIfNull(spec);
        int pairs = mother.Length / 2 * spec.RepeatFactor;
        int len = spec.KeepT1.Length;
        int o = 0;
        for (int p = 0; p < pairs; p++)
        {
            int n = p / spec.RepeatFactor;
            int col = p % len;
            if (spec.KeepT1[col] != 0)
            {
                punctured[o++] = mother[2 * n];
            }

            if (spec.KeepT2[col] != 0)
            {
                punctured[o++] = mother[(2 * n) + 1];
            }
        }

        return o;
    }

    /// <summary>RX: expands received LLRs back onto the 2N mother lattice. Punctured
    /// positions stay LLR 0 (erasure); repeated copies are <b>summed</b> (optimal combining
    /// of independent LLRs).</summary>
    public static void Depuncture(PunctureSpec spec, ReadOnlySpan<float> rxLlrs, Span<float> motherLlrs)
    {
        ArgumentNullException.ThrowIfNull(spec);
        motherLlrs.Clear();
        int pairs = motherLlrs.Length / 2 * spec.RepeatFactor;
        int len = spec.KeepT1.Length;
        int j = 0;
        for (int p = 0; p < pairs; p++)
        {
            int n = p / spec.RepeatFactor;
            int col = p % len;
            if (spec.KeepT1[col] != 0)
            {
                motherLlrs[2 * n] += rxLlrs[j++];
            }

            if (spec.KeepT2[col] != 0)
            {
                motherLlrs[(2 * n) + 1] += rxLlrs[j++];
            }
        }

        if (j != rxLlrs.Length)
        {
            throw new ArgumentException("rxLlrs length does not match the puncture spec", nameof(rxLlrs));
        }
    }

    private static PunctureSpec Spec(string t1, string t2, int repeat)
    {
        return new PunctureSpec(ToBits(t1), ToBits(t2), repeat);
    }

    private static byte[] ToBits(string mask)
    {
        var bits = new byte[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            bits[i] = (byte)(mask[i] - '0');
        }

        return bits;
    }
}
