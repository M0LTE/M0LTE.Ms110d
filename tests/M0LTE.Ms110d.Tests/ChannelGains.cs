using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Bridges the fading-gain trajectories recorded by <see cref="WattersonChannel"/>, whose own
/// complex type belongs to another package, to this package's <see cref="Complex32"/>. Tests only.
/// </summary>
internal static class ChannelGains
{
    /// <summary>The recorded path gains as <see cref="Complex32"/>, or null when none were recorded.</summary>
    public static IReadOnlyList<Complex32[]>? PathGains(this WattersonChannel channel)
    {
        var recorded = channel.LastPathGains;
        if (recorded is null)
        {
            return null;
        }

        var result = new Complex32[recorded.Count][];
        for (int p = 0; p < recorded.Count; p++)
        {
            var src = recorded[p];
            var dst = new Complex32[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                dst[i] = new Complex32(src[i].Re, src[i].Im);
            }

            result[p] = dst;
        }

        return result;
    }
}
