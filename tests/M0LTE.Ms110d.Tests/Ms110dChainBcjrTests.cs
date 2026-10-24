using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Component tests for the chain-decomposed exact BCJR (phase-b-plan §B2.2). The central
/// claim - chain decomposition is EXACT MAP under the two-tap model - is pinned by brute
/// force: enumerating every symbol sequence and marginalizing must reproduce the chain
/// LLRs to numerical tolerance, echoes and known preceding symbols included. The other
/// tests pin the analytic BPSK calibration identity (LLR scale, the turbo contract), the
/// Gaussian two-path decode bound of the legacy trellis, and a delay far beyond the
/// 2^delay ceiling the legacy trellis cannot represent (issue #64).
/// </summary>
public class Ms110dChainBcjrTests
{
    private static readonly Complex32[] Bpsk = [new(1, 0), new(-1, 0)];

    // Descrambled-domain QPSK: dibit d → 8PSK ring index {0→0, 1→2, 3→4, 2→6}
    // (Table D-IV), i.e. points {1, +j, −1, −j} carry labels {0, 1, 3, 2}.
    private static readonly Complex32[] Qpsk = [new(1, 0), new(0, 1), new(-1, 0), new(0, -1)];
    private static readonly byte[] QpskLabels = [0, 1, 3, 2];

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private static double LogSumExp(double a, double b)
    {
        if (double.IsNegativeInfinity(a))
        {
            return b;
        }

        double max = Math.Max(a, b);
        return max + Math.Log(Math.Exp(a - max) + Math.Exp(b - max));
    }

    [Fact]
    public void Flat_Channel_Llrs_Match_The_Analytic_Bpsk_Calibration()
    {
        // Same calibration contract as the legacy trellis test: on a flat channel the
        // exact per-symbol LLR is 2·Re(y)/σ² (σ² per complex dimension). With h2 = 0 the
        // chains decouple completely, so exact log-sum-exp equals the two-hypothesis
        // analytic value with no approximation at all.
        const int n = 64;
        const float noiseVar = 0.05f;
        var random = new Random(52);
        var rx = new Complex32[n];
        var h1 = new Complex32[n];
        var h2 = new Complex32[n];
        float sigma = MathF.Sqrt(noiseVar);
        for (int i = 0; i < n; i++)
        {
            h1[i] = new Complex32(1, 0);
            h2[i] = Complex32.Zero;
            float symbol = random.Next(2) == 0 ? 1f : -1f;
            rx[i] = new Complex32(
                symbol + (sigma * (float)Gaussian(random)),
                sigma * (float)Gaussian(random));
        }

        var llrs = new float[n];
        Ms110dChainBcjr.Equalize(rx, h1, h2, delay: 3, noiseVar, Bpsk, [], 1, [], llrs);

        for (int i = 0; i < n; i++)
        {
            float expected = 2f * rx[i].Re / noiseVar;
            llrs[i].Should().BeApproximately(expected, 0.01f * Math.Abs(expected) + 0.01f,
                $"flat-channel exact LLR[{i}] must equal the analytic 2·Re(y)/σ²");
        }
    }

