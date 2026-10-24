using System.Numerics;
using M0LTE.Fec;
using M0LTE.Ms110d.Fec;

namespace M0LTE.Ms110d;

/// <summary>
/// The MFB-form block decoder for QAM16 (wn8-program W5b2, evidence
/// 2026-07-31-wn8-w5a/-w5b1/-w5b2): the measured successor to the §B3.4 turbo
/// exclusion - and, since the Poor-gate successor program's G1d (evidence
/// 2026-08-20-poorgate-g1/-g1d), the second member of the 8PSK per-block ensemble,
/// where it runs beside the DFE-chain path and <see cref="Price"/> arbitrates. Per burst, a ridged wide LS over the first block's probes accumulates a
/// per-tap energy profile (phases decorrelate across fading, delays do not) and places
/// a 26-tap detection window - acquisition centres the chip clock on whichever path
/// wins the preamble (the §B3.5b phenomenon, measured at WN8 on the disjoint
/// specimen), so the window cannot be fixed. Per block: label-free per-probe
/// composite-FIR anchors on ISI-clean probe interiors, per-tap linear interpolation,
/// per-symbol matched projection priced by the believed Gram, and the measured
/// schedule - SISO-soft cancellation rungs (wrong decisions self-attenuate, the §B3.3
/// lesson in this architecture), one convergence-gated decision-directed anchor
/// re-fit (mid-frame anchors from the loop's own decisions; the gate is the W5b1
/// label-quality condition - an ungated re-fit on a still-poor block poisons it), then
/// hard-cancellation rungs to an EXACT fixed point. No fixed point by the cap reverts
/// to the first-pass decode (the §B3.3 revert principle). Constants are structural or
/// cap-class (support arithmetic, percent-scale gates, wall-clock caps), documented in
/// place; the W5c off-rig direction check measures them off the D.6.1 geometry.
/// </summary>
internal sealed class Ms110dMfbBlockDecoder
{
    // Wide delay-profile scan bounds and the placed-window width, T/2 half-chips:
    // ±(2.3 ms spread + raised-cosine tails) at 4800 Hz - support arithmetic, not
    // tuning. 38 ISI-clean rows per probe against 26 unknowns.
    private const int ScanMin = -18;
    private const int ScanMax = 19;
    private const int ScanLen = ScanMax - ScanMin;
    private const int WinLen = 26;

    // Schedule caps (cap-class: they trade wall-clock, never correctness - the exact
    // fixed point / cycle-accept / revert triple does the real termination) and the
    // re-fit gate: the W5b2 corpse measured refit-good handovers at ≤2% decode churn
    // and the W5b1 poison case at ~30% - 5% sits in the measured gap with margin on
    // both sides.
    private const int SoftCap = 30;
    // G2f: a soft-phase fixed point is honoured as the handover only from this rung. With
    // the MMSE cold rung the hard decode stabilises by rung 4-7, while the SISO posteriors
    // the re-fit is trained on are still sharpening; a re-fit taken ten rungs in finishes
    // the blocks that one taken at the first fixed point leaves at 15-62 errors (evidence
    // 2026-08-20-poorgate-g2, G2e: identical results for re-fits anywhere from rung 10 to
    // 40). Cap-class: clean blocks pay at most five extra soft rungs.
    private const int SoftMinRungs = 10;
    // G2f: the soft phase hands over when its reconstruction residual has stopped falling
    // (no improvement on the best so far by more than SoftPlateauTolerance for
    // SoftPlateauRungs consecutive rungs, from SoftMinRungs) rather than only at the cap
    // or at a fixed point - an oscillating block (the G2e stragglers) otherwise spends the
    // cap oscillating and hands its worst soft decisions to the re-fit. Cap-class: it
    // trades soft rungs that were not helping for a hard tail that starts earlier (the
    // prototype's soft-to-10 schedule, measured 34 on the block the cap left at 62).
    private const int SoftPlateauRungs = 3;
    private const double SoftPlateauTolerance = 1e-3;
    // 72: the W5b2 corpse measured the three deep-fade-lottery blocks (canonical
    // b3/b6, disjoint b4) needing ~45-60 rungs while 20/22 blocks converge by ~15-35;
    // at 48 exactly those three cap-reverted. The §B3.4 Amendment 3 asymmetry argument
    // applies verbatim: a cap revert throws away a repaired block, extra rungs only
    // cost wall-clock on the rare stragglers.
    private const int TotalCap = 72;
    private const double RefitChurnGate = 0.05;

    private readonly Ms110dMode _mode;
    private readonly Ms110dInterleaverParams _il;
    private readonly ConvolutionalCode _code;
    private readonly PunctureSpec _puncture;
    private readonly Ms110dInterleaver _interleaver;
    private readonly TailBitingViterbiDecoder _viterbi;
    private readonly TailBitingSisoDecoder _siso;

