using M0LTE.Ms110d;

namespace M0LTE.Ms110d.RefGen;

/// <summary>One reference case: everything that determines the output bytes.</summary>
public sealed record RefCase(
    int WaveformNumber,
    Ms110dInterleaverKind Interleaver,
    int ConstraintLength,
    ulong Seed,
    int PayloadBits,
    int PreambleSuperframes = 3,
    int TlcBlocks = 0)
{
    /// <summary>Directory name, e.g. <c>wn6_short_k7_s1_p100</c>; non-default preamble
    /// settings add <c>_m{M}_t{N}</c>.</summary>
    public string DirectoryName
    {
        get
        {
            string name = $"wn{WaveformNumber}_{Interleaver.ToString().ToLowerInvariant()}_k{ConstraintLength}_s{Seed}_p{PayloadBits}";
            if (PreambleSuperframes != 3 || TlcBlocks != 0)
            {
                name += $"_m{PreambleSuperframes}_t{TlcBlocks}";
            }

            return name;
        }
    }

    /// <summary>The transmit settings this case runs the library with (library defaults for
    /// everything not listed: EOM and EOT on, amplitude 0.5, roll-off 0.35).</summary>
    public Ms110dTxSettings Settings => new()
    {
        WaveformNumber = WaveformNumber,
        Interleaver = Interleaver,
        ConstraintLength = ConstraintLength,
        PreambleSuperframes = PreambleSuperframes,
        TlcBlocks = TlcBlocks,
    };
}
