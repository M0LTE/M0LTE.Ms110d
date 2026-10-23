using M0LTE.Ofdm;

namespace M0LTE.Ms110d;

/// <summary>
/// MIL-STD-188-110D Appendix D constants for the 3 kHz serial-tone waveform. Every array here
/// is transcribed from the dual-verified tables in <c>docs/dev/ms110d/tables/</c> (see
/// <c>docs/dev/ms110d/README.md</c> § Ledger clearance) - nothing is re-derived. File names cited
/// per member.
/// </summary>
public static class Ms110dTables
{
    /// <summary>Chips per preamble channel symbol at 3 kHz (Table D-XIII,
    /// <c>d13-preamble-channel-symbol-length.csv</c> row 3).</summary>
    public const int PreambleChipsPerSymbol = 32;

    /// <summary>Symbol rate at 3 kHz (Table D-I, <c>d01-symbol-rates.csv</c>).</summary>
    public const int SymbolRate = 2400;

    /// <summary>Audio sub-carrier at 3 kHz (Table D-I): 300 + BW/2 = 1800 Hz.</summary>
    public const int SubCarrierHz = 1800;

    /// <summary>EOM marker, 32 bits, leftmost (most-significant) bit sent first
    /// (D.5.4.3, doc p. 236, text-layer verified).</summary>
    public const uint Eom = 0x4B65A5B2;

    /// <summary>Table D-XVIII fixedPN[256] (<c>d18-fixedpn.txt</c>) - TLC / Fixed
    /// synchronization preamble scrambling sequence, 8PSK symbol numbers.</summary>
    public static ReadOnlySpan<byte> FixedPn =>
    [
        2, 4, 0, 0, 6, 2, 1, 4, 6, 1, 0, 5, 7, 3, 4, 1, 2, 6, 1, 7, 0, 7, 3, 2, 2, 2, 3, 2, 4, 6, 3, 6,
        6, 3, 7, 5, 4, 7, 5, 6, 7, 4, 0, 2, 6, 1, 5, 3, 0, 4, 2, 4, 6, 4, 5, 2, 5, 4, 5, 3, 1, 5, 4, 5,
        6, 5, 1, 0, 7, 1, 0, 1, 0, 5, 3, 5, 2, 2, 4, 5, 4, 0, 6, 4, 1, 4, 0, 3, 3, 0, 0, 3, 3, 7, 3, 4,
        2, 7, 4, 4, 4, 0, 3, 4, 7, 6, 4, 2, 6, 2, 0, 3, 5, 3, 2, 2, 4, 5, 2, 0, 0, 3, 5, 0, 3, 2, 6, 6,
        1, 4, 2, 3, 6, 1, 3, 0, 3, 3, 2, 4, 2, 2, 6, 5, 5, 3, 6, 7, 6, 5, 6, 6, 5, 2, 5, 4, 2, 3, 3, 3,
        5, 7, 5, 5, 3, 7, 0, 4, 7, 0, 4, 1, 6, 2, 3, 5, 5, 6, 2, 6, 4, 6, 3, 4, 0, 7, 0, 0, 5, 2, 1, 5,
        4, 3, 4, 5, 7, 0, 5, 3, 7, 6, 6, 6, 4, 5, 6, 0, 2, 0, 4, 2, 3, 4, 4, 0, 7, 6, 6, 2, 0, 0, 3, 3,
        0, 5, 2, 4, 2, 2, 4, 5, 4, 6, 6, 6, 3, 2, 1, 0, 3, 2, 6, 0, 6, 2, 4, 0, 6, 4, 1, 3, 3, 5, 3, 6,
    ];