    // The symbol map (G1d: modulation-generic, the prototype's form). A fetched number
    // is _bps bits MSB-first; the per-frame scramble sequence comes from the register
    // reset at each data frame (D.5.1.3, identical every frame): QAM16 XORs the nibble
    // (NextQam), 8PSK transcodes the tribit through Table D-V and adds the scrambler
    // tribit modulo 8 (NextPsk), exactly as Ms110dModulator does. The QAM16 arithmetic
    // is unchanged operation for operation (the W5b2 numbers reproduce byte-identically).
    private readonly bool _qam;
    private readonly int _bps;
    private readonly int _points;
    private readonly int[] _scr;
    private readonly byte[] _transcode8 = Ms110dTables.Transcode8Psk.ToArray();
    private readonly Complex[] _ring;
    private readonly Complex[] _decisions;
    private readonly Complex[] _proj;
    private readonly double[] _gramT;
    private readonly long[] _dataChip;
    private readonly float[] _llrs;
    private readonly byte[] _dec;
    private readonly byte[] _prevDec;
    private readonly byte[] _prevDec2;
    private readonly float[] _softPunctured;
    private readonly float[] _softMother;
    private readonly float[] _softMotherPost;
    private readonly float[] _softWire;
    private readonly List<double> _anchorChip = new();
    private readonly List<Complex[]> _anchorH = new();
    private readonly List<double> _probeAnchorChip = new();
    private readonly List<Complex[]> _probeAnchorH = new();
    private readonly Complex[,] _gram;
    private readonly Complex[] _rhs;
    private readonly Complex[] _phiRow;
    private readonly double[] _metric = new double[16];
    private readonly double[] _p0 = new double[4];

    private bool _windowSet;
    private int _lMin;

    // G2d: the MMSE cold rung (Poor-gate successor program, evidence 2026-08-20-poorgate-g2).
    // Per-symbol linear MMSE over the response window at the uncancelled rung, known
    // probes subtracted exactly, neighbouring data chips as Es-power unknowns, the
    // anchor-fit residual as the label-free noise estimate. Shipped for QAM16 at G2d and
    // for 8PSK at G4 (2026-08-21), where it is the ensemble's second member.
    private readonly double _es;
    private readonly Complex[,] _rMat = new Complex[WinLen, WinLen];
    private readonly Complex[] _zVec = new Complex[WinLen];
    private readonly Complex[] _hCol = new Complex[WinLen];
    private readonly Complex[] _hNb = new Complex[WinLen];
    private readonly Complex[] _hShift = new Complex[WinLen];
    private double _anchorResidSum;
    private long _anchorResidRows;

    // Which mini-probe sits at anchor p of a block (D.5.2.2): the probe PRECEDING frame p
    // (p < frames) follows frame p - 1, and anchor p == frames is the probe FOLLOWING the
    // last frame. Whether that probe is cyclically shifted is the burst's probe pattern
    // (ProbePattern: the current rule or the standard one, whichever the demodulator's
    // per-probe scoring has found), asked by absolute frame number. The preamble-ending
    // probe (frame -1) is never shifted, which is what makes block 0's preceding probe
    // unshifted even with one frame per block.
    private bool ProbeIsBoundary(int p, int frames)
    {
        long after = _blockBase + (p < frames ? p - 1 : frames - 1);
        return _pattern.Boundary(after);
    }

    private ProbePattern _pattern = new();
    private long _blockBase;

    public Ms110dMfbBlockDecoder(
        Ms110dMode mode, Ms110dInterleaverParams il, ConvolutionalCode code,
        PunctureSpec puncture, Ms110dInterleaver interleaver,
        TailBitingViterbiDecoder viterbi, TailBitingSisoDecoder siso)
    {
        _mode = mode;
        _il = il;
        _code = code;
        _puncture = puncture;
        _interleaver = interleaver;
        _viterbi = viterbi;
        _siso = siso;

        int u = mode.U;
        int k = mode.K;
        int spanHc = (2 * (((il.Frames - 1) * (u + k)) + u + (2 * k))) + (2 * (ScanMax - ScanMin));
        _ring = new Complex[spanHc];
        _decisions = new Complex[il.Frames * u];
        _proj = new Complex[il.Frames * u];
        _gramT = new double[il.Frames * u];
        _dataChip = new long[il.Frames * u];
        _llrs = new float[il.SizeBits];
        _dec = new byte[il.InputBits];
        _prevDec = new byte[il.InputBits];
        _prevDec2 = new byte[il.InputBits];
        _softPunctured = new float[il.SizeBits];
        _softMother = new float[2 * il.InputBits];
        _softMotherPost = new float[2 * il.InputBits];
        _softWire = new float[il.SizeBits];
        _gram = new Complex[WinLen, WinLen];
        _rhs = new Complex[WinLen];
        _phiRow = new Complex[WinLen];

        _qam = mode.Modulation switch
        {
            Ms110dModulation.Qam16 => true,
            Ms110dModulation.Psk8 => false,
            _ => throw new ArgumentException("the MFB-form decoder speaks QAM16 and 8PSK", nameof(mode)),
        };
        _bps = mode.BitsPerSymbol;
        _points = 1 << _bps;
        _scr = new int[u];
        var scrambler = new Ms110dScrambler();
        scrambler.Reset();
        for (int i = 0; i < u; i++)
        {
            _scr[i] = _qam ? scrambler.NextQam(0, 4) : scrambler.NextPsk(0);
        }

        double es = 0;
        for (int q = 0; q < _points; q++)
        {
            Complex pt = Point(q);
            es += (pt.Real * pt.Real) + (pt.Imaginary * pt.Imaginary);
        }

        _es = es / _points;
    }

    private Complex Wire(int number, int u)
    {
        Complex32 w = _qam
            ? Ms110dTables.Qam16[number ^ _scr[u]]
            : Ms110dTables.Psk8[(_transcode8[number] + _scr[u]) & 7];
        return new Complex(w.Re, w.Im);
    }

    private Complex Point(int q)
    {
        Complex32 w = _qam ? Ms110dTables.Qam16[q] : Ms110dTables.Psk8[q];
        return new Complex(w.Re, w.Im);
    }

