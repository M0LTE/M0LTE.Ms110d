using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Targeted tests for the <see cref="Dfe"/> RLS recursion - chiefly the weighted-RLS
/// consistency property (issue #64): row weight must enter the gain denominator and the
/// P update together, or sustained advisory-weight rows freeze adaptation.
/// </summary>
public class DfeTests
{
    private static Complex32[] RandomBpsk(int count, int seed)
    {
        var random = new Random(seed);
        var symbols = new Complex32[count];
        for (int i = 0; i < count; i++)
        {
            symbols[i] = new Complex32(random.Next(2) == 0 ? 1f : -1f, 0);
        }

        return symbols;
    }

    /// <summary>Feeds one RLS update for the trivial identity channel (window carries the
    /// current and previous symbol; the desired output is ±current) and returns the
    /// pre-update error magnitude.</summary>
    private static float Update(Dfe dfe, Complex32 current, Complex32 previous, Complex32 desired, float weight)
    {
        Span<Complex32> window = [current, previous];
        Complex32 y = dfe.RlsUpdate(window, [], desired, weight);
        return (desired - y).Abs();
    }

    [Fact]
    public void Rls_Adaptation_Survives_Sustained_Advisory_Weight()
    {
        // The issue-#64 freeze: converge at full weight, then invert the channel and offer
        // only advisory (0.1) rows. The old recursion scaled the tap step by the weight but
        // applied the full-confidence P update, so P collapsed while the taps barely moved -
        // measured ~0.9 residual error after 150 post-flip rows. Consistent weighted RLS is
        // scale-invariant in the weight, so the advisory rows re-converge just like full ones.
        var dfe = new Dfe(ffTaps: 2, fbTaps: 0);
        dfe.BeginRls(lambda: 0.95f, pInit: 1.0f);
        Complex32[] symbols = RandomBpsk(260, seed: 7);

        for (int i = 1; i < 100; i++)
        {
            Update(dfe, symbols[i], symbols[i - 1], symbols[i], weight: 1f);
        }

        float error = float.NaN;
        for (int i = 100; i < 250; i++)
        {
            error = Update(dfe, symbols[i], symbols[i - 1], symbols[i] * -1f, weight: 0.1f);
        }

        error.Should().BeLessThan(0.05f,
            "advisory-weight rows must keep adapting (weighted-RLS consistency, issue #64)");
    }

    [Fact]
    public void Rls_Uniform_Weight_Is_Scale_Invariant()
    {
        // Least squares is invariant to a uniform row scaling, so once the P0 prior washes
        // out an all-0.1-weight run must track exactly like an all-1.0 run. Drive both with
        // the same slowly-rotating channel: any weight/P inconsistency shows up as a 10×
        // longer tracking lag in the advisory run and the final taps diverge.
        Complex32[] symbols = RandomBpsk(400, seed: 11);
        Complex32[][] finals = new Complex32[2][];
        float[] weights = [1f, 0.1f];
        for (int run = 0; run < 2; run++)
        {
            var dfe = new Dfe(ffTaps: 2, fbTaps: 0);
            dfe.BeginRls(lambda: 0.95f, pInit: 1.0f);
            for (int i = 1; i < 400; i++)
            {
                Complex32 gain = Complex32.Cmplx(0.01f * i);
                Update(dfe, symbols[i], symbols[i - 1], gain * symbols[i], weights[run]);
            }

            finals[run] = dfe.SnapshotTaps();
        }

        for (int t = 0; t < finals[0].Length; t++)
        {
            (finals[0][t] - finals[1][t]).Abs().Should().BeLessThan(0.15f,
                $"tap {t}: uniform weight must not change the RLS trajectory");
        }
    }

