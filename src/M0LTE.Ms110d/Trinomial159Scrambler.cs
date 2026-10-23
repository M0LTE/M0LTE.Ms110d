namespace M0LTE.Ms110d;

/// <summary>
/// Waveform-ID 0 Walsh data scrambler (D.5.1.4, doc pp. 161-162): a Trinomial (159, 31)
/// bit shift register with the printed 159-bit initialization, iterated 16 times per
/// generated 8PSK scramble symbol, output <c>(b2&lt;&lt;2)|(b1&lt;&lt;1)|b0</c>. The C code,
/// the init state, the printed first-32 sequence and the worked combine row are all
/// text-layer verbatim in <c>docs/dev/ms110d/tables/text-layer-extracts.md</c> and mutually
/// consistent (machine-checked at transcription) - the implementation follows the printed
/// <c>tri()</c> exactly. The sequence wraps at the 2048-symbol boundary and resets to the
/// initialization value at each interleaver boundary.
/// </summary>
public sealed class Trinomial159Scrambler
{
    /// <summary>Printed initialization state, <c>int bitshift[159]</c> (D.5.1.4).</summary>
    private static ReadOnlySpan<byte> Init =>
    [
        0, 0, 0, 1, 0, 0, 1, 1, 0, 1, 1, 0, 0, 1, 0, 1,
        1, 1, 1, 1, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        1, 0, 1, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 1, 1, 0,
        1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0,
        1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1, 1, 0, 1,
        0, 1, 1, 1, 1, 0, 1, 0, 1, 0, 0, 0, 1, 1, 1, 1,
        1, 1, 0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1, 0, 1,
        1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 0,
        1, 1, 1, 0, 0, 1, 1, 1, 0, 0, 0, 1, 1, 0, 0, 0,
        1, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 0, 0, 1, 1,
    ];

    private readonly byte[] _bitshift = new byte[159];
    private int _symbolCount;

    /// <summary>Creates the scrambler in the reset (initialization) state.</summary>
    public Trinomial159Scrambler()
    {
        Reset();
    }

    /// <summary>Resets to the printed initialization value - call at every interleaver
    /// boundary (D.5.1.4 final sentence).</summary>
    public void Reset()
    {
        Init.CopyTo(_bitshift);
        _symbolCount = 0;
    }

    /// <summary>Generates the next 8PSK scramble symbol (16 register shifts, printed
    /// <c>tri()</c>), wrapping around the 2048-symbol boundary.</summary>
    public int Next()
    {
        if (_symbolCount == 2048)
        {
            // "the sequences are continuously wrapped around the 2048 symbol boundary".
            Init.CopyTo(_bitshift);
            _symbolCount = 0;
        }

        for (int j = 0; j < 16; j++)
        {
            byte bitout = _bitshift[158];
            byte bittap = _bitshift[31];
            for (int i = 158; i >= 1; i--)
            {
                _bitshift[i] = _bitshift[i - 1];
            }

            _bitshift[0] = (byte)(bitout ^ bittap);
        }

        _symbolCount++;
        return (_bitshift[2] << 2) + (_bitshift[1] << 1) + _bitshift[0];
    }
}