    private void SetDecisionsFrom(ReadOnlySpan<byte> info)
    {
        byte[] fetched = Ms110dFraming.EncodeBlock(_code, _puncture, _interleaver, info);
        int u = _mode.U;
        int bit = 0;
        for (int d = 0; d < _decisions.Length; d++)
        {
            int number = 0;
            for (int i = 0; i < _bps; i++)
            {
                number = (number << 1) | fetched[bit++];
            }

            _decisions[d] = Wire(number, d % u);
        }
    }

    /// <summary>The decoder's final iterate for the last block, fixed point or not -
    /// the ensemble candidate <see cref="Price"/> arbitrates.</summary>
    public void LastDecode(Span<byte> dst)
    {
        _dec.AsSpan().CopyTo(dst);
    }

    /// <summary>The label-free price of a block decode: re-encode it to wire symbols,
    /// reconstruct the last block's span through its final anchor set, and return the
    /// mean squared residual per complex sample over the data region - the same
    /// likelihood proxy the cycle-accept selection uses, offered to a decode from
    /// outside (G1c measured it choosing the zero-error decode on every contested
    /// block between this decoder and the DFE-chain path).</summary>
    public double Price(ReadOnlySpan<byte> info, IReadOnlyList<long> frameChips)
    {
        SetDecisionsFrom(info);
        return ReconResidual(frameChips, _lastHc0, _lastN, _lMin);
    }

    /// <summary>The number of complex samples a <see cref="Price"/> averages over (the
    /// data region of the last block, in T/2 rows) - what turns a residual ratio into a
    /// Gaussian log-likelihood difference.</summary>
    public long PriceRows => 2 * ((_il.Frames - 1) * (_mode.U + _mode.K) + _mode.U);

    private long _lastHc0;
    private int _lastN;
    private bool _lastCycleAccepted;
    private readonly List<byte[]> _decPool = new();

    /// <summary>Clears the per-burst window state (a new burst may lock on the other
    /// path - the window must be re-scanned).</summary>
    public void ResetBurst()
    {
        _windowSet = false;
    }

    /// <summary>Decodes one QAM16 or 8PSK block in place. Returns true when the schedule
    /// reached an exact fixed point (the decode is <paramref name="info"/>); false
    /// means no fixed point by the cap and the caller must keep its first-pass decode
    /// (the revert principle).</summary>
    public bool DecodeBlock(Ms110dDemodulator demod, IReadOnlyList<long> frameChips, byte[] info)
    {
        int u = _mode.U;
        int k = _mode.K;
        int frames = _il.Frames;
        Action<string>? diag = demod.FrameDiagnosticsForInstruments;

        // Pull the block's ring span through the shipped CFO/timing-corrected read.
        long hc0 = (2 * (frameChips[0] - k)) + (2 * ScanMin);
        long hcEnd = (2 * (frameChips[frames - 1] + u + k)) + (2 * ScanMax);
        int n = (int)(hcEnd - hc0);
        _lastHc0 = hc0;
        _lastN = n;
        _pattern = demod.ProbePatternForBlocks;
        _blockBase = (long)demod.BlockIndex * frames;
        for (long hc = hc0; hc < hcEnd; hc++)
        {
            Complex32 v = demod.RingReadT2(hc);
            _ring[hc - hc0] = new Complex(v.Re, v.Im);
        }

        for (int f = 0; f < frames; f++)
        {
            for (int i = 0; i < u; i++)
            {
                _dataChip[(f * u) + i] = frameChips[f] + i;
            }
        }

        if (!_windowSet)
        {
            ScanWindow(frameChips, hc0);
            _windowSet = true;
        }

        int lMin = _lMin;
        int lMax = lMin + WinLen;

        // Probe anchors: the preceding probe of every frame plus the following probe
        // of the last frame, each an LS over its ISI-clean interior.
        _probeAnchorChip.Clear();
        _probeAnchorH.Clear();
        _anchorResidSum = 0;
        _anchorResidRows = 0;
        for (int p = 0; p <= frames; p++)
        {
            bool preceding = p < frames;
            long ps = preceding ? frameChips[p] - k : frameChips[frames - 1] + u;
            Complex32[] probe = MiniProbe.Get(k, ProbeIsBoundary(p, frames));
            ClearNormal(WinLen);
            long rowLo = (2 * ps) + lMax;
            long rowHi = (2 * (ps + k)) + lMin;
            for (long hc = rowLo; hc < rowHi; hc++)
            {
                for (int i = 0; i < WinLen; i++)
                {
                    long src = hc - (lMin + i);
                    if ((src & 1) != 0)
                    {
                        _phiRow[i] = Complex.Zero;
                        continue;
                    }

                    Complex32 x = probe[(src / 2) - ps];
                    _phiRow[i] = new Complex(x.Re, x.Im);
                }

                AccumulateRow(_ring[hc - hc0], WinLen);
            }

            if (SolveRidged(WinLen, 1e-3))
            {
                _probeAnchorChip.Add(ps + (k / 2.0));
                _probeAnchorH.Add((Complex[])_rhs.Clone());

                // The anchor-fit residual on its own rows: the label-free noise estimate
                // the MMSE cold rung prices with (the prototype's sigmaAnchor).
                for (long hc = rowLo; hc < rowHi; hc++)
                {
                    Complex model = Complex.Zero;
                    for (int i = 0; i < WinLen; i++)
                    {
                        long src = hc - (lMin + i);
                        if ((src & 1) != 0)
                        {
                            continue;
                        }

                        Complex32 x = probe[(src / 2) - ps];
                        model += _rhs[i] * new Complex(x.Re, x.Im);
                    }

                    Complex d = _ring[hc - hc0] - model;
                    _anchorResidSum += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                    _anchorResidRows++;
                }
            }
        }

        RebuildAnchors(midFrame: false, frameChips);

        // The measured schedule, with the W6a diversity fallback: the soft-first and
        // hard-first basins differ per block (the W5b1 schedule table - hard solved
        // blocks soft left, and vice versa), so a block the shipped soft-first
        // schedule cannot terminate reruns once hard-first before the revert. Fires
        // only where the alternative is a coin-flip block.
        bool converged = RunSchedule(softFirst: true, frameChips, hc0, n, lMin, diag);
        if (!converged)
        {
            RebuildAnchors(midFrame: false, frameChips); // drop attempt-0 refit anchors
            converged = RunSchedule(softFirst: false, frameChips, hc0, n, lMin, diag);
        }
        else if (_lastCycleAccepted)
        {
            // G2f: a cycle-accepted decode is a verdict under one schedule, not the block's
            // last word - the hard-first schedule finds exact fixed points on blocks the
            // soft-first one cycles on (measured on the G2e stragglers). Run it; an exact
            // fixed point wins outright; two cycle members are priced through the SAME
            // model (probe anchors only - residuals through different re-fit models are
            // not comparable, the G2e banked negative) and the lower residual is kept.
            if (_decPool.Count == 0)
            {
                _decPool.Add(new byte[_il.InputBits]);
            }

            byte[] decA = _decPool[0];
            Array.Copy(_dec, decA, _dec.Length);
            RebuildAnchors(midFrame: false, frameChips);
            bool convergedB = RunSchedule(softFirst: false, frameChips, hc0, n, lMin, diag);
            if (!convergedB)
            {
                Array.Copy(decA, _dec, _dec.Length);
            }
            else if (_lastCycleAccepted)
            {
                RebuildAnchors(midFrame: false, frameChips);
                SetDecisionsFrom(_dec);
                double priceB = ReconResidual(frameChips, hc0, n, lMin);
                SetDecisionsFrom(decA);
                double priceA = ReconResidual(frameChips, hc0, n, lMin);
                diag?.Invoke(FormattableString.Invariant(
                    $"mfb-dual block cycle/cycle priceSoftFirst={priceA:E4} priceHardFirst={priceB:E4} keep={(priceB < priceA ? "hard-first" : "soft-first")}"));
                if (priceB >= priceA)
                {
                    Array.Copy(decA, _dec, _dec.Length);
                }
            }
            else
            {
                diag?.Invoke("mfb-dual block cycle/fixed-point keep=hard-first");
            }
        }

        if (converged)
        {
            Array.Copy(_dec, info, info.Length);
        }

        return converged;
    }

