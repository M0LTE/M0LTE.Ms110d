using System.Buffers;

namespace M0LTE.Ms110d;

/// <summary>
/// Max-log BCJR equalizer for a 2-path frequency-selective fading channel: the
/// forward-backward recursions on a 2^L-state trellis (L = echo delay in symbols), with
/// max in place of log-sum-exp throughout - the standard max-log approximation, NOT exact
/// MAP (an earlier version of this header claimed exact posteriors; issue #65). LLRs are
/// best-path metric differences: well calibrated when <c>noiseVar</c> is honest, slightly
/// over-confident where competing paths run close.
/// </summary>
internal static class Ms110dBcjr
{
    private const float NegInf = -1e30f;

    /// <summary>
    /// Computes soft LLRs for BPSK symbols on a 2-path channel.
    /// </summary>
    /// <param name="rx">Received complex samples (one per symbol).</param>
    /// <param name="h1">Main path channel gain at each position.</param>
    /// <param name="h2">Delayed path channel gain at each position.</param>
    /// <param name="delay">Delay spread in symbols (L).</param>
    /// <param name="noiseVar">Noise variance (per complex dimension).</param>
    /// <returns>LLR for each symbol (positive = bit 0 = symbol +1).</returns>
    public static float[] Equalize(
        ReadOnlySpan<Complex32> rx, ReadOnlySpan<Complex32> h1, ReadOnlySpan<Complex32> h2, int delay, float noiseVar)
    {
        int n = rx.Length;
        int l = delay;
        int nStates = 1 << l;
        float invTwoSigma2 = 1f / (2f * noiseVar);

        // Forward recursion (log domain). Trellis scratch comes from the shared pool
        // (rented arrays may be oversized - all indexing stays within [0, total)).
        int total = (n + 1) * nStates;
        float[] logAlpha = ArrayPool<float>.Shared.Rent(total);
        Array.Fill(logAlpha, NegInf, 0, total);
        logAlpha[0] = 0f; // start in state 0

        for (int t = 0; t < n; t++)
        {
            int baseCur = t * nStates;
            int baseNext = (t + 1) * nStates;
            for (int s = 0; s < nStates; s++)
            {
                float alpha = logAlpha[baseCur + s];
                if (alpha < NegInf * 0.5f) continue;

                for (int b = 0; b < 2; b++)
                {
                    float xN = b == 0 ? 1f : -1f;
                    float xDelayed = GetDelayedSymbol(s, l, delay);
                    Complex32 yExp = h1[t] * xN + h2[t] * xDelayed;
                    Complex32 diff = rx[t] - yExp;
                    float gamma = -diff.Cnorm() * invTwoSigma2;
                    int nextState = ((s << 1) | b) & (nStates - 1);
                    float val = alpha + gamma;
                    if (val > logAlpha[baseNext + nextState])
                        logAlpha[baseNext + nextState] = val;
                }
            }
        }

        // Backward recursion (log domain)
        float[] logBeta = ArrayPool<float>.Shared.Rent(total);
        Array.Fill(logBeta, NegInf, 0, total);
        for (int s = 0; s < nStates; s++)
            logBeta[n * nStates + s] = 0f;

        for (int t = n - 1; t >= 0; t--)
        {
            int baseCur = t * nStates;
            int baseNext = (t + 1) * nStates;
            for (int s = 0; s < nStates; s++)
            {
                float best = NegInf;
                for (int b = 0; b < 2; b++)
                {
                    float xN = b == 0 ? 1f : -1f;
                    float xDelayed = GetDelayedSymbol(s, l, delay);
                    Complex32 yExp = h1[t] * xN + h2[t] * xDelayed;
                    Complex32 diff = rx[t] - yExp;
                    float gamma = -diff.Cnorm() * invTwoSigma2;
                    int nextState = ((s << 1) | b) & (nStates - 1);
                    float val = logBeta[baseNext + nextState] + gamma;
                    if (val > best) best = val;
                }

                logBeta[baseCur + s] = best;
            }
        }

        // Compute LLRs
        var llrs = new float[n];
        for (int t = 0; t < n; t++)
        {
            int baseCur = t * nStates;
            int baseNext = (t + 1) * nStates;
            float logP0 = NegInf, logP1 = NegInf;

            for (int s = 0; s < nStates; s++)
            {
                float alpha = logAlpha[baseCur + s];
                if (alpha < NegInf * 0.5f) continue;

                for (int b = 0; b < 2; b++)
                {
                    float xN = b == 0 ? 1f : -1f;
                    float xDelayed = GetDelayedSymbol(s, l, delay);
                    Complex32 yExp = h1[t] * xN + h2[t] * xDelayed;
                    Complex32 diff = rx[t] - yExp;
                    float gamma = -diff.Cnorm() * invTwoSigma2;
                    int nextState = ((s << 1) | b) & (nStates - 1);
                    float val = alpha + gamma + logBeta[baseNext + nextState];

                    if (b == 0)
                    {
                        if (val > logP0) logP0 = val;
                    }
                    else
                    {
                        if (val > logP1) logP1 = val;
                    }
                }
            }

            llrs[t] = logP0 - logP1;
        }

        ArrayPool<float>.Shared.Return(logAlpha);
        ArrayPool<float>.Shared.Return(logBeta);
        return llrs;
    }

    private static float GetDelayedSymbol(int state, int l, int delay)
    {
        // The delayed symbol x[n-delay] is at bit position (delay-1) in the state.
        // State bit i represents x[n-1-i]. So x[n-delay] is at position delay-1.
        if (delay > l) return 1f;
        int bit = (state >> (delay - 1)) & 1;
        return bit == 0 ? 1f : -1f;
    }
}
