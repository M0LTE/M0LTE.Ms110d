namespace M0LTE.Ms110d;

/// <summary>
/// Appendix D data scrambler (D.5.1.3, doc pp. 160-161, text-layer verbatim in
/// <c>docs/dev/ms110d/tables/text-layer-extracts.md</c>): generator x⁹+x⁴+1, 9-bit register
/// initialized to 1 at the start of each data frame. PSK symbols are scrambled by modulo-8
/// addition of the numerical value of the rightmost three register bits; 2^N-QAM symbols XOR
/// the rightmost N bits. The register iterates <b>after</b> use, so the first symbol of every
/// data frame is scrambled by the initialization value 000000001.
/// </summary>
/// <remarks>
/// Register convention (Figure D-6 is an image page; the convention follows the printed
/// D.5.1.4 trinomial C code from the same standard, which shifts toward higher indices with
/// feedback into index 0 and reads the output from the lowest indices): bits b[8..0] live in
/// an int with b[i] at bit i; one iteration is
/// <c>b0' = b8 ⊕ b4</c> (the x⁹ and x⁴ taps), everything else shifting up. The rightmost
/// three bits are (b2 b1 b0) with b2 the MSB, exactly as the trinomial's
/// <c>(bitshift[2]&lt;&lt;2)+(bitshift[1]&lt;&lt;1)+bitshift[0]</c>. This convention is
/// loopback-blind (checklist L3); the wire-side unit test pins the hand-computed sequence
/// from init 1.
/// </remarks>
public sealed class Ms110dScrambler
{
    private int _sr = 1;

    /// <summary>Resets the register to the D.5.1.3 initialization value 000000001 - call at
    /// the start of every data frame.</summary>
    public void Reset()
    {
        _sr = 1;
    }

    /// <summary>Scrambles a transcoded PSK symbol number: (symbol + rightmost 3 bits) mod 8,
    /// then iterates the register 3 times (8PSK count; BPSK/QPSK transcode first onto the
    /// 8PSK ring, D.5.1.2.1).</summary>
    public int NextPsk(int transcoded)
    {
        int scrambled = (transcoded + (_sr & 7)) & 7;
        Iterate(3);
        return scrambled;
    }

    /// <summary>Scrambles a 2^<paramref name="bits"/>-QAM symbol number by XOR with the
    /// rightmost <paramref name="bits"/> register bits, then iterates that many times
    /// (Phase B/C waveforms; carried for completeness).</summary>
    public int NextQam(int symbol, int bits)
    {
        int scrambled = symbol ^ (_sr & ((1 << bits) - 1));
        Iterate(bits);
        return scrambled;
    }

    private void Iterate(int times)
    {
        for (int i = 0; i < times; i++)
        {
            int feedback = ((_sr >> 8) ^ (_sr >> 4)) & 1;
            _sr = ((_sr << 1) | feedback) & 0x1FF;
        }
    }
}