    private bool RunSchedule(
        bool softFirst, IReadOnlyList<long> frameChips,
        long hc0, int n, int lMin, Action<string>? diag)
    {
        bool haveDecisions = false;
        bool converged = false;
        bool cycleAccepted = false;
        bool refitDone = false;
        bool refitApplied = false;
        int handoverChurn = -1;
        int rung = 0;
        int lastChurn = int.MaxValue;
        int hardStart = softFirst ? int.MaxValue : 0; // the first hard-phase rung (G2f / #316)
        double bestSoftSigma = double.MaxValue;
        int plateauRungs = 0;
        bool plateau = false;
        for (; rung <= TotalCap; rung++)
        {
            bool softPhase = softFirst && rung < SoftCap && !refitDone && !plateau;
            if (!softPhase && rung < hardStart)
            {
                hardStart = rung;
            }

            if (!softPhase && !refitDone && (rung >= SoftCap || plateau))
            {
                refitDone = true; // handover - re-fit first if the gate admits it
                handoverChurn = lastChurn;
                if (haveDecisions && lastChurn <= (int)(RefitChurnGate * _il.InputBits))
                {
                    RefitAnchors(frameChips, hc0);
                    RebuildAnchors(midFrame: true, frameChips);
                    refitApplied = true;
                }
            }

            Project(frameChips, hc0, n, lMin, haveDecisions);
            if (softPhase && haveDecisions)
            {
                // Soft-phase plateau watch on the reconstruction residual just priced.
                if (_sigma2 < bestSoftSigma * (1 - SoftPlateauTolerance))
                {
                    bestSoftSigma = _sigma2;
                    plateauRungs = 0;
                }
                else if (rung >= SoftMinRungs && ++plateauRungs >= SoftPlateauRungs)
                {
                    plateau = true; // next rung hands over through the cap path below
                }
            }

            BuildLlrs(lMin);
            Array.Copy(_prevDec, _prevDec2, _prevDec.Length);
            Array.Copy(_dec, _prevDec, _dec.Length);
            Ms110dFraming.DecodeBlock(_viterbi, _puncture, _interleaver, _llrs, _dec);
            int churn = 0;
            if (rung > 0)
            {
                for (int i = 0; i < _dec.Length; i++)
                {
                    churn += _dec[i] != _prevDec[i] ? 1 : 0;
                }
            }
            else
            {
                churn = _dec.Length;
            }

            lastChurn = churn;
            diag?.Invoke(FormattableString.Invariant(
                $"mfb-rung r{rung} churn={churn} soft={(softPhase ? 1 : 0)} sigma={_sigma2:E2}"));
            if (rung > 0 && churn == 0)
            {
                if (softPhase && rung < SoftMinRungs)
                {
                    // Stable as a hard decode, not yet saturated as soft posteriors: keep
                    // the soft rungs going so the re-fit below trains on sharper rows.
                    SetDecisionsSoft();
                    haveDecisions = true;
                    continue;
                }

                if (softPhase)
                {
                    // A stable soft decode is a handover signal, not the final fixed
                    // point - force the re-fit + hard tail to confirm it exactly.
                    refitDone = true;
                    handoverChurn = lastChurn;
                    if (lastChurn <= (int)(RefitChurnGate * _il.InputBits))
                    {
                        RefitAnchors(frameChips, hc0);
                        RebuildAnchors(midFrame: true, frameChips);
                        refitApplied = true;
                    }

                    SetDecisionsHard();
                    haveDecisions = true;
                    continue;
                }

                converged = true;
                break;
            }

            // An exact period-2 limit cycle in the hard tail: two near-identical
            // decodes swapping forever (measured on the deep-fade-lottery blocks -
            // b3 canonical cycles at churn 53 with σ² alternating by 0.4%). Accept the
            // cycle member whose reconstruction explains the ring better - a
            // label-free likelihood selection. The §B3.4 confident-wrong attractor
            // cannot satisfy decode == decode-two-rungs-ago exactly, so the revert
            // protection stands for genuine wander.
            // G2f (#316, QAM16 scope): both cycle members must be hard-phase decodes - at
            // the handover rung _prevDec2 is still a soft-phase decode, and accepting a
            // soft oscillation there held the WN8 stragglers at 15-62 errors (evidence
            // 2026-08-20-poorgate-g2). 8PSK keeps the W5b2 behaviour until its own leg.
            bool membersHard = rung >= hardStart + 2; // G4: both branches since 2026-08-21
            if (!softPhase && rung > 1 && churn > 0 && membersHard &&
                _dec.AsSpan().SequenceEqual(_prevDec2))
            {
                double sigmaB = _sigma2; // priced _prevDec's reconstruction this rung
                SetDecisionsHard();      // from _dec - price the other cycle member
                double sigmaA = ReconResidual(frameChips, hc0, n, lMin);
                if (sigmaB < sigmaA)
                {
                    Array.Copy(_prevDec, _dec, _dec.Length);
                }

                converged = true;
                cycleAccepted = true;
                break;
            }

            if (softPhase)
            {
                SetDecisionsSoft();
            }
            else
            {
                SetDecisionsHard();
            }

            haveDecisions = true;
        }

        _lastCycleAccepted = cycleAccepted;
        diag?.Invoke(
            FormattableString.Invariant(
                $"mfb-block sched={(softFirst ? "soft" : "hard")} rungs={rung} window=[{lMin},{lMin + WinLen}) conv={(cycleAccepted ? 2 : converged ? 1 : 0)}") +
            FormattableString.Invariant(
                $" handoverChurn={handoverChurn} refit={(refitApplied ? 1 : 0)} finalChurn={lastChurn}"));
        return converged;
    }

