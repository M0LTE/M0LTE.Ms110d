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
    int TlcBlocks = 0,
    bool AppendEom = true,
    bool AppendEot = true,
    float Amplitude = 0.5f,
    double RollOff = 0.35)
{
    /// <summary>Directory name, e.g. <c>wn6_short_k7_s1_p100</c>; each non-default option
    /// adds a suffix: <c>_m{M}_t{N}</c> (preamble), <c>_noeom</c>, <c>_noeot</c>,
    /// <c>_amp{A}</c>, <c>_ro{R}</c>.</summary>
    public string DirectoryName
    {
        get
        {
            string name = $"wn{WaveformNumber}_{Interleaver.ToString().ToLowerInvariant()}_k{ConstraintLength}_s{Seed}_p{PayloadBits}";
            if (PreambleSuperframes != 3 || TlcBlocks != 0)
            {
                name += $"_m{PreambleSuperframes}_t{TlcBlocks}";
            }

            if (!AppendEom)
            {
                name += "_noeom";
            }

            if (!AppendEot)
            {
                name += "_noeot";
            }

            if (Amplitude != 0.5f)
            {
                name += "_amp" + Amplitude.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (RollOff != 0.35)
            {
                name += "_ro" + RollOff.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            }

            return name;
        }
    }

    /// <summary>The transmit settings this case runs the library with .</summary>
    public Ms110dTxSettings Settings => new()
    {
        WaveformNumber = WaveformNumber,
        Interleaver = Interleaver,
        ConstraintLength = ConstraintLength,
        PreambleSuperframes = PreambleSuperframes,
        TlcBlocks = TlcBlocks,
        AppendEom = AppendEom,
        AppendEot = AppendEot,
        Amplitude = Amplitude,
        RollOff = RollOff,
    };
}