    /// <summary>Table D-XIX cntPN[256] (<c>d19-cntpn.txt</c>) - Count synchronization
    /// preamble scrambling sequence.</summary>
    public static ReadOnlySpan<byte> CntPn =>
    [
        5, 5, 2, 2, 0, 2, 5, 6, 7, 1, 3, 5, 1, 5, 6, 5, 3, 7, 0, 4, 0, 3, 3, 2, 1, 3, 0, 3, 1, 6, 2, 6,
        0, 6, 4, 1, 2, 5, 6, 3, 5, 3, 7, 4, 2, 6, 7, 3, 0, 2, 0, 1, 7, 5, 0, 6, 1, 5, 0, 3, 2, 2, 5, 2,
        5, 2, 3, 4, 2, 7, 6, 1, 1, 5, 2, 1, 5, 4, 0, 3, 5, 5, 0, 3, 1, 4, 0, 5, 0, 3, 0, 6, 0, 0, 3, 1,
        6, 1, 4, 4, 7, 7, 0, 5, 7, 0, 1, 5, 1, 0, 1, 3, 1, 5, 0, 7, 1, 2, 2, 2, 7, 1, 2, 5, 0, 3, 3, 2,
        2, 0, 4, 5, 1, 3, 1, 3, 5, 3, 1, 7, 5, 2, 7, 1, 3, 1, 5, 6, 2, 4, 6, 0, 6, 1, 0, 0, 3, 6, 2, 7,
        3, 2, 4, 7, 6, 4, 1, 3, 6, 6, 0, 3, 0, 0, 7, 5, 4, 5, 1, 2, 1, 5, 0, 3, 1, 0, 4, 6, 6, 1, 0, 5,
        2, 6, 3, 2, 7, 4, 2, 4, 0, 1, 7, 0, 7, 0, 5, 1, 4, 5, 7, 2, 0, 4, 4, 3, 5, 2, 7, 7, 4, 5, 1, 4,
        4, 6, 3, 3, 0, 5, 1, 5, 5, 4, 3, 2, 0, 3, 0, 4, 7, 4, 5, 1, 5, 5, 7, 7, 6, 2, 4, 3, 5, 2, 2, 4,
    ];

    /// <summary>Table D-XX widPN[256] (<c>d20-widpn.txt</c>, PDF text-layer verbatim) -
    /// Waveform-ID synchronization preamble scrambling sequence.</summary>
    public static ReadOnlySpan<byte> WidPn =>
    [
        2, 3, 0, 3, 7, 3, 3, 0, 1, 4, 4, 6, 5, 5, 4, 5, 6, 2, 0, 5, 6, 6, 5, 3, 5, 5, 2, 2, 1, 2, 3, 6,
        1, 1, 4, 3, 1, 0, 5, 1, 0, 3, 3, 0, 3, 0, 4, 4, 6, 2, 5, 6, 1, 7, 2, 6, 2, 0, 0, 4, 7, 2, 3, 5,
        2, 7, 1, 6, 5, 0, 4, 1, 6, 2, 1, 5, 4, 3, 5, 0, 3, 4, 1, 3, 2, 1, 6, 1, 5, 7, 0, 4, 7, 6, 6, 0,
        4, 7, 6, 6, 6, 6, 2, 3, 5, 0, 7, 0, 3, 1, 5, 1, 2, 0, 5, 3, 2, 4, 5, 6, 6, 7, 7, 3, 5, 1, 6, 0,
        1, 4, 4, 5, 6, 0, 6, 7, 2, 4, 4, 0, 3, 7, 2, 0, 0, 1, 4, 0, 7, 1, 7, 4, 5, 4, 5, 5, 5, 3, 3, 2,
        0, 5, 1, 3, 1, 5, 3, 4, 1, 5, 4, 1, 4, 4, 2, 2, 4, 3, 0, 7, 4, 1, 5, 7, 1, 4, 7, 2, 5, 5, 6, 6,
        1, 6, 5, 6, 3, 0, 2, 5, 7, 7, 4, 4, 3, 4, 4, 6, 0, 7, 2, 2, 0, 0, 2, 1, 0, 0, 3, 6, 6, 4, 0, 2,
        4, 3, 4, 5, 2, 6, 3, 7, 7, 5, 7, 3, 0, 7, 0, 0, 7, 2, 6, 2, 2, 6, 1, 4, 3, 7, 6, 5, 0, 6, 5, 4,
    ];

    /// <summary>Table D-XIV 4-ary Walsh sequences (<c>d14-walsh-sync-sequences.csv</c>,
    /// dual-transcribed - the provisional 10↔11 swap is resolved): di-bit →
    /// {00→0000, 01→0404, 10→0044, 11→0440}, repeated to the Table D-XIII length.</summary>
    public static readonly byte[][] Walsh =
    [
        [0, 0, 0, 0],
        [0, 4, 0, 4],
        [0, 0, 4, 4],
        [0, 4, 4, 0],
    ];

    /// <summary>Fixed-subsection di-bits for the 9-Walsh-symbol case, 3 (binary 11) last
    /// (D.5.2.1.3 prose, <c>preamble-fixed-tlc-prose.md</c>).</summary>
    public static ReadOnlySpan<byte> FixedDibits => [0, 0, 2, 1, 2, 1, 0, 2, 3];

    /// <summary>8PSK symbol constellation, Table D-VI (<c>d06-8psk-symbol-mapping.csv</c>):
    /// symbol s at phase s·45°. Printed I/Q values used verbatim.</summary>
    public static readonly Cf[] Psk8 =
    [
        new(1.000000f, 0.000000f),
        new(0.707107f, 0.707107f),
        new(0.000000f, 1.000000f),
        new(-0.707107f, 0.707107f),
        new(-1.000000f, 0.000000f),
        new(-0.707107f, -0.707107f),
        new(0.000000f, -1.000000f),
        new(0.707107f, -0.707107f),
    ];

