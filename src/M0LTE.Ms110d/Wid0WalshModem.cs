
namespace M0LTE.Ms110d;

/// <summary>
/// Waveform-ID 0 Walsh orthogonal data modulation (75 bps): after the preamble, WN 0 sends
/// no mini-probes - each channel symbol is a 32-chip Walsh sequence carrying one coded and
/// interleaved di-bit (D.5.2 final paragraph, <c>docs/dev/ms110d/tables/walsh-data-sequence-prose.md</c>),
/// chip-wise modulo-8 combined with the Trinomial (159, 31) scramble sequence (D.5.1.4).
/// </summary>
/// <remarks>
/// Di-bit order within a channel symbol is not stated by the spec for the data path; the QPSK
/// rule (leftmost = older = fetched first, D.5.1.2.1.2) is adopted - checklist L6, recorded
/// open interpretation. The Table D-XIV map itself is dual-transcribed (10→0044, 11→0440).
/// </remarks>
public sealed class Wid0WalshModem
{
    /// <summary>Chips per WN 0 data channel symbol at 3 kHz.</summary>
    public const int ChipsPerSymbol = 32;

    /// <summary>RAKE finger count for <see cref="DemodulateRake"/>: integer chip delays
    /// −6..+6 - T-spaced (≈ decorrelated under the chip pulse). Symmetric since §B3.5b
    /// rung G: acquisition anchors whichever Poor path is stronger during the preamble
    /// (the echo-lock lottery, ~31% of bursts land on the LATER path), so the other
    /// path's 2 ms (4.8-chip) offset must be covered on BOTH sides - fingers ±(4,5).
    /// The original causal 0..6 window (§B3.5) made echo-locked bursts single-path:
    /// the whole census error tail, 18/18 concordance
    /// (evidence/2026-07-26-phase-b35b-wn0genie Amendment 1).</summary>
    public const int Fingers = 13;

    /// <summary>Fingers at negative (anti-causal) chip delays: finger index k covers
    /// delay k − NegFingers; the chip buffer starts NegFingers chips BEFORE the symbol
    /// boundary.</summary>
    public const int NegFingers = 6;

    /// <summary>Chips <see cref="DemodulateRake"/> consumes per channel symbol: the
    /// 32-chip symbol plus the finger reach on both sides (the buffer spans
    /// [−NegFingers, ChipsPerSymbol + Fingers − 1 − NegFingers) around the symbol).</summary>
    public const int RakeChips = ChipsPerSymbol + Fingers - 1;

    private const float GainAlpha = 1f / 6; // ≈80 ms one-pole - inside the 1 Hz fade coherence time
    private const int WarmupSymbols = 8;    // noncoherent selection while the DD gains are cold

    private readonly Trinomial159Scrambler _scrambler = new();
    private readonly Complex32[] _gains = new Complex32[Fingers];
    private int _symbolsSeen;

    /// <summary>Resets the scramble sequence - call at every interleaver boundary. The
    /// DD finger gains deliberately survive: the channel is continuous across blocks;
    /// only the scrambler restarts (§B3.5).</summary>
    public void Reset()
    {
        _scrambler.Reset();
    }

    /// <summary>Modulates fetched (interleaved) bits - two per channel symbol, first bit =
    /// MSB of the di-bit - into scrambled 8PSK chips
    /// (<paramref name="psk8Chips"/>.Length = fetchedBits.Length × 16).</summary>
    public void Modulate(ReadOnlySpan<byte> fetchedBits, Span<byte> psk8Chips)
    {
        if (fetchedBits.Length % 2 != 0 || psk8Chips.Length != fetchedBits.Length * 16)
        {
            throw new ArgumentException("need whole di-bits and 32 chips per di-bit", nameof(psk8Chips));
        }

        int o = 0;
        for (int n = 0; n < fetchedBits.Length; n += 2)
        {
            int dibit = (fetchedBits[n] << 1) | fetchedBits[n + 1];
            byte[] walsh = Ms110dTables.Walsh[dibit];
            for (int i = 0; i < ChipsPerSymbol; i++)
            {
                psk8Chips[o++] = (byte)((walsh[i & 3] + _scrambler.Next()) & 7);
            }
        }
    }