    [Fact]
    public void Translate_Taps_Carries_A_Deviation_Across_A_Base_Change()
    {
        // §B2.1a contract: the interpolated base trajectory advances underneath the RLS
        // deviation. taps = base + deviation before the translate must give
        // taps = base' + THE SAME deviation after it.
        var dfe = new Dfe(ffTaps: 2, fbTaps: 1);
        Complex32[] baseA = [new(1f, 0.5f), new(-0.25f, 0f), new(0f, -1f)];
        Complex32[] baseB = [new(0.5f, 0.75f), new(0.25f, -0.5f), new(1f, 1f)];
        Complex32[] deviation = [new(0.1f, -0.2f), new(0f, 0.05f), new(-0.3f, 0f)];

        var withDeviation = new Complex32[3];
        for (int i = 0; i < 3; i++)
        {
            withDeviation[i] = baseA[i] + deviation[i];
        }

        dfe.LoadTaps(withDeviation);
        dfe.TranslateTaps(baseA, baseB);

        Complex32[] result = dfe.SnapshotTaps();
        for (int i = 0; i < 3; i++)
        {
            (result[i] - (baseB[i] + deviation[i])).Abs().Should().BeLessThan(1e-6f,
                $"tap {i}: the deviation must ride the base change unchanged");
        }
    }

    [Fact]
    public void Tap_Trajectory_Preserves_Magnitude_Under_Pure_Rotation()
    {
        // §B2.1a: when the end anchor is the start anchor rotated by φ, the trajectory
        // must rotate at constant magnitude. Chord (plain linear) interpolation dips to
        // cos(φ/2) at midspan - 6 % low at the 40°/frame rotations of the WN7 autopsy,
        // far worse through fade swings - which is exactly why the phase ramp exists.
        Complex32[] start = [new(1f, 0f), new(0.3f, -0.7f)];
        const float phi = 1.2f; // ~69°, a fade-swing-scale rotation
        Complex32 rotor = Complex32.Cmplx(phi);
        // The caller hands TapTrajectory the END taps DE-rotated by φ - for a pure
        // rotation that is the start taps again.
        var trajectory = new Complex32[2];
        Ms110dDemodulator.TapTrajectory(start, start, phi, 0.5f, trajectory);
        for (int i = 0; i < 2; i++)
        {
            trajectory[i].Abs().Should().BeApproximately(start[i].Abs(), 1e-6f,
                $"tap {i}: magnitude must be preserved through the rotation");
            trajectory[i].Arg().Should().BeApproximately(start[i].Arg() + (phi * 0.5f), 1e-5f,
                $"tap {i}: phase must ramp linearly");
        }

        // Endpoint exactness: f = 0 is the start anchor, f = 1 the end anchor.
        Ms110dDemodulator.TapTrajectory(start, start, phi, 0f, trajectory);
        (trajectory[0] - start[0]).Abs().Should().BeLessThan(1e-6f);
        Ms110dDemodulator.TapTrajectory(start, start, phi, 1f, trajectory);
        (trajectory[0] - (start[0] * rotor)).Abs().Should().BeLessThan(1e-6f);
    }

    [Fact]
    public void Rls_Converges_On_The_Identity_Channel()
    {
        var dfe = new Dfe(ffTaps: 2, fbTaps: 0);
        dfe.BeginRls(lambda: 0.95f, pInit: 1.0f);
        Complex32[] symbols = RandomBpsk(120, seed: 3);
        float error = float.NaN;
        for (int i = 1; i < 120; i++)
        {
            error = Update(dfe, symbols[i], symbols[i - 1], symbols[i], weight: 1f);
        }

        error.Should().BeLessThan(1e-3f, "noiseless RLS must converge to the exact solution");
    }

    // ------------------------------------------------------------ §B3.3 TIR shortening solve

    private static Complex32[] RandomPsk8(int count, int seed)
    {
        var random = new Random(seed);
        var symbols = new Complex32[count];
        for (int i = 0; i < count; i++)
        {
            double phi = random.Next(8) * Math.PI / 4;
            symbols[i] = new Complex32((float)Math.Cos(phi), (float)Math.Sin(phi));
        }

        return symbols;
    }

