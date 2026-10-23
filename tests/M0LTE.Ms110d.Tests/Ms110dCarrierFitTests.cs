using M0LTE.Ofdm;
using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Component tests for the fade-robust carrier fit (§B3 tail autopsy, issue #69) that
/// replaced the weighted phase regression in RefineCarrier. The central test reproduces
/// the measured killer: a deep fade inside the fit window whose noise phases random-walk
/// a sequential unwrap several turns, which made the old regression fit −4.86 Hz on a
/// channel that had barely rotated (WN5 Poor burst w0/b3 - dead end-to-end at SER 0.55).
/// The lag-product estimator must read the same window as ≈0 Hz.
/// </summary>
public class Ms110dCarrierFitTests
{
    private const double RadPerGroupPerHz = 2.0 * Math.PI * 32.0 / 2400.0; // 32-chip groups

    [Fact]
    public void Clean_Cfo_Ramp_Is_Recovered()
    {
        var groups = new Cf[40];
        for (int j = 0; j < 40; j++)
        {
            groups[j] = Cf.Cmplx((float)((0.3 * j) + 0.7)) * 5f;
        }

        Ms110dDemodulator.EstimateCarrierFit(groups, out double slope, out double intercept)
            .Should().BeTrue();
        slope.Should().BeApproximately(0.3, 1e-3);
        intercept.Should().BeApproximately(0.7, 1e-3);
    }

    [Fact]
    public void Deep_Fade_Noise_Phases_Do_Not_Tilt_The_Fit()
    {
        // The corpse scenario (autopsy dump wn5-w0-b3, refine@9216): ~20 coherent groups,
        // a fade whose groups carry pure noise phase at ~2% magnitude, then strong
        // coherent groups again at the SAME phase. The old unwrap chain walked ~3.5 turns
        // through the fade and the regression fitted −4.86 Hz; the true rotation across
        // the window was near zero. Faded groups must self-suppress instead.
        var random = new Random(41);
        var groups = new Cf[72];
        for (int j = 0; j < 20; j++)
        {
            groups[j] = Cf.Cmplx(0.9f) * 3f;
        }

        for (int j = 20; j < 35; j++)
        {
            groups[j] = Cf.Cmplx((float)((random.NextDouble() * 2 * Math.PI) - Math.PI)) * 0.05f;
        }

        for (int j = 35; j < 72; j++)
        {
            groups[j] = Cf.Cmplx(0.9f) * 3f;
        }

        Ms110dDemodulator.EstimateCarrierFit(groups, out double slope, out double intercept)
            .Should().BeTrue();
        double hz = slope / RadPerGroupPerHz;
        Math.Abs(hz).Should().BeLessThan(0.3,
            "a fade inside the window must not manufacture CFO (the old fit read −4.86 Hz here)");
        intercept.Should().BeApproximately(0.9, 0.05);
    }

    [Fact]
    public void Fast_Ramp_Resolves_The_Long_Lag_Branch()
    {
        // 0.5 rad/group (≈7.5 Hz): the lag-8 product's raw phase wraps (4.0 rad ≡ −2.28),
        // so the branch must be resolved from the lag-1 estimate. Mild per-group phase
        // noise makes the lag-8 refinement matter without burying the branch decision.
        var random = new Random(43);
        var groups = new Cf[40];
        for (int j = 0; j < 40; j++)
        {
            double noise = 0.1 * ((random.NextDouble() * 2) - 1);
            groups[j] = Cf.Cmplx((float)((0.5 * j) + noise)) * 2f;
        }

        Ms110dDemodulator.EstimateCarrierFit(groups, out double slope, out _)
            .Should().BeTrue();
        slope.Should().BeApproximately(0.5, 0.02,
            "the lag-8 phase must land on the branch nearest the lag-1 estimate");
    }

    [Fact]
    public void An_Empty_Window_Refuses_To_Fit()
    {
        var groups = new Cf[40]; // all zero - no signal at all
        Ms110dDemodulator.EstimateCarrierFit(groups, out _, out _)
            .Should().BeFalse("retuning the carrier on pure noise is worse than keeping it");
    }
}
