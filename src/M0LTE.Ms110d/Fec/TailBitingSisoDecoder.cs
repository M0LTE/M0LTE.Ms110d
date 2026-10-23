using System.Numerics;
using M0LTE.Fec;

namespace M0LTE.Ms110d.Fec;

/// <summary>
/// Soft-in/soft-out (log-MAP BCJR) decoder for the Appendix D tail-biting rate-1/2 mother
/// codes - the outer half of the §B3.3 soft-feedback turbo. Where the Viterbi returns hard
/// info bits, this returns a posterior LLR for every <b>coded</b> (mother-lattice) position;
/// the turbo re-punctures and re-interleaves those posteriors into per-symbol soft
/// expectations E[x] for the chain-BCJR re-estimation. LLR convention matches
/// <see cref="TailBitingViterbiDecoder"/>: <b>positive ⇒ bit 0</b>, punctured positions
/// carry LLR 0 on input (their outputs are then the code's own opinion of the erased bit).
/// </summary>
/// <remarks>
/// Trellis convention (must match <see cref="TailBitingEncoder"/> exactly): the K-bit branch
/// register v holds the oldest bit at the MSB and the newest at the LSB, so a branch runs
/// from state v&gt;&gt;1 to state v &amp; (2^(K−1)−1) and emits parity(v &amp; PolyT1/T2).
/// Tail-biting is handled the same way as the Viterbi: circular wrap-extension with
/// W = min(6·K, N) warm-up stages on each end and equal starting metrics, keeping only the
/// middle N stages' outputs. max* uses a 128-entry log(1+e^−d) correction table (step 1/16,
/// clamped at 8 - worst-case error &lt; 0.04, standard log-MAP practice).
/// </remarks>
public sealed class TailBitingSisoDecoder
{
    private readonly ConvolutionalCode _code;
    private readonly byte[] _outputs;
    private float[] _alpha = [];
    private float[] _betaA = [];
    private float[] _betaB = [];

    private const int CorrEntries = 128;
    private const float CorrStep = 1f / 16f;
    private static readonly float[] Corr = BuildCorr();

    /// <summary>Creates a decoder for <paramref name="code"/> (K7 or K9).</summary>
    public TailBitingSisoDecoder(ConvolutionalCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        _code = code;
        _outputs = new byte[1 << code.K];
        for (uint v = 0; v < _outputs.Length; v++)
        {
            int t1 = BitOperations.PopCount(v & code.PolyT1) & 1;
            int t2 = BitOperations.PopCount(v & code.PolyT2) & 1;
            _outputs[v] = (byte)((t1 << 1) | t2);
        }
    }