    /// <summary>Demodulates one received 32-chip channel symbol (complex, carrier-corrected)
    /// into two max-log LLRs (positive ⇒ bit 0; [0] = di-bit MSB). The instance's own
    /// scramble sequence is consumed - feed symbols in order and <see cref="Reset"/> at
    /// interleaver boundaries. Also reports the winning di-bit and its correlation, for
    /// decision-directed carrier tracking.</summary>
    public void Demodulate(ReadOnlySpan<Complex32> chips, Span<float> llrs, out int bestDibit, out Complex32 bestCorrelation)
    {
        if (chips.Length != ChipsPerSymbol || llrs.Length != 2)
        {
            throw new ArgumentException("expected 32 chips and 2 LLRs", nameof(chips));
        }

        // Descramble (rotate each chip by the conjugate of its scramble symbol), then
        // correlate against the four Walsh candidates, whose chips are 0 or 4 → ±1 real.
        Span<Complex32> descrambled = stackalloc Complex32[ChipsPerSymbol];
        for (int i = 0; i < ChipsPerSymbol; i++)
        {
            descrambled[i] = chips[i] * Ms110dTables.Psk8[_scrambler.Next()].Conj();
        }

        Span<Complex32> corr = stackalloc Complex32[4];
        for (int s = 0; s < 4; s++)
        {
            byte[] walsh = Ms110dTables.Walsh[s];
            var acc = Complex32.Zero;
            for (int i = 0; i < ChipsPerSymbol; i++)
            {
                acc = walsh[i & 3] == 0 ? acc + descrambled[i] : acc - descrambled[i];
            }

            corr[s] = acc;
        }

        // Candidate selection is non-coherent (magnitude): it stays reliable while the
        // carrier loop is still pulling in, and the winner's argument is then exactly the
        // phase-error observable the loop needs. The LLRs below stay coherent (Re).
        bestDibit = 0;
        for (int s = 1; s < 4; s++)
        {
            if (corr[s].Cnorm() > corr[bestDibit].Cnorm())
            {
                bestDibit = s;
            }
        }

        bestCorrelation = corr[bestDibit];

        // Max-log per-bit LLRs from the coherent (real) correlations.
        llrs[0] = Math.Max(corr[0].Re, corr[1].Re) - Math.Max(corr[2].Re, corr[3].Re);
        llrs[1] = Math.Max(corr[0].Re, corr[2].Re) - Math.Max(corr[1].Re, corr[3].Re);
    }