    // ------------------------------------------------------------------ estimation

    private void ScanWindow(IReadOnlyList<long> frameChips, long hc0)
    {
        // Ridged wide LS per probe over the first 8 probes, tap energies accumulated.
        int k = _mode.K;
        Span<double> profile = stackalloc double[ScanLen];
        profile.Clear();
        var gram = new Complex[ScanLen, ScanLen];
        var rhs = new Complex[ScanLen];
        var phi = new Complex[ScanLen];
        int scanProbes = Math.Min(8, frameChips.Count); // short interleavers have fewer frames per block
        for (int p = 0; p < scanProbes; p++)
        {
            long ps = frameChips[p] - k;
            Complex32[] probe = MiniProbe.Get(k, ProbeIsBoundary(p, _il.Frames));
            Array.Clear(rhs);
            for (int i = 0; i < ScanLen; i++)
            {
                for (int j = 0; j < ScanLen; j++)
                {
                    gram[i, j] = Complex.Zero;
                }
            }

            for (long hc = (2 * ps) + ScanMax; hc < (2 * (ps + k)) + ScanMin; hc++)
            {
                for (int i = 0; i < ScanLen; i++)
                {
                    long src = hc - (ScanMin + i);
                    if ((src & 1) != 0)
                    {
                        phi[i] = Complex.Zero;
                        continue;
                    }

                    Complex32 x = probe[(src / 2) - ps];
                    phi[i] = new Complex(x.Re, x.Im);
                }

                Complex row = _ring[hc - hc0];
                for (int i = 0; i < ScanLen; i++)
                {
                    Complex pi = Complex.Conjugate(phi[i]);
                    rhs[i] += pi * row;
                    for (int j = 0; j < ScanLen; j++)
                    {
                        gram[i, j] += pi * phi[j];
                    }
                }
            }

            double trace = 0;
            for (int i = 0; i < ScanLen; i++)
            {
                trace += gram[i, i].Real;
            }

            double ridge = Math.Max(1e-9, 3e-2 * trace / ScanLen);
            for (int i = 0; i < ScanLen; i++)
            {
                gram[i, i] += ridge;
            }

            if (!SolveHermitian(gram, rhs, ScanLen))
            {
                continue;
            }

            for (int i = 0; i < ScanLen; i++)
            {
                profile[i] += (rhs[i].Real * rhs[i].Real) + (rhs[i].Imaginary * rhs[i].Imaginary);
            }
        }

        double peak = 0;
        for (int i = 0; i < ScanLen; i++)
        {
            peak = Math.Max(peak, profile[i]);
        }

        int pkLo = 0;
        int pkHi = ScanLen - 1;
        for (int i = 0; i < ScanLen; i++)
        {
            if (profile[i] >= 0.1 * peak)
            {
                pkLo = i;
                break;
            }
        }

        for (int i = ScanLen - 1; i >= 0; i--)
        {
            if (profile[i] >= 0.1 * peak)
            {
                pkHi = i;
                break;
            }
        }

        _lMin = ScanMin + pkLo - 7;
        if (ScanMin + pkHi + 8 > _lMin + WinLen)
        {
            _lMin = (ScanMin + pkHi + 8) - WinLen;
        }
    }