    /// <summary>Decodes one tail-biting block. <paramref name="motherLlrs"/> is the full
    /// rate-1/2 lattice (2 LLRs per info bit, erasures = 0 where punctured);
    /// <paramref name="motherPosteriors"/> (same length) receives the posterior LLR of
    /// every coded position given the whole block and the code constraint.</summary>
    public void Decode(ReadOnlySpan<float> motherLlrs, Span<float> motherPosteriors)
    {
        int n = motherLlrs.Length / 2;
        if (motherPosteriors.Length != motherLlrs.Length || (motherLlrs.Length & 1) != 0)
        {
            throw new ArgumentException("motherPosteriors must match motherLlrs, both 2*N long");
        }

        if (n < _code.K)
        {
            throw new ArgumentException($"block must be at least K={_code.K} bits", nameof(motherLlrs));
        }

        int states = _code.States;
        int half = states >> 1;
        int wrap = Math.Min(6 * _code.K, n);
        if (_alpha.Length < n * states)
        {
            _alpha = new float[n * states];
            _betaA = new float[states];
            _betaB = new float[states];
        }

        Span<float> g = stackalloc float[4];

        // Forward: α over ext stages [−W, N); α before branch p is stored at _alpha[p·M].
        Span<float> cur = _betaA.AsSpan(0, states);
        Span<float> next = _betaB.AsSpan(0, states);
        cur.Clear();
        for (int i = -wrap; i < n; i++)
        {
            int p = i < 0 ? i + n : i;
            if (i >= 0)
            {
                cur.CopyTo(_alpha.AsSpan(p * states, states));
            }

            BranchMetrics(motherLlrs, p, g);
            float norm = float.MinValue;
            for (int k = 0; k < states; k++)
            {
                float a = cur[k >> 1] + g[_outputs[k]];
                float b = cur[(k >> 1) + half] + g[_outputs[k | states]];
                float m = MaxStar(a, b);
                next[k] = m;
                if (m > norm)
                {
                    norm = m;
                }
            }

            for (int k = 0; k < states; k++)
            {
                next[k] -= norm;
            }

            Span<float> t = cur;
            cur = next;
            next = t;
        }

        // Backward: β over ext stages [N+W−1, 0], emitting posteriors for stages < N.
        // betaAfter[s] is the metric at the boundary AFTER the current branch.
        Span<float> betaAfter = cur;
        Span<float> betaBefore = next;
        betaAfter.Clear();
        for (int i = n + wrap - 1; i >= 0; i--)
        {
            int p = i >= n ? i - n : i;
            BranchMetrics(motherLlrs, p, g);
            if (i < n)
            {
                ReadOnlySpan<float> alpha = _alpha.AsSpan(p * states, states);
                float t10 = float.MinValue, t11 = float.MinValue;
                float t20 = float.MinValue, t21 = float.MinValue;
                for (int v = 0; v < 2 * states; v++)
                {
                    int output = _outputs[v];
                    float m = alpha[v >> 1] + g[output] + betaAfter[v & (states - 1)];
                    if ((output & 2) == 0)
                    {
                        t10 = MaxStar(t10, m);
                    }
                    else
                    {
                        t11 = MaxStar(t11, m);
                    }

                    if ((output & 1) == 0)
                    {
                        t20 = MaxStar(t20, m);
                    }
                    else
                    {
                        t21 = MaxStar(t21, m);
                    }
                }

                motherPosteriors[2 * p] = t10 - t11;
                motherPosteriors[(2 * p) + 1] = t20 - t21;
            }

            float norm = float.MinValue;
            for (int s = 0; s < states; s++)
            {
                int v0 = s << 1;
                float a = g[_outputs[v0]] + betaAfter[v0 & (states - 1)];
                float b = g[_outputs[v0 | 1]] + betaAfter[(v0 | 1) & (states - 1)];
                float m = MaxStar(a, b);
                betaBefore[s] = m;
                if (m > norm)
                {
                    norm = m;
                }
            }

            for (int s = 0; s < states; s++)
            {
                betaBefore[s] -= norm;
            }

            Span<float> t = betaAfter;
            betaAfter = betaBefore;
            betaBefore = t;
        }
    }

    /// <summary>log P(pair) up to a constant: γ = ½·(±L1 ± L2), + for bit 0
    /// (positive-LLR-is-zero convention), indexed by the (t1&lt;&lt;1)|t2 output code.</summary>
    private static void BranchMetrics(ReadOnlySpan<float> motherLlrs, int p, Span<float> g)
    {
        float l1 = 0.5f * motherLlrs[2 * p];
        float l2 = 0.5f * motherLlrs[(2 * p) + 1];
        g[0] = l1 + l2;
        g[1] = l1 - l2;
        g[2] = -l1 + l2;
        g[3] = -l1 - l2;
    }

    private static float MaxStar(float a, float b)
    {
        float m = a > b ? a : b;
        float d = a > b ? a - b : b - a;
        if (float.IsNegativeInfinity(m) || d >= CorrEntries * CorrStep)
        {
            return m;
        }

        return m + Corr[(int)(d * (1f / CorrStep))];
    }

    private static float[] BuildCorr()
    {
        var corr = new float[CorrEntries];
        for (int i = 0; i < CorrEntries; i++)
        {
            corr[i] = MathF.Log(1f + MathF.Exp(-(i * CorrStep)));
        }

        return corr;
    }
}