    /// <summary>DD-MRC multi-finger demodulation (§B3.5 "Walsh RAKE"): per-finger Walsh
    /// correlations at chip delays −6..+6 (§B3.5b rung G), combined with decision-directed
    /// per-finger channel gains into an MRC statistic whose max-log LLRs are
    /// quadratic-CSI-weighted. <paramref name="chips"/> holds <see cref="RakeChips"/>
    /// carrier-corrected chips starting <see cref="NegFingers"/> chips before the symbol
    /// boundary (finger k's window = chips[k..k+32], delay k − NegFingers); the symbol's
    /// 32 scramble rotors apply to every window. <paramref name="combined"/> is
    /// Σ ĝ*·corr(winner) - real-positive when locked; its argument drives the common-CFO
    /// PLL. <paramref name="maxFingerAbs"/> is max over fingers of |corr(winner)| for the
    /// all-fingers-weak discriminator.</summary>
    public void DemodulateRake(
        ReadOnlySpan<Complex32> chips, Span<float> llrs, out int bestDibit, out Complex32 combined,
        out double maxFingerAbs)
    {
        if (chips.Length != RakeChips || llrs.Length != 2)
        {
            throw new ArgumentException("expected 44 chips and 2 LLRs", nameof(chips));
        }

        // One scramble draw per symbol, applied to every finger's window.
        Span<Complex32> rot = stackalloc Complex32[ChipsPerSymbol];
        for (int i = 0; i < ChipsPerSymbol; i++)
        {
            rot[i] = Ms110dTables.Psk8[_scrambler.Next()].Conj();
        }

        // Per-finger candidate correlations via quarter-phase sums: the four Walsh rows
        // only depend on i mod 4, so q[j] = Σ_{i≡j} descrambled[i] gives every candidate
        // as ±q[0]±q[1]±q[2]±q[3].
        Span<Complex32> corr = stackalloc Complex32[Fingers * 4];
        Span<Complex32> q = stackalloc Complex32[4];
        for (int k = 0; k < Fingers; k++)
        {
            q.Clear();
            for (int i = 0; i < ChipsPerSymbol; i++)
            {
                q[i & 3] += chips[k + i] * rot[i];
            }

            for (int s = 0; s < 4; s++)
            {
                byte[] walsh = Ms110dTables.Walsh[s];
                var acc = Complex32.Zero;
                for (int j = 0; j < 4; j++)
                {
                    acc = walsh[j] == 0 ? acc + q[j] : acc - q[j];
                }

                corr[(k * 4) + s] = acc;
            }
        }

        // MRC decision statistic per candidate; warm-up (cold gains) selects
        // noncoherently on total finger power instead.
        Span<float> d = stackalloc float[4];
        Span<double> power = stackalloc double[4];
        for (int s = 0; s < 4; s++)
        {
            float acc = 0;
            double pow = 0;
            for (int k = 0; k < Fingers; k++)
            {
                Complex32 c = corr[(k * 4) + s];
                acc += (_gains[k].Conj() * c).Re;
                pow += c.Cnorm();
            }

            d[s] = acc;
            power[s] = pow;
        }

        bool warmup = _symbolsSeen < WarmupSymbols;
        bestDibit = 0;
        for (int s = 1; s < 4; s++)
        {
            bool wins = warmup ? power[s] > power[bestDibit] : d[s] > d[bestDibit];
            if (wins)
            {
                bestDibit = s;
            }
        }

        // LLRs always from the MRC statistic: during warm-up they are near-zero
        // (self-erasure - 8 of 576 symbols per block, the code absorbs it).
        llrs[0] = Math.Max(d[0], d[1]) - Math.Max(d[2], d[3]);
        llrs[1] = Math.Max(d[0], d[2]) - Math.Max(d[1], d[3]);

        combined = Complex32.Zero;
        maxFingerAbs = 0;
        for (int k = 0; k < Fingers; k++)
        {
            Complex32 c = corr[(k * 4) + bestDibit];
            combined += _gains[k].Conj() * c;
            double abs = c.Abs();
            if (abs > maxFingerAbs)
            {
                maxFingerAbs = abs;
            }

            _gains[k] += ((c * (1f / ChipsPerSymbol)) - _gains[k]) * GainAlpha;
        }

        _symbolsSeen++;
    }

