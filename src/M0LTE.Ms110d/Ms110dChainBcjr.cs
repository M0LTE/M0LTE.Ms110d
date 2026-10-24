using System.Buffers;

namespace M0LTE.Ms110d;

/// <summary>
/// Chain-decomposed exact BCJR equalizer for the sparse two-tap channel
/// y[t] = h1[t]·x[t] + h2[t]·x[t−d] (phase-b-plan §B2.2). Because y[t] couples x[t] only
/// to x[t−d] and per-symbol metrics do not cross residue classes, the symbol dependency
/// graph splits into d independent memory-1 chains {c, c+d, c+2d, …}: each chain's BCJR
/// carries M states (M = constellation size) instead of the shift-register trellis's
/// M^delay, so cost is O(n·M²) regardless of the delay - the 2^delay echo ceiling of
/// <see cref="Ms110dBcjr"/> (issue #64) is gone and QPSK/8PSK cost the same per state as
/// BPSK. Exact MAP under the two-tap model: true log-sum-exp, no max-log approximation.
/// The d symbols BEFORE the block (the preceding probe's tail, descrambled) enter as
/// known constants in each chain's first branch metric. Data symbols in the final d
/// positions get no echo observation from the following probe region (their y[t+d] lies
/// outside the block) - the same truncation the shift-register trellis makes.
/// </summary>
internal static class Ms110dChainBcjr
{
    private const float NegInf = -1e30f;