    private static Complex32 Gauss(Random random, float sigma)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        double r = Math.Sqrt(-2.0 * Math.Log(u1)) * sigma;
        return new Complex32(
            (float)(r * Math.Cos(2 * Math.PI * u2)),
            (float)(r * Math.Sin(2 * Math.PI * u2)));
    }

    /// <summary>Accumulates rows for a symbol-spaced channel y[u] = x[u] + g·x[u−5] + n
    /// through a 4-tap window - the lag-5 inverse is IIR (taps at 5, 10, …), so full
    /// inversion is infeasible by construction and the shortened target 1 + c·z⁻⁵ is
    /// exact.</summary>
    private static void AccumulateEchoRows(Dfe dfe, float echoGain, float sigma, int noiseSeed)
    {
        const int N = 400;
        Complex32[] x = RandomPsk8(N, seed: 42);
        var noise = new Random(noiseSeed);
        var y = new Complex32[N];
        for (int u = 0; u < N; u++)
        {
            Complex32 echo = u >= 5 ? x[u - 5] * echoGain : Complex32.Zero;
            y[u] = x[u] + echo + Gauss(noise, sigma);
        }

        dfe.BeginTraining();
        Span<Complex32> window = stackalloc Complex32[dfe.FfTaps];
        Span<Complex32> past = stackalloc Complex32[dfe.FbTaps];
        for (int u = 16; u < N; u++)
        {
            for (int i = 0; i < window.Length; i++)
            {
                window[i] = y[u - i];
            }

            for (int j = 0; j < past.Length; j++)
            {
                past[j] = x[u - 1 - j];
            }

            dfe.AddTrainingRow(window, past, x[u]);
        }
    }

    [Fact]
    public void Tir_Solve_Finds_The_Uninvertible_Echo_And_Leaves_It_At_Its_Lag()
    {
        var dfe = new Dfe(ffTaps: 4, fbTaps: 8);
        AccumulateEchoRows(dfe, echoGain: 0.8f, sigma: 0.05f, noiseSeed: 43);
        Dfe.TirSolve tir = dfe.SolveTrainingTir(regularization: 1e-3f, ffNoisePower: 0f, maxLag: 8);

        tir.Solved.Should().BeTrue();
        tir.Lag.Should().Be(5, "the designed echo lag must win the candidate search");
        Math.Sqrt(tir.Coefficient.Cnorm()).Should().BeApproximately(0.8, 0.05,
            "the post-FF echo coefficient is the channel's echo gain");
        tir.SseTir.Should().BeLessThan(0.2f * tir.SseNull,
            "shortening must beat the truncated-IIR inversion decisively");
    }

    [Fact]
    public void Tir_Solve_Rejects_The_Free_Parameter_On_An_Echo_Free_Channel()
    {
        // Every lag candidate's SSE gain is noise-fit only; the 4·ln(L)·SSE₀/rows margin
        // must keep the null (full-inversion) solve - no fake designed echo.
        var dfe = new Dfe(ffTaps: 4, fbTaps: 8);
        AccumulateEchoRows(dfe, echoGain: 0f, sigma: 0.2f, noiseSeed: 44);
        Dfe.TirSolve tir = dfe.SolveTrainingTir(regularization: 1e-3f, ffNoisePower: 0f, maxLag: 8);

        tir.Solved.Should().BeTrue();
        tir.Lag.Should().Be(0);
        tir.Coefficient.Abs().Should().Be(0f);
    }

    /// <summary>Rows for y[u] = x[u] + gₐ·x[u−5] + g_b·x[u−6] + n - a fractional-delay
    /// echo's straddle pair on the symbol grid (§B3.3 two-adjacent-lag model).</summary>
    private static void AccumulateStraddleEchoRows(
        Dfe dfe, float gainMain, float gainAdjacent, float sigma, int noiseSeed)
    {
        const int N = 400;
        Complex32[] x = RandomPsk8(N, seed: 42);
        var noise = new Random(noiseSeed);
        var y = new Complex32[N];
        for (int u = 0; u < N; u++)
        {
            Complex32 echo = u >= 6 ? (x[u - 5] * gainMain) + (x[u - 6] * gainAdjacent) : Complex32.Zero;
            y[u] = x[u] + echo + Gauss(noise, sigma);
        }

        dfe.BeginTraining();
        Span<Complex32> window = stackalloc Complex32[dfe.FfTaps];
        Span<Complex32> past = stackalloc Complex32[dfe.FbTaps];
        for (int u = 16; u < N; u++)
        {
            for (int i = 0; i < window.Length; i++)
            {
                window[i] = y[u - i];
            }

            for (int j = 0; j < past.Length; j++)
            {
                past[j] = x[u - 1 - j];
            }

            dfe.AddTrainingRow(window, past, x[u]);
        }
    }

    [Fact]
    public void Tir_Pair_Solve_Finds_Both_Straddle_Taps()
    {
        var dfe = new Dfe(ffTaps: 4, fbTaps: 8);
        AccumulateStraddleEchoRows(dfe, gainMain: 0.7f, gainAdjacent: 0.35f, sigma: 0.05f, noiseSeed: 47);
        Dfe.TirSolve tir = dfe.SolveTrainingTir(regularization: 1e-3f, ffNoisePower: 0f, maxLag: 8);

        tir.Solved.Should().BeTrue();
        tir.Lag.Should().Be(5, "the dominant tap wins the single-lag search");
        tir.Lag2.Should().Be(6, "the straddle neighbour must be detected as the second tap");
        Math.Sqrt(tir.Coefficient.Cnorm()).Should().BeApproximately(0.7, 0.05);
        Math.Sqrt(tir.Coefficient2.Cnorm()).Should().BeApproximately(0.35, 0.05);
    }

    [Fact]
    public void Tir_Floating_Refit_Does_Not_Invert_A_Weak_Cursor()
    {
        // §B3.3 eigen-TIR: cursor path faded to 0.3 while the echo path carries 1.0 -
        // the monic solve would boost the FF by ~1/0.3 to manufacture unit cursor gain;
        // the floating refit must instead report the true echo-to-cursor ratio with a
        // matched-filter-scale FF.
        var dfe = new Dfe(ffTaps: 4, fbTaps: 8);
        AccumulateWeakCursorEchoRows(dfe, cursorGain: 0.3f, echoGain: 1.0f, sigma: 0.05f, noiseSeed: 51);
        Dfe.TirSolve tir = dfe.SolveTrainingTir(regularization: 1e-3f, ffNoisePower: 0f, maxLag: 8);

        tir.Solved.Should().BeTrue();
        tir.Lag.Should().Be(5, "the echo is real and must be established by the monic evidence");
        Math.Sqrt(tir.Coefficient.Cnorm()).Should().BeApproximately(1.0 / 0.3, 0.4,
            "the floating refit reports the echo relative to the true faded cursor");
        dfe.FfEnergy.Should().BeLessThan(3f,
            "the unit-norm target must not boost the FF toward inverting the 0.3 cursor");
    }

    /// <summary>Rows for y[u] = g_c·x[u] + g_e·x[u−5] + n - a faded cursor path with a
    /// dominant echo path (§B3.3 eigen-TIR weak-cursor case).</summary>
    private static void AccumulateWeakCursorEchoRows(
        Dfe dfe, float cursorGain, float echoGain, float sigma, int noiseSeed)
    {
        const int N = 400;
        Complex32[] x = RandomPsk8(N, seed: 42);
        var noise = new Random(noiseSeed);
        var y = new Complex32[N];
        for (int u = 0; u < N; u++)
        {
            Complex32 echo = u >= 5 ? x[u - 5] * echoGain : Complex32.Zero;
            y[u] = (x[u] * cursorGain) + echo + Gauss(noise, sigma);
        }

        dfe.BeginTraining();
        Span<Complex32> window = stackalloc Complex32[dfe.FfTaps];
        Span<Complex32> past = stackalloc Complex32[dfe.FbTaps];
        for (int u = 16; u < N; u++)
        {
            for (int i = 0; i < window.Length; i++)
            {
                window[i] = y[u - i];
            }

            for (int j = 0; j < past.Length; j++)
            {
                past[j] = x[u - 1 - j];
            }

            dfe.AddTrainingRow(window, past, x[u]);
        }
    }

    [Fact]
    public void Tir_Pair_Is_Rejected_When_The_Echo_Is_A_Single_Lag()
    {
        // The extra parameter's noise-only gain must not clear the 4·SSE₀/rows margin -
        // a clean single-lag echo keeps Lag2 = 0 and the cancellation path stays off.
        var dfe = new Dfe(ffTaps: 4, fbTaps: 8);
        AccumulateEchoRows(dfe, echoGain: 0.8f, sigma: 0.05f, noiseSeed: 43);
        Dfe.TirSolve tir = dfe.SolveTrainingTir(regularization: 1e-3f, ffNoisePower: 0f, maxLag: 8);

        tir.Lag.Should().Be(5);
        tir.Lag2.Should().Be(0);
        tir.Coefficient2.Abs().Should().Be(0f);
    }

    [Fact]
    public void Tir_Null_Candidate_Matches_The_Plain_Training_Solve()
    {
        // With no feedback columns the TIR method degenerates to the FF-only system that
        // SolveTraining solves - same Gram, same ridge scale, same Cholesky.
        var plain = new Dfe(ffTaps: 4, fbTaps: 0);
        var tir = new Dfe(ffTaps: 4, fbTaps: 0);
        AccumulateEchoRows(plain, echoGain: 0.5f, sigma: 0.1f, noiseSeed: 45);
        AccumulateEchoRows(tir, echoGain: 0.5f, sigma: 0.1f, noiseSeed: 45);

        plain.SolveTraining(regularization: 1e-3f, anchorToCurrentTaps: true).Should().BeTrue();
        Dfe.TirSolve result = tir.SolveTrainingTir(regularization: 1e-3f, ffNoisePower: 0f, maxLag: 0);

        result.Solved.Should().BeTrue();
        result.Lag.Should().Be(0);
        Complex32[] expected = plain.SnapshotTaps();
        Complex32[] actual = tir.SnapshotTaps();
        for (int i = 0; i < expected.Length; i++)
        {
            (expected[i] - actual[i]).Abs().Should().BeLessThan(1e-6f, $"tap {i}");
        }
    }

    [Fact]
    public void Soft_Variance_Rows_Reduce_To_Hard_Rows_At_Zero_And_Shrink_The_Echo_Above_It()
    {
        // The EM diagonal: zero past-variance must be bit-equivalent to the hard overload;
        // real variance grows the echo column's Gram diagonal, shrinking the coefficient
        // toward the prior exactly as an uncertain regressor should.
        var hard = new Dfe(ffTaps: 4, fbTaps: 8);
        var zero = new Dfe(ffTaps: 4, fbTaps: 8);
        var soft = new Dfe(ffTaps: 4, fbTaps: 8);
        AccumulateEchoRows(hard, echoGain: 0.8f, sigma: 0.05f, noiseSeed: 46);

        const int N = 400;
        Complex32[] x = RandomPsk8(N, seed: 42);
        var noiseZ = new Random(46);
        var y = new Complex32[N];
        for (int u = 0; u < N; u++)
        {
            Complex32 echo = u >= 5 ? x[u - 5] * 0.8f : Complex32.Zero;
            y[u] = x[u] + echo + Gauss(noiseZ, 0.05f);
        }

        zero.BeginTraining();
        soft.BeginTraining();
        Span<Complex32> window = stackalloc Complex32[4];
        Span<Complex32> past = stackalloc Complex32[8];
        Span<float> noVar = stackalloc float[8];
        Span<float> bigVar = stackalloc float[8];
        bigVar.Fill(0.5f);
        for (int u = 16; u < N; u++)
        {
            for (int i = 0; i < 4; i++)
            {
                window[i] = y[u - i];
            }

            for (int j = 0; j < 8; j++)
            {
                past[j] = x[u - 1 - j];
            }

            zero.AddTrainingRow(window, past, noVar, x[u]);
            soft.AddTrainingRow(window, past, bigVar, x[u]);
        }

        Dfe.TirSolve hardResult = hard.SolveTrainingTir(1e-3f, 0f, maxLag: 8);
        Dfe.TirSolve zeroResult = zero.SolveTrainingTir(1e-3f, 0f, maxLag: 8);
        Dfe.TirSolve softResult = soft.SolveTrainingTir(1e-3f, 0f, maxLag: 8);

        zeroResult.Lag.Should().Be(hardResult.Lag);
        (zeroResult.Coefficient - hardResult.Coefficient).Abs().Should().BeLessThan(1e-6f);
        softResult.Lag.Should().Be(5, "a real echo must survive honest regressor uncertainty");
        softResult.Coefficient.Abs().Should().BeLessThan(hardResult.Coefficient.Abs(),
            "variance on the echo column must shrink the coefficient");
    }
}