    /// <summary>§B3.5b genie-gain oracle variant (instrument, never the shipped path -
    /// evidence/2026-07-26-phase-b35b-wn0genie): detection runs on the noisy
    /// <paramref name="chips"/> exactly as <see cref="DemodulateRake"/>, but the finger
    /// gains come from <paramref name="cleanChips"/> - the genie's noise-free copy of the
    /// same realization - correlated against the TRUE Walsh row
    /// (<paramref name="trueDibit"/>): zero additive noise, zero decision errors.
    /// <paramref name="pole"/> false = O-inst (instantaneous truth, no lag, no warm-up);
    /// true = O-pole (the shipped one-pole/α/warm-up with the truth as innovation -
    /// keeps the 80 ms lag, isolating it from the noise+decision component). The
    /// carrier-PLL observable self-cancels under truth gains (registered mechanism
    /// note 1) - the caller skips RetuneCarrier in oracle mode.</summary>
    public void DemodulateRakeOracle(
        ReadOnlySpan<Complex32> chips, ReadOnlySpan<Complex32> cleanChips, int trueDibit, bool pole,
        Span<float> llrs, out int bestDibit, out Complex32 combined, out double maxFingerAbs)
    {
        if (chips.Length != RakeChips || cleanChips.Length != RakeChips || llrs.Length != 2)
        {
            throw new ArgumentException("expected 44 noisy + 44 clean chips and 2 LLRs", nameof(chips));
        }

        Span<Complex32> rot = stackalloc Complex32[ChipsPerSymbol];
        for (int i = 0; i < ChipsPerSymbol; i++)
        {
            rot[i] = Ms110dTables.Psk8[_scrambler.Next()].Conj();
        }

        // Noisy candidate correlations - the same quarter-phase construction as shipped.
        Span<Complex32> corr = stackalloc Complex32[Fingers * 4];
        Span<Complex32> q = stackalloc Complex32[4];
        byte[] trueWalsh = Ms110dTables.Walsh[trueDibit];
        Span<Complex32> truth = stackalloc Complex32[Fingers];
        for (int k = 0; k < Fingers; k++)
        {
            q.Clear();
            var cleanAcc = Complex32.Zero;
            for (int i = 0; i < ChipsPerSymbol; i++)
            {
                q[i & 3] += chips[k + i] * rot[i];
                Complex32 clean = cleanChips[k + i] * rot[i];
                cleanAcc = trueWalsh[i & 3] == 0 ? cleanAcc + clean : cleanAcc - clean;
            }

            truth[k] = cleanAcc * (1f / ChipsPerSymbol);
            for (int s = 0; s < 4; s++)
            {
                byte[] walsh = Ms110dTables.Walsh[s];
                var acc = Complex32.Zero;
                for (int j = 0; j < 4; j++)
                {
                    acc = walsh[j] == 0 ? acc + q[j] : acc - q[j];
                }

                corr[(k * 4) + s] = acc;
            }
        }

        if (!pole)
        {
            // O-inst: the truth IS the gain, warm from symbol 0.
            for (int k = 0; k < Fingers; k++)
            {
                _gains[k] = truth[k];
            }
        }

        Span<float> d = stackalloc float[4];
        Span<double> power = stackalloc double[4];
        for (int s = 0; s < 4; s++)
        {
            float acc = 0;
            double pow = 0;
            for (int k = 0; k < Fingers; k++)
            {
                Complex32 c = corr[(k * 4) + s];
                acc += (_gains[k].Conj() * c).Re;
                pow += c.Cnorm();
            }

            d[s] = acc;
            power[s] = pow;
        }

        // O-pole keeps the shipped warm-up (cold one-pole state); O-inst never needs it.
        bool warmup = pole && _symbolsSeen < WarmupSymbols;
        bestDibit = 0;
        for (int s = 1; s < 4; s++)
        {
            bool wins = warmup ? power[s] > power[bestDibit] : d[s] > d[bestDibit];
            if (wins)
            {
                bestDibit = s;
            }
        }

        llrs[0] = Math.Max(d[0], d[1]) - Math.Max(d[2], d[3]);
        llrs[1] = Math.Max(d[0], d[2]) - Math.Max(d[1], d[3]);

        combined = Complex32.Zero;
        maxFingerAbs = 0;
        for (int k = 0; k < Fingers; k++)
        {
            Complex32 c = corr[(k * 4) + bestDibit];
            combined += _gains[k].Conj() * c;
            double abs = c.Abs();
            if (abs > maxFingerAbs)
            {
                maxFingerAbs = abs;
            }

            if (pole)
            {
                // The shipped update rule and order (use-then-update), truth innovation.
                _gains[k] += (truth[k] - _gains[k]) * GainAlpha;
            }
        }

        _symbolsSeen++;
    }

    /// <summary>Current DD finger-gain magnitudes (diagnostics).</summary>
    public void CopyGainMagnitudes(Span<float> mags)
    {
        for (int k = 0; k < Fingers && k < mags.Length; k++)
        {
            mags[k] = _gains[k].Abs();
        }
    }
}