    private void RefitAnchors(IReadOnlyList<long> frameChips, long hc0)
    {
        // Two mid-frame decision anchors per frame, rows drawn wholly from decided
        // data chips - the anchor grid triples and the interpolation gap through deep
        // fades drops from ~120 ms to ~40 ms.
        int u = _mode.U;
        int lMin = _lMin;
        int lMax = lMin + WinLen;
        _anchorChip.Clear();
        _anchorH.Clear();
        _anchorChip.AddRange(_probeAnchorChip);
        _anchorH.AddRange(_probeAnchorH);
        for (int f = 0; f < _il.Frames; f++)
        {
            for (int half = 0; half < 2; half++)
            {
                long start = frameChips[f] + 16 + (half * 128);
                long end = start + 96;
                ClearNormal(WinLen);
                for (long hc = (2 * start) + lMax; hc < (2 * end) + lMin; hc++)
                {
                    for (int i = 0; i < WinLen; i++)
                    {
                        long src = hc - (lMin + i);
                        if ((src & 1) != 0)
                        {
                            _phiRow[i] = Complex.Zero;
                            continue;
                        }

                        _phiRow[i] = _decisions[((long)f * u) + ((src / 2) - frameChips[f])];
                    }

                    AccumulateRow(_ring[hc - hc0], WinLen);
                }

                if (SolveRidged(WinLen, 1e-3))
                {
                    _anchorChip.Add((start + end) / 2.0);
                    _anchorH.Add((Complex[])_rhs.Clone());
                }
            }
        }

        SortAnchors();
    }

    private void RebuildAnchors(bool midFrame, IReadOnlyList<long> frameChips)
    {
        if (!midFrame)
        {
            _anchorChip.Clear();
            _anchorH.Clear();
            _anchorChip.AddRange(_probeAnchorChip);
            _anchorH.AddRange(_probeAnchorH);
        }

        SortAnchors();
    }

    private void SortAnchors()
    {
        // Insertion-sort the paired lists by anchor time (nearly sorted already).
        for (int i = 1; i < _anchorChip.Count; i++)
        {
            double key = _anchorChip[i];
            Complex[] keyH = _anchorH[i];
            int j = i - 1;
            while (j >= 0 && _anchorChip[j] > key)
            {
                _anchorChip[j + 1] = _anchorChip[j];
                _anchorH[j + 1] = _anchorH[j];
                j--;
            }

            _anchorChip[j + 1] = key;
            _anchorH[j + 1] = keyH;
        }
    }

    private void InterpolateH(double chip, Complex[] h)
    {
        int hi = 0;
        while (hi < _anchorChip.Count && _anchorChip[hi] < chip)
        {
            hi++;
        }

        if (hi >= _anchorChip.Count)
        {
            _anchorH[^1].CopyTo(h, 0);
            return;
        }

        if (hi == 0)
        {
            _anchorH[0].CopyTo(h, 0);
            return;
        }

        double frac = (chip - _anchorChip[hi - 1]) / (_anchorChip[hi] - _anchorChip[hi - 1]);
        Complex[] a = _anchorH[hi - 1];
        Complex[] b = _anchorH[hi];
        for (int i = 0; i < WinLen; i++)
        {
            h[i] = (a[i] * (1 - frac)) + (b[i] * frac);
        }
    }

    // ------------------------------------------------------------------ detection

    private readonly Complex[] _hAt = new Complex[WinLen];
    private Complex[]? _recon;
    private double _sigma2;

    /// <summary>Rebuilds the block reconstruction from the current
    /// <see cref="_decisions"/> (probes always known) and returns the mean squared
    /// residual per complex sample over the data span - the label-free noise price and
    /// the likelihood proxy the cycle-accept selection uses.</summary>
    private double ReconResidual(IReadOnlyList<long> frameChips, long hc0, int n, int lMin)
    {
        int u = _mode.U;
        int k = _mode.K;
        int frames = _il.Frames;
        _recon ??= new Complex[_ring.Length];
        Array.Clear(_recon, 0, n);
        void AddChip(long c, Complex x)
        {
            InterpolateH(c, _hAt);
            for (int i = 0; i < WinLen; i++)
            {
                long hc = (2 * c) + lMin + i;
                if (hc >= hc0 && hc < hc0 + n)
                {
                    _recon[hc - hc0] += _hAt[i] * x;
                }
            }
        }

        for (int p = 0; p <= frames; p++)
        {
            bool preceding = p < frames;
            long ps = preceding ? frameChips[p] - k : frameChips[frames - 1] + u;
            Complex32[] probe = MiniProbe.Get(k, ProbeIsBoundary(p, frames));
            for (int c = 0; c < k; c++)
            {
                AddChip(ps + c, new Complex(probe[c].Re, probe[c].Im));
            }
        }

        for (int d = 0; d < _decisions.Length; d++)
        {
            AddChip(_dataChip[d], _decisions[d]);
        }

        double resid = 0;
        long rows = 0;
        for (long hc = 2 * frameChips[0]; hc < 2 * (frameChips[frames - 1] + u); hc++)
        {
            Complex diff = _ring[hc - hc0] - _recon[hc - hc0];
            resid += (diff.Real * diff.Real) + (diff.Imaginary * diff.Imaginary);
            rows++;
        }

        return Math.Max(resid / rows, 1e-12);
    }

