using M0LTE.Fec;
using M0LTE.Ms110d.Fec;

namespace M0LTE.Ms110d;

/// <summary>
/// The Appendix D bit pipeline shared by modulator and demodulator: payload → EOM → zero
/// fill → per-interleaver-block tail-biting encode → puncture/repeat → interleave (TX), and
/// the exact soft inverse (RX). Chain order per D.5.3 (encode → puncture → interleave; one
/// code block == one interleaver block; D.5.3.1: the first fetched bit becomes the MSB of the
/// first symbol - checklist L12).
/// </summary>
public static class Ms110dFraming
{
    /// <summary>Builds the TX bit stream: payload bits (0/1 bytes), optionally the 32-bit
    /// EOM leftmost-bit-first (D.5.4.3, checklist L7), zero-filled to a whole number of
    /// input-data blocks (at least one).</summary>
    public static byte[] BuildTxBits(ReadOnlySpan<byte> payloadBits, bool appendEom, int inputBits)
    {
        int used = payloadBits.Length + (appendEom ? 32 : 0);
        int blocks = Math.Max(1, (used + inputBits - 1) / inputBits);
        var bits = new byte[blocks * inputBits];
        payloadBits.CopyTo(bits);
        if (appendEom)
        {
            for (int i = 0; i < 32; i++)
            {
                bits[payloadBits.Length + i] = (byte)((Ms110dTables.Eom >> (31 - i)) & 1);
            }
        }

        return bits;
    }

    /// <summary>TX: one interleaver block of info bits → fetched (wire-order) bits.</summary>
    public static byte[] EncodeBlock(
        ConvolutionalCode code, PunctureSpec puncture, Ms110dInterleaver interleaver, ReadOnlySpan<byte> info)
    {
        var coded = new byte[2 * info.Length];
        TailBitingEncoder.Encode(code, info, coded);
        var punctured = new byte[puncture.OutputLength(info.Length)];
        int written = Ms110dPuncture.Apply(puncture, coded, punctured);
        if (written != interleaver.SizeBits)
        {
            // "the coded and punctured block shall still fit exactly within the interleaver"
            // (D.5.3.2) - a mismatch is a table bug, not a runtime condition.
            throw new InvalidOperationException(
                $"punctured block ({written} bits) does not fill the interleaver ({interleaver.SizeBits} bits)");
        }

        var fetched = new byte[interleaver.SizeBits];
        interleaver.Interleave(punctured, fetched);
        return fetched;
    }

    /// <summary>RX: wire-order LLRs of one interleaver block → decoded info bits.</summary>
    internal static void DecodeBlock(
        TailBitingViterbiDecoder decoder,
        PunctureSpec puncture,
        Ms110dInterleaver interleaver,
        ReadOnlySpan<float> wireLlrs,
        Span<byte> info)
    {
        var llrs = new float[wireLlrs.Length];
        interleaver.Deinterleave(wireLlrs, llrs);
        var mother = new float[2 * info.Length];
        Ms110dPuncture.Depuncture(puncture, llrs, mother);
        decoder.Decode(mother, info);
    }

    /// <summary>Scans <paramref name="bits"/> for the EOM marker starting the search at
    /// <paramref name="from"/>; returns the bit index of the EOM start, or −1.</summary>
    internal static int FindEom(IReadOnlyList<byte> bits, int from)
    {
        uint window = 0;
        int start = Math.Max(0, from);
        for (int i = start; i < bits.Count; i++)
        {
            window = (window << 1) | bits[i];
            if (i - start >= 31 && window == Ms110dTables.Eom)
            {
                return i - 31;
            }
        }

        return -1;
    }
}
