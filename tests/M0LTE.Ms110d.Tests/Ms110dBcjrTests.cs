using M0LTE.Ofdm;
using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Component tests for the max-log BCJR equalizer. Rewritten for issue #67: Gaussian noise
/// (the channel model the LLR arithmetic assumes - the old tests used uniform), an ANALYTIC
/// calibration identity that pins the LLR scale (turbo trusts it; the #65 total-vs-per-dim
/// noiseVar bug halved every LLR and this test would have caught it), and a measured
/// ISI-exploitation comparison instead of a loose 80 % bound.
/// </summary>
public class Ms110dBcjrTests
{
    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    [Fact]
    public void Flat_Channel_Llrs_Match_The_Analytic_Bpsk_Calibration()
    {
        // On a flat channel (h2 = 0) the trellis timesteps decouple and max-log equals the
        // exact per-symbol LLR: (|y+1|² − |y−1|²)/(2σ²) = 2·Re(y)/σ² with σ² per complex
        // dimension. This identity IS the calibration contract turbo's tanh(llr/2) soft
        // symbols rely on; a wrong noiseVar convention (issue #65) breaks it by exactly 2×.
        const int n = 64;
        const float noiseVar = 0.05f;
        var random = new Random(52);
        var rx = new Cf[n];
        var h1 = new Cf[n];
        var h2 = new Cf[n];
        float sigma = MathF.Sqrt(noiseVar);
        for (int i = 0; i < n; i++)
        {
            h1[i] = new Cf(1, 0);
            h2[i] = Cf.Zero;
            float symbol = random.Next(2) == 0 ? 1f : -1f;
            rx[i] = new Cf(
                symbol + (sigma * (float)Gaussian(random)),
                sigma * (float)Gaussian(random));
        }

        float[] llrs = Ms110dBcjr.Equalize(rx, h1, h2, delay: 3, noiseVar);

        for (int i = 0; i < n; i++)
        {
            float expected = 2f * rx[i].Re / noiseVar;
            llrs[i].Should().BeApproximately(expected, 0.01f * Math.Abs(expected) + 0.01f,
                $"flat-channel max-log LLR[{i}] must equal the analytic 2·Re(y)/σ²");
        }
    }

    [Fact]
    public void Two_Path_Gaussian_Channel_Decodes_Reliably()
    {
        // 2-path ISI with Gaussian noise at a moderate SNR: the trellis must resolve the
        // echo. Fixed seed; the bound is measured-with-margin, not aspirational.
        const int n = 400;
        const int delay = 4;
        const float h2Mag = 0.6f;
        const float noiseVar = 0.05f;
        var random = new Random(53);
        var tx = new float[n];
        for (int i = 0; i < n; i++)
        {
            tx[i] = random.Next(2) == 0 ? 1f : -1f;
        }

        var h1 = new Cf[n];
        var h2 = new Cf[n];
        var rx = new Cf[n];
        float sigma = MathF.Sqrt(noiseVar);
        for (int i = 0; i < n; i++)
        {
            h1[i] = new Cf(1, 0);
            h2[i] = new Cf(h2Mag, 0);
            float signal = tx[i] + (i >= delay ? h2Mag * tx[i - delay] : 0f);
            rx[i] = new Cf(
                signal + (sigma * (float)Gaussian(random)),
                sigma * (float)Gaussian(random));
        }

        float[] llrs = Ms110dBcjr.Equalize(rx, h1, h2, delay, noiseVar);

        int bcjrErrors = 0;
        int isiBlindErrors = 0;
        for (int i = 0; i < n; i++)
        {
            if ((llrs[i] > 0) != (tx[i] > 0))
            {
                bcjrErrors++;
            }

            // The ISI-blind reference: slice Re(y) directly, echo treated as noise.
            if ((rx[i].Re > 0) != (tx[i] > 0))
            {
                isiBlindErrors++;
            }
        }

        bcjrErrors.Should().BeLessThanOrEqualTo(4,
            "the trellis must resolve a 0.6-amplitude echo at 13 dB Es/N0 (measured 0-1 with margin)");
        bcjrErrors.Should().BeLessThan(isiBlindErrors,
            "BCJR must beat the ISI-blind slicer on an ISI channel - the reason it exists");
    }
}
