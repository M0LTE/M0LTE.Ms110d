
namespace M0LTE.Ms110d;

/// <summary>
/// Appendix D mini-probes (D.5.2.2, Table D-XXI - <c>docs/dev/ms110d/tables/d21-miniprobes.csv</c>):
/// each K-symbol probe is a base sequence cyclically extended to K. The probe following the
/// second-to-last data block of each interleaver block is transmitted cyclically <b>shifted</b>
/// by the Table D-XXI shift column, marking the interleaver boundary for broadcast late entry.
/// </summary>
/// <remarks>
/// Shift convention (an interpretation - the rotation direction survives loopback either way
/// and is applied identically at both ends): the boundary probe starts <c>shift</c> symbols
/// into the base sequence, i.e. <c>probe[i] = base[(i + shift) mod baseLength]</c>.
/// </remarks>
public static class MiniProbe
{
    // Deterministic per (k, boundary); built once under the CLR type-initializer lock so
    // concurrent demodulators share them safely.
    private static readonly Complex32[][] BaseProbes =
        [Build(24, boundary: false), Build(32, boundary: false), Build(36, boundary: false), Build(48, boundary: false)];

    private static readonly Complex32[][] BoundaryProbes =
        [Build(24, boundary: true), Build(32, boundary: true), Build(36, boundary: true), Build(48, boundary: true)];

    /// <summary>Returns the K-symbol probe (base, or boundary-shifted). 3 kHz lengths:
    /// 24 → base 13 shift 6, 32 → base 16 shift 8, 48 → base 25 shift 12.
    /// The returned array is cached and shared - callers must not mutate it.</summary>
    public static Complex32[] Get(int k, bool boundary)
    {
        Complex32[][] probes = boundary ? BoundaryProbes : BaseProbes;
        return k switch
        {
            24 => probes[0],
            32 => probes[1],
            36 => probes[2],
            48 => probes[3],
            _ => Build(k, boundary), // unsupported k throws the Sequence() range error
        };
    }

    private static Complex32[] Build(int k, bool boundary)
    {
        (Complex32[] baseSeq, int shift) = Sequence(k);
        var probe = new Complex32[k];
        int offset = boundary ? shift : 0;
        for (int i = 0; i < k; i++)
        {
            probe[i] = baseSeq[(i + offset) % baseSeq.Length];
        }

        return probe;
    }

    /// <summary>Base sequence and boundary shift for a probe length (Table D-XXI rows used
    /// at 3 kHz).</summary>
    internal static (Complex32[] Base, int Shift) Sequence(int k)
    {
        return k switch
        {
            24 => (Ms110dTables.Base13, 6),
            32 => (Ms110dTables.Base16, 8),
            36 => (Ms110dTables.Base19, 9),
            48 => (Ms110dTables.Base25, 12),
            _ => throw new ArgumentOutOfRangeException(
                nameof(k), k, "not a 3 kHz mini-probe length (Table D-XXI subset carried here)"),
        };
    }
}