    private void Project(IReadOnlyList<long> frameChips, long hc0, int n, int lMin, bool cancel)
    {
        int u = _mode.U;
        _recon ??= new Complex[_ring.Length];
        if (cancel)
        {
            _sigma2 = ReconResidual(frameChips, hc0, n, lMin);
        }
        else
        {
            ProjectMmseCold(frameChips, hc0, n, lMin); // G4: both branches since 2026-08-21
            return;
        }

        var r0Dist = cancel ? null : new List<double>(_proj.Length);
        for (int d = 0; d < _proj.Length; d++)
        {
            long c = _dataChip[d];
            InterpolateH(c, _hAt);
            double g2 = 0;
            Complex acc = Complex.Zero;
            for (int i = 0; i < WinLen; i++)
            {
                long hc = (2 * c) + lMin + i;
                if (hc < hc0 || hc >= hc0 + n)
                {
                    continue;
                }

                Complex row = cancel
                    ? _ring[hc - hc0] - _recon[hc - hc0] + (_hAt[i] * _decisions[d])
                    : _ring[hc - hc0];
                acc += Complex.Conjugate(_hAt[i]) * row;
                g2 += (_hAt[i].Real * _hAt[i].Real) + (_hAt[i].Imaginary * _hAt[i].Imaginary);
            }

            _proj[d] = acc / Math.Max(g2, 1e-12);
            _gramT[d] = g2;
            if (r0Dist is not null)
            {
                double best = double.MaxValue;
                for (int q = 0; q < _points; q++)
                {
                    Complex cq = Point(q);
                    double dr = _proj[d].Real - cq.Real;
                    double di = _proj[d].Imaginary - cq.Imaginary;
                    best = Math.Min(best, (dr * dr) + (di * di));
                }

                r0Dist.Add(best * g2);
            }
        }

        if (r0Dist is not null)
        {
            // The cold rung's crude global price: the median ISI-inclusive squared
            // distance to the nearest point. Every later rung prices with the measured
            // reconstruction residual.
            r0Dist.Sort();
            _sigma2 = Math.Max(r0Dist[r0Dist.Count / 2], 1e-9);
        }
    }

    /// <summary>The MMSE cold rung (G2d). y_data = ring minus the exact probe reconstruction;
    /// for the symbol at chip c the rows 2c+lMin..2c+lMax-1 see the neighbouring data
    /// chips |c'-c| &lt;= (WinLen-1)/2 through their own interpolated responses.
    /// R = Es H H^H + sigma^2 I; z = R^-1 h_c; mu = Es h_c^H z; xhat/mu = x + v with
    /// var(v) = Es (1-mu)/mu, so the LLR weight is mu / (Es (1-mu)) against sigma^2 = 1.
    /// Measured on the G2 specimens: every block starts at 4-13k errors instead of
    /// coin-flip and the soft cancellation finishes in five or six rungs.</summary>
    private void ProjectMmseCold(IReadOnlyList<long> frameChips, long hc0, int n, int lMin)
    {
        int u = _mode.U;
        int frames = _il.Frames;
        Array.Clear(_decisions);
        ReconResidual(frameChips, hc0, n, lMin); // _recon now holds the probes alone
        Complex[] probesOnly = _recon!;
        double sigmaNoise = Math.Max(_anchorResidRows > 0 ? _anchorResidSum / _anchorResidRows : 1e-9, 1e-12);
        int half = (WinLen - 1) / 2;
        for (int d = 0; d < _proj.Length; d++)
        {
            long c = _dataChip[d];
            int f0 = d / u;
            InterpolateH(c, _hCol);
            for (int i = 0; i < WinLen; i++)
            {
                for (int j = 0; j < WinLen; j++)
                {
                    _rMat[i, j] = Complex.Zero;
                }

                _rMat[i, i] = sigmaNoise;
            }

            for (long cn = c - half; cn <= c + half; cn++)
            {
                // A data chip of this block, or a probe/outside chip (already subtracted)?
                bool isData = false;
                for (int f = Math.Max(0, f0 - 1); f <= Math.Min(frames - 1, f0 + 1) && !isData; f++)
                {
                    isData = cn >= frameChips[f] && cn < frameChips[f] + u;
                }

                if (!isData)
                {
                    continue;
                }

                Complex[] hn;
                if (cn == c)
                {
                    hn = _hCol;
                }
                else
                {
                    InterpolateH(cn, _hNb);
                    hn = _hNb;
                }

                int off = (int)(2 * (cn - c));
                for (int i = 0; i < WinLen; i++)
                {
                    int kk = i - off;
                    _hShift[i] = kk >= 0 && kk < WinLen ? hn[kk] : Complex.Zero;
                }

                for (int i = 0; i < WinLen; i++)
                {
                    if (_hShift[i] == Complex.Zero)
                    {
                        continue;
                    }

                    for (int j = 0; j < WinLen; j++)
                    {
                        _rMat[i, j] += _es * _hShift[i] * Complex.Conjugate(_hShift[j]);
                    }
                }
            }

            Array.Copy(_hCol, _zVec, WinLen);
            Complex mu = Complex.Zero;
            Complex xhat = Complex.Zero;
            if (SolveHermitian(_rMat, _zVec, WinLen))
            {
                for (int i = 0; i < WinLen; i++)
                {
                    long hc = (2 * c) + lMin + i;
                    Complex y = hc >= hc0 && hc < hc0 + n ? _ring[hc - hc0] - probesOnly[hc - hc0] : Complex.Zero;
                    mu += Complex.Conjugate(_hCol[i]) * _zVec[i];
                    xhat += Complex.Conjugate(_zVec[i]) * y;
                }

                mu *= _es;
                xhat *= _es;
            }

            double muR = Math.Clamp(mu.Real, 1e-6, 1 - 1e-6);
            _proj[d] = xhat / muR;
            _gramT[d] = muR / (_es * (1 - muR));
        }

        _sigma2 = 1.0;
    }

