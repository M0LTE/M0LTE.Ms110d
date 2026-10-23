using M0LTE.Ms110d;
using M0LTE.Ofdm;

namespace M0LTE.Ms110d.Tests;

public class MiniProbeTests
{
    [Fact]
    public void Probe_24_Prefix_Is_Barker_13()
    {
        Cf[] probe = MiniProbe.Get(24, boundary: false);
        float[] barker = [1, 1, 1, 1, 1, -1, -1, 1, 1, -1, 1, -1, 1];
        for (int i = 0; i < 13; i++)
        {
            probe[i].Re.Should().Be(barker[i]);
            probe[i].Im.Should().Be(0);
        }

        // Cyclic extension: symbols 13…23 repeat the base from the top.
        for (int i = 13; i < 24; i++)
        {
            probe[i].Should().Be(probe[i - 13]);
        }
    }

    [Theory]
    [InlineData(24, 13, 6)]
    [InlineData(32, 16, 8)]
    [InlineData(48, 25, 12)]
    public void Boundary_Probe_Is_The_Base_Cyclically_Shifted_By_The_Table_DXxi_Shift(
        int k, int baseLength, int shift)
    {
        Cf[] probe = MiniProbe.Get(k, boundary: false);
        Cf[] boundary = MiniProbe.Get(k, boundary: true);
        for (int i = 0; i < k; i++)
        {
            boundary[i].Should().Be(probe[(i + shift) % baseLength]);
        }
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    public void Base_Sequences_Have_Low_Periodic_Autocorrelation_Sidelobes(int k)
    {
        (Cf[] baseSeq, _) = MiniProbe.Sequence(k);
        int n = baseSeq.Length;
        float peak = 0;
        float worstSidelobe = 0;
        for (int lag = 0; lag < n; lag++)
        {
            var acc = Cf.Zero;
            for (int i = 0; i < n; i++)
            {
                acc += baseSeq[i] * baseSeq[(i + lag) % n].Conj();
            }

            float magnitude = acc.Abs();
            if (lag == 0)
            {
                peak = magnitude;
            }
            else
            {
                worstSidelobe = Math.Max(worstSidelobe, magnitude);
            }
        }

        peak.Should().BeApproximately(n, 0.01f);

        // Probe bases are chosen for near-ideal periodic autocorrelation - the property the
        // per-frame channel estimator relies on. Bound: ≤ 15 % of the peak.
        worstSidelobe.Should().BeLessThan(0.15f * peak);
    }

    [Fact]
    public void All_Probe_Symbols_Are_Unit_Magnitude()
    {
        foreach (int k in new[] { 24, 32, 36, 48 })
        {
            foreach (Cf symbol in MiniProbe.Get(k, boundary: false))
            {
                symbol.Abs().Should().BeApproximately(1f, 1e-5f);
            }
        }
    }
}