    /// <summary>
    /// Computes exact per-bit LLRs for one data block.
    /// </summary>
    /// <param name="rx">Received (equalized+descrambled domain) samples, one per symbol.</param>
    /// <param name="h1">Main-tap channel gain at each position.</param>
    /// <param name="h2">Echo-tap channel gain at each position.</param>
    /// <param name="delay">Echo delay in symbols (d ≥ 1).</param>
    /// <param name="noiseVar">Noise variance per complex dimension.</param>
    /// <param name="constellation">The M candidate symbols (descrambled domain).</param>
    /// <param name="bitLabels">Per-symbol-index bit label, MSB first (bit b of symbol s is
    /// <c>(bitLabels[s] &gt;&gt; (bits−1−b)) &amp; 1</c>); null means the label IS the
    /// symbol index.</param>
    /// <param name="bitsPerSymbol">Bits per symbol (log2 M).</param>
    /// <param name="preceding">The d known symbols before the block in natural time order
    /// (<c>preceding[k] = x[k − d]</c>); empty treats pre-block echo as absent.</param>
    /// <param name="llrs">Output, length rx.Length·bitsPerSymbol: bit b of symbol t at
    /// [t·bitsPerSymbol + b], positive = bit 0 (the <see cref="Ms110dBcjr"/> convention).</param>
    /// <param name="symbolLogPriors">Optional per-symbol log-priors from the outer code's
    /// extrinsics (§B3.3 turbo priors), log P(x[t] = constellation[s]) at [t·M + s] up to a
    /// per-symbol constant; empty runs prior-free. Each symbol's prior enters its branch
    /// metric exactly once (forward AND backward), so the output LLRs are full posteriors -
    /// the caller subtracts the per-bit prior back out to hand the outer code detector
    /// extrinsics only.</param>
    /// <param name="noiseVarPerSymbol">Optional per-position noise variance (per complex
    /// dimension, length rx.Length) overriding <paramref name="noiseVar"/> - §B4.1
    /// per-segment noise pricing: within fade-crossing frames the residual is
    /// heteroscedastic, and a frame-constant floor over-confidences exactly the spans
    /// where the model is worst. Empty broadcasts the scalar.</param>
    public static void Equalize(
        ReadOnlySpan<Complex32> rx,
        ReadOnlySpan<Complex32> h1,
        ReadOnlySpan<Complex32> h2,
        int delay,
        float noiseVar,
        ReadOnlySpan<Complex32> constellation,
        ReadOnlySpan<byte> bitLabels,
        int bitsPerSymbol,
        ReadOnlySpan<Complex32> preceding,
        Span<float> llrs,
        ReadOnlySpan<float> symbolLogPriors = default,
        ReadOnlySpan<float> noiseVarPerSymbol = default)
    {
        int n = rx.Length;
        int m = constellation.Length;
        bool hasPriors = symbolLogPriors.Length > 0;
        float[] inv2Rented = ArrayPool<float>.Shared.Rent(Math.Max(1, n));
        Span<float> inv2 = inv2Rented.AsSpan(0, Math.Max(1, n));
        if (noiseVarPerSymbol.Length == n && n > 0)
        {
            for (int t = 0; t < n; t++)
            {
                inv2[t] = 1f / (2f * noiseVarPerSymbol[t]);
            }
        }
        else
        {
            inv2.Fill(1f / (2f * noiseVar));
        }

        // Scratch: α for the longest chain, one branch-metric column, one β ping-pong pair.
        int maxLen = ((n + delay) - 1) / delay;
        float[] alpha = ArrayPool<float>.Shared.Rent(maxLen * m);
        float[] beta = ArrayPool<float>.Shared.Rent(2 * m);
        float[] post = ArrayPool<float>.Shared.Rent(m);

        for (int c = 0; c < Math.Min(delay, n); c++)
        {
            int len = ((n - c) + delay - 1) / delay;

            // Forward: α[k][s] includes γ at step k, so the posterior is α + β(exclusive).
            for (int k = 0; k < len; k++)
            {
                int t = c + (k * delay);
                int baseCur = k * m;
                for (int s = 0; s < m; s++)
                {
                    float prior = hasPriors ? symbolLogPriors[(t * m) + s] : 0f;
                    Complex32 main = h1[t] * constellation[s];
                    if (k == 0)
                    {
                        Complex32 expected = preceding.Length > 0 ? main + (h2[t] * preceding[c]) : main;
                        alpha[baseCur + s] = (-(rx[t] - expected).Cnorm() * inv2[t]) + prior;
                        continue;
                    }

                    int basePrev = (k - 1) * m;
                    float acc = NegInf;
                    for (int r = 0; r < m; r++)
                    {
                        Complex32 expected = main + (h2[t] * constellation[r]);
                        float val = alpha[basePrev + r] - ((rx[t] - expected).Cnorm() * inv2[t]);
                        acc = LogSumExp(acc, val);
                    }

                    alpha[baseCur + s] = acc + prior;
                }
            }

            // Backward with on-the-fly posteriors/LLRs, β ping-pong between two columns.
            int cur = 0;
            for (int s = 0; s < m; s++)
            {
                beta[s] = 0f;
            }

            for (int k = len - 1; k >= 0; k--)
            {
                int t = c + (k * delay);
                int baseCur = k * m;
                for (int s = 0; s < m; s++)
                {
                    post[s] = alpha[baseCur + s] + beta[(cur * m) + s];
                }

                for (int b = 0; b < bitsPerSymbol; b++)
                {
                    float log0 = NegInf, log1 = NegInf;
                    for (int s = 0; s < m; s++)
                    {
                        int label = bitLabels.Length > 0 ? bitLabels[s] : s;
                        if (((label >> (bitsPerSymbol - 1 - b)) & 1) == 0)
                        {
                            log0 = LogSumExp(log0, post[s]);
                        }
                        else
                        {
                            log1 = LogSumExp(log1, post[s]);
                        }
                    }

                    llrs[(t * bitsPerSymbol) + b] = log0 - log1;
                }

                if (k == 0)
                {
                    break;
                }

                // β[k−1][r] = logsumexp_s(β[k][s] + γ_k(r, s)); γ carries symbol s's prior.
                int next = 1 - cur;
                for (int r = 0; r < m; r++)
                {
                    Complex32 echo = h2[t] * constellation[r];
                    float acc = NegInf;
                    for (int s = 0; s < m; s++)
                    {
                        Complex32 expected = (h1[t] * constellation[s]) + echo;
                        float val = beta[(cur * m) + s] - ((rx[t] - expected).Cnorm() * inv2[t])
                            + (hasPriors ? symbolLogPriors[(t * m) + s] : 0f);
                        acc = LogSumExp(acc, val);
                    }

                    beta[(next * m) + r] = acc;
                }

                cur = next;
            }
        }

        ArrayPool<float>.Shared.Return(alpha);
        ArrayPool<float>.Shared.Return(beta);
        ArrayPool<float>.Shared.Return(post);
        ArrayPool<float>.Shared.Return(inv2Rented);
    }

    private static float LogSumExp(float a, float b)
    {
        if (a < b)
        {
            (a, b) = (b, a);
        }

        // a ≥ b: a + log(1 + e^{b−a}); the guard keeps e^{b−a} from underflowing to a
        // wasted MathF.Exp call when the difference exceeds float precision anyway.
        float diff = b - a;
        return diff < -20f ? a : a + MathF.Log(1f + MathF.Exp(diff));
    }
}
