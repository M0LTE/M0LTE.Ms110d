namespace M0LTE.Ms110d;

/// <summary>
/// Which mini-probes of a burst are cyclically shifted (D.5.2.2).
/// </summary>
/// <remarks>
/// Data frames are numbered from 0 across the whole burst; <c>g</c> is the frame the probe
/// FOLLOWS (-1 is the probe that ends the preamble, which is never shifted). The probe is
/// shifted only when <c>g % L == L - 2</c>, L the LONG interleaver's frame count for the
/// waveform (256, 128 or 64), whatever interleaver is selected: the shifted probe marks the
/// long interleaver boundary for late entry. With the Long interleaver this is the probe
/// after the second-to-last frame of every block; with a shorter one most blocks have no
/// shifted probe at all.
/// </remarks>
internal static class ProbePattern
{
    /// <summary>Frame count of the waveform's Long interleaver, the shift pattern's period.</summary>
    public static int LongFrames(int wn) =>
        Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long).Frames;

    /// <summary>The probe after frame g is cyclically shifted.</summary>
    public static bool ShiftedAfter(long g, int longFrames) => g >= 0 && g % longFrames == longFrames - 2;
}