    [Fact]
    public void Per_Symbol_Noise_Floor_Prices_Each_Position_Analytically()
    {
        // §B4.1 per-segment pricing contract: a uniform noiseVarPerSymbol span must
        // reproduce the scalar path bit-for-bit, and a per-position span must price each
        // decoupled flat-channel LLR by its OWN floor (2·Re(y)/σ²[t]).
        const int n = 32;
        const float noiseVar = 0.05f;
        var random = new Random(53);
        var rx = new Complex32[n];
        var h1 = new Complex32[n];
        var h2 = new Complex32[n];
        float sigma = MathF.Sqrt(noiseVar);
        for (int i = 0; i < n; i++)
        {
            h1[i] = new Complex32(1, 0);
            h2[i] = Complex32.Zero;
            float symbol = random.Next(2) == 0 ? 1f : -1f;
            rx[i] = new Complex32(
                symbol + (sigma * (float)Gaussian(random)),
                sigma * (float)Gaussian(random));
        }

        var scalarLlrs = new float[n];
        Ms110dChainBcjr.Equalize(rx, h1, h2, delay: 3, noiseVar, Bpsk, [], 1, [], scalarLlrs);

        var uniform = new float[n];
        Array.Fill(uniform, noiseVar);
        var uniformLlrs = new float[n];
        Ms110dChainBcjr.Equalize(
            rx, h1, h2, delay: 3, noiseVar, Bpsk, [], 1, [], uniformLlrs,
            noiseVarPerSymbol: uniform);
        uniformLlrs.Should().Equal(scalarLlrs, "a uniform span must reproduce the scalar path exactly");

        var perSymbol = new float[n];
        for (int i = 0; i < n; i++)
        {
            perSymbol[i] = i % 2 == 0 ? noiseVar : 4f * noiseVar;
        }

        var pricedLlrs = new float[n];
        Ms110dChainBcjr.Equalize(
            rx, h1, h2, delay: 3, noiseVar, Bpsk, [], 1, [], pricedLlrs,
            noiseVarPerSymbol: perSymbol);
        for (int i = 0; i < n; i++)
        {
            float expected = 2f * rx[i].Re / perSymbol[i];
            pricedLlrs[i].Should().BeApproximately(expected, 0.01f * Math.Abs(expected) + 0.01f,
                $"per-position floor must price LLR[{i}] by its own σ²");
        }
    }

    [Fact]
    public void Chain_Llrs_Match_Brute_Force_Marginalization_On_Qpsk()
    {
        // The exactness proof. n = 6 QPSK symbols, delay 2, complex-valued taps varying
        // per position, KNOWN preceding symbols feeding the first branch of each chain:
        // enumerate all 4^6 sequences, marginalize per bit, compare. Any modelling error -
        // chain split, preceding handling, label mapping, log-sum-exp - breaks this.
        const int n = 6;
        const int delay = 2;
        const float noiseVar = 0.3f;
        var random = new Random(97);
        var h1 = new Complex32[n];
        var h2 = new Complex32[n];
        var rx = new Complex32[n];
        var tx = new int[n];
        Complex32[] preceding = [Qpsk[2], Qpsk[1]]; // x[−2], x[−1]
        float sigma = MathF.Sqrt(noiseVar);
        for (int i = 0; i < n; i++)
        {
            h1[i] = new Complex32(1f - (0.05f * i), 0.2f + (0.03f * i));
            h2[i] = new Complex32(0.5f, -0.25f + (0.04f * i));
            tx[i] = random.Next(4);
            Complex32 echoSym = i >= delay ? Qpsk[tx[i - delay]] : preceding[i];
            Complex32 signal = (h1[i] * Qpsk[tx[i]]) + (h2[i] * echoSym);
            rx[i] = signal + new Complex32(
                sigma * (float)Gaussian(random), sigma * (float)Gaussian(random));
        }

        var llrs = new float[n * 2];
        Ms110dChainBcjr.Equalize(rx, h1, h2, delay, noiseVar, Qpsk, QpskLabels, 2, preceding, llrs);

        // Brute force: joint log-likelihood of every sequence, per-bit marginals.
        var log0 = new double[n * 2];
        var log1 = new double[n * 2];
        Array.Fill(log0, double.NegativeInfinity);
        Array.Fill(log1, double.NegativeInfinity);
        var seq = new int[n];
        for (int code = 0; code < 4096; code++)
        {
            int v = code;
            for (int i = 0; i < n; i++)
            {
                seq[i] = v & 3;
                v >>= 2;
            }

            double logLik = 0;
            for (int i = 0; i < n; i++)
            {
                Complex32 echoSym = i >= delay ? Qpsk[seq[i - delay]] : preceding[i];
                Complex32 expected = (h1[i] * Qpsk[seq[i]]) + (h2[i] * echoSym);
                logLik -= (rx[i] - expected).MagnitudeSquared() / (2.0 * noiseVar);
            }

            for (int i = 0; i < n; i++)
            {
                int label = QpskLabels[seq[i]];
                for (int b = 0; b < 2; b++)
                {
                    int idx = (i * 2) + b;
                    if (((label >> (1 - b)) & 1) == 0)
                    {
                        log0[idx] = LogSumExp(log0[idx], logLik);
                    }
                    else
                    {
                        log1[idx] = LogSumExp(log1[idx], logLik);
                    }
                }
            }
        }

        for (int idx = 0; idx < n * 2; idx++)
        {
            float expected = (float)(log0[idx] - log1[idx]);
            llrs[idx].Should().BeApproximately(expected, 0.02f * Math.Abs(expected) + 0.02f,
                $"chain BCJR bit {idx} must equal brute-force exact marginalization");
        }
    }