    private void BuildLlrs(int lMin)
    {
        int u = _mode.U;
        for (int d = 0; d < _proj.Length; d++)
        {
            int pos = d % u;
            for (int q = 0; q < _points; q++)
            {
                Complex cq = Wire(q, pos);
                double dr = _proj[d].Real - cq.Real;
                double di = _proj[d].Imaginary - cq.Imaginary;
                _metric[q] = ((dr * dr) + (di * di)) * _gramT[d] / _sigma2;
            }

            for (int bb = 0; bb < _bps; bb++)
            {
                double m0 = double.MaxValue, m1 = double.MaxValue;
                for (int q = 0; q < _points; q++)
                {
                    if (((q >> (_bps - 1 - bb)) & 1) != 0)
                    {
                        m1 = Math.Min(m1, _metric[q]);
                    }
                    else
                    {
                        m0 = Math.Min(m0, _metric[q]);
                    }
                }

                _llrs[(d * _bps) + bb] = (float)(m1 - m0); // positive => bit 0
            }
        }
    }

    private void SetDecisionsHard()
    {
        SetDecisionsFrom(_dec);
    }

    private void SetDecisionsSoft()
    {
        // SISO per-bit posteriors → per-symbol E[x] (independent-bit approximation,
        // nib-permuted). Wrong decisions self-attenuate toward zero.
        _interleaver.Deinterleave(_llrs, _softPunctured);
        Ms110dPuncture.Depuncture(_puncture, _softPunctured, _softMother);
        _siso.Decode(_softMother, _softMotherPost);
        Ms110dPuncture.Apply(_puncture, _softMotherPost, _softPunctured);
        _interleaver.Interleave(_softPunctured, _softWire);
        int u = _mode.U;
        for (int d = 0; d < _decisions.Length; d++)
        {
            for (int bb = 0; bb < _bps; bb++)
            {
                double l = Math.Clamp(_softWire[(d * _bps) + bb], -30f, 30f);
                _p0[bb] = 1.0 / (1.0 + Math.Exp(-l));
            }

            int pos = d % u;
            Complex ex = Complex.Zero;
            for (int m = 0; m < _points; m++)
            {
                double pm = 1.0;
                for (int bb = 0; bb < _bps; bb++)
                {
                    bool one = ((m >> (_bps - 1 - bb)) & 1) != 0;
                    pm *= one ? 1.0 - _p0[bb] : _p0[bb];
                }

                ex += pm * Wire(m, pos);
            }

            _decisions[d] = ex;
        }
    }

    // ------------------------------------------------------------------ linear algebra

    private void ClearNormal(int len)
    {
        Array.Clear(_rhs, 0, len);
        for (int i = 0; i < len; i++)
        {
            for (int j = 0; j < len; j++)
            {
                _gram[i, j] = Complex.Zero;
            }
        }
    }

    private void AccumulateRow(Complex row, int len)
    {
        for (int i = 0; i < len; i++)
        {
            Complex pi = Complex.Conjugate(_phiRow[i]);
            _rhs[i] += pi * row;
            for (int j = 0; j < len; j++)
            {
                _gram[i, j] += pi * _phiRow[j];
            }
        }
    }

    private bool SolveRidged(int len, double ridgeScale)
    {
        double trace = 0;
        for (int i = 0; i < len; i++)
        {
            trace += _gram[i, i].Real;
        }

        double ridge = Math.Max(1e-9, ridgeScale * trace / len);
        for (int i = 0; i < len; i++)
        {
            _gram[i, i] += ridge;
        }

        return SolveHermitian(_gram, _rhs, len);
    }

    private static bool SolveHermitian(Complex[,] a, Complex[] b, int n)
    {
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (a[r, col].Magnitude > a[pivot, col].Magnitude)
                {
                    pivot = r;
                }
            }

            if (a[pivot, col].Magnitude < 1e-12)
            {
                return false;
            }

            if (pivot != col)
            {
                for (int c = 0; c < n; c++)
                {
                    (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
                }

                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            for (int r = col + 1; r < n; r++)
            {
                Complex factor = a[r, col] / a[col, col];
                if (factor == Complex.Zero)
                {
                    continue;
                }

                for (int c = col; c < n; c++)
                {
                    a[r, c] -= factor * a[col, c];
                }

                b[r] -= factor * b[col];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            Complex acc = b[r];
            for (int c = r + 1; c < n; c++)
            {
                acc -= a[r, c] * b[c];
            }

            b[r] = acc / a[r, r];
        }

        return true;
    }
}