    /// <summary>Table D-V 8PSK transcoding (<c>d05-transcoding-8psk.csv</c>): tribit →
    /// 8PSK ring symbol number. Index = tribit value (MSB-first), value = symbol.</summary>
    public static ReadOnlySpan<byte> Transcode8Psk => [1, 0, 2, 3, 6, 7, 5, 4];

    /// <summary>16-QAM constellation, Table D-VII (<c>d07-constellation-16qam.csv</c>):
    /// 12 outer-ring points at radius 1.0 (30° spacing) + 4 inner-ring points at
    /// radius ≈0.366 (45° spacing). Printed I/Q values used verbatim.</summary>
    public static readonly Cf[] Qam16 =
    [
        new(0.866025f, 0.500000f),
        new(0.500000f, 0.866025f),
        new(1.000000f, 0.000000f),
        new(0.258819f, 0.258819f),
        new(-0.500000f, 0.866025f),
        new(0.000000f, 1.000000f),
        new(-0.866025f, 0.500000f),
        new(-0.258819f, 0.258819f),
        new(0.500000f, -0.866025f),
        new(0.000000f, -1.000000f),
        new(0.866025f, -0.500000f),
        new(0.258819f, -0.258819f),
        new(-0.866025f, -0.500000f),
        new(-0.500000f, -0.866025f),
        new(-1.000000f, 0.000000f),
        new(-0.258819f, -0.258819f),
    ];

    /// <summary>Table D-XXII(a): 13-symbol mini-probe base sequence (Barker-13, real BPSK,
    /// <c>d22a-base13-miniprobe.csv</c>).</summary>
    public static readonly Cf[] Base13 =
    [
        new(1, 0), new(1, 0), new(1, 0), new(1, 0), new(1, 0), new(-1, 0), new(-1, 0),
        new(1, 0), new(1, 0), new(-1, 0), new(1, 0), new(-1, 0), new(1, 0),
    ];

    /// <summary>Table D-XXIII: 16-symbol mini-probe base sequence
    /// (<c>d23-base16-miniprobe.csv</c>).</summary>
    public static readonly Cf[] Base16 =
    [
        new(1, 0), new(1, 0), new(1, 0), new(1, 0), new(1, 0), new(0, -1), new(-1, 0), new(0, 1),
        new(1, 0), new(-1, 0), new(1, 0), new(-1, 0), new(1, 0), new(0, 1), new(-1, 0), new(0, -1),
    ];

    /// <summary>Table D-XXV: 25-symbol mini-probe base sequence
    /// (<c>d25-base25-miniprobe.csv</c>; printed six-decimal values verbatim, including the
    /// row-24 0.309016 quirk).</summary>
    public static readonly Cf[] Base25 =
    [
        new(1.000000f, 0.000000f), new(1.000000f, 0.000000f), new(1.000000f, 0.000000f),
        new(1.000000f, 0.000000f), new(1.000000f, 0.000000f), new(1.000000f, 0.000000f),
        new(0.309017f, -0.951057f), new(-0.809017f, -0.587785f), new(-0.809017f, 0.587785f),
        new(0.309017f, 0.951056f), new(1.000000f, 0.000000f), new(-0.809017f, -0.587785f),
        new(0.309017f, 0.951056f), new(0.309017f, -0.951057f), new(-0.809017f, 0.587785f),
        new(1.000000f, 0.000000f), new(-0.809017f, 0.587785f), new(0.309017f, -0.951057f),
        new(0.309017f, 0.951057f), new(-0.809017f, -0.587785f), new(1.000000f, 0.000000f),
        new(0.309017f, 0.951056f), new(-0.809017f, 0.587785f), new(-0.809017f, -0.587785f),
        new(0.309016f, -0.951057f),
    ];

    /// <summary>Table D-XXIV: 19-symbol mini-probe base sequence
    /// (<c>d24-base19-miniprobe.csv</c>) - not used at 3 kHz; carried for completeness.</summary>
    public static readonly Cf[] Base19 =
    [
        new(1, 0), new(-1, 0), new(1, 0), new(1, 0), new(-1, 0), new(-1, 0), new(-1, 0),
        new(-1, 0), new(1, 0), new(-1, 0), new(1, 0), new(-1, 0), new(1, 0), new(1, 0),
        new(1, 0), new(1, 0), new(-1, 0), new(-1, 0), new(1, 0),
    ];
}