    [Fact]
    public void Two_Path_Gaussian_Channel_Decodes_Reliably()
    {
        // The legacy trellis's measured decode bound, reproduced by the chain version on
        // the identical scenario (0.6-amplitude echo, 13 dB Es/N0).
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

        var h1 = new Complex32[n];
        var h2 = new Complex32[n];
        var rx = new Complex32[n];
        float sigma = MathF.Sqrt(noiseVar);
        for (int i = 0; i < n; i++)
        {
            h1[i] = new Complex32(1, 0);
            h2[i] = new Complex32(h2Mag, 0);
            float signal = tx[i] + (i >= delay ? h2Mag * tx[i - delay] : 0f);
            rx[i] = new Complex32(
                signal + (sigma * (float)Gaussian(random)),
                sigma * (float)Gaussian(random));
        }

        var llrs = new float[n];
        Ms110dChainBcjr.Equalize(rx, h1, h2, delay, noiseVar, Bpsk, [], 1, [], llrs);

        int bcjrErrors = 0;
        int isiBlindErrors = 0;
        for (int i = 0; i < n; i++)
        {
            if ((llrs[i] > 0) != (tx[i] > 0))
            {
                bcjrErrors++;
            }

            if ((rx[i].Re > 0) != (tx[i] > 0))
            {
                isiBlindErrors++;
            }
        }

        bcjrErrors.Should().BeLessThanOrEqualTo(4,
            "the chain decomposition must match the legacy trellis's measured decode bound");
        bcjrErrors.Should().BeLessThan(isiBlindErrors,
            "exact MAP must beat the ISI-blind slicer on an ISI channel");
    }

    [Fact]
    public void Echo_Beyond_The_Legacy_Trellis_Ceiling_Decodes()
    {
        // Delay 21 symbols - the D-LXV 9 ms static spread. The legacy trellis would need
        // 2^21 states (issue #64's ceiling capped it at 8); each chain here still has 2.
        const int n = 210;
        const int delay = 21;
        const float h2Mag = 0.7f;
        const float noiseVar = 0.05f;
        var random = new Random(59);
        var tx = new float[n];
        for (int i = 0; i < n; i++)
        {
            tx[i] = random.Next(2) == 0 ? 1f : -1f;
        }

        var h1 = new Complex32[n];
        var h2 = new Complex32[n];
        var rx = new Complex32[n];
        float sigma = MathF.Sqrt(noiseVar);
        for (int i = 0; i < n; i++)
        {
            h1[i] = new Complex32(1, 0);
            h2[i] = new Complex32(h2Mag, 0);
            float signal = tx[i] + (i >= delay ? h2Mag * tx[i - delay] : 0f);
            rx[i] = new Complex32(
                signal + (sigma * (float)Gaussian(random)),
                sigma * (float)Gaussian(random));
        }

        var llrs = new float[n];
        Ms110dChainBcjr.Equalize(rx, h1, h2, delay, noiseVar, Bpsk, [], 1, [], llrs);

        int errors = 0;
        for (int i = 0; i < n; i++)
        {
            if ((llrs[i] > 0) != (tx[i] > 0))
            {
                errors++;
            }
        }

        errors.Should().BeLessThanOrEqualTo(4,
            "a 21-symbol echo must decode as cheaply as a 4-symbol one - the ceiling is gone");
    }
}
