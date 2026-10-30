using M0LTE.Fec;
using M0LTE.Dsp;
using M0LTE.Ms110d.Fec;

namespace M0LTE.Ms110d;

/// <summary>
/// Appendix D 3 kHz serial-tone receiver: autobaud - one receiver decodes the whole ladder
/// (D.4). Native 9600 Hz in; complex baseband at T/2 (4800 Hz) behind an 1800 Hz mixer and
/// the SRRC receive filter; matched-filter acquisition of the preamble Fixed subsection over
/// a ±75 Hz coarse CFO grid; downcount/WID Walsh decode with checksum verification; then
/// probe-trained fractionally-spaced NLMS DFE demodulation (design §2.5/§2.6) with the
/// mandatory D.5.4.5 exits (unconditional EOM scan, block limit, terminate command).
/// </summary>
/// <remarks>
/// Phase A limitations, stated: acquisition requires the 9-Walsh-symbol Fixed subsection,
/// i.e. transmissions with M ≥ 2 super-frames (the M = 1 single-symbol preamble is generated
/// on TX but not yet acquired); clock skew is followed by the slow per-probe timing tracker -
/// measured (Ms110dClockSkewTests, 2026-07-23): ±50 ppm decodes bit-exact with wide margin
/// (breaking points ±700 ppm on ~4 s bursts, ±300-400 ppm on ~11 s - tolerance is
/// burst-duration-dependent; longer transmissions unmeasured); WN 0 has no timing tracker at
/// all (chip clock assumed nominal, as in loopback and the D.6 simulation rigs).
/// </remarks>
public sealed class Ms110dDemodulator
{
    private const int RingBits = 16;
    // 65536 T/2 samples ≈ 13.7 s. Must exceed the longest 3 kHz Long-interleaver block
    // (WN 1/2: 256 frames × 96 symbols = 10.24 s on air) plus tail: TurboReequalize
    // re-reads the whole block from the ring at FinishBlock time, and Interpolate cannot
    // detect overwritten slots (BlockSamplesResident is the backstop).
    private const int RingSize = 1 << RingBits;
    private const int ChipsFixed = 288;           // 9 × 32 (M ≥ 2)
    private const int ChipsSuperframe = 576;
    private const int InterpHalf = 4;             // 8-tap interpolator

    // #101 input signal-level AGC (see the _agcGain field). AgcLevelFloor is the receive
    // level below which a burst is judged globally weak and normalized up to AgcNominalLevel;
    // at or above it the gain is exactly 1.0 (a dead-zone that makes the AGC a no-op at the
    // sim's nominal level and every stronger level → masks byte-identical). Both are the
    // measured fade-averaged Fixed-section correlation amplitude (|Σ y·k̄|/32, k unit-magnitude
    // 8PSK); AgcMaxGain caps the boost so a near-noise burst is not amplified without bound.
    // Measured (data/agc-scale-calibration.log): the sim nominal Fixed-section correlation
    // amplitude is ~0.119, linear in receive level (−20 dB → 0.012). The dead-zone floor 0.04
    // sits far below the fade-averaged mask levels (which cluster near nominal because the
    // ~1 Hz fade averages out over the ~2 s window) and far above the −20 dB real-RF level, so
    // the AGC fires zero times in-family (masks byte-identical) yet catches the real-RF offset.
    private const float AgcNominalLevel = 0.12f;  // boost target - the level the ridges were tuned for
    private const float AgcLevelFloor = 0.04f;    // dead-zone edge: at/above this the gain is exactly 1.0
    private const float AgcMaxGain = 32f;

    // Issue #7: the K=48 waveforms (WN1/WN2) always normalize to this level, in both
    // directions, instead of using the dead-zone above. Their equalizer ridges are order-1
    // (InitializeDfe), so the solve's effective regularization goes with the square of the
    // receive level: a burst at a preamble level of 0.05 to 0.15 started at about a tenth of
    // the right gain and lost interleaver block 0. 0.25 is the level the masks' AWGN gates
    // measure with EstimatePreambleLevel (the sim's channel output; Poor reads about 0.17
    // there, because the estimate only sees the cursor path, so Poor bursts get about 1.5x,
    // which measured slightly better on Poor than leaving them at unity). Other K keep the
    // dead-zone: their 1e-3 ridges barely notice the level.
    private const float K48LevelTarget = 0.25f;
    private static readonly double[] BinsHz = [-75, -50, -25, 0, 25, 50, 75];

    /// <summary>Encoded count words for every field value - the joint count vote's
    /// per-candidate expected dibits (§B3.5 Amendment 1).</summary>
    private static readonly byte[][] CountWords = BuildCountWords();

    private static byte[][] BuildCountWords()
    {
        var words = new byte[32][];
        for (int c = 0; c < 32; c++)
        {
            words[c] = PreambleGenerator.EncodeCount(c);
        }

        return words;
    }

    private readonly Ms110dDemodOptions _options;
    private FirFilter _rxFilterRe;
    private FirFilter _rxFilterIm;
    private readonly float[] _rxPulse;
    private readonly Complex32[] _mixTable;
    private readonly Complex32[][] _mfReference; // [bin][288] conj(fixed chip)·e^{−jθ_bin·i}

    // T/2 ring.
    private readonly Complex32[] _ring = new Complex32[RingSize];
    private long _written;
    private int _mixPhase;
    private bool _decimateToggle;
    private readonly double[] _combEnergy = new double[2];

    // Channel-truth genie (diagnostic instrument, phase-b-plan §B0). A noise-free copy of
    // the SAME channel realization runs through an identical front end (its own
    // filter/mixer state; the shared carrier/timing model applies to both), and the
    // ESTIMATION reads - training rows, carrier refinement, probe timing, turbo channel
    // estimation - come from this ring while DETECTION stays on the noisy one. A genie run
    // is the achievability bound of the current detector under perfect channel
    // observation: genie ≪ measured says tracking/estimation is the deficit, genie ≈
    // measured says detection is. Acquisition and the WN 0 Walsh path take no estimation
    // reads and are unaffected. Genie numbers are a diagnostic bound, never performance
    // evidence.
    private Complex32[]? _genieRing;
    private FirFilter? _genieFilterRe;
    private FirFilter? _genieFilterIm;
    private int _genieMixPhase;
    private bool _genieDecimateToggle;
    private long _genieWritten;
    // Measured per-T/2-sample additive-noise power: mean |noisy − clean|² between the two
    // rings. Genie LS solves add σ²·Σweight to the FF Gram diagonal - the term the noisy
    // rows contribute implicitly - so the genie computes the true-channel MMSE equalizer,
    // not the zero-forcing one (first WN4 Poor genie run: ZF inverted the fading notches
    // and came out 30× WORSE than the baseline it is meant to bound).
    private double _genieNoiseSum;
    private long _genieNoiseCount;

    // Search state. The metric ring keeps recent per-candidate metrics so the accepted
    // peak can be moved back to the EARLIEST multipath arrival (design §2.6): on the
    // D-LXV static channels the equalizer geometry only works cursor-first - echoes are
    // post-cursor for the feedback taps; locking a later path puts them outside the
    // feed-forward span.
    private double _bestMetric;
    private long _bestStart = -1;
    private int _bestBin;
    private readonly double[] _metricRing = new double[256];
    private readonly byte[] _metricBinRing = new byte[256];

    /// <summary>Highest matched-filter metric seen since construction/reset - an
    /// acquisition diagnostic (normalized 0…1).</summary>
    public double PeakSearchMetric { get; private set; }

    // Lock state.
    private Ms110dRxState _state = Ms110dRxState.Searching;
    private double _chip0;        // absolute T/2 position of chip 0 of the matched super-frame
    private double _tau;          // slow timing correction, T/2 units
    private double _omega;        // carrier phase increment per T/2 sample (residual CFO)
    private double _omegaAcquired; // ω at data start - the tracking loop's clamp centre
    private double _thetaBase;    // carrier phase at _chip0
    private Ms110dLockInfo? _lock;
    private Ms110dMode? _mode;
    private Ms110dInterleaverParams? _il;
    private long _dataStartChip;
    private bool _trackingInitialized;
    // #101 input signal-level AGC: a per-burst scalar applied to the DFE read path so a
    // globally-low receive level (real-RF, no AGC upstream) is normalized to the level the
    // K=48 cold-restart solves were tuned for. 1.0 = no-op (set for every nominal-or-stronger
    // burst by the dead-zone, except K=48, which always normalizes: issue #7). Estimated from a
    // fade-averaged preamble SIGNAL correlation, so a nominal fade never trips it. See
    // InitializeDfe / EstimatePreambleLevel.
    private float _agcGain = 1.0f;

    // Tracking state (WN ≥ 1).
    private Dfe? _dfe;
    private int _ffLead;
    private float _initRidge;      // initial LS ridge (MMSE-scaled per mini-probe class)
    private float _trackRidge;     // per-probe re-solve ridge
    private Complex32[] _known = [];
    private Complex32[] _decisions = [];  // ring of past FbTaps decisions, newest at [0]
    private readonly Ms110dScrambler _scrambler = new();
    private long _frameChip;
    private int _frameInBlock;

    // Which mini-probes of this burst are shifted (D.5.2.2, see ProbePattern): the period is
    // the waveform's Long interleaver frame count, whatever interleaver the burst uses.
    private int _probeLongFrames = 1;

    internal bool ProbeShiftedAfter(long frameNumber) => ProbePattern.ShiftedAfter(frameNumber, _probeLongFrames);
    private int _badProbes;
    private double _probeMse;
    private double _probeGainRef;
    private double _probeEnergyRef;
    private int _collapsedProbes;
    private bool _collapseArmed;

    // Signal-absent release (issue #553, docs/dev/ms110d/signal-absent.md). Each frame's
    // mini-probe is matched-filtered against the raw received symbols over a lag window; the
    // coherent energy found there, less what noise puts there and with up to
    // PresenceMaxCarriers steady carriers cancelled (a carrier on a probe line correlates with
    // the probe at every lag), is averaged over the last PresenceWindowSeconds of frames.
    // Noise-only, that average has a standard deviation of about sqrt(lags / frames); the lock is released when it falls below PresenceSigmas of
    // those. The window is long on purpose: a slow, deep NVIS fade takes the probes away for
    // many seconds inside a burst that is still worth reading, and losing that burst's frames
    // costs more than holding a dead lock a few seconds longer. It reads the ring through the
    // receiver's carrier and timing but writes no receiver state, so until it fires the
    // receiver is bit-identical to one without it.
    /// <summary>How long, in seconds, a lock is held with nothing but noise on its mini-probes
    /// before the receiver lets it go and listens afresh.</summary>
    public const double PresenceWindowSeconds = 16.0;
    private const double PresenceSigmas = 4.0;
    private double[] _presenceRing = [];
    private int _presenceFrames;
    private double _presenceSum;
    private double _presenceThreshold;
    private long _presenceProbeChip;
    private Complex32[] _presenceProbe = [];
    private double _presenceBackground;
    private double _presenceFrameSeconds;
    private double[] _presenceSpectrum = [];
    private bool _presenceSpectrumSeeded;
    private double[] _presenceDftRe = [];
    private double[] _presenceDftIm = [];

    /// <summary>How long the background a lag is judged against is averaged over: long
    /// enough to steady it, short enough to follow a signal's fade down to the noise.</summary>
    private const double PresenceBackgroundSeconds = 2.0;

    /// <summary>How long the lag spectrum carriers are found in is averaged over: long enough
    /// that a carrier well under the noise stands out of it, short enough that one arriving
    /// after a burst shows within a few seconds.</summary>
    private const double PresenceSpectrumSeconds = 4.0;

    /// <summary>The most steady carriers cancelled at once. Each takes two degrees of freedom
    /// out of the lags (its sinusoid and that sinusoid's first-order term in frequency), so four
    /// leave seven of the fifteen K = 32 lags, enough for the median to stand on.</summary>
    private const int PresenceMaxCarriers = 4;

    /// <summary>A line in the averaged lag spectrum is taken for a carrier when it stands this
    /// many times above what noise alone puts in a spectrum bin: low, so a carrier too weak to be
    /// found adds less than half a unit to the statistic (under every mode's line).</summary>
    private const double PresenceCarrierOverNoise = 1.5;

    /// <summary>... and when its excess over the noise is this many times the spectrum's median
    /// excess, which is what a signal's own channel puts in every bin (its spectrum across the
    /// lags is the channel's frequency response, so two equal paths ripple it by a factor of
    /// two and no more): a readable burst's ripple is never taken for a carrier.</summary>
    private const double PresenceCarrierOverSignal = 4.0;

    // Tracking state (WN 0).
    private Wid0WalshModem? _walsh;
    private long _symbolChip;
    private int _symbolInBlock;
    private int _weakSymbols;
    private Complex32 _walshPhaseAcc;
    private int _walshPhaseCount;

    // Block/burst assembly.
    private TailBitingViterbiDecoder? _viterbi;
    private PunctureSpec? _puncture;
    private Ms110dInterleaver? _interleaver;
    private ConvolutionalCode? _code;
    private float[] _blockLlrs = [];
    private int _blockLlrCount;

    // §B3.3 soft-feedback turbo: SISO outer decoder + block-sized work buffers
    // (allocated at lock; the FinishBlock cadence is not a per-sample hot path but the
    // buffers are stable per burst regardless).
    private TailBitingSisoDecoder? _siso;
    private float[] _softPunctured = [];
    private float[] _softMother = [];
    private float[] _softMotherPost = [];
    private float[] _softWireLlrs = [];
    private float[] _softWireExt = [];
    private Complex32[] _softExpected = [];
    private float[] _softVar = [];
    private Complex32[] _turboExpected = [];
    private int _blockIndex;

    /// <summary>The 0-based index of the block being decoded within the current burst
    /// (the MFB decoder needs it: whether a probe is shifted depends on its frame number
    /// across the whole burst, see ProbePattern).</summary>
    internal int BlockIndex => _blockIndex;
    private readonly List<byte> _burstBits = [];
    private readonly List<long> _blockFrameChips = [];

    // Fading detector state (see ProcessFrame). The per-frame statistic (CFO-immune
    // fractional tap change) has heavily overlapping LEVEL distributions between AWGN at
    // mask SNR and Poor between fades (measured WN4: AWGN median 0.045/max 0.12; Poor
    // median 0.033/max 0.33) - the discriminator is temporal structure: fading recurs as
    // excursions above a min-tracking noise floor (the EnergyBusyDetector pattern), AWGN
    // stays in a tight band. Enter on 2 excursions ≤ 24 frames apart (one 1 Hz fade event
    // spans several frames), exit after 32 excursion-free frames.
    private const float FadeExcursionRatio = 3.5f;
    private const int FadeEnterWindowFrames = 24;
    private const int FadeExitFrames = 32;
    private double _fadeFloor;
    private bool _fadeFloorSeeded;
    private int _framesSinceExcursion = int.MaxValue / 2;
    private bool _fading;
    private bool _fadingLatched;
    private bool _terminate;

    /// <summary>Creates the receiver.</summary>
    public Ms110dDemodulator(Ms110dDemodOptions? options = null)
    {
        _options = options ?? new Ms110dDemodOptions();
        Ms110dModulator.ValidateRollOff(_options.RollOff, nameof(options));
        _rxPulse = Ms110dModulator.DesignPulse(_options.RollOff);
        _rxFilterRe = new FirFilter(_rxPulse);
        _rxFilterIm = new FirFilter(_rxPulse);

        // 1800 Hz = 3/16 of a cycle per 9600 Hz sample: sixteenth-turn table, index step 3.
        _mixTable = new Complex32[16];
        for (int i = 0; i < 16; i++)
        {
            _mixTable[i] = Complex32.FromPhase(-((float)(2.0 * Math.PI * i / 16.0)));
        }

        byte[] fixedChips = new PreambleGenerator(0, 2).FixedSectionChips();
        _mfReference = new Complex32[BinsHz.Length][];
        for (int b = 0; b < BinsHz.Length; b++)
        {
            var reference = new Complex32[ChipsFixed];
            for (int i = 0; i < ChipsFixed; i++)
            {
                double theta = 2.0 * Math.PI * BinsHz[b] * i / Ms110dTables.SymbolRate;
                reference[i] = Ms110dTables.Psk8[fixedChips[i]].Conj() * Complex32.FromPhase(-((float)theta));
            }

            _mfReference[b] = reference;
        }
    }

    /// <summary>Debug-only: fires per equalized, descrambled data symbol.</summary>
    public event Action<Complex32>? DataSymbolEqualized;

    /// <summary>Diagnostic: one formatted line per processed frame (probe gain/MSE,
    /// timing, carrier, fading state). Formatted only when subscribed - the library
    /// itself writes no console output (issue #65); hosts that want the old
    /// <c>MS110D_DEBUG</c> stderr behaviour subscribe and print.</summary>
    public event Action<string>? FrameDiagnostics;

    /// <summary>Diagnostic: fires once per interleaver block BEFORE the first decode, with
    /// the block index and the first-pass wire-order LLRs (fetch order, pre-deinterleave,
    /// pre-turbo). The buffer is reused per block - receivers must copy what they keep.
    /// Comparing sign(LLR) against the re-encoded transmitted stream gives the uncoded
    /// channel-bit error rate, the §5.3 uncoded-vs-coded split (phase-b-plan §B0).</summary>
    public event Action<int, float[]>? FirstPassBlockLlrs;

    /// <summary>Diagnostic (phase-b-plan §B3.3 basin): fires once per interleaver block
    /// AFTER the turbo loop settles, with the block index and the wire-order LLRs of the
    /// last turbo iterate - on converged blocks the fixed-point stream, on reverted blocks
    /// the wander state the loop was in when the cap hit (the shipped DECODE on those
    /// blocks is the first-pass one; the stream here is what the loop believed). Does not
    /// fire on skipped or aborted blocks. The buffer is reused per block - copy to keep.</summary>
    public event Action<int, float[]>? TurboBlockLlrs;

    /// <summary>Diagnostic (phase-b-plan §B3.3): when set, FinishBlock runs ONE extra
    /// turbo re-equalization trained on the returned TRUE info bits for the block -
    /// oracle labels - after the normal pipeline has finished with the block. This
    /// measures the ceiling a CONVERGED soft-feedback turbo could reach with the chain
    /// BCJR's channel/echo model: perfect labels, same estimation machinery. A null
    /// return skips the block. The shipped decode is never touched, and the demodulator
    /// is bit-identical when the hook is unset. Results fire on
    /// <see cref="OracleBlockLlrs"/>. Diagnostic bound only, never performance.</summary>
    public Func<int, byte[]?>? OracleInfo;

    /// <summary>Companion to <see cref="OracleInfo"/>: the block index, the oracle-pass
    /// wire-order LLRs (buffer reused per block - copy to keep), and the Viterbi decode
    /// of those LLRs.</summary>
    public event Action<int, float[], byte[]>? OracleBlockLlrs;

    /// <summary>W1 true-channel injection instrument (wn8-program-plan §4, evidence
    /// 2026-07-31-wn8-w1): when set alongside <see cref="OracleInfo"/>, FinishBlock runs
    /// one further re-equalization whose channel TIME VARIATION comes from the recorded
    /// Watterson truth - per-symbol h(u) = a·g₁(u) + b·g₂(u) per model tap, with only
    /// the static per-frame gauge constants LS-fitted on the true symbols - instead of
    /// the oracle's label-trained segment anchors. The delegate maps an absolute input
    /// sample position (9600 Hz domain, lead-in included) to the two recorded path gains
    /// at that instant: the rig owns lead-in/gain-rate alignment, the demodulator owns
    /// chip→sample (2·PositionOfChip). Null (the default) leaves every path
    /// bit-identical. Results fire on <see cref="TruthBlockLlrs"/>. Instrument only.</summary>
    internal Func<double, (Complex32 G1, Complex32 G2)>? TruthGainsAtSample { get; set; }

    /// <summary>Companion to <see cref="TruthGainsAtSample"/>: block index, truth-pass
    /// wire-order LLRs (buffer reused - copy to keep), and their Viterbi decode.</summary>
    internal event Action<int, float[], byte[]>? TruthBlockLlrs;

    /// <summary>W5a instrument seam: fires in FinishBlock with the block index and the
    /// block's frame-start chips while the block's samples are still resident, so a rig
    /// can pull the ring spans it needs synchronously (the ring wraps long before a
    /// burst ends). Passive - null (the default) changes nothing.</summary>
    internal Action<int, long[]>? InstrumentBlockReady { get; set; }

    /// <summary>The shipped CFO/timing-corrected T/2 ring read at 2·chip + δ half-chip
    /// positions - used by the W5b2 MFB-form block decoder and the W5a instrument rig.
    /// Read-only.</summary>
    internal Complex32 RingReadT2(double halfChips)
    {
        return ReadT2(halfChips);
    }

    /// <summary>Bridge for out-of-class shipped/instrument components (the MFB block
    /// decoder) to emit <see cref="FrameDiagnostics"/> lines; null when nothing
    /// listens, so callers can skip formatting entirely.</summary>
    internal Action<string>? FrameDiagnosticsForInstruments =>
        FrameDiagnostics is null ? null : line => FrameDiagnostics?.Invoke(line);

    /// <summary>W2 V-split: gauge fits per frame partition (1 = the W1 whole-frame fit,
    /// 2 = independent half-frame fits - prices within-frame gauge drift). Truth-pass
    /// instrument knob only.</summary>
    internal int TruthGaugeSplit { get; set; } = 1;

    /// <summary>W2 V-xtaps: adds cursor±1 tap pairs to the gauge basis (responses to
    /// x[u+1]/x[u−1]), soft-cancelled from the observation before the chains exactly
    /// like the straddle - the 16QAM re-measurement of the B3.7 beyond-model revival
    /// condition. Truth-pass instrument knob only.</summary>
    internal bool TruthGaugeXtaps { get; set; }

    /// <summary>§B3.5b WN0 genie-gain oracle (instrument,
    /// evidence/2026-07-26-phase-b35b-wn0genie): returns the TRUE transmitted di-bit for
    /// (blockIndex, symbolInBlock), or −1 for no-truth symbols (post-EOM), which fall
    /// back to the shipped DD path. When set, TrackWalsh detects with truth-derived
    /// finger gains read from the genie stream (required - throws without one) and skips
    /// the carrier PLL retune (the phase-error observable self-cancels under truth
    /// gains). Unset = the shipped path, bit-identical. Armed only by the autopsy rig.</summary>
    internal Func<int, int, int>? WalshOracleDibit { get; set; }

    /// <summary>§B3.5b companion: true = O-pole (shipped one-pole/warm-up with truth
    /// innovations - keeps the 80 ms lag), false = O-inst (instantaneous truth).</summary>
    internal bool WalshOraclePole { get; set; }

    /// <summary>§B3.6 instrument (evidence/2026-07-26-phase-b36-wn7loop): replaces the
    /// turbo loop's iteration-0 labels for one block - perturbed restarts (M1b) and
    /// staged seeding (M2b). Receives the block index and the first-pass decode; a null
    /// return leaves the start unchanged. Revert protection is untouched: the fallback
    /// stream remains the TRUE first pass. Unset = the shipped path, bit-identical.
    /// Armed only by the autopsy rig.</summary>
    internal Func<int, byte[], byte[]?>? TurboStartOverride { get; set; }

    /// <summary>§B3.6 M1a instrument: when set (with <see cref="FrameDiagnostics"/>),
    /// every TurboCore pass emits one <c>turbo-probe</c> line per block - the solved
    /// channel priced on the frames' PRECEDING mini-probe rows (known symbols, the only
    /// training-domain evidence decode labels cannot launder). Rows keep their feedback
    /// history and TIR echo sources wholly inside the probe, mirroring the solve's own
    /// probe-row construction.</summary>
    internal bool TurboProbeDiag { get; set; }

    /// <summary>§B3.6 C2a stage (M2a measurement): when set (with
    /// <see cref="FrozenBlockLlrs"/> subscribed), FinishBlock runs one extra label-free
    /// re-detection pass per block after the normal pipeline - probe-only TIR solve,
    /// probe-anchored h1, the solve's own shortening target as the chain echo model,
    /// probe-priced noise floor, chains as no-prior exact-MAP. No decode label touches
    /// any estimate (the anti-echo-chamber construction). The shipped decode is never
    /// touched; unset = bit-identical. Armed only by the autopsy rig; PSK modes only.</summary>
    internal bool TurboFrozenProbe { get; set; }

    /// <summary>§B3.6 companion: the block index, the frozen-pass wire-order LLRs
    /// (buffer reused per block - copy to keep), and the Viterbi decode of those
    /// LLRs.</summary>
    internal event Action<int, float[], byte[]>? FrozenBlockLlrs;

    /// <summary>§B3.7 M1a instrument: when set (with <see cref="FrameDiagnostics"/>),
    /// every frozen-pass frame ALSO runs a straddle-pair TIR solve on the same probe
    /// rows and emits one <c>frozen-pair</c> line - log-only; the applied path stays
    /// the single-lag solve, which runs last so its taps stand for the frame's
    /// Equalize calls. Unset = bit-identical.</summary>
    internal bool TurboFrozenPairDiag { get; set; }

    /// <summary>§B3.7 E1′ (Amendment 1): burst-consensus constrained frozen solve - a
    /// vote sweep of free probe-only solves picks the block's modal accepted lag, and
    /// every frame's applied solve then tests ONLY that lag under the single-candidate
    /// margin. Kills the ln L acceptance starvation and the 16-periodic-probe
    /// pre-cursor alias (M1a: the lag-11 cluster). Reachable only from the salvage
    /// rung and the frozen diag pass; unset = bit-identical. Measured RED (the E1′
    /// post-mortem): the free solve's per-frame choices are frame-local channel truth.
    /// Kept as a measurement seam.</summary>
    internal bool TurboFrozenConsensus { get; set; }

    /// <summary>§B3.7 E1″(a) (Amendment 2): on frozen-pass frames whose accepted lag
    /// exceeds half the probe base period - the pre-cursor folded through the periodic
    /// probe, NOT a causal echo - drop the chain echo model and price the pre-cursor
    /// into the noise floor. The solve, FF, anchors and floor stand (they fit the true
    /// response through the folded column); only the chain application changes.
    /// Frozen/salvage path only; unset = bit-identical.</summary>
    internal bool TurboFrozenAliasNull { get; set; }

    /// <summary>§B3.7 E1″(b), SHIPPED default (Amendment 3): on alias frames, run the
    /// chains EXACTLY on the pre-cursor structure via the observation shift - o[u] =
    /// y[u−d] couples x[u] (through the pre-cursor coefficient, which rides the cursor
    /// slot rotor-free) and x[u−d] (through h1), with d = period − lag. The last d
    /// data symbols are observed only through the pre-cursor coefficient (the mirror
    /// of the causal form's tail truncation). Takes precedence over E1″(a) on alias
    /// frames when both are set. Frozen/salvage path only; false restores the
    /// pre-B3.7 causal-alias application (measurement seam).</summary>
    internal bool TurboFrozenPreCursor { get; set; } = true;

    /// <summary>§B3.8 E3 (Amendments 1/3): the late-lock salvage rung. A causal
    /// acceptance with the echo above the cursor (late path dominant) can leave the
    /// feedback-free FF unable to equalize the frame - the priced floor blows up
    /// 30-80× and the chain drowns on honestly-priced garbage (the trio's bad-frame
    /// class; the same physics rides cleanly on the natural pre-cursor frames). When
    /// the STANDARD salvage fails (Amendment 3 - converging blocks are structurally
    /// untouched: a ceiling block's fixed point can wobble a few bits under ANY seed
    /// change, the w0/b0 b5 lesson), the salvage is retried with the frozen pass
    /// offering the late-lock geometry per causal-accept frame: re-train with the
    /// equalizer window shifted by the accepted lag (the shift performs the re-lock;
    /// the tap shape carries over, so the ridge anchor stays approximately right in
    /// shifted coordinates), solve only the aliased pre-cursor lag, and adopt when
    /// the shifted floor is decisively lower (<see cref="TurboFrozenRelockMargin"/>).
    /// Winning frames run the existing E1″(b) pre-cursor chain with the shift
    /// threaded through. Frozen/salvage path only; false = bit-identical to #93
    /// (measurement seam). SHIPPED default-on (Amendment 3 ship bar).</summary>
    internal bool TurboFrozenRelock { get; set; } = true;

    private bool _frozenRelockActive;

    /// <summary>§B3.8 Amendment 2: the late-lock offer adopts only when the shifted
    /// geometry is decisively better - altVar &lt; margin·noiseVar. 1 = adopt on any
    /// improvement (the pre-margin E3 form). Marginal adoptions within gauge noise of
    /// the two anchor passes are coin flips; the measured target class clears any
    /// reasonable margin (floor improvements of 10-30×, trio adoption medians
    /// 7-38×), so 0.5 keeps the decisive mass and drops the coin flips.</summary>
    internal float TurboFrozenRelockMargin { get; set; } = 0.5f;

    /// <summary>Diagnostic (phase-b-plan §B3.3 fade-crossing): while the oracle
    /// re-equalization runs, TurboCore emits one <c>turbo-frame</c> line per frame with
    /// the per-segment channel anchors and the BCJR noise floor, so the corpse can
    /// compare the estimated tap trajectory against the recorded channel truth.</summary>
    private bool _turboFrameDiag;

    /// <summary>Turbo blocks that reached a decode fixed point (since construction/Reset).</summary>
    public int TurboConverged { get; private set; }

    /// <summary>Turbo blocks reverted to the first-pass decode (no fixed point in 5 iterations).</summary>
    public int TurboReverted { get; private set; }

    /// <summary>§B3.6 salvage: revert-path blocks recovered by the frozen-probe
    /// re-detection seed (8PSK only) - a fresh soft loop from a label-free start found
    /// a fixed point where the first-pass-seeded loop wandered. Salvaged blocks also
    /// count as converged.</summary>
    public int TurboSalvaged { get; private set; }

    /// <summary>Turbo blocks aborted mid-re-equalization (samples no longer available).</summary>
    public int TurboAborted { get; private set; }

    /// <summary>Blocks where the turbo gate declined to run (QAM16, non-resident samples,
    /// or the WN 0 path - the flat-channel skip retired with the DFE-re-solve fallback,
    /// §B2.3).</summary>
    public int TurboSkipped { get; private set; }

    /// <summary>Fresh non-anchored probe re-solves after tracking collapse (phase-b-plan
    /// §B2.1c; since construction/Reset). Zero on healthy runs - a nonzero count says
    /// decision-directed tracking collapsed and was restarted from the probe alone.</summary>
    public int CollapseResolves { get; private set; }

    /// <summary>Locks released because the mini-probes showed no signal for a whole presence
    /// window (<see cref="Ms110dBurstEndReason.SignalAbsent"/>, issue #553; since
    /// construction/Reset). Zero on every decodable burst; one per weak burst that was
    /// acquired but could not be read, counted when the receiver lets go of it.</summary>
    public int SignalAbsentReleases { get; private set; }

    /// <summary>Bursts whose input AGC gain was not unity. WN1/WN2 (K=48) always normalize
    /// their receive level (issue #7), so nearly every such burst counts. Every other
    /// waveform is only boosted below the dead-zone floor (issue #101), so for those a zero
    /// total across a mask point proves the AGC was a strict no-op there.</summary>
    public int AgcResolves { get; private set; }

    /// <summary>8PSK blocks offered to the per-block ensemble (Poor-gate successor G1d):
    /// the chain path's final decode and the MFB-form decoder's, both priced by the
    /// MFB reconstruction residual. Report-only; the census format does not carry it.</summary>
    public int MfbOffered { get; private set; }

    /// <summary>8PSK blocks where the ensemble kept the MFB-form decode over the chain
    /// path's (lower reconstruction residual). G1 measured the two receivers never wrong
    /// on the same block and the residual right on every contested one.</summary>
    public int MfbSelected { get; private set; }

    private Ms110dMfbBlockDecoder? _mfb; // W5b2: lazily built at the first QAM16/8PSK block
    private byte[]? _mfbInfo;            // G1d: the MFB's candidate decode for the ensemble

    /// <summary>Instrument seam (issue #553 tuning): per frame, the input samples consumed so
    /// far, the plain probe-presence statistic and the carrier-cancelled one the release
    /// decides on.
    /// Null (the default) changes nothing.</summary>
    public event Action<long, double, double>? PresenceTraced;

    /// <summary>Fires for every decoded input-data block.</summary>
    public event Action<Ms110dRxBlock>? BlockDecoded;

    /// <summary>Fires when a burst ends (any D.5.4.5 exit).</summary>
    public event Action<Ms110dBurst>? BurstCompleted;

    /// <summary>Current receiver state.</summary>
    public Ms110dRxState State => _state;

    /// <summary>Autobaud result while locked, else null.</summary>
    /// <remarks>Read with <c>Volatile.Read</c>, so a thread other than the one
    /// calling <see cref="Process"/> (a status page, a host modem reporting the locked waveform)
    /// sees the latest lock the receiver published rather than a stale cached one. The lock is an
    /// immutable record, replaced whole, so what it sees is always one consistent lock.</remarks>
    public Ms110dLockInfo? Lock => Volatile.Read(ref _lock);

    /// <summary>True from preamble detection until the burst ends.</summary>
    public bool CarrierDetect => _state != Ms110dRxState.Searching;

    /// <summary>Terminate-receive command (D.5.4.5.2): ends any in-progress burst and
    /// returns to acquisition.</summary>
    public void Terminate()
    {
        _terminate = true;
    }

    /// <summary>Clears all receive state back to Searching.</summary>
    public void Reset()
    {
        _written = 0;
        _mixPhase = 0;
        _decimateToggle = false;
        _combEnergy[0] = _combEnergy[1] = 0;
        Array.Clear(_ring);
        _rxFilterRe = new FirFilter(_rxPulse);
        _rxFilterIm = new FirFilter(_rxPulse);
        if (_genieRing is not null)
        {
            Array.Clear(_genieRing);
            _genieFilterRe = new FirFilter(_rxPulse);
            _genieFilterIm = new FirFilter(_rxPulse);
            _genieMixPhase = 0;
            _genieDecimateToggle = false;
            _genieWritten = 0;
            _genieNoiseSum = 0;
            _genieNoiseCount = 0;
        }

        PeakSearchMetric = 0; // documented as "since construction/reset"
        TurboConverged = 0;
        TurboReverted = 0;
        TurboAborted = 0;
        TurboSkipped = 0;
        CollapseResolves = 0;
        AgcResolves = 0;
        SignalAbsentReleases = 0;
        MfbOffered = 0;
        MfbSelected = 0;
        EndBurst();
    }

    /// <summary>Channel-truth genie seam (diagnostic): feeds the noise-free copy of the
    /// same channel realization. Feed in bounded chunks interleaved with
    /// <see cref="Process"/> - genie ahead of every read, but never more than ~1.7 s ahead
    /// of the noisy stream (the rings are ~13.7 s circles and turbo re-reads whole
    /// interleaver blocks, so running far ahead would overwrite history both streams still
    /// need; this throws rather than silently wrap). While enabled, estimation reads use
    /// this stream and detection keeps the noisy one - see the field comment.</summary>
    public void WriteGenie(ReadOnlySpan<float> samples)
    {
        if (_genieRing is null)
        {
            _genieRing = new Complex32[RingSize];
            _genieFilterRe = new FirFilter(_rxPulse);
            _genieFilterIm = new FirFilter(_rxPulse);
        }

        if (_genieWritten + (samples.Length / 2) - _written > 8192)
        {
            throw new InvalidOperationException(
                "genie stream too far ahead of the noisy stream: interleave WriteGenie/Process in chunks");
        }

        foreach (float sample in samples)
        {
            Complex32 mixed = _mixTable[_genieMixPhase] * sample;
            _genieMixPhase = (_genieMixPhase + 3) & 15;
            float re = _genieFilterRe!.Next(mixed.Re);
            float im = _genieFilterIm!.Next(mixed.Im);
            _genieDecimateToggle = !_genieDecimateToggle;
            if (_genieDecimateToggle)
            {
                continue;
            }

            _genieRing[_genieWritten & (RingSize - 1)] = new Complex32(re, im);
            _genieWritten++;
        }
    }

    /// <summary>Feeds received audio at 9600 Hz.</summary>
    public void Process(ReadOnlySpan<float> samples)
    {
        if (_genieRing is not null && _genieWritten < _written + (samples.Length / 2))
        {
            throw new InvalidOperationException(
                "genie stream behind the noisy stream: call WriteGenie with the covering span before Process");
        }

        foreach (float sample in samples)
        {
            Complex32 mixed = _mixTable[_mixPhase] * sample;
            _mixPhase = (_mixPhase + 3) & 15;
            float re = _rxFilterRe.Next(mixed.Re);
            float im = _rxFilterIm.Next(mixed.Im);
            _decimateToggle = !_decimateToggle;
            if (_decimateToggle)
            {
                continue;
            }

            PushT2(new Complex32(re, im));
        }
    }

    private void PushT2(Complex32 sample)
    {
        long p = _written;
        int parity = (int)(p & 1);
        _combEnergy[parity] += sample.MagnitudeSquared();
        if (p >= 576)
        {
            _combEnergy[parity] -= _ring[(p - 576) & (RingSize - 1)].MagnitudeSquared();
        }

        _ring[p & (RingSize - 1)] = sample;
        if (_genieRing is not null && p < _genieWritten)
        {
            _genieNoiseSum += (sample - _genieRing[p & (RingSize - 1)]).MagnitudeSquared();
            _genieNoiseCount++;
        }

        _written = p + 1;

        if (_terminate)
        {
            _terminate = false;
            if (_state == Ms110dRxState.Tracking)
            {
                CompleteBurst(Ms110dBurstEndReason.Terminated);
                return;
            }

            if (_state == Ms110dRxState.ReadingPreamble)
            {
                BackToSearch();
            }
        }

        switch (_state)
        {
            case Ms110dRxState.Searching:
                SearchStep();
                break;
            case Ms110dRxState.ReadingPreamble:
                TryReadPreamble();
                break;
            case Ms110dRxState.Tracking:
                TryTrack();
                break;
        }
    }

    // ------------------------------------------------------------------ acquisition

    private void SearchStep()
    {
        long p = _written - 1;
        if (p < 574)
        {
            return;
        }

        long s = p - 574;
        double energy = _combEnergy[(int)(s & 1)];
        if (energy <= 1e-12)
        {
            return;
        }

        double best = 0;
        int bestBin = 0;
        for (int b = 0; b < BinsHz.Length; b++)
        {
            double metric = Metric(s, b, energy, null);
            if (metric > best)
            {
                best = metric;
                bestBin = b;
            }
        }

        _metricRing[s & 255] = best;
        _metricBinRing[s & 255] = (byte)bestBin;

        if (best > PeakSearchMetric)
        {
            PeakSearchMetric = best;
        }

        if (best > _options.SyncThreshold && best > _bestMetric)
        {
            _bestMetric = best;
            _bestStart = s;
            _bestBin = bestBin;
        }

        if (_bestStart >= 0 && s - _bestStart > 192)
        {
            AcceptPeak();
        }
    }

    private double Metric(long s, int bin, double energy, Complex32[]? segments)
    {
        Complex32[] reference = _mfReference[bin];
        double sum = 0;
        for (int k = 0; k < 9; k++)
        {
            var c = Complex32.Zero;
            int chip = 32 * k;
            for (int i = 0; i < 32; i++, chip++)
            {
                c += _ring[(s + (2 * chip)) & (RingSize - 1)] * reference[chip];
            }

            if (segments is not null)
            {
                segments[k] = c;
            }

            sum += c.Abs();
        }

        return sum / (Math.Sqrt(ChipsFixed) * Math.Sqrt(energy) + 1e-12);
    }

    private void AcceptPeak()
    {
        long s = _bestStart;
        int bin = _bestBin;

        // Earliest-arrival selection: walk back up to 9 ms (the widest D-LXV spread,
        // 21.6 symbols = 44 T/2 samples) and take the first candidate within −4.4 dB of
        // the best - on equal-power multipath every arrival correlates comparably, and
        // the DFE needs the cursor on the FIRST one. On a clean channel the matched-filter
        // response collapses within ±1 sample, so this is a no-op there.
        double floor = Math.Max(_options.SyncThreshold, 0.6 * _bestMetric);
        for (long candidate = Math.Max(574, s - 44); candidate < s; candidate++)
        {
            if (_metricRing[candidate & 255] >= floor)
            {
                s = candidate;
                bin = _metricBinRing[candidate & 255];
                break;
            }
        }

        FrameDiagnostics?.Invoke(
            $"accept@{s}: best={_bestStart} metric={_bestMetric:F3} " +
            $"walkback={_bestStart - s} bin={bin}");

        double energy = _combEnergy[(int)(s & 1)];

        // Sub-sample timing from a parabolic fit through the metric at s−1, s, s+1.
        var segments = new Complex32[9];
        double m0 = Metric(s, bin, energy, segments);
        double mm = Metric(s - 1, bin, _combEnergy[(int)((s - 1) & 1)], null);
        double mp = Metric(s + 1, bin, _combEnergy[(int)((s + 1) & 1)], null);
        double denom = mm - (2 * m0) + mp;
        double delta = Math.Abs(denom) > 1e-9 ? 0.5 * (mm - mp) / denom : 0;
        delta = Math.Clamp(delta, -1, 1);

        // Fine CFO from the phase progression across the nine 32-chip segment correlations
        // (±37.5 Hz unambiguous - inside the 25 Hz grid residual).
        var rotation = Complex32.Zero;
        for (int k = 0; k < 8; k++)
        {
            rotation += segments[k + 1] * segments[k].Conj();
        }

        double fineHz = rotation.Arg() * Ms110dTables.SymbolRate / (2.0 * Math.PI * 32.0);
        double cfoHz = BinsHz[bin] + fineHz;

        _chip0 = s + delta;
        _tau = 0;
        _omega = 2.0 * Math.PI * cfoHz / (2.0 * Ms110dTables.SymbolRate);
        _thetaBase = 0;
        _lock = new Ms110dLockInfo(-1, Ms110dInterleaverKind.Short, 7, cfoHz);
        _state = Ms110dRxState.ReadingPreamble;
        _bestMetric = 0;
        _bestStart = -1;
    }

    private void TryReadPreamble()
    {
        // Need the whole matched super-frame plus interpolation margin.
        if (_written < (long)Math.Ceiling(_chip0 + (2 * ChipsSuperframe * 1.0)) + (2 * InterpHalf) + 2)
        {
            return;
        }

        Span<byte> countDibits = stackalloc byte[4];
        for (int j = 0; j < 4; j++)
        {
            countDibits[j] = ReadWalshSymbol(ChipsFixed + (32 * j), Ms110dTables.CntPn, 32 * j);
        }

        if (!PreambleGenerator.TryDecodeCount(countDibits, out int count))
        {
            FrameDiagnostics?.Invoke($"reject@{_chip0}: gate=count cfo={_lock!.CfoHz:F1}");
            BackToSearch();
            return;
        }

        FrameDiagnostics?.Invoke(
            $"count@{_chip0}: dibits={countDibits[0]}{countDibits[1]}{countDibits[2]}{countDibits[3]} count={count}");

        // §B3 WID vote (issue #69): the WID section repeats identically in every
        // remaining preamble super-frame, all of which arrive BEFORE data start - so
        // soft-combining it across super-frames costs zero latency and rides out a
        // fade over any one of them. Read from a single super-frame, a −15 dB fade
        // corrupted the dibits AND beat the checksum twice in the WN1 Poor census:
        // lock=K9/tx=K7 and lock=Medium/tx=Long, each decoding its whole burst to
        // ~50% garbage while the wire-order telemetry stayed healthy (Class D).
        int votes = Math.Min(count + 1, 5);
        if (_written < (long)Math.Ceiling(
                _chip0 + (2.0 * ChipsSuperframe * votes)) + (2 * InterpHalf) + 2)
        {
            return; // wait for the vote span - still entirely pre-data
        }

        Span<double> widMags = stackalloc double[5 * 4];
        widMags.Clear();
        for (int v = 0; v < votes; v++)
        {
            for (int j = 0; j < 5; j++)
            {
                AccumulateWalshMags(
                    (v * ChipsSuperframe) + ChipsFixed + 128 + (32 * j),
                    Ms110dTables.WidPn, 32 * j, widMags.Slice(j * 4, 4));
            }
        }

        Span<byte> widDibits = stackalloc byte[5];
        double marginSum = 0;
        for (int j = 0; j < 5; j++)
        {
            ReadOnlySpan<double> mags = widMags.Slice(j * 4, 4);
            byte bestDibit = 0;
            for (byte s = 1; s < 4; s++)
            {
                if (mags[s] > mags[bestDibit])
                {
                    bestDibit = s;
                }
            }

            double second = 0;
            for (int s = 0; s < 4; s++)
            {
                if (s != bestDibit && mags[s] > second)
                {
                    second = mags[s];
                }
            }

            marginSum += (mags[bestDibit] - second) / (mags[bestDibit] + 1e-9);
            widDibits[j] = bestDibit;
        }

        // Vote-confidence gate: a wrong acquisition (e.g. a −18 Hz CFO-bin miss, WN1
        // census burst w1/b28) turns every Walsh correlation to mush, and an argmax
        // over summed noise beats the weak WID checksum ~1-in-8 - where the OLD
        // single-read path failed the checksum per super-frame and fell back to
        // re-search, eventually re-acquiring the true peak. Keep that safety valve:
        // a mushy vote (mean winner-vs-runner-up margin below the floor) is a failed
        // acquisition candidate, not a lock. A genuine lock with one faded
        // super-frame still clears the floor easily (the healthy super-frames
        // dominate the sums).
        double voteMargin = marginSum / 5;
        FrameDiagnostics?.Invoke($"wid@{_chip0}: votes={votes} margin={voteMargin:F3}");
        if (voteMargin < 0.20)
        {
            FrameDiagnostics?.Invoke(
                $"reject@{_chip0}: gate=margin margin={voteMargin:F3} cfo={_lock!.CfoHz:F1}");
            BackToSearch();
            return;
        }

        // §B3.5 Amendment 1: joint count vote over the same span the WID vote already
        // waits for. The count is the last single-read acquisition field (4 dibits,
        // 3 check bits - corrupted reads beat the check 1-in-8), and a wrong count
        // places data start whole super-frames off behind a clean-looking lock (four
        // coin-flip bursts in the WN0 Poor census; the WID's Class-D failure, again).
        // The field decrements per super-frame, so the vote is decrement-aligned:
        // candidate c at vote frame v expects EncodeCount(c−v).
        Span<double> cntMags = stackalloc double[5 * 4 * 4];
        cntMags.Clear();
        for (int v = 0; v < votes; v++)
        {
            for (int j = 0; j < 4; j++)
            {
                AccumulateWalshMags(
                    (v * ChipsSuperframe) + ChipsFixed + (32 * j),
                    Ms110dTables.CntPn, 32 * j, cntMags.Slice(((v * 4) + j) * 4, 4));
            }
        }

        double bestScore = -1, secondScore = -1;
        int jointCount = -1;
        for (int c = votes - 1; c < 32; c++)
        {
            double score = 0;
            for (int v = 0; v < votes; v++)
            {
                byte[] exp = CountWords[c - v];
                for (int j = 0; j < 4; j++)
                {
                    score += cntMags[(((v * 4) + j) * 4) + exp[j]];
                }
            }

            if (score > bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                jointCount = c;
            }
            else if (score > secondScore)
            {
                secondScore = score;
            }
        }

        double countMargin = (bestScore - secondScore) / (bestScore + 1e-9);
        FrameDiagnostics?.Invoke(
            $"count-vote@{_chip0}: single={count} joint={jointCount} margin={countMargin:F3} frames={votes}");
        if (countMargin < 0.10)
        {
            BackToSearch(); // mushy joint = failed acquisition candidate, not a lock
            return;
        }

        count = jointCount;

        if (!PreambleGenerator.TryDecodeWid(widDibits, out int wn, out Ms110dInterleaverKind il, out int k) ||
            !IsSupported(wn) ||
            !Ms110dInterleaverParams.Has3k(wn, il))
        {
            // Has3k: at low SNR a corrupted WID can pass its checksum yet name a
            // (waveform, interleaver) pair Table D-XXXVII does not define - e.g.
            // (WN 0, UltraShort). That is a failed acquisition candidate, not a
            // crash: Get3k throwing here killed the receiver mid-burst (found by
            // the Poor WN0 mask run at −1 dB, seed 500, ~2.8M bits in).
            FrameDiagnostics?.Invoke(
                $"reject@{_chip0}: gate=wid wn={wn} il={il} k={k} cfo={_lock!.CfoHz:F1}");
            BackToSearch();
            return;
        }

        RefineCarrier(0, SuperframeChips(count, wn, il, k));
        _lock = new Ms110dLockInfo(wn, il, k, _lock!.CfoHz);

        // Autopsy hook: the (waveform, interleaver, K) actually locked, so a burst that
        // reaches Tracking yet decodes nothing can be told apart from a mis-voted WID that
        // merely passed its checksum. Fire-and-forget; observing the lock cannot change it.
        FrameDiagnostics?.Invoke($"lock@{_chip0}: wn={wn} il={il} k={k} cfo={_lock.CfoHz:F1}");
        _mode = Ms110dMode.Mode3k(wn);
        _il = Ms110dInterleaverParams.Get3k(wn, il);
        ConvolutionalCode code = k == 9 ? ConvolutionalCode.K9 : ConvolutionalCode.K7;
        _code = code;
        _viterbi = new TailBitingViterbiDecoder(code);
        _puncture = Ms110dPuncture.Get(code, _mode.CodeRate);
        _interleaver = new Ms110dInterleaver(_il.SizeBits, _il.Increment);
        _blockLlrs = new float[_il.SizeBits];
        _siso = new TailBitingSisoDecoder(code);
        _softPunctured = new float[_il.SizeBits];
        _softMother = new float[2 * _il.InputBits];
        _softMotherPost = new float[2 * _il.InputBits];
        _softWireLlrs = new float[_il.SizeBits];
        _softWireExt = new float[_il.SizeBits];
        int blockSymbols = _il.Frames * _mode.U; // 0 for WN0 (Walsh) - turbo never runs there
        _softExpected = new Complex32[blockSymbols];
        _softVar = new float[blockSymbols];
        _turboExpected = new Complex32[blockSymbols];
        _blockLlrCount = 0;
        _blockIndex = 0;
        _probeLongFrames = ProbePattern.LongFrames(_mode.Wn);
        _burstBits.Clear();
        _dataStartChip = ChipsSuperframe * (long)(count + 1);
        _trackingInitialized = false;
        _state = Ms110dRxState.Tracking;
    }

    /// <summary>How many trailing preamble super-frames the carrier re-fit may use: all
    /// the super-frames we know exist (from the matched one to data start), capped at 4
    /// (~1 s - enough baseline to average Rayleigh phase drift out of the CFO fit).</summary>
    private int TailRefineSuperframes()
    {
        return (int)Math.Clamp(_dataStartChip / ChipsSuperframe, 1, 4);
    }

    private static bool IsSupported(int wn)
    {
        return wn is >= 0 and <= 8 or 13;
    }

    private byte ReadWalshSymbol(int startChip, ReadOnlySpan<byte> pn, int pnOffset)
    {
        Span<Complex32> corr = stackalloc Complex32[4];
        byte[] w1 = Ms110dTables.Walsh[1];
        byte[] w2 = Ms110dTables.Walsh[2];
        byte[] w3 = Ms110dTables.Walsh[3];
        for (int i = 0; i < 32; i++)
        {
            Complex32 r = ReadChip(startChip + i) * Ms110dTables.Psk8[pn[pnOffset + i]].Conj();
            corr[0] += r;
            corr[1] = w1[i & 3] == 0 ? corr[1] + r : corr[1] - r;
            corr[2] = w2[i & 3] == 0 ? corr[2] + r : corr[2] - r;
            corr[3] = w3[i & 3] == 0 ? corr[3] + r : corr[3] - r;
        }

        byte bestDibit = 0;
        float best = corr[0].MagnitudeSquared();
        for (byte s = 1; s < 4; s++)
        {
            float m = corr[s].MagnitudeSquared();
            if (m > best)
            {
                best = m;
                bestDibit = s;
            }
        }

        return bestDibit;
    }

    /// <summary>Adds the four Walsh-hypothesis correlation magnitudes for one preamble
    /// dibit into <paramref name="mags"/> - the soft accumulator for the §B3 WID vote
    /// (magnitudes, not phasors: superframes a Rayleigh fade apart are not coherent).</summary>
    private void AccumulateWalshMags(int startChip, ReadOnlySpan<byte> pn, int pnOffset, Span<double> mags)
    {
        Span<Complex32> corr = stackalloc Complex32[4];
        byte[] w1 = Ms110dTables.Walsh[1];
        byte[] w2 = Ms110dTables.Walsh[2];
        byte[] w3 = Ms110dTables.Walsh[3];
        for (int i = 0; i < 32; i++)
        {
            Complex32 r = ReadChip(startChip + i) * Ms110dTables.Psk8[pn[pnOffset + i]].Conj();
            corr[0] += r;
            corr[1] = w1[i & 3] == 0 ? corr[1] + r : corr[1] - r;
            corr[2] = w2[i & 3] == 0 ? corr[2] + r : corr[2] - r;
            corr[3] = w3[i & 3] == 0 ? corr[3] + r : corr[3] - r;
        }

        for (int s = 0; s < 4; s++)
        {
            mags[s] += corr[s].Abs();
        }
    }

    /// <summary>Known chips of the last <paramref name="superframes"/> preamble
    /// super-frames before data start (downcounts n−1 … 0).</summary>
    private static byte[] TailSuperframeChips(int superframes, int wn, Ms110dInterleaverKind il, int k)
    {
        var chips = new byte[superframes * ChipsSuperframe];
        for (int i = 0; i < superframes; i++)
        {
            SuperframeChips(superframes - 1 - i, wn, il, k).CopyTo(chips, i * ChipsSuperframe);
        }

        return chips;
    }

    private static byte[] SuperframeChips(int count, int wn, Ms110dInterleaverKind il, int k)
    {
        byte[] known = new byte[ChipsSuperframe];
        new PreambleGenerator(0, 2).FixedSectionChips().CopyTo(known, 0);
        PreambleGenerator.CountSectionChips(count).CopyTo(known, ChipsFixed);
        PreambleGenerator.WidSectionChips(wn, il, k).CopyTo(known, ChipsFixed + 128);
        return known;
    }

    /// <summary>Fits the residual carrier phase/frequency over fully known preamble
    /// chips at <paramref name="baseChip"/> and re-tunes the carrier model anchored
    /// inside the measurement window. Longer windows (several super-frames) matter on
    /// fading channels: a single 240 ms super-frame reads Rayleigh phase drift as CFO -
    /// a ~1 s baseline averages it out.</summary>
    private void RefineCarrier(long baseChip, byte[] knownChips)
    {
        int count = knownChips.Length / 32;
        Span<Complex32> groups = count <= 72 ? stackalloc Complex32[72] : new Complex32[count];
        groups = groups[..count];
        for (int j = 0; j < count; j++)
        {
            var c = Complex32.Zero;
            for (int i = 0; i < 32; i++)
            {
                int chip = (32 * j) + i;
                // Estimation read: the carrier fit is channel estimation (genie-eligible).
                c += ReadChipEst(baseChip + chip) * Ms110dTables.Psk8[knownChips[chip]].Conj();
            }

            groups[j] = c;
        }

        if (!EstimateCarrierFit(groups, out double slope, out double intercept))
        {
            return;
        }

        // Anchor the correction at the window midpoint (group j's phase sits at its
        // centre, chip 32j+16); slope converts to rad per T/2 sample by ÷64.
        double mid = (count - 1) / 2.0;
        double href = (2.0 * (baseChip + 16 + (32 * mid))) + _tau;
        RetuneCarrier(href, intercept + (slope * mid), slope / 64.0);
        _lock = _lock! with { CfoHz = _lock.CfoHz + (slope / 64.0 * 2.0 * Ms110dTables.SymbolRate / (2.0 * Math.PI)) };

        if (FrameDiagnostics is not null)
        {
            // §B3 autopsy: the per-group correlation phases and magnitudes behind the fit.
            var sb = new System.Text.StringBuilder((count * 12) + 64);
            sb.Append($"refine@{baseChip}: groups={count} " +
                $"slopeHz={slope / 64.0 * 2.0 * Ms110dTables.SymbolRate / (2.0 * Math.PI):F2} " +
                $"intercept={intercept:F3} pw=");
            for (int j = 0; j < count; j++)
            {
                sb.Append($"{groups[j].Arg():F2}/{groups[j].Abs():F1} ");
            }

            FrameDiagnostics.Invoke(sb.ToString());
        }
    }

    /// <summary>
    /// Carrier phase/frequency fit over per-group correlation phasors - the fade-robust
    /// replacement for the weighted phase regression (§B3 tail autopsy, issue #69). The
    /// regression's sequential phase UNWRAP was its undoing: groups inside a deep fade
    /// carry pure noise phases, and although the regression downweighted them, they still
    /// formed the unwrap chain - a &gt;200 ms fade let the chain random-walk several full
    /// turns, the post-fade groups re-entered on the wrong branch with strong weights,
    /// and the fit manufactured a multi-Hz CFO from a channel that had barely rotated
    /// (WN5 Poor census, burst w0/b3: −4.86 Hz fitted, burst dead end-to-end at SER 0.55
    /// while every probe statistic read healthy). Here the slope comes from weighted
    /// lag products - arg Σ c[j+L]·c̄[j] - where a faded group self-suppresses
    /// (the product magnitude vanishes) instead of poisoning its neighbours: lag 1 is
    /// unambiguous to ±π per group (±37.5 Hz), then a longer-lag pass sharpens the
    /// estimate with the lag-1 value resolving its branch. The intercept is the phase of
    /// the coherent de-rotated sum - fade-robust for the same reason.
    /// </summary>
    /// <returns>False when the window carries no usable signal (keep the current
    /// carrier rather than retune on noise).</returns>
    internal static bool EstimateCarrierFit(
        ReadOnlySpan<Complex32> groups, out double slopeRadPerGroup, out double interceptRad)
    {
        slopeRadPerGroup = 0;
        interceptRad = 0;
        int count = groups.Length;
        if (count < 2)
        {
            return false;
        }

        var lag1 = Complex32.Zero;
        for (int j = 0; j + 1 < count; j++)
        {
            lag1 += groups[j + 1] * groups[j].Conj();
        }

        if (lag1.MagnitudeSquared() < 1e-12)
        {
            return false;
        }

        double slope = lag1.Arg();

        int lag = Math.Min(8, count - 1);
        if (lag > 1)
        {
            var lagL = Complex32.Zero;
            for (int j = 0; j + lag < count; j++)
            {
                lagL += groups[j + lag] * groups[j].Conj();
            }

            if (lagL.MagnitudeSquared() > 1e-12)
            {
                // Resolve the lag-L phase onto the branch nearest the lag-1 estimate.
                double expected = slope * lag;
                double phiL = lagL.Arg();
                phiL += 2.0 * Math.PI * Math.Round((expected - phiL) / (2.0 * Math.PI));
                slope = phiL / lag;
            }
        }

        var anchor = Complex32.Zero;
        for (int j = 0; j < count; j++)
        {
            anchor += groups[j] * Complex32.FromPhase((float)(-slope * j));
        }

        if (anchor.MagnitudeSquared() < 1e-12)
        {
            return false;
        }

        slopeRadPerGroup = slope;
        interceptRad = anchor.Arg();
        return true;
    }

    private void BackToSearch()
    {
        _state = Ms110dRxState.Searching;
        _lock = null;
        _bestMetric = 0;
        _bestStart = -1;
    }

    // ------------------------------------------------------------------ sample access

    private double PositionOfChip(double chip)
    {
        return _chip0 + (2.0 * chip) + _tau;
    }

    private Complex32 ReadT2(double halfChips)
    {
        double pos = _chip0 + halfChips + _tau;
        var value = Interpolate(pos, _ring, _written);
        double theta = _thetaBase + (_omega * (pos - _chip0));
        // #101 input AGC (_agcGain): normalizes the receive level ahead of the equalizer.
        // Unity during acquisition; after it, unity for every nominal-or-stronger burst
        // except WN1/WN2, which always normalize (issue #7).
        return value * Complex32.FromPhase(-((float)theta)) * _agcGain;
    }

    /// <summary>Estimation-side read: the genie ring when the genie is enabled, otherwise
    /// the normal noisy read. Same carrier/timing model either way - the genie replaces
    /// the DATA under the estimators, not the receiver's own state.</summary>
    private Complex32 ReadT2Est(double halfChips)
    {
        if (_genieRing is null)
        {
            return ReadT2(halfChips);
        }

        double pos = _chip0 + halfChips + _tau;
        // Bound by the NOISY stream's availability: the genie replaces sample values, not
        // the receiver's causality - the interpolator's head truncation must be identical
        // in both rings or a genie run differs even when fed the noisy stream itself.
        var value = Interpolate(pos, _genieRing, Math.Min(_genieWritten, _written));
        double theta = _thetaBase + (_omega * (pos - _chip0));
        return value * Complex32.FromPhase(-((float)theta)) * _agcGain; // #101 AGC (see ReadT2)
    }

    private Complex32 ReadChip(double chip)
    {
        return ReadT2(2.0 * chip);
    }

    private Complex32 ReadChipEst(double chip)
    {
        return ReadT2Est(2.0 * chip);
    }

    private Complex32 Interpolate(double pos, Complex32[] ring, long written)
    {
        long i0 = (long)Math.Floor(pos);
        double frac = pos - i0;
        var acc = Complex32.Zero;
        for (int j = -InterpHalf + 1; j <= InterpHalf; j++)
        {
            double u = j - frac;
            double w;
            if (Math.Abs(u) < 1e-9)
            {
                w = 1;
            }
            else
            {
                w = Math.Sin(Math.PI * u) / (Math.PI * u) * (0.5 + (0.5 * Math.Cos(Math.PI * u / InterpHalf)));
            }

            long idx = i0 + j;
            if (idx >= 0 && idx < written)
            {
                acc += ring[idx & (RingSize - 1)] * (float)w;
            }
        }

        return acc;
    }

    private bool HaveSamplesForChip(double chip)
    {
        return _written > (long)Math.Ceiling(PositionOfChip(chip)) + InterpHalf + 1;
    }

    // ------------------------------------------------------------------ tracking

    private void TryTrack()
    {
        if (_mode!.Wn == 0)
        {
            TrackWalsh();
            return;
        }

        if (!_trackingInitialized)
        {
            if (!HaveSamplesForChip(_dataStartChip + _mode.K + 4))
            {
                return;
            }

            InitializeDfe();
            _trackingInitialized = true;
        }

        while (_state == Ms110dRxState.Tracking &&
               HaveSamplesForChip(_frameChip + _mode.U + _mode.K + 4))
        {
            ProcessFrame();
        }
    }

    /// <summary>Fade-averaged global receive SIGNAL level (issue #101): coherently correlate
    /// the received Fixed subsection of each trailing preamble super-frame against the known
    /// Fixed chips in 32-chip groups - noise averages out of each group, and |Σ y·k̄|/32 is
    /// that group's signal amplitude - then average over all groups. Averaging several
    /// super-frames (~1-2 s) averages the ~1 Hz Watterson fade out, so the result reflects the
    /// GLOBAL level (a real-RF weak signal), not the instantaneous fade - the property that
    /// lets the AGC no-op through a nominal fade yet catch a globally-low level. Reads at the
    /// current <see cref="_agcGain"/> (unity here, before the AGC is set), so it measures the
    /// true level. Matched to the signal, NOT total power - total RMS tracks SNR, so a
    /// total-power AGC would attenuate at low SNR and disturb the masks.</summary>
    private double EstimatePreambleLevel()
    {
        byte[] fixedChips = new PreambleGenerator(0, 2).FixedSectionChips();
        int sfAvail = (int)Math.Clamp(_dataStartChip / ChipsSuperframe, 1, 8);
        double sum = 0;
        int groups = 0;
        for (int sf = 1; sf <= sfAvail; sf++)
        {
            long baseChip = _dataStartChip - (sf * ChipsSuperframe);
            for (int g = 0; g < ChipsFixed / 32; g++)
            {
                var c = Complex32.Zero;
                for (int i = 0; i < 32; i++)
                {
                    int chip = (g * 32) + i;
                    c += ReadChipEst(baseChip + chip) * Ms110dTables.Psk8[fixedChips[chip]].Conj();
                }

                sum += c.Abs() / 32.0;
                groups++;
            }
        }

        return groups > 0 ? sum / groups : 0;
    }

    private void InitializeDfe()
    {
        // #101 input signal-level AGC: estimate the global receive level and set the per-burst
        // normalization BEFORE the carrier refit and DFE training read the (now-normalized)
        // signal. _agcGain is unity here, so EstimatePreambleLevel measures the true level; the
        // dead-zone keeps _agcGain = 1.0 for every nominal-or-stronger burst (a strict no-op -
        // acquisition already happened at unity and the sim masks are unchanged by
        // construction), and only a globally-low burst (real-RF) is scaled up to nominal.
        // WN1/WN2 (K=48) skip the dead-zone and always normalize to K48LevelTarget (issue #7).
        double agcLevel = EstimatePreambleLevel();
        if (agcLevel <= 0)
        {
            _agcGain = 1.0f;
        }
        else if (_mode!.K == 48)
        {
            _agcGain = (float)Math.Min(K48LevelTarget / agcLevel, AgcMaxGain); // issue #7
        }
        else
        {
            _agcGain = agcLevel >= AgcLevelFloor
                ? 1.0f
                : (float)Math.Min(AgcNominalLevel / agcLevel, AgcMaxGain);
        }

        if (_agcGain != 1.0f)
        {
            AgcResolves++;
        }

        FrameDiagnostics?.Invoke($"agc@{_dataStartChip}: level={agcLevel:F4} gain={_agcGain:F3}");

        // Same stale-extrapolation concern as the WN0 path: re-fit the carrier over the
        // final super-frames before training (see TrackWalsh).
        int tail = TailRefineSuperframes();
        RefineCarrier(
            _dataStartChip - (tail * ChipsSuperframe),
            TailSuperframeChips(tail, _lock!.WaveformNumber, _lock.Interleaver, _lock.ConstraintLength));
        _omegaAcquired = _omega;

        // Tap counts per design §2.5. Leads are sized so the feed-forward window spans
        // roughly ±2 ms around the cursor: forward to collect echo energy (the 3 ms path
        // of the WID 2 static rig at K=48), and BACKWARD far enough that a path earlier
        // than the locked one stays equalizable - on the fading Poor channel the lock can
        // land on the later path while the earlier one is faded, and its return puts a
        // −2 ms (−9.6 T/2) pre-cursor into the window.
        (int ff, int fb, int lead, float initRidge, float trackRidge) = _mode!.K switch
        {
            // K=48 (WN1/2, rate 1/8 & 1/4, run at the −3/0 dB and 5 dB static gates) has the
            // widest DFE - 32 FF + 22 FB = 54 complex taps - yet the FEWEST data symbols per
            // frame (U=48) to excite them, at the LOWEST SNR of the ladder. A weak ridge lets
            // the off-cursor feed-forward taps fit noise (measured: WN1 AWGN 4.5E-5 vs the
            // 1E-5 gate; a shrunk 12-tap FF cleared AWGN but starved the static echo). The MMSE
            // ridge at −3 dB is order-1×trace (noise ≈ signal), so K=48 uses a strong ridge
            // toward zero (initial). K=32/24 keep their original (already-green) light ridge.
            //
            // The per-probe TRACK ridge is stronger still (§B3.2, issue #69): the WN2 Poor
            // genie pair measured a flat estimation-noise tax (+0.02 SER in healthy frames,
            // uniform - NOT fade-edge lag), and the anchor ridge is the solve's cross-frame
            // memory. The measured sweep (WN2 +5 dB smoke): ridge 0.5/1/2/4/8/16 →
            // 43/42/20/5/1/23 coded errors - an optimum at 8, where the anchored equalizer
            // coasts instead of chasing fades with noisy solves. Uncoded SER RISES (lag) but
            // wrong-sign LLR mass drops 2.35×: where the channel deviates from the anchored
            // estimate the output amplitude collapses, so errors self-report low confidence
            // (soft erasures) instead of confident coin-flips. The 40 ms K=48 frame makes
            // ~8-frame memory ≈ 300 ms, inside the 1 Hz coherence time; U=256's 120 ms
            // frames forbid this (measured: WN13 at 4× its ridge → 4.9E-2), which is why
            // the value is per-K, not global.
            48 => (32, 22, 16, 1.0f, 8.0f),
            24 => (16, 6, 8, 1e-3f, 0.15f),
            _ => (24, 12, 13, 1e-3f, 0.15f),
        };
        _dfe = new Dfe(ff, fb);
        _ffLead = lead;
        _initRidge = initRidge;
        _trackRidge = _options.TrackRidge ?? trackRidge;

        // Known symbols for chips [dataStart−576, dataStart+K): the final super-frame
        // (count = 0) plus the preamble-ending probe (design §2.4).
        int k = _mode.K;
        _known = new Complex32[ChipsSuperframe + k];
        byte[] chips = new byte[ChipsSuperframe];
        new PreambleGenerator(0, 2).FixedSectionChips().CopyTo(chips, 0);
        PreambleGenerator.CountSectionChips(0).CopyTo(chips, ChipsFixed);
        PreambleGenerator.WidSectionChips(_lock!.WaveformNumber, _lock.Interleaver, _lock.ConstraintLength)
            .CopyTo(chips, ChipsFixed + 128);
        for (int i = 0; i < ChipsSuperframe; i++)
        {
            _known[i] = Ms110dTables.Psk8[chips[i]];
        }

        MiniProbe.Get(k, boundary: false).CopyTo(_known, ChipsSuperframe);

        // Regularized LS solve over the last half super-frame + probe.
        long baseChip = _dataStartChip - ChipsSuperframe;
        _dfe.BeginTraining();
        Span<Complex32> window = stackalloc Complex32[ff];
        Span<Complex32> past = stackalloc Complex32[fb];
        for (int n = ChipsFixed; n < ChipsSuperframe + k; n++)
        {
            FillWindowEst(baseChip + n, window);
            for (int j = 0; j < fb; j++)
            {
                past[j] = _known[n - 1 - j];
            }

            _dfe.AddTrainingRow(window, past, _known[n]);
        }

        // RLS forgetting policy - a DOCUMENTED DEVIATION from design §2.5 (issue #64):
        // λ = 1 − ln10/U ties the exponential window to the frame (memory U/ln10 ≈ 0.43·U
        // symbols, i.e. a 10× down-weight per data span), so the per-probe anchored batch
        // solve, not the RLS recursion, owns cross-frame memory. §2.5 specified a fixed
        // λ = 0.995 (≈200-symbol/83 ms memory, set by the 1 Hz coherence time); for U=48
        // the frame-tied window is only ~21 symbols ≈ 8.7 ms - far shorter than the physics
        // needs, noisier than it has to be. Which policy wins is a measurement question:
        // the Phase B RLS-vs-NLMS A/B (phase-b-plan §B2.4) settles it; until then this is
        // the measured-baseline value, kept so evidence stays comparable.
        _dfe.BeginRls(
            _options.RlsForgettingFactor ?? (float)(1.0 - Math.Log(10.0) / _mode.U), pInit: 1.0f);
        _dfe.SeedRlsFromTraining(_initRidge, pFallback: 1.0f, ffNoisePower: GenieNoisePower());
        _dfe.SolveTraining(regularization: _initRidge, ffNoisePower: GenieNoisePower());
        _dfe.BeginTraining();

        // Seed the decision history with the probe tail and measure the training MSE.
        _decisions = new Complex32[fb];
        for (int j = 0; j < fb; j++)
        {
            _decisions[j] = _known[ChipsSuperframe + k - 1 - j];
        }

        double mse = 0;
        double energy = 0;
        var gain = Complex32.Zero;
        for (int i = 0; i < k; i++)
        {
            int n = ChipsSuperframe + i;
            FillWindowEst(baseChip + n, window);
            for (int j = 0; j < fb; j++)
            {
                past[j] = _known[n - 1 - j];
            }

            Complex32 y = _dfe.Equalize(window, past);
            gain += y * _known[n].Conj();
            Complex32 err = y - _known[n];
            mse += err.MagnitudeSquared();
            energy += window[_ffLead].MagnitudeSquared();
        }

        _probeMse = mse / k;
        _probeGainRef = gain.Abs() / k;
        _probeEnergyRef = energy / k;
        _badProbes = 0;
        _collapsedProbes = 0;
        _collapseArmed = true;
        StartPresenceWindow(_mode!);
        _frameChip = _dataStartChip + k;
        _frameInBlock = 0;
    }

    private void FillWindow(double symbolChip, Span<Complex32> window)
    {
        double h = 2.0 * symbolChip;
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = ReadT2(h + _ffLead - i);
        }
    }

    /// <summary>Estimation-side window fill (training rows, channel estimation): genie ring
    /// when enabled, otherwise identical to <see cref="FillWindow"/>.</summary>
    private void FillWindowEst(double symbolChip, Span<Complex32> window)
    {
        double h = 2.0 * symbolChip;
        for (int i = 0; i < window.Length; i++)
        {
            window[i] = ReadT2Est(h + _ffLead - i);
        }
    }

    private void ProcessFrame()
    {
        Ms110dMode mode = _mode!;
        Dfe dfe = _dfe!;
        Span<Complex32> window = stackalloc Complex32[dfe.FfTaps];
        Span<Complex32> probePast = stackalloc Complex32[dfe.FbTaps];
        // Genie mode fills a second, estimation-side window per symbol: detection reads
        // stay noisy, adaptation rows read the clean stream (see the genie field comment).
        bool genie = _genieRing is not null;
        Span<Complex32> estWindow = stackalloc Complex32[dfe.FfTaps];

        // Probe-directed batch-LS: known symbols accumulate into the Gram with
        // authoritative weight; the ridge-regularized solve (anchored to current taps)
        // handles the probe's rank deficiency (≤16 distinct patterns for 36+ taps).
        long probeChip = _frameChip + mode.U;
        long frameNumber = ((long)_blockIndex * _il!.Frames) + _frameInBlock;
        Complex32[] probe = MiniProbe.Get(mode.K, ProbeShiftedAfter(frameNumber));
        Complex32[] startTaps = dfe.SnapshotTaps();
        var probePhase = Complex32.Zero;
        double mse = 0;
        double probeEnergy = 0;
        int statRows = 0;
        for (int i = dfe.FbTaps; i < mode.K; i++)
        {
            // Estimation reads: the probe solve and its gain/MSE discriminators.
            FillWindowEst(probeChip + i, window);
            for (int j = 0; j < dfe.FbTaps; j++)
            {
                probePast[j] = probe[i - 1 - j];
            }

            Complex32 y = dfe.Equalize(window, probePast);
            probePhase += y * probe[i].Conj();
            mse += (y - probe[i]).MagnitudeSquared();
            probeEnergy += window[_ffLead].MagnitudeSquared();
            statRows++;
            dfe.AddTrainingRow(window, probePast, probe[i], weight: 6f);
        }

        // Collapse detection (phase-b-plan §B2.1c; WN7 autopsy: once decision-directed
        // tracking collapses it PERSISTS, because the next probe solve is anchored to the
        // collapsed taps). Collapse = the BAD-PROBE criterion with signal energy present,
        // two consecutive probes:
        //  - probe correlation below the Phase-A signal-lost discriminator's line
        //    (< max(0.10, 0.45·healthy reference)): correlation normalizes by the burst's
        //    own healthy level, so this is SNR-invariant - WN1's −3 dB AWGN probes are
        //    noisy but correlated (an earlier absolute-MSE test misfired there and cost
        //    the AWGN gate), while a hard collapse kills correlation at any SNR, however
        //    early in the burst it strikes.
        //  - probe-window energy ≥ ¼ of its healthy reference (−6 dB, the deep-fade
        //    threshold): signal is PRESENT, so this is a collapse, not a fade. During a
        //    fade, coasting on the anchor is right and a fresh solve would fit noise;
        //    a bad probe WITHOUT energy stays the fade/signal-lost path's business.
        //  - ARMED: one fresh solve per unhealthy episode. Re-arming requires an observed
        //    healthy probe first - without this, a fresh solve whose ridge-shrunk solution
        //    starts below the health line (K=48: ridge 1.0 over 26 rows for 54 taps)
        //    re-fires two frames later, the taps never rebuild, and the spiral rode
        //    straight into SignalLost (WN2 Poor measured BER 0.73 with 4 dead bursts).
        double preMse = mse / statRows;
        double probeEnergyMean = probeEnergy / statRows;
        double probeGain = probePhase.Abs() / mode.K;
        bool collapsed = _collapseArmed &&
            probeGain < Math.Max(0.10, 0.45 * _probeGainRef) &&
            probeEnergyMean >= 0.25 * _probeEnergyRef;
        _collapsedProbes = collapsed ? _collapsedProbes + 1 : 0;
        bool freshSolve = _collapsedProbes >= 2;
        if (freshSolve)
        {
            // Fresh non-anchored re-solve: discard the accumulator (the collapsed frame's
            // DD rows are poison), rebuild the probe-only rows, and zero the taps so the
            // anchored ridge degenerates to a plain ridge toward zero. The probe alone is
            // rank-deficient; the ridge sends the unobserved directions to zero - a cold
            // restart of tracking on the spot, with P re-seeded from the fresh Gram.
            dfe.BeginTraining();
            for (int i = dfe.FbTaps; i < mode.K; i++)
            {
                FillWindowEst(probeChip + i, window);
                for (int j = 0; j < dfe.FbTaps; j++)
                {
                    probePast[j] = probe[i - 1 - j];
                }

                dfe.AddTrainingRow(window, probePast, probe[i], weight: 6f);
            }

            Span<Complex32> zeroTaps = stackalloc Complex32[startTaps.Length];
            dfe.LoadTaps(zeroTaps);
            CollapseResolves++;
            _collapsedProbes = 0;
            _collapseArmed = false;
        }

        dfe.SeedRlsFromTraining(_trackRidge, pFallback: 1.0f, ffNoisePower: GenieNoisePower());
        dfe.SolveTraining(regularization: _trackRidge, anchorToCurrentTaps: true, ffNoisePower: GenieNoisePower());

        // §B2.1 per-probe phase re-anchor. The anchored ridge solve corrects the tap
        // SHAPE only fractionally per probe - correct for slow shape drift, but for the
        // channel's common rotation it leaves a steady-state phase lag θ·(1−α)/α that
        // scatter measurement showed parking WN7 in a half-locked limbo (probe gain ≈ 0.4,
        // preMse ≈ 0.75). Phase is a single parameter with ~K known symbols to estimate
        // it: re-equalize the probe with the post-solve taps and take out the residual
        // common phase error at gain 1. Gated on the same 0.10 absolute correlation floor
        // as the signal-lost discriminator - below it the probe carries no usable phase
        // and rotating by its noise during a deep fade would break the coasting rule.
        var postPhase = Complex32.Zero;
        for (int i = dfe.FbTaps; i < mode.K; i++)
        {
            FillWindowEst(probeChip + i, window);
            for (int j = 0; j < dfe.FbTaps; j++)
            {
                probePast[j] = probe[i - 1 - j];
            }

            postPhase += dfe.Equalize(window, probePast) * probe[i].Conj();
        }

        if (postPhase.Abs() / statRows >= 0.10)
        {
            dfe.RotateTaps(Complex32.FromPhase(-((float)postPhase.Arg())));
        }

        Complex32[] endTaps = dfe.SnapshotTaps();
        if (freshSolve)
        {
            // The old anchor is the collapsed solution - this frame's data span is
            // equalized with the fresh solve held constant (no trajectory from garbage),
            // and the tap-rotation CFO trim below sees zero rotation.
            startTaps = endTaps;
        }

        // Residual-CFO trim from the mean tap rotation between consecutive probe solves.
        var tapRotation = Complex32.Zero;
        for (int i = 0; i < endTaps.Length; i++)
        {
            tapRotation += endTaps[i] * startTaps[i].Conj();
        }

        if (tapRotation.MagnitudeSquared() > 1e-12)
        {
            int frameT2 = 2 * (mode.U + mode.K);
            double deltaOmega = -0.1 * tapRotation.Arg() / frameT2;
            double window3Hz = 2.0 * Math.PI * 3.0 / (2.0 * Ms110dTables.SymbolRate);
            deltaOmega = Math.Clamp(
                deltaOmega, (_omegaAcquired - window3Hz) - _omega, (_omegaAcquired + window3Hz) - _omega);
            RetuneCarrier((2.0 * (probeChip + (mode.K / 2))) + _tau, 0, deltaOmega);
        }

        // Start accumulating rows for the NEXT solve: DD rows join the next probe's rows
        // to complete the excitation (the probe alone is rank-deficient).
        dfe.BeginTraining();

        // Fading statistic: fractional tap change per frame BEYOND the common rotation.
        // Pure residual CFO rotates all taps together - after removing the common rotation
        // the change is ≈ solve noise - while fading reshapes the tap vector. (The previous
        // detector thresholded the rotation ANGLE itself, i.e. it was a residual-CFO
        // detector that happened to separate the two simulation rigs - issue #64.)
        // EWMA'd so one noisy solve cannot flip the mode, with hysteresis so per-frame
        // chatter cannot mix bidirectional and single-pass LLR statistics in one block.
        // A fresh re-solve frame is excluded outright: startTaps was reassigned to the
        // fresh solution, so its tapChange is identically zero - folding that into the
        // min-tracking floor would collapse the floor and turn every later frame into an
        // excursion.
        double tapChange = 0;
        if (!freshSolve)
        {
            double startNorm = 0, changeNorm = 0;
            if (tapRotation.MagnitudeSquared() > 1e-12)
            {
                Complex32 rot = tapRotation * (float)(1.0 / tapRotation.Abs());
                for (int i = 0; i < endTaps.Length; i++)
                {
                    startNorm += startTaps[i].MagnitudeSquared();
                    changeNorm += (endTaps[i] - (rot * startTaps[i])).MagnitudeSquared();
                }
            }

            tapChange = startNorm > 1e-12 ? changeNorm / startNorm : 0;
            if (!_fadeFloorSeeded)
            {
                _fadeFloor = tapChange;
                _fadeFloorSeeded = true;
            }
            else
            {
                // Min-tracking floor: drops instantly, recovers 5 %/frame - so a fade's own
                // excursions cannot drag the floor up to meet them.
                _fadeFloor = Math.Min(tapChange, (_fadeFloor * 1.05) + 1e-4);
            }

            bool excursion = tapChange > FadeExcursionRatio * _fadeFloor;
            if (excursion)
            {
                if (_framesSinceExcursion <= FadeEnterWindowFrames)
                {
                    _fading = true;
                }

                _framesSinceExcursion = 0;
            }
            else if (++_framesSinceExcursion > FadeExitFrames)
            {
                _fading = false;
            }
        }

        // RLS weight: 1.0 on fading channels (full tracking), 0.1 on static/AWGN
        // (minimal noise accumulation while still providing some adaptation).
        // Path selection LATCHES per burst (§B2.1): a channel that faded does not become
        // AWGN mid-burst, but the excursion detector chatters on continuous fading (the
        // min-tracking floor never sees a quiet stretch to settle on - measured 40/130
        // WN7 Poor frames misclassified flat, running the 3-pass path mid-collapse). A
        // collapse latches too: two consecutive uncorrelated probes with signal present
        // cannot happen on a flat channel.
        _fadingLatched |= _fading || freshSolve;
        bool fading = _fadingLatched;
        float rlsWeight = fading ? 1.0f : 0.1f;

        _scrambler.Reset();
        float ddGate = DdGateRadius(mode.Modulation);

        // §B2.1a: probe-anchored retrospective tap trajectory for fading frames. startTaps
        // is anchored at the previous probe's centre (K/2 before the data span), endTaps at
        // the following probe's centre (K/2 beyond it) - both known here, because the probe
        // AFTER the span is solved before any data symbol is equalized (the block-buffered
        // architecture's free non-causality). Fraction for data symbol u:
        // f(u) = (u + K/2)/(U + K). The common rotation φ between the anchors is applied as
        // a phase ramp over linearly-interpolated de-rotated taps - chord interpolation of
        // a rotation understates amplitude (cos(φ/2) at midspan), and tens-of-degrees
        // per-frame rotation on the 1 Hz Poor channel is exactly the WN7/WN8 autopsy
        // mechanism, so the explicit phase ramp is the load-bearing part.
        int nTaps = endTaps.Length;
        Span<Complex32> trajectoryEnd = stackalloc Complex32[nTaps];
        Span<Complex32> tapsPrev = stackalloc Complex32[nTaps];
        Span<Complex32> tapsCur = stackalloc Complex32[nTaps];
        double phi = 0;
        if (fading)
        {
            phi = tapRotation.MagnitudeSquared() > 1e-12 ? tapRotation.Arg() : 0.0;
            Complex32 derot = Complex32.FromPhase(-((float)phi));
            for (int i = 0; i < nTaps; i++)
            {
                trajectoryEnd[i] = endTaps[i] * derot;
            }
        }

        if (mode.Modulation == Ms110dModulation.Qam16)
        {
            if (fading)
            {
                TapTrajectory(startTaps, trajectoryEnd, phi, FrameFraction(mode, 0), tapsCur);
                dfe.LoadTaps(tapsCur);
            }

            for (int u = 0; u < mode.U; u++)
            {
                if (fading && u > 0)
                {
                    tapsCur.CopyTo(tapsPrev);
                    TapTrajectory(startTaps, trajectoryEnd, phi, FrameFraction(mode, u), tapsCur);
                    dfe.TranslateTaps(tapsPrev, tapsCur);
                }

                FillWindow(_frameChip + u, window);
                int scrambleNibble = _scrambler.NextQam(0, 4);
                Complex32 y = dfe.Equalize(window, _decisions);
                Complex32 clean = Slice(y, mode.Modulation);
                DataSymbolEqualized?.Invoke(y);
                PushMaxLogLlrs(y, Ms110dTables.Qam16, null, 4, 10.0f, scrambleNibble);
                Span<Complex32> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                // Same decision-confidence gate as the interpolated fading path below:
                // QAM16's tight regions make ungated DD updates the fastest way to
                // self-destruct (WN8 autopsy - phase AND amplitude).
                if ((y - clean).MagnitudeSquared() < ddGate)
                {
                    dfe.RlsUpdate(row, _decisions, clean, weight: rlsWeight);
                    dfe.AddTrainingRow(row, _decisions, clean, weight: 0.25f);
                }

                PushDecision(clean);
            }
        }
        else if (!fading)
        {
            // 3-pass equalization from three tap seeds (end, start, midpoint of the
            // probe-to-probe trajectory), outputs averaged. The passes share the frame's
            // noise, so this is non-causal smoothing of the tap estimate rather than
            // diversity; it buys the WN5 rate-3/4 point its margin at the 6 dB AWGN mask.
            // Every pass must start from the frame's true decision history (the previous
            // probe's tail): passes 2/3 previously inherited the PREVIOUS pass's
            // end-of-frame decisions, feeding the frame's head through feedback taps
            // filled with its own tail (issue #64).
            Span<Complex32> pass1 = stackalloc Complex32[mode.U];
            Span<Complex32> pass2 = stackalloc Complex32[mode.U];
            Span<Complex32> frameStartDecisions = stackalloc Complex32[_decisions.Length];
            _decisions.CopyTo(frameStartDecisions);
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(_frameChip + u, window);
                Complex32 rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Complex32 y = dfe.Equalize(window, _decisions);
                Complex32 descrambled = y * rotor.Conj();
                Complex32 clean = Slice(descrambled, mode.Modulation);
                pass1[u] = descrambled;
                Span<Complex32> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                dfe.RlsUpdate(row, _decisions, clean * rotor, weight: rlsWeight);
                if ((descrambled - clean).MagnitudeSquared() < ddGate)
                {
                    dfe.AddTrainingRow(row, _decisions, clean * rotor, weight: 0.25f);
                }

                PushDecision(clean * rotor);
            }

            dfe.LoadTaps(startTaps);
            frameStartDecisions.CopyTo(_decisions);
            _scrambler.Reset();
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(_frameChip + u, window);
                Complex32 rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Complex32 y = dfe.Equalize(window, _decisions);
                Complex32 descrambled = y * rotor.Conj();
                Complex32 clean = Slice(descrambled, mode.Modulation);
                pass2[u] = descrambled;
                Span<Complex32> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                dfe.RlsUpdate(row, _decisions, clean * rotor, weight: rlsWeight);
                PushDecision(clean * rotor);
            }

            Complex32[] midTaps = new Complex32[endTaps.Length];
            for (int i = 0; i < midTaps.Length; i++)
            {
                midTaps[i] = (startTaps[i] + endTaps[i]) * 0.5f;
            }

            dfe.LoadTaps(midTaps);
            frameStartDecisions.CopyTo(_decisions);
            _scrambler.Reset();
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(_frameChip + u, window);
                Complex32 rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Complex32 y = dfe.Equalize(window, _decisions);
                Complex32 descrambled = y * rotor.Conj();
                Complex32 clean = Slice(descrambled, mode.Modulation);
                Complex32 averaged = (pass1[u] + pass2[u] + descrambled) * (1f / 3f);
                DataSymbolEqualized?.Invoke(averaged);
                PushLlrs(averaged, mode.Modulation);
                Span<Complex32> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                dfe.RlsUpdate(row, _decisions, clean * rotor, weight: rlsWeight);
                PushDecision(clean * rotor);
            }
        }
        else
        {
            // Fading channel (§B2.1a): single pass with the tap base interpolated between
            // the bracketing probe solves and the RLS deviation riding on top
            // (TranslateTaps tracks the residual only). Before Phase B2 this pass
            // equalized the whole span from endTaps - the tail anchor applied statically
            // to the head, the 107 ms staleness every U=256 mode pays and the WN7/WN8
            // smear amplifier - and U≤96 fading frames took the 3-pass average, which
            // mixes fading states.
            TapTrajectory(startTaps, trajectoryEnd, phi, FrameFraction(mode, 0), tapsCur);
            dfe.LoadTaps(tapsCur);
            for (int u = 0; u < mode.U; u++)
            {
                if (u > 0)
                {
                    tapsCur.CopyTo(tapsPrev);
                    TapTrajectory(startTaps, trajectoryEnd, phi, FrameFraction(mode, u), tapsCur);
                    dfe.TranslateTaps(tapsPrev, tapsCur);
                }

                FillWindow(_frameChip + u, window);
                Complex32 rotor = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                Complex32 y = dfe.Equalize(window, _decisions);
                Complex32 descrambled = y * rotor.Conj();
                Complex32 clean = Slice(descrambled, mode.Modulation);
                DataSymbolEqualized?.Invoke(descrambled);
                PushLlrs(descrambled, mode.Modulation);
                Span<Complex32> row = window;
                if (genie)
                {
                    FillWindowEst(_frameChip + u, estWindow);
                    row = estWindow;
                }

                // Decision-confidence gate on BOTH the RLS update and the training row: an
                // ungated weight-1.0 update on a wrong decision is precisely the DD
                // self-destruction mechanism of the WN7 autopsy - measured on the scatter
                // rig, the §B2.1c fresh re-solve re-collapsed within ONE frame until the
                // RLS update was gated. When decisions are unusable the interpolated base
                // carries the channel; when they are trustworthy RLS tracks the residual.
                if ((descrambled - clean).MagnitudeSquared() < ddGate)
                {
                    dfe.RlsUpdate(row, _decisions, clean * rotor, weight: rlsWeight);
                    dfe.AddTrainingRow(row, _decisions, clean * rotor, weight: 0.25f);
                }

                PushDecision(clean * rotor);
            }
        }

        dfe.SymmetrizeP(pMax: 10f);
        dfe.LoadTaps(endTaps);
        for (int i = 0; i < mode.K; i++)
        {
            PushDecision(probe[i]);
        }

        _probeMse = (0.7 * _probeMse) + (0.3 * preMse);

        TrackProbeTiming(probeChip, probe);

        // The probe one frame back: every lag of it is resident however the input was cut
        // into blocks (this frame's own probe may not have its last lags written yet), so
        // the statistic, and the release, do not depend on the caller's block size.
        double presence = ProbePresence(_presenceProbeChip, _presenceProbe, out double plainPresence);
        PresenceTraced?.Invoke(2 * _written, plainPresence, presence);
        _presenceProbeChip = probeChip;
        _presenceProbe = probe;
        FrameDiagnostics?.Invoke(
            $"frame@{_frameChip}: gain={probeGain:F3} ref={_probeGainRef:F3} mse={mse / mode.K:F3} " +
            $"tau={_tau:F3} omega={_omega:E2} bad={_badProbes} " +
            $"tapChange={tapChange:F4} floor={_fadeFloor:F4} fading={_fading}/{fading} " +
            $"preMse={preMse:F3} energy={probeEnergyMean:F3}/{_probeEnergyRef:F3} fresh={freshSolve} " +
            // §B3 autopsy fields: the pre-solve probe correlation PHASE (the incoming
            // taps' rotation error - |Σy·p̄| alone is rotation-invariant, issue #69),
            // the residual the re-anchor removed (or coasted on below the 0.10 floor),
            // and the slerp's common rotation φ for the data span that follows.
            $"phase={probePhase.Arg():F3} anchor={postPhase.Abs() / statRows:F3}@{postPhase.Arg():F3} " +
            $"phi={(tapRotation.MagnitudeSquared() > 1e-12 ? tapRotation.Arg() : 0f):F3} " +
            $"presence={presence:F2}/{_presenceThreshold:F2}");

        if (probeGain < Math.Max(0.10, 0.45 * _probeGainRef))
        {
            // Signal-lost patience is WALL-CLOCK, not a probe count: a probe count
            // scales the patience with frame length - 25 probes was 3 s at U=256
            // (never false-fired) but only 1 s at U=48, and the §B3 census measured
            // Poor-channel fades outliving it: WN1 abandoned 10/248 bursts and WN2
            // 4/124 mid-fade, discarding the rest of each burst - 83% and 99% of those
            // points' total errors. ~4 s covers the deep-fade tail at 1 Hz Doppler
            // spread; a real carrier drop still exits, just uniformly across modes.
            int badProbeLimit = (int)Math.Ceiling(
                4.0 * Ms110dTables.SymbolRate / (mode.U + mode.K));
            if (++_badProbes >= badProbeLimit)
            {
                CompleteBurst(Ms110dBurstEndReason.SignalLost);
                return;
            }
        }
        else
        {
            _badProbes = 0;
            _probeGainRef = (0.95 * _probeGainRef) + (0.05 * probeGain);
            // The energy reference tracks HEALTHY probes only: a long fade must not drag
            // it down (fade-vs-collapse discrimination depends on it), and during a
            // collapse it must stay pinned at the pre-collapse level. A healthy probe
            // also re-arms collapse recovery for the next unhealthy episode.
            _probeEnergyRef = (0.95 * _probeEnergyRef) + (0.05 * probeEnergyMean);
            _collapseArmed = true;
        }

        if (SignalAbsent(presence) && !_options.PresenceReleaseOff)
        {
            SignalAbsentReleases++;
            CompleteBurst(Ms110dBurstEndReason.SignalAbsent);
            return;
        }

        _blockFrameChips.Add(_frameChip);
        _frameChip += mode.U + mode.K;
        _frameInBlock++;
        if (_frameInBlock == _il.Frames)
        {
            _frameInBlock = 0;
            FinishBlock();
            _blockFrameChips.Clear();
        }
    }

    /// <summary>Sizes the signal-absent window for the locked mode (issue #553): as many
    /// frames as span <see cref="PresenceWindowSeconds"/>, and the release line
    /// <see cref="PresenceSigmas"/> noise standard deviations of the window mean above
    /// zero.</summary>
    private void StartPresenceWindow(Ms110dMode mode)
    {
        int frames = (int)Math.Ceiling(PresenceWindowSeconds * Ms110dTables.SymbolRate / (mode.U + mode.K));
        _presenceRing = new double[frames];
        _presenceFrames = 0;
        _presenceSum = 0;
        _presenceThreshold = PresenceSigmas * Math.Sqrt(PresenceLags(mode.K) / (double)frames);

        // The first frame scores the probe that ends the preamble (unshifted, design §2.4).
        _presenceProbeChip = _dataStartChip;
        _presenceProbe = MiniProbe.Get(mode.K, boundary: false);
        _presenceBackground = 0;
        _presenceFrameSeconds = (mode.U + mode.K) / (double)Ms110dTables.SymbolRate;

        // The lag spectrum: 4 bins per lag across the full circle, and the DFT kernel for it,
        // e^{-jωd} with d counted from the middle lag.
        int lags = PresenceLags(mode.K);
        int bins = 4 * lags;
        _presenceSpectrum = new double[bins];
        _presenceSpectrumSeeded = false;
        _presenceDftRe = new double[bins * lags];
        _presenceDftIm = new double[bins * lags];
        double centre = (lags - 1) / 2.0;
        for (int j = 0; j < bins; j++)
        {
            double w = PresenceBinFrequency(j, bins);
            for (int d = 0; d < lags; d++)
            {
                _presenceDftRe[(j * lags) + d] = Math.Cos(w * (d - centre));
                _presenceDftIm[(j * lags) + d] = -Math.Sin(w * (d - centre));
            }
        }
    }

    /// <summary>Symbol lags the probe matched filter searches: half a probe base period
    /// either side of the cursor (±6, ±7 and ±12 for K = 24, 32 and 48). It covers the
    /// D.6.1 Poor rig's 2 ms echo (4.8 symbols) on either side of whichever path the cursor
    /// locked to, and stays inside one base period so the periodic probe cannot alias a
    /// path onto a second lag.</summary>
    private static int PresenceLags(int k)
    {
        return (2 * ((MiniProbe.Sequence(k).Base.Length - 1) / 2)) + 1;
    }

    /// <summary>The frequency, radians per symbol in [-π, π), of lag-spectrum bin j.</summary>
    private static double PresenceBinFrequency(int j, int bins) => (2.0 * Math.PI * j / bins) - Math.PI;

    /// <summary>
    /// How much more coherent mini-probe energy the received symbols hold than the background
    /// would put there (issue #553). For each lag d of <see cref="PresenceLags"/> the K
    /// received symbols from probe start + d are correlated with the known probe, giving c_d.
    /// A signal puts its channel energy into the few lags its paths occupy; noise puts the
    /// same expected energy into every lag.
    /// <para>A steady carrier is the one other thing that correlates with the probe, and its
    /// c_d is an exact complex sinusoid in d, A·e^{jωd}, whatever its frequency; on one of the
    /// probe's spectral lines (1800 Hz plus a multiple of 2400 / base length) it is a strong
    /// one. Across the lags, then, a carrier is a line in the spectrum of c, where a signal is
    /// broad: a signal's lag spectrum is its channel's frequency response. So the spectrum of c
    /// is averaged over the last <see cref="PresenceSpectrumSeconds"/> of earlier frames (never
    /// this one, so on noise what is found is independent of the frame it is applied to); the
    /// strongest line in it that stands out of both the noise and the signal's own spectrum is
    /// taken for a carrier, its leakage taken off the averaged spectrum, and the search
    /// repeated, up to <see cref="PresenceMaxCarriers"/>. This frame's c is then projected off
    /// every carrier's e^{jωd}, and off d·e^{jωd} (the first-order term in ω, so an estimate a
    /// little off the carrier, as the receiver's own carrier loop wanders, still takes all of
    /// it).</para>
    /// <para>The background each lag is judged against is measured in the residual lags
    /// themselves: each residual lag's energy over its own noise variance after the projection
    /// (1 less the projection's weight at that lag), the median of those over the median's
    /// expectation on noise, averaged over the last couple of seconds of frames. Normalizing by
    /// the received power instead would let any strong interferer that does not correlate with
    /// the probe push the score below the noise and end a burst that is being read.</para>
    /// <para>The result is the residual lag energy over that background, less the residual's
    /// degrees of freedom (lags less the projection's rank), which averages zero on noise and
    /// on any number of carriers up to the limit, whatever their levels, and grows by the
    /// signal's in-window channel energy over the background. <paramref name="plain"/> is the
    /// old uncancelled, power-normalized sum less the lag count, kept for the instrument.</para>
    /// <para>Read straight from the ring with the receiver's own timing and carrier, but
    /// independent of the equalizer, its reference levels and the AGC (every term is a ratio),
    /// which is what lets it see noise as noise after the DFE has adapted itself to that
    /// noise.</para>
    /// </summary>
    private double ProbePresence(long probeChip, Complex32[] probe, out double plain)
    {
        int k = probe.Length;
        int lags = PresenceLags(k);
        int first = -(lags - 1) / 2;
        int span = k + lags - 1;
        Span<Complex32> received = stackalloc Complex32[span];
        double power = 0;
        for (int i = 0; i < span; i++)
        {
            received[i] = ReadChip(probeChip + first + i);
            power += received[i].MagnitudeSquared();
        }

        power /= span;
        if (power <= 0)
        {
            plain = 0;
            return 0;
        }

        // Scaled by the received power only to keep the numbers near unity; every quantity
        // below is a ratio of them.
        double scale = 1.0 / Math.Sqrt(k * power);
        Span<double> re = stackalloc double[lags];
        Span<double> im = stackalloc double[lags];
        double energy = 0;
        for (int d = 0; d < lags; d++)
        {
            var c = Complex32.Zero;
            for (int i = 0; i < k; i++)
            {
                c += received[i + d] * probe[i].Conj();
            }

            re[d] = c.Re * scale;
            im[d] = c.Im * scale;
            energy += (re[d] * re[d]) + (im[d] * im[d]);
        }

        plain = energy - lags;

        // This frame's lag spectrum, before anything is removed, for the frames after it.
        int bins = _presenceSpectrum.Length;
        Span<double> spectrum = stackalloc double[bins];
        for (int j = 0; j < bins; j++)
        {
            double xRe = 0, xIm = 0;
            int row = j * lags;
            for (int d = 0; d < lags; d++)
            {
                double kRe = _presenceDftRe[row + d], kIm = _presenceDftIm[row + d];
                xRe += (re[d] * kRe) - (im[d] * kIm);
                xIm += (re[d] * kIm) + (im[d] * kRe);
            }

            spectrum[j] = (xRe * xRe) + (xIm * xIm);
        }

        // The carriers the earlier frames point to, and the orthonormal basis they span.
        Span<double> carriers = stackalloc double[PresenceMaxCarriers];
        int found = _presenceSpectrumSeeded ? FindPresenceCarriers(lags, carriers) : 0;
        Span<double> basisRe = stackalloc double[2 * PresenceMaxCarriers * lags];
        Span<double> basisIm = stackalloc double[2 * PresenceMaxCarriers * lags];
        int rank = 0;
        double centre = (lags - 1) / 2.0;
        for (int n = 0; n < 2 * found; n++)
        {
            double w = carriers[n / 2];
            Span<double> vRe = basisRe.Slice(rank * lags, lags);
            Span<double> vIm = basisIm.Slice(rank * lags, lags);
            for (int d = 0; d < lags; d++)
            {
                double weight = n % 2 == 0 ? 1.0 : d - centre;
                vRe[d] = weight * Math.Cos(w * (d - centre));
                vIm[d] = weight * Math.Sin(w * (d - centre));
            }

            // Modified Gram-Schmidt against the vectors already in the basis.
            for (int q = 0; q < rank; q++)
            {
                ReadOnlySpan<double> qRe = basisRe.Slice(q * lags, lags);
                ReadOnlySpan<double> qIm = basisIm.Slice(q * lags, lags);
                double dotRe = 0, dotIm = 0;
                for (int d = 0; d < lags; d++)
                {
                    dotRe += (qRe[d] * vRe[d]) + (qIm[d] * vIm[d]);
                    dotIm += (qRe[d] * vIm[d]) - (qIm[d] * vRe[d]);
                }

                for (int d = 0; d < lags; d++)
                {
                    vRe[d] -= (dotRe * qRe[d]) - (dotIm * qIm[d]);
                    vIm[d] -= (dotRe * qIm[d]) + (dotIm * qRe[d]);
                }
            }

            double norm = 0;
            for (int d = 0; d < lags; d++)
            {
                norm += (vRe[d] * vRe[d]) + (vIm[d] * vIm[d]);
            }

            if (norm < 1e-6)
            {
                continue; // already spanned
            }

            double inv = 1.0 / Math.Sqrt(norm);
            for (int d = 0; d < lags; d++)
            {
                vRe[d] *= inv;
                vIm[d] *= inv;
            }

            rank++;
        }

        // Project the carriers off this frame, and keep each lag's remaining noise variance.
        Span<double> variance = stackalloc double[lags];
        variance.Fill(1.0);
        for (int q = 0; q < rank; q++)
        {
            ReadOnlySpan<double> qRe = basisRe.Slice(q * lags, lags);
            ReadOnlySpan<double> qIm = basisIm.Slice(q * lags, lags);
            double dotRe = 0, dotIm = 0;
            for (int d = 0; d < lags; d++)
            {
                dotRe += (qRe[d] * re[d]) + (qIm[d] * im[d]);
                dotIm += (qRe[d] * im[d]) - (qIm[d] * re[d]);
            }

            for (int d = 0; d < lags; d++)
            {
                re[d] -= (dotRe * qRe[d]) - (dotIm * qIm[d]);
                im[d] -= (dotRe * qIm[d]) + (dotIm * qRe[d]);
                variance[d] -= (qRe[d] * qRe[d]) + (qIm[d] * qIm[d]);
            }
        }

        Span<double> lagEnergy = stackalloc double[lags];
        double residual = 0;
        for (int d = 0; d < lags; d++)
        {
            double e = (re[d] * re[d]) + (im[d] * im[d]);
            residual += e;
            lagEnergy[d] = e / Math.Max(variance[d], 0.05);
        }

        // The median residual lag, each over its own noise variance, over the median of that
        // many unit exponentials (within 0.3 % of the true expectation for the projections
        // used here, by simulation).
        lagEnergy.Sort();
        double background = lagEnergy[lags / 2] / ExpectedMedianOfUnitExponentials(lags);
        double level = _presenceBackground > 0 ? _presenceBackground : background;
        double presence = (residual / level) - (lags - rank);

        // Then fold this frame into the estimates, for the frames after it: the background over
        // the last PresenceBackgroundSeconds, the lag spectrum over the last
        // PresenceSpectrumSeconds, in units of the background so it is scale-free.
        double frameSeconds = _presenceFrameSeconds;
        double beta = Math.Min(1.0, frameSeconds / PresenceBackgroundSeconds);
        _presenceBackground = _presenceBackground > 0
            ? _presenceBackground + (beta * (background - _presenceBackground))
            : background;
        double alpha = _presenceSpectrumSeeded ? Math.Min(1.0, frameSeconds / PresenceSpectrumSeconds) : 1.0;
        for (int j = 0; j < bins; j++)
        {
            _presenceSpectrum[j] += alpha * ((spectrum[j] / level) - _presenceSpectrum[j]);
        }

        _presenceSpectrumSeeded = true;
        return presence;
    }

    /// <summary>
    /// The steady carriers in the averaged lag spectrum, strongest first, up to
    /// <see cref="PresenceMaxCarriers"/>: the strongest bin is a carrier if it stands
    /// <see cref="PresenceCarrierOverNoise"/> times above the noise's level in a bin (lags, in
    /// units of the background) and its excess over that is
    /// <see cref="PresenceCarrierOverSignal"/> times the spectrum's median excess; its frequency
    /// is refined by a parabola through the bin and its neighbours, its leakage (the lag
    /// window's Dirichlet kernel, scaled to its excess) is taken off a copy of the spectrum, and
    /// the next is looked for. Returns how many were found.
    /// </summary>
    private int FindPresenceCarriers(int lags, Span<double> carriers)
    {
        int bins = _presenceSpectrum.Length;
        Span<double> left = stackalloc double[bins];
        _presenceSpectrum.CopyTo(left);
        Span<double> sorted = stackalloc double[bins];
        _presenceSpectrum.CopyTo(sorted);
        sorted.Sort();
        double median = sorted[bins / 2];
        double floor = Math.Max(lags, median);
        double threshold = Math.Max(
            PresenceCarrierOverNoise * lags,
            lags + (PresenceCarrierOverSignal * Math.Max(0, median - lags)));
        int found = 0;
        while (found < carriers.Length)
        {
            int peak = 0;
            for (int j = 1; j < bins; j++)
            {
                if (left[j] > left[peak])
                {
                    peak = j;
                }
            }

            if (left[peak] <= threshold)
            {
                break;
            }

            double below = left[(peak + bins - 1) % bins], at = left[peak], above = left[(peak + 1) % bins];
            double curvature = below - (2 * at) + above;
            double offset = curvature < 0 ? Math.Clamp(0.5 * (below - above) / curvature, -0.5, 0.5) : 0;
            double w = PresenceBinFrequency(peak, bins) + (offset * 2.0 * Math.PI / bins);
            carriers[found++] = w;

            double excess = at - floor;
            for (int j = 0; j < bins; j++)
            {
                double delta = PresenceBinFrequency(j, bins) - w;
                double half = Math.Sin(delta / 2);
                double kernel = Math.Abs(half) < 1e-9 ? lags : Math.Sin(lags * delta / 2) / half;
                left[j] -= excess * kernel * kernel / (lags * (double)lags);
            }
        }

        return found;
    }

    /// <summary>The expected middle order statistic of n (odd) independent unit-mean
    /// exponentials: the sum of 1/i for i from (n + 1) / 2 to n (Renyi's representation).
    /// 0.7254 for 15 lags, 0.7127 for 25.</summary>
    private static double ExpectedMedianOfUnitExponentials(int n)
    {
        double sum = 0;
        for (int i = (n + 1) / 2; i <= n; i++)
        {
            sum += 1.0 / i;
        }

        return sum;
    }

    /// <summary>Folds one frame's presence into the window; true once the window is full
    /// and its mean has fallen below the noise line.</summary>
    private bool SignalAbsent(double presence)
    {
        int slot = _presenceFrames % _presenceRing.Length;
        if (_presenceFrames >= _presenceRing.Length)
        {
            _presenceSum -= _presenceRing[slot];
        }

        _presenceRing[slot] = presence;
        _presenceSum += presence;
        _presenceFrames++;
        return _presenceFrames >= _presenceRing.Length &&
            _presenceSum / _presenceRing.Length < _presenceThreshold;
    }

    /// <summary>Trajectory fraction of data symbol <paramref name="u"/> between the
    /// bracketing probe-solve anchors: the previous probe's centre sits K/2 before the
    /// data span and the following probe's centre K/2 beyond it (§B2.1a).</summary>
    private static float FrameFraction(Ms110dMode mode, int u)
    {
        return (u + (0.5f * mode.K)) / (mode.U + mode.K);
    }

    /// <summary>Evaluates the §B2.1a probe-anchored tap trajectory at fraction
    /// <paramref name="f"/> ∈ [0,1]: linear interpolation of the de-rotated taps with the
    /// anchors' common rotation φ applied as a phase ramp e^{jφf}. At f = 0 this is
    /// <paramref name="start"/> exactly; at f = 1 the end solve exactly
    /// (<paramref name="endDerotated"/>·e^{jφ}). Internal for the unit test that pins the
    /// magnitude-preserving property chord interpolation lacks.</summary>
    internal static void TapTrajectory(
        ReadOnlySpan<Complex32> start, ReadOnlySpan<Complex32> endDerotated, double phi, float f, Span<Complex32> taps)
    {
        Complex32 ramp = Complex32.FromPhase((float)(phi * f));
        float g = 1f - f;
        for (int i = 0; i < taps.Length; i++)
        {
            taps[i] = ((start[i] * g) + (endDerotated[i] * f)) * ramp;
        }
    }

    /// <summary>Per-modulation DD confidence gate (squared radius). PSK family keeps the
    /// proven 0.4; QAM16 inner-ring min distance is 0.366 so the gate must be tighter to
    /// avoid accepting wrong decisions that self-confirm via feedback.</summary>
    private static float DdGateRadius(Ms110dModulation modulation)
    {
        return modulation switch
        {
            Ms110dModulation.Qam16 => 0.0225f, // (0.15)², ≈0.4× inner-ring min distance 0.366
            Ms110dModulation.Psk8 => 0.16f,    // (0.4)², min distance 0.765 → generous
            _ => 0.4f,                         // BPSK/QPSK proven value
        };
    }

    private void TrackProbeTiming(long probeChip, Complex32[] probe)
    {
        Span<double> magnitudes = stackalloc double[3];
        for (int d = 0; d < 3; d++)
        {
            double offset = (d - 1) * 0.5;
            var c = Complex32.Zero;
            for (int i = 0; i < probe.Length; i++)
            {
                // Estimation read: probe-peak timing is channel estimation (genie-eligible).
                c += ReadT2Est((2.0 * (probeChip + i)) + offset) * probe[i].Conj();
            }

            magnitudes[d] = c.Abs();
        }

        double denom = magnitudes[0] - (2 * magnitudes[1]) + magnitudes[2];
        if (Math.Abs(denom) < 1e-9)
        {
            return;
        }

        // Slow slew only - real clock skew is ppm-scale; anything faster is estimator noise.
        double delta = 0.5 * (magnitudes[0] - magnitudes[2]) / denom * 0.5;
        _tau += Math.Clamp(0.1 * delta, -0.03, 0.03);
    }

    /// <summary>Applies a phase/frequency correction with the phase model re-anchored at
    /// <paramref name="halfChipsRef"/> - an ω change must not re-rotate history, only the
    /// future (the loop is unstable otherwise, since the anchor sits at chip 0).</summary>
    private void RetuneCarrier(double halfChipsRef, double deltaTheta, double deltaOmega)
    {
        _omega += deltaOmega;
        _thetaBase += deltaTheta - (deltaOmega * halfChipsRef);
    }

    private static Complex32 Slice(Complex32 descrambled, Ms110dModulation modulation)
    {
        if (modulation == Ms110dModulation.Bpsk)
        {
            return descrambled.Re >= 0 ? new Complex32(1, 0) : new Complex32(-1, 0);
        }

        if (modulation == Ms110dModulation.Psk8)
        {
            return NearestPoint(descrambled, Ms110dTables.Psk8);
        }

        if (modulation == Ms110dModulation.Qam16)
        {
            return NearestPoint(descrambled, Ms110dTables.Qam16);
        }

        // QPSK points sit on the axes (Table D-IV → 8PSK symbols 0/2/4/6).
        return Math.Abs(descrambled.Re) >= Math.Abs(descrambled.Im)
            ? new Complex32(Math.Sign(descrambled.Re) >= 0 ? 1 : -1, 0)
            : new Complex32(0, Math.Sign(descrambled.Im) >= 0 ? 1 : -1);
    }

    private static Complex32 NearestPoint(Complex32 y, Complex32[] constellation)
    {
        float best = float.MaxValue;
        Complex32 result = constellation[0];
        for (int s = 0; s < constellation.Length; s++)
        {
            float d = (y - constellation[s]).MagnitudeSquared();
            if (d < best)
            {
                best = d;
                result = constellation[s];
            }
        }

        return result;
    }

    private void PushLlrs(Complex32 descrambled, Ms110dModulation modulation, int scramble = 0)
    {
        if (modulation == Ms110dModulation.Bpsk)
        {
            AddLlr(4f * descrambled.Re);
            return;
        }

        if (modulation == Ms110dModulation.Psk8)
        {
            // Max-log over 8 points. Bit label of ring symbol s = tribit t where
            // Transcode8Psk[t] == s (inverse map precomputed below).
            PushMaxLogLlrs(descrambled, Ms110dTables.Psk8, SymbolToTribit8, 3, 2.0f, 0);
            return;
        }

        if (modulation == Ms110dModulation.Qam16)
        {
            // QAM16 LLRs come from the first-pass PushMaxLogLlrs call with the live 10.0
            // scale; routing QAM16 through here (historically scale 2.0) would silently
            // drop LLR magnitudes 5× - refuse rather than mis-scale.
            throw new InvalidOperationException("QAM16 LLRs must use the first-pass PushMaxLogLlrs path");
        }

        // Table D-IV Gray map: MSB=0 ⇔ {+1, +j}, LSB=0 ⇔ {+1, −j}.
        AddLlr(2f * (descrambled.Re + descrambled.Im));
        AddLlr(2f * (descrambled.Re - descrambled.Im));
    }

    /// <summary>Ring symbol → tribit inverse map for 8PSK LLR bit labels.</summary>
    private static readonly byte[] SymbolToTribit8 = BuildInverseTranscode();

    // Descrambled-domain constellations for the chain-BCJR turbo path (§B2.3). BPSK's
    // labels are the indices themselves; QPSK's points sit on the axes (Table D-IV dibit →
    // ring {0→0, 1→2, 3→4, 2→6}), so ring order [0,2,4,6] carries labels [0,1,3,2].
    private static readonly Complex32[] TurboBpsk = [Ms110dTables.Psk8[0], Ms110dTables.Psk8[4]];
    private static readonly Complex32[] TurboQpsk =
        [Ms110dTables.Psk8[0], Ms110dTables.Psk8[2], Ms110dTables.Psk8[4], Ms110dTables.Psk8[6]];
    private static readonly byte[] TurboQpskLabels = [0, 1, 3, 2];

    private static byte[] BuildInverseTranscode()
    {
        var inv = new byte[8];
        for (int t = 0; t < 8; t++)
        {
            inv[Ms110dTables.Transcode8Psk[t]] = (byte)t;
        }

        return inv;
    }

    private void PushMaxLogLlrs(Complex32 y, Complex32[] constellation, byte[]? bitLabels, int bits, float scale, int scramble)
    {
        for (int b = 0; b < bits; b++)
        {
            float min0 = float.MaxValue, min1 = float.MaxValue;
            for (int s = 0; s < constellation.Length; s++)
            {
                int label = bitLabels != null ? bitLabels[s] : (s ^ scramble);
                float d = (y - constellation[s]).MagnitudeSquared();
                if (((label >> (bits - 1 - b)) & 1) == 0)
                {
                    if (d < min0) min0 = d;
                }
                else
                {
                    if (d < min1) min1 = d;
                }
            }

            AddLlr(scale * (min1 - min0));
        }
    }

    private void AddLlr(float llr)
    {
        _blockLlrs[_blockLlrCount++] = llr;
    }

    private void PushDecision(Complex32 wireSymbol)
    {
        for (int j = _decisions.Length - 1; j > 0; j--)
        {
            _decisions[j] = _decisions[j - 1];
        }

        if (_decisions.Length > 0)
        {
            _decisions[0] = wireSymbol;
        }
    }

    // ------------------------------------------------------------------ WN 0 tracking

    private void TrackWalsh()
    {
        if (!_trackingInitialized)
        {
            if (!HaveSamplesForChip(_dataStartChip + 2))
            {
                return;
            }

            // Re-estimate the carrier over the final super-frames: the matched super-frame
            // can be many seconds back (M up to 32) and the extrapolated phase stale.
            int tail = TailRefineSuperframes();
            RefineCarrier(
                _dataStartChip - (tail * ChipsSuperframe),
                TailSuperframeChips(tail, _lock!.WaveformNumber, _lock.Interleaver, _lock.ConstraintLength));

            _walsh = new Wid0WalshModem();
            _walsh.Reset();
            _symbolChip = _dataStartChip;
            _symbolInBlock = 0;
            _weakSymbols = 0;
            _walshPhaseAcc = Complex32.Zero;
            _walshPhaseCount = 0;
            _trackingInitialized = true;
        }

        Span<Complex32> chips = stackalloc Complex32[Wid0WalshModem.RakeChips];
        Span<Complex32> cleanChips = stackalloc Complex32[Wid0WalshModem.RakeChips];
        Span<float> llrs = stackalloc float[2];
        Span<float> gainMags = stackalloc float[Wid0WalshModem.Fingers];
        bool walshOracle = WalshOracleDibit is not null;
        if (walshOracle && _genieRing is null)
        {
            throw new InvalidOperationException("the WN0 gain oracle requires the genie stream");
        }

        // Forward availability is unchanged by the §B3.5b anti-causal fingers: the
        // buffer extends NegFingers chips BACK (always in ring history) and the same
        // 38 chips forward.
        double forwardChips = Wid0WalshModem.RakeChips - Wid0WalshModem.NegFingers;
        while (_state == Ms110dRxState.Tracking && HaveSamplesForChip(_symbolChip + forwardChips + 2))
        {
            for (int i = 0; i < Wid0WalshModem.RakeChips; i++)
            {
                chips[i] = ReadChip(_symbolChip - Wid0WalshModem.NegFingers + i);
            }

            int bestDibit;
            Complex32 combined;
            double maxFingerAbs;
            int trueDibit = walshOracle ? WalshOracleDibit!(_blockIndex, _symbolInBlock) : -1;
            if (trueDibit >= 0)
            {
                for (int i = 0; i < Wid0WalshModem.RakeChips; i++)
                {
                    cleanChips[i] = ReadChipEst(_symbolChip - Wid0WalshModem.NegFingers + i);
                }

                _walsh!.DemodulateRakeOracle(
                    chips, cleanChips, trueDibit, WalshOraclePole, llrs,
                    out bestDibit, out combined, out maxFingerAbs);
            }
            else
            {
                _walsh!.DemodulateRake(chips, llrs, out bestDibit, out combined, out maxFingerAbs);
            }

            AddLlr(llrs[0]);
            AddLlr(llrs[1]);

            // Decision-directed carrier: the MRC-combined winner statistic Σ ĝ*·corr
            // should be real and positive; its argument is the residual COMMON phase
            // error (the fingers absorb per-path phase - §B3.5). Average over 8 channel
            // symbols before applying the correction - the per-symbol phase estimate at
            // the −6 dB operating point is too noisy to drive the frequency integrator
            // directly. During warm-up (cold gains) the combined statistic is near zero
            // and contributes nothing, which is the desired behaviour.
            if (!walshOracle)
            {
                _walshPhaseAcc += combined;
                if (++_walshPhaseCount == 8)
                {
                    if (_walshPhaseAcc.MagnitudeSquared() > 1e-12)
                    {
                        double err = _walshPhaseAcc.Arg();
                        RetuneCarrier((2.0 * (_symbolChip + 16)) + _tau, 0.4 * err, 0.06 * err / 512.0);
                    }

                    _walshPhaseAcc = Complex32.Zero;
                    _walshPhaseCount = 0;
                }
            }

            if (FrameDiagnostics is not null)
            {
                _walsh.CopyGainMagnitudes(gainMags);
                var mags = new System.Text.StringBuilder(gainMags.Length * 6);
                for (int k = 0; k < gainMags.Length; k++)
                {
                    mags.Append(k == 0 ? "" : " ").Append(gainMags[k].ToString("F3"));
                }

                FrameDiagnostics.Invoke(
                    $"walsh sym={_symbolInBlock} d*={bestDibit} llr0={llrs[0]:F2} llr1={llrs[1]:F2} " +
                    $"|g|=[{mags}] argC={combined.Arg():F3}");
            }

            // Signal-lost discriminator (WN 0): the winning-correlation-to-chip-energy
            // ratio ≈ 0.5 at the −6 dB mask point but ≈ 0.23 on noise alone (max over 7
            // causal fingers; the §B3.5b 13-finger window lifts the noise-side statistic
            // ~15%, margin watched via census end-reasons). Weak only when ALL fingers
            // are weak - a finger-0-only test would false-fire on a direct-path fade
            // with a strong echo, exactly the fades MRC rides (§B3.5). The energy window
            // stays the symbol's own 32 chips regardless of the finger span.
            double sumMag = 0;
            for (int i = 0; i < 32; i++)
            {
                sumMag += chips[Wid0WalshModem.NegFingers + i].Abs();
            }

            if (maxFingerAbs < 0.35 * sumMag)
            {
                // ~1.2 s - long enough to ride a deep Poor-channel fade (see the DFE
                // path's discriminator for the rationale).
                if (++_weakSymbols >= 90)
                {
                    CompleteBurst(Ms110dBurstEndReason.SignalLost);
                    return;
                }
            }
            else
            {
                _weakSymbols = 0;
            }

            _symbolChip += 32;
            _symbolInBlock++;
            if (2 * _symbolInBlock == _il!.SizeBits)
            {
                _symbolInBlock = 0;
                _walsh.Reset(); // scramble sequence resets at the interleaver boundary
                FinishBlock();
            }
        }
    }

    // ------------------------------------------------------------------ block/burst

    private void FinishBlock()
    {
        if (_blockLlrCount != _il!.SizeBits)
        {
            throw new InvalidOperationException("interleaver block LLR accounting error");
        }

        FirstPassBlockLlrs?.Invoke(_blockIndex, _blockLlrs);

        var info = new byte[_il.InputBits];
        Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, info);

        // Turbo re-equalization: SISO soft feedback (§B3.3) - a log-MAP pass over the
        // outer code turns the current block LLRs into per-symbol soft expectations for
        // the chain-BCJR re-estimation, then decode again - for every DFE mode except
        // QAM16. The §B3.4 exclusion is MEASURED, not a scale trap any more: the wiring
        // below supports QAM16 end-to-end (wire-domain chains, permuted priors, true
        // second moments - the oracle instrument exercises it, ceiling 9.3E-4 on the
        // w0/b0 corpse), but the shipped loop cannot bootstrap - the first decode is
        // coin-flip (rank-starved first pass), and every label-free start measured
        // (probe-row solves, probe-anchored bootstrap chains, cap 96) descends into a
        // self-consistent wrong attractor whose decode is still 50% (banked:
        // evidence/2026-07-25-phase-b34-wn8/qam16-turbo-full.patch). The gate reopens
        // when a model-front leg moves the bootstrap or the ceiling. The flat-channel
        // skip retired with the DFE-re-solve fallback that motivated it (§B2.3): on a
        // flat channel the chain BCJR degenerates to an exact soft-output matched
        // filter - the reason BPSK U>48 was always allowed through - while the WN2
        // Poor λ A/B caught the skip misclassifying short-frame fading bursts as flat
        // (turbo 2c/158s, 7× BER cost): the excursion statistic is weakest exactly
        // where probes are densest.
        if (_dfe is not null && _mode is not null &&
            !_options.DisableTurbo &&
            _mode.Modulation is not Ms110dModulation.Qam16 &&
            _blockFrameChips.Count == _il.Frames &&
            BlockSamplesResident())
        {
            // The DD rows accumulated since the last probe solve belong to the NEXT probe's
            // solve ("join the next probe's rows to complete the excitation"); the turbo
            // pass re-trains repeatedly on the same Dfe and used to destroy them, leaving
            // the first post-block probe solve probe-only and rank-deficient (issue #65).
            _dfe.SnapshotTraining();
            var firstPass = new byte[info.Length];
            Array.Copy(info, firstPass, info.Length);

            // §B3.6 seam: an armed instrument may replace the iteration-0 labels
            // (perturbed restart / staged seed). firstPass was captured above, so the
            // revert fallback stays the true first-pass decode regardless.
            if (TurboStartOverride?.Invoke(_blockIndex, info) is byte[] startInfo)
            {
                if (startInfo.Length != info.Length)
                {
                    throw new InvalidOperationException("TurboStartOverride length mismatch");
                }

                Array.Copy(startInfo, info, info.Length);
            }

            var prevInfo = new byte[info.Length];
            bool converged = false;
            bool aborted = false;
            for (int iter = 0; iter < 24; iter++)
            {
                // Hybrid bootstrap (§B3.3): iteration 0 trains on hard re-encoded labels -
                // the first-pass LLR stream's fixed max-log scale is far too timid at high
                // SNR (measured WN6 corpse: mean |LLR| 1.6 where the calibrated chain-BCJR
                // output runs 12+), so a soft start spends three iterations rediscovering
                // confidence. The hard pass hands iteration 1 properly-scaled LLRs, and
                // the SISO soft iterations take over from there. The cap is generous
                // because the costs are asymmetric: a cap-limited revert throws away ~10k
                // repaired errors per block, extra iterations only cost wall-clock on the
                // rare non-converging blocks. Measured on the WN6 w2/b2 corpse: one dead
                // block reverted at cap 8 while still halving its decode-changes and
                // converged at 9; the other rode out a mid-loop excursion (1490 → 2428 →
                // 988 → 179 → 9 → 0) and converged at 15 - so the cap carries headroom
                // over the worst measured path. Healthy blocks still exit on the first
                // fixed point, almost always iteration 0 or 1.
                if (iter == 0)
                {
                    TurboReequalize(info);
                }
                else
                {
                    TurboReequalizeSoft();
                }

                if (_blockLlrCount != _il.SizeBits)
                {
                    aborted = true; // partial re-equalization; the current decode stands
                    break;
                }

                Array.Copy(info, prevInfo, info.Length);
                Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, info);
                if (FrameDiagnostics is not null)
                {
                    int diffs = 0;
                    for (int i = 0; i < info.Length; i++)
                    {
                        diffs += info[i] != prevInfo[i] ? 1 : 0;
                    }

                    FrameDiagnostics.Invoke($"turbo-iter b{_blockIndex} i{iter} decode-changes={diffs}");
                }

                if (info.AsSpan().SequenceEqual(prevInfo))
                {
                    converged = true;
                    break;
                }
            }

            if (!aborted)
            {
                TurboBlockLlrs?.Invoke(_blockIndex, _blockLlrs);
            }

            if (converged)
            {
                TurboConverged++;
            }
            else if (aborted)
            {
                TurboAborted++;
            }
            else if (_mode.Modulation == Ms110dModulation.Psk8 &&
                (TrySalvageRevert(info, prevInfo) || TrySalvageRelock(info, prevInfo)))
            {
                // §B3.6 salvage (evidence/2026-07-26-phase-b36-wn7loop, Amendment 1):
                // the wander states are scaffold-starved - too few frames with clean
                // labels to anchor the label-trained solves - so a label-free frozen
                // probe pass re-detects the block and a fresh soft loop runs from that
                // seed. A fixed point there is accepted on the same converged ⇒ correct
                // evidence as the primary loop (measured: zero wrong convergences across
                // every §B3.6 ensemble); no fixed point falls through to the revert.
                TurboSalvaged++;
                TurboConverged++;
            }
            else
            {
                // Five decode→re-equalize→decode rounds without a fixed point: the loop
                // is oscillating, and a self-trained iterate with no fixed point is not
                // evidence (issue #65). Keep the first-pass decode.
                TurboReverted++;
                Array.Copy(firstPass, info, info.Length);
            }

            _dfe.RestoreTraining();

            if (_mode.Modulation is Ms110dModulation.Psk8)
            {
                // G1d (Poor-gate successor program): the 8PSK per-block ensemble. The
                // chain path above is the better WN7 receiver on most blocks and the
                // MFB-form decoder on others, and G1 measured them never wrong on the
                // same block (0 of 88). Each receiver is asked for its evidence in its
                // own log-likelihood units and the larger wins:
                //   - the MFB's: its Gaussian log-likelihood gain over the block,
                //     rows x ln(chain residual / MFB residual), both decodes priced
                //     through the MFB's probe-anchored trajectory;
                //   - the chain's: the sum of |LLR| of its final wire LLRs at the
                //     positions where the two decodes re-encode differently (B3.9:
                //     honest erasures sit near 1, right bits at 12-68).
                // Measured on the G1 specimens plus the one AWGN WN7 block the
                // residual-only selector got wrong (evidence 2026-08-20-poorgate-g1d):
                // the 13 blocks the chain had wrong show MFB evidence 65-880 against
                // chain evidence 4.5-26 (8x at the thinnest); the AWGN block - an MFB
                // nearest-neighbour codeword error, 5 wire bits, the residual-minimising
                // decode under the MFB's own model and so invisible to a residual
                // comparison - shows chain 22.3 against MFB 5.0; the five Poor MFB basin
                // traps show negative MFB evidence. No constant is tuned. Only a
                // CONVERGED MFB decode is offered (the B3.3 principle), and a block whose
                // chain LLR buffer is not complete keeps the chain's decode. Structural
                // scoping: only 8PSK reaches this block; QAM16 keeps its W5b2 branch
                // below and every other waveform is bit-identical by construction.
                _mfb ??= new Ms110dMfbBlockDecoder(
                    _mode, _il, _code!, _puncture!, _interleaver!, _viterbi!, _siso!);
                _mfbInfo ??= new byte[info.Length];
                Array.Copy(info, _mfbInfo, info.Length);
                bool mfbConverged = _mfb.DecodeBlock(this, _blockFrameChips, _mfbInfo);
                if (mfbConverged)
                {
                    double chainPrice = _mfb.Price(info, _blockFrameChips);
                    double mfbPrice = _mfb.Price(_mfbInfo, _blockFrameChips);
                    double mfbEvidence = _mfb.PriceRows * Math.Log(chainPrice / mfbPrice);
                    double chainEvidence = 0;
                    int diffBits = 0;
                    bool switchToMfb = false;
                    MfbOffered++;
                    if (mfbEvidence > 0 && _blockLlrCount == _il.SizeBits)
                    {
                        // Two re-encodes per offered block: the same per-block allocation
                        // class as the turbo path's first-pass copy, seconds apart on air.
                        byte[] chainWire = Ms110dFraming.EncodeBlock(_code!, _puncture!, _interleaver!, info);
                        byte[] mfbWire = Ms110dFraming.EncodeBlock(_code!, _puncture!, _interleaver!, _mfbInfo);
                        for (int i = 0; i < chainWire.Length; i++)
                        {
                            if (chainWire[i] != mfbWire[i])
                            {
                                diffBits++;
                                chainEvidence += Math.Abs(_blockLlrs[i]);
                            }
                        }

                        switchToMfb = diffBits > 0 && mfbEvidence > chainEvidence;
                    }

                    if (switchToMfb)
                    {
                        Array.Copy(_mfbInfo, info, info.Length);
                        MfbSelected++;
                    }

                    FrameDiagnosticsForInstruments?.Invoke(FormattableString.Invariant(
                        $"mfb-ensemble block={_blockIndex} chain={chainPrice:E6} mfb={mfbPrice:E6} diff={diffBits} chainEvidence={chainEvidence:F1} mfbEvidence={mfbEvidence:F1} pick={(switchToMfb ? "mfb" : "chain")}"));
                }
                else
                {
                    FrameDiagnosticsForInstruments?.Invoke(FormattableString.Invariant(
                        $"mfb-ensemble block={_blockIndex} mfb=unconverged pick=chain"));
                }
            }
        }
        else if (_dfe is not null && _mode is not null &&
            !_options.DisableTurbo &&
            _mode.Modulation is Ms110dModulation.Qam16 &&
            _blockFrameChips.Count == _il.Frames &&
            BlockSamplesResident())
        {
            // W5b2 (wn8-program): the MFB-form block decoder - the measured successor
            // to the §B3.4 QAM16 exclusion above. The whole detection stack is
            // replaced for QAM16 blocks (composite-FIR probe anchors, per-burst
            // delay-profile window, matched projection, soft/hard cancellation
            // schedule with the gated decision-directed re-fit); no fixed point by
            // the cap keeps the first-pass decode (the §B3.3 revert principle).
            // Structural scoping: only QAM16 reaches this branch, so every other
            // waveform's path is bit-identical by construction.
            _mfb ??= new Ms110dMfbBlockDecoder(
                _mode, _il, _code!, _puncture!, _interleaver!, _viterbi!, _siso!);
            var firstPass = new byte[info.Length];
            Array.Copy(info, firstPass, info.Length);
            if (_mfb.DecodeBlock(this, _blockFrameChips, info))
            {
                TurboConverged++;
            }
            else
            {
                TurboReverted++;
                Array.Copy(firstPass, info, info.Length);
            }
        }
        else
        {
            TurboSkipped++;
        }

        // §B3.3 oracle-labels instrument: one extra chain-BCJR re-equalization trained
        // on the TRUE info bits, after the normal pipeline so the shipped decode above
        // is untouched. Same gate as the turbo (minus DisableTurbo - the instrument
        // composes with MS110D_AUTOPSY_NOTURBO).
        if (OracleInfo?.Invoke(_blockIndex) is byte[] oracleInfo &&
            _dfe is not null && _mode is not null &&
            _blockFrameChips.Count == _il.Frames &&
            BlockSamplesResident())
        {
            _dfe.SnapshotTraining();
            _turboFrameDiag = true;
            TurboReequalize(oracleInfo, trustedLabels: true);
            _turboFrameDiag = false;
            if (_blockLlrCount == _il.SizeBits)
            {
                var oracleDecode = new byte[_il.InputBits];
                Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, oracleDecode);
                OracleBlockLlrs?.Invoke(_blockIndex, _blockLlrs, oracleDecode);
            }

            _dfe.RestoreTraining();
        }

        // W1 true-channel injection (wn8-program): one further re-equalization with
        // truth time-variation, after the oracle pass so the two bounds land side by
        // side on the same block. The shipped decode above is untouched; the pass emits
        // truth-frame diagnostics of its own (never turbo-frame - those stay oracle's).
        if (TruthGainsAtSample is not null &&
            OracleInfo?.Invoke(_blockIndex) is byte[] truthInfo &&
            _dfe is not null && _mode is not null &&
            _blockFrameChips.Count == _il.Frames &&
            BlockSamplesResident())
        {
            _dfe.SnapshotTraining();
            TurboReequalize(truthInfo, trustedLabels: true, truthChannel: true);
            if (_blockLlrCount == _il.SizeBits)
            {
                var truthDecode = new byte[_il.InputBits];
                Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, truthDecode);
                TruthBlockLlrs?.Invoke(_blockIndex, _blockLlrs, truthDecode);
            }

            _dfe.RestoreTraining();
        }

        // §B3.6 C2a stage measurement (M2a): one label-free re-detection pass after the
        // normal pipeline - see <see cref="TurboFrozenProbe"/>. The shipped decode above
        // is untouched.
        if (TurboFrozenProbe && FrozenBlockLlrs is not null &&
            _dfe is not null && _mode is not null &&
            _blockFrameChips.Count == _il.Frames &&
            BlockSamplesResident())
        {
            _dfe.SnapshotTraining();
            TurboFrozenProbePass();
            if (_blockLlrCount == _il.SizeBits)
            {
                var frozenDecode = new byte[_il.InputBits];
                Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, frozenDecode);
                FrozenBlockLlrs.Invoke(_blockIndex, _blockLlrs, frozenDecode);
            }

            _dfe.RestoreTraining();
        }

        // W5a instrument seam (see InstrumentBlockReady) - after every shipped and
        // instrument pass, while the block's samples are still resident.
        if (InstrumentBlockReady is not null &&
            _blockFrameChips.Count == _il.Frames && BlockSamplesResident())
        {
            InstrumentBlockReady.Invoke(_blockIndex, [.. _blockFrameChips]);
        }

        _blockLlrCount = 0;

        int searchFrom = Math.Max(0, _burstBits.Count - 31);
        _burstBits.AddRange(info);
        BlockDecoded?.Invoke(new Ms110dRxBlock(_blockIndex, info));
        _blockIndex++;

        int eom = Ms110dFraming.FindEom(_burstBits, searchFrom);
        if (eom >= 0)
        {
            var payload = new byte[eom];
            _burstBits.CopyTo(0, payload, 0, eom);
            EmitBurst(payload, Ms110dBurstEndReason.Eom);
            return;
        }

        if (_options.MaxInputDataBlocks > 0 && _blockIndex >= _options.MaxInputDataBlocks)
        {
            CompleteBurst(Ms110dBurstEndReason.MaxInputDataBlocks);
        }
    }

    private bool BlockSamplesResident()
    {
        // Turbo re-reads the whole block; a block that has outlived the ring would
        // silently train against overwritten samples (the head frames degrade to LLR
        // erasures the outer code must then bridge). Never trips for the 3 kHz set
        // with RingBits = 16 - this is the backstop for wider future configs.
        double oldest = PositionOfChip(_blockFrameChips[0]) - _dfe!.FfTaps - InterpHalf;
        return oldest > _written - RingSize;
    }

    /// <summary>Measured additive-noise power per complex T/2 sample (genie mode only:
    /// mean |noisy − clean|² between the rings; 0 when the genie is off, leaving every
    /// solve bit-identical to the normal path).</summary>
    private float GenieNoisePower()
    {
        return _genieRing is null || _genieNoiseCount == 0
            ? 0f
            : (float)(_genieNoiseSum / _genieNoiseCount);
    }

    /// <summary>In-place Gaussian elimination with partial pivoting for the W1/W2
    /// truth-gauge normal equations (n ≤ 10, row-major 10-wide storage). The solution
    /// lands in <paramref name="b"/>; returns false on a singular system (the caller
    /// ridges the diagonal, so effectively never).</summary>
    private static bool SolveComplex(Span<Complex32> a, Span<Complex32> b, int n)
    {
        const int stride = 10;
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            float best = a[(col * stride) + col].MagnitudeSquared();
            for (int r = col + 1; r < n; r++)
            {
                float m = a[(r * stride) + col].MagnitudeSquared();
                if (m > best)
                {
                    best = m;
                    pivot = r;
                }
            }

            if (best < 1e-20f)
            {
                return false;
            }

            if (pivot != col)
            {
                for (int c = col; c < n; c++)
                {
                    (a[(col * stride) + c], a[(pivot * stride) + c]) =
                        (a[(pivot * stride) + c], a[(col * stride) + c]);
                }

                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            Complex32 inv = a[(col * stride) + col].Conj() * (1f / a[(col * stride) + col].MagnitudeSquared());
            for (int r = col + 1; r < n; r++)
            {
                Complex32 factor = a[(r * stride) + col] * inv;
                if (factor.MagnitudeSquared() == 0f)
                {
                    continue;
                }

                for (int c = col; c < n; c++)
                {
                    a[(r * stride) + c] -= factor * a[(col * stride) + c];
                }

                b[r] -= factor * b[col];
            }
        }

        for (int r = n - 1; r >= 0; r--)
        {
            Complex32 acc = b[r];
            for (int c = r + 1; c < n; c++)
            {
                acc -= a[(r * stride) + c] * b[c];
            }

            Complex32 inv = a[(r * stride) + r].Conj() * (1f / a[(r * stride) + r].MagnitudeSquared());
            b[r] = acc * inv;
        }

        return true;
    }

    private static int TurboBitsPerSymbol(Ms110dModulation modulation)
    {
        return modulation switch
        {
            Ms110dModulation.Bpsk => 1,
            Ms110dModulation.Qpsk => 2,
            Ms110dModulation.Psk8 => 3,
            _ => 4,
        };
    }

    /// <summary>Hard-label turbo re-equalization: re-encode <paramref name="info"/> and run
    /// the core on the exact expected wire symbols. This is the §B3.3 oracle instrument's
    /// path (true info bits ⇒ the converged-soft-feedback ceiling) - the shipped turbo loop
    /// uses <see cref="TurboReequalizeSoft"/> instead.</summary>
    private void TurboReequalize(byte[] info, bool trustedLabels = false, bool truthChannel = false)
    {
        var mode = _mode!;

        // Re-encode decoded info → fetched (wire-order) bits → expected wire symbols.
        byte[] fetched = Ms110dFraming.EncodeBlock(_code!, _puncture!, _interleaver!, info);
        int bitsPerSymbol = TurboBitsPerSymbol(mode.Modulation);
        int bit = 0;
        for (int f = 0; f < _il!.Frames; f++)
        {
            _scrambler.Reset();
            for (int u = 0; u < mode.U; u++)
            {
                int symbolNumber = 0;
                for (int b = 0; b < bitsPerSymbol; b++)
                {
                    symbolNumber = (symbolNumber << 1) | (bit < fetched.Length ? fetched[bit++] : 0);
                }

                // QAM16 scrambling is an XOR label permutation (D.5.1.3), not a ring
                // rotation - the expected wire symbol is the permuted constellation
                // point directly (the modulator's own mapping: 4 fetched bits MSB-first
                // ARE the symbol number, no transcode table).
                _turboExpected[(f * mode.U) + u] = mode.Modulation switch
                {
                    Ms110dModulation.Bpsk => Ms110dTables.Psk8[
                        _scrambler.NextPsk(symbolNumber == 0 ? 0 : 4)],
                    Ms110dModulation.Qpsk => Ms110dTables.Psk8[_scrambler.NextPsk(
                        symbolNumber switch { 0 => 0, 1 => 2, 3 => 4, _ => 6 })],
                    Ms110dModulation.Psk8 => Ms110dTables.Psk8[_scrambler.NextPsk(
                        Ms110dTables.Transcode8Psk[symbolNumber & 7])],
                    _ => Ms110dTables.Qam16[_scrambler.NextQam(symbolNumber, 4)],
                };
            }
        }

        TurboCore(_turboExpected, null, null, allowPair: trustedLabels, truthChannel);
    }

    /// <summary>Soft-feedback turbo re-equalization (§B3.3): a SISO log-MAP pass over the
    /// outer tail-biting code turns the CURRENT block LLRs (first-pass DFE output on
    /// iteration 0, the previous chain-BCJR output afterwards) into per-coded-bit
    /// posteriors, and the core trains on the resulting per-symbol expectations E[x]
    /// instead of hard re-encoded decisions. Uncertain symbols shrink toward 0 - exactly
    /// the EM E-step for every estimation consumer (rows, h1/h2 correlations keep their
    /// /count normalizations because E[|x|²] = 1 on the PSK ring; QAM16 carries the true
    /// second moment instead, §B3.4) - so mid-frame channel information flows from the
    /// code without the 45%-garbage hard labels that stalled the WN13 fade-cluster
    /// specimen (§B3.2/§B3.3).</summary>
    private void TurboReequalizeSoft()
    {
        var mode = _mode!;

        // Outer SISO decode → posterior LLR for every wire-order coded bit.
        _interleaver!.Deinterleave(_blockLlrs, _softPunctured);
        Ms110dPuncture.Depuncture(_puncture!, _softPunctured, _softMother);
        _siso!.Decode(_softMother, _softMotherPost);
        Ms110dPuncture.Apply(_puncture!, _softMotherPost, _softPunctured);
        _interleaver.Interleave(_softPunctured, _softWireLlrs);

        // Code EXTRINSICS (posterior − channel input, mother domain) → wire order: the
        // chain-BCJR priors (§B3.3). Posterior would double-count - the detector's own
        // last-round output echoed back as prior locks the loop onto itself. _softMother
        // (the SISO input) is consumed here; repeated copies (rates < 1/2) share their
        // mother position's extrinsic, which excludes the sibling copies' channel LLRs
        // too - conservative, and exact for the mask-only rates (repeat = 1).
        for (int i = 0; i < _softMother.Length; i++)
        {
            _softMother[i] = _softMotherPost[i] - _softMother[i];
        }

        Ms110dPuncture.Apply(_puncture!, _softMother, _softPunctured);
        _interleaver.Interleave(_softPunctured, _softWireExt);

        // Per-symbol soft expectations over the descrambled constellation, rotated onto
        // the wire by the scrambler (Psk8[(s+r)&7] == Psk8[s]·Psk8[r] - NextPsk is an
        // additive ring rotation). Variance 1−|E[x]|² feeds the core's noise estimate.
        // QAM16 (§B3.4) folds the XOR nibble inside the sum instead (the scramble is a
        // label permutation, not a rotation) and carries the TRUE second moment: XOR
        // moves symbols BETWEEN rings, so E[|x|²] is a probability-weighted average of
        // both ring energies, and the variance is E[|x|²] − |E[x]|².
        int bitsPerSymbol = TurboBitsPerSymbol(mode.Modulation);
        bool qamSoft = mode.Modulation == Ms110dModulation.Qam16;
        int bit = 0;
        Span<float> p0 = stackalloc float[4];
        for (int f = 0; f < _il!.Frames; f++)
        {
            _scrambler.Reset();
            for (int u = 0; u < mode.U; u++)
            {
                for (int b = 0; b < bitsPerSymbol; b++)
                {
                    p0[b] = Sigmoid(bit < _softWireLlrs.Length ? _softWireLlrs[bit++] : float.MaxValue);
                }

                int nibble = qamSoft ? _scrambler.NextQam(0, 4) : 0;
                Complex32 e = Complex32.Zero;
                float e2 = 0f;
                for (int t = 0; t < 1 << bitsPerSymbol; t++)
                {
                    float pt = 1f;
                    for (int b = 0; b < bitsPerSymbol; b++)
                    {
                        float pBitZero = p0[b];
                        pt *= ((t >> (bitsPerSymbol - 1 - b)) & 1) == 0 ? pBitZero : 1f - pBitZero;
                    }

                    if (qamSoft)
                    {
                        Complex32 wire = Ms110dTables.Qam16[t ^ nibble];
                        e += wire * pt;
                        e2 += wire.MagnitudeSquared() * pt;
                        continue;
                    }

                    int ring = mode.Modulation switch
                    {
                        Ms110dModulation.Bpsk => t == 0 ? 0 : 4,
                        Ms110dModulation.Qpsk => t switch { 0 => 0, 1 => 2, 3 => 4, _ => 6 },
                        _ => Ms110dTables.Transcode8Psk[t & 7],
                    };
                    e += Ms110dTables.Psk8[ring] * pt;
                }

                int idx = (f * mode.U) + u;
                if (qamSoft)
                {
                    _softExpected[idx] = e;
                    _softVar[idx] = Math.Max(0f, e2 - e.MagnitudeSquared());
                }
                else
                {
                    _softExpected[idx] = e * Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                    _softVar[idx] = Math.Max(0f, 1f - e.MagnitudeSquared());
                }
            }
        }

        if (FrameDiagnostics is not null)
        {
            double meanIn = 0, meanPost = 0, meanE = 0;
            int weak = 0;
            for (int i = 0; i < _il.SizeBits; i++)
            {
                meanIn += Math.Abs(_blockLlrs[i]);
                meanPost += Math.Abs(_softWireLlrs[i]);
            }

            for (int i = 0; i < _softExpected.Length; i++)
            {
                meanE += Math.Sqrt(_softExpected[i].MagnitudeSquared());
                weak += _softVar[i] > 0.25f ? 1 : 0;
            }

            FrameDiagnostics.Invoke(FormattableString.Invariant(
                $"turbo-soft b{_blockIndex}: mean|llrIn|={meanIn / _il.SizeBits:F2} mean|llrPost|={meanPost / _il.SizeBits:F2} mean|E|={meanE / _softExpected.Length:F3} weak={weak}/{_softExpected.Length}"));
        }

        TurboCore(_softExpected, _softVar, _softWireExt, allowPair: true);
    }

    /// <summary>P(bit = 0) from an LLR under the positive-⇒-0 convention.</summary>
    private static float Sigmoid(float llr)
    {
        return llr >= 0f
            ? 1f / (1f + MathF.Exp(-llr))
            : MathF.Exp(llr) / (1f + MathF.Exp(llr));
    }

    /// <summary>Numerically stable log(1 + eˣ).</summary>
    private static float Softplus(float x)
    {
        if (x > 20f)
        {
            return x;
        }

        return x < -20f ? 0f : MathF.Log(1f + MathF.Exp(x));
    }

    /// <summary>§B3.6 C2a stage: label-free re-detection of the block. Per frame: a
    /// probe-only TIR shortening solve (both bounding mini-probes' rows, feedback
    /// history wholly inside the probe), feedback-free application over the data span,
    /// h1 from the two probe anchors interpolated across the frame, the solve's own
    /// shortening target c·z^{-lag} as the chain echo model, and a probe-priced noise
    /// floor - then the chain BCJR as a no-prior exact-MAP detector. No decode label
    /// enters any estimate, so nothing here can have been laundered by a wrong decode
    /// (the §B3.6 anti-echo-chamber construction). LLRs land in the block buffer for
    /// the caller to decode; a mid-block sample shortfall aborts with a partial count,
    /// exactly like TurboCore.</summary>
    private void TurboFrozenProbePass()
    {
        var mode = _mode!;
        var dfe = _dfe!;
        if (mode.Modulation == Ms110dModulation.Qam16)
        {
            throw new InvalidOperationException("frozen probe pass is a PSK-program instrument");
        }

        Complex32[] savedTaps = dfe.SnapshotTaps();
        int fb = dfe.FbTaps;
        Span<Complex32> window = stackalloc Complex32[dfe.FfTaps];
        Span<Complex32> past = stackalloc Complex32[fb];
        int bitsPerSymbol = TurboBitsPerSymbol(mode.Modulation);
        _blockLlrCount = 0;

        (Complex32[] constellation, byte[] labels) = mode.Modulation switch
        {
            Ms110dModulation.Bpsk => (TurboBpsk, Array.Empty<byte>()),
            Ms110dModulation.Qpsk => (TurboQpsk, TurboQpskLabels),
            _ => (Ms110dTables.Psk8, SymbolToTribit8),
        };

        Span<Complex32> probeY = stackalloc Complex32[2 * mode.K];
        Span<int> probeIdx = stackalloc int[2 * mode.K];
        Span<Complex32> anchor = stackalloc Complex32[2];
        Span<float> anchorPos = stackalloc float[2];
        // §B3.8 E3 scratch: the late-lock offer's candidate anchors (adopted into
        // anchor/anchorPos only when the shifted geometry wins the floor).
        Span<Complex32> anchorAlt = stackalloc Complex32[2];
        Span<float> anchorPosAlt = stackalloc float[2];
        // §B3.7 E1″: the probe is a base sequence cyclically extended to K, so
        // probe-row regressors repeat with this period and an accepted lag beyond
        // half of it is the −(period−lag) pre-cursor folded into the causal search.
        int probePeriod = MiniProbe.Sequence(mode.K).Base.Length;

        // Probe-only shortening rows for frame f. Rows keep their feedback history
        // inside the probe (i ≥ fb), mirroring the §B3.4 Amendment 1 probe-row
        // construction. The accumulation is consumed by each solve, so every solve
        // (vote, pair diagnostic, applied) re-runs this. §B3.8 E3: shift > 0 trains
        // the same rows with the equalizer window advanced by that many chips - the
        // late-lock geometry, where the cursor rides the delayed path and the early
        // path returns as the (aliased) pre-cursor; desired and history columns are
        // symbol-indexed and do not move.
        int AccumulateProbeRows(int f, Span<Complex32> win, Span<Complex32> hist, int shift = 0)
        {
            long frameChip = _blockFrameChips[f];
            dfe.BeginTraining();
            int rows = 0;
            for (int p = 0; p < 2; p++)
            {
                Complex32[] probe = MiniProbe.Get(mode.K, boundary: ProbeShiftedAfter(((long)_blockIndex * _il!.Frames) + f + p - 1));
                long probeChip = p == 0 ? frameChip - mode.K : frameChip + mode.U;
                for (int i = fb; i < mode.K; i++)
                {
                    if (!HaveSamplesForChip(probeChip + i + shift + 2))
                    {
                        continue; // burst-edge probe tail
                    }

                    FillWindow(probeChip + i + shift, win);
                    for (int j = 0; j < fb; j++)
                    {
                        hist[j] = probe[i - 1 - j];
                    }

                    dfe.AddTrainingRow(win, hist, probe[i], weight: 1.0f);
                    rows++;
                }
            }

            return rows;
        }

        // §B3.7 E1′ (Amendment 1) vote sweep: each frame's FREE probe-only solve votes
        // with its accepted lag; the modal lag is the burst-level consensus the
        // detection sweep below constrains to. The echo delay is a physical constant
        // of the burst - the per-frame free search pays an L-fold selection margin on
        // 2·(K−fb) rows (acceptance starvation) and, on the 16-periodic K=32 probe,
        // aliases the −d pre-cursor into causal lag K/2−d (M1a's lag-11 cluster).
        // Votes only; nothing is applied here.
        int consensusLag = 0;
        if (TurboFrozenConsensus)
        {
            int voteMaxLag = Math.Min(fb, mode.U / 2);
            Span<int> votes = stackalloc int[voteMaxLag + 1];
            int totalVotes = 0;
            for (int f = 0; f < _il!.Frames; f++)
            {
                if (AccumulateProbeRows(f, window, past) == 0)
                {
                    continue;
                }

                Dfe.TirSolve vote = dfe.SolveTrainingTir(
                    regularization: _trackRidge, ffNoisePower: GenieNoisePower(),
                    maxLag: voteMaxLag, allowPair: false);
                if (vote.Lag > 0)
                {
                    votes[vote.Lag]++;
                    totalVotes++;
                }
            }

            for (int lag = 1; lag <= voteMaxLag; lag++)
            {
                if (votes[lag] > (consensusLag > 0 ? votes[consensusLag] : 0))
                {
                    consensusLag = lag;
                }
            }

            FrameDiagnostics?.Invoke(FormattableString.Invariant(
                $"frozen-consensus b{_blockIndex}: lag={consensusLag} votes={(consensusLag > 0 ? votes[consensusLag] : 0)}/{totalVotes}"));
        }

        for (int f = 0; f < _il!.Frames; f++)
        {
            long frameChip = _blockFrameChips[f];
            Complex32[] precedingProbe = MiniProbe.Get(mode.K, boundary: ProbeShiftedAfter(((long)_blockIndex * _il.Frames) + f - 1));
            Complex32[] followingProbe = MiniProbe.Get(mode.K, boundary: ProbeShiftedAfter(((long)_blockIndex * _il.Frames) + f));

            int solveRows = AccumulateProbeRows(f, window, past);
            if (solveRows == 0)
            {
                dfe.LoadTaps(savedTaps);
                dfe.BeginTraining();
                return;
            }

            // §B3.7 M1a: log-only straddle-pair solve on the same rows. The applied
            // solve runs LAST so its taps stand for the frame's Equalize calls.
            if (TurboFrozenPairDiag && FrameDiagnostics is not null)
            {
                Dfe.TirSolve pairTir = dfe.SolveTrainingTir(
                    regularization: _trackRidge, ffNoisePower: GenieNoisePower(),
                    maxLag: Math.Min(fb, mode.U / 2), allowPair: true);
                FrameDiagnostics.Invoke(FormattableString.Invariant(
                    $"frozen-pair b{_blockIndex} f{f}: rows={solveRows} lag={pairTir.Lag} |c1|={Math.Sqrt(pairTir.Coefficient.MagnitudeSquared()):F3} lag2={pairTir.Lag2} |c2|={Math.Sqrt(pairTir.Coefficient2.MagnitudeSquared()):F3} sseN={pairTir.SseNull:E3} sseP={pairTir.SseTir:E3}"));
                AccumulateProbeRows(f, window, past);
            }

            // §B3.7 E1′: constrained to the burst consensus when one exists (single-
            // candidate margin inside the solve); consensusLag == 0 = the free search,
            // bit-identical to the pre-E1′ pass.
            Dfe.TirSolve tir = dfe.SolveTrainingTir(
                regularization: _trackRidge, ffNoisePower: GenieNoisePower(),
                maxLag: Math.Min(fb, mode.U / 2), allowPair: false, onlyLag: consensusLag);
            int delay = Math.Max(1, tir.Lag);
            Complex32 h2Wire = tir.Lag > 0 ? tir.Coefficient : Complex32.Zero;

            // Feedback-free application everywhere (ISI lives in the chain model): the
            // data span, and the probe rows the anchors/noise floor are measured on -
            // SAME domain as the chains will see.
            for (int j = 0; j < fb; j++)
            {
                past[j] = Complex32.Zero;
            }

            var rxWire = new Complex32[mode.U];
            for (int u = 0; u < mode.U; u++)
            {
                if (!HaveSamplesForChip(frameChip + u + 2))
                {
                    dfe.LoadTaps(savedTaps);
                    dfe.BeginTraining();
                    return;
                }

                FillWindow(frameChip + u, window);
                rxWire[u] = dfe.Equalize(window, past);
            }

            // Probe anchors: post-FF response on probe rows, echo term removed with the
            // solve's own coefficient. Row centres give the interpolation abscissae.
            float noiseAcc = 0f;
            int noiseRows = 0;
            for (int p = 0; p < 2; p++)
            {
                Complex32[] probe = p == 0 ? precedingProbe : followingProbe;
                long probeChip = p == 0 ? frameChip - mode.K : frameChip + mode.U;
                float uBase = p == 0 ? -mode.K : mode.U;
                int firstRow = tir.Lag > 0 ? tir.Lag : 0;
                var acc = Complex32.Zero;
                int n = 0;
                float posSum = 0f;
                for (int i = firstRow; i < mode.K; i++)
                {
                    if (!HaveSamplesForChip(probeChip + i + 2))
                    {
                        continue;
                    }

                    FillWindow(probeChip + i, window);
                    Complex32 y = dfe.Equalize(window, past);
                    if (tir.Lag > 0)
                    {
                        y -= h2Wire * probe[i - tir.Lag];
                    }

                    probeY[(p * mode.K) + n] = y;
                    probeIdx[(p * mode.K) + n] = i;
                    acc += y * probe[i].Conj();
                    posSum += uBase + i;
                    n++;
                }

                anchor[p] = n > 0 ? acc * (1f / n) : Complex32.Zero;
                anchorPos[p] = n > 0 ? posSum / n : (p == 0 ? -mode.K * 0.5f : mode.U + (mode.K * 0.5f));
                for (int r = 0; r < n; r++)
                {
                    int i = probeIdx[(p * mode.K) + r];
                    Complex32 resid = probeY[(p * mode.K) + r] - (anchor[p] * probe[i]);
                    noiseAcc += resid.MagnitudeSquared();
                    noiseRows++;
                }
            }

            // MagnitudeSquared() sums both complex dimensions; the BCJR wants σ² per dimension
            // (the #65 2×-under-confidence lesson).
            float noiseVar = Math.Max(noiseRows > 0 ? 0.5f * noiseAcc / noiseRows : 1e-2f, 1e-6f);

            // §B3.8 E3 (Amendment 1): late-lock geometry offer. The causal accept can
            // sit on a frame whose delayed path dominates the cursor (|c| ≳ 1) - the
            // feedback-free FF then fails to equalize the frame and the priced floor
            // explodes 30-80×, drowning the frame in honestly-priced garbage LLRs,
            // while the identical physics rides cleanly in the late-lock geometry
            // (the natural pre-cursor frames' floors). Re-train with the window
            // shifted by the accepted lag (the shift performs the re-lock; the tap
            // shape carries over, so the ridge anchor stays approximately right in
            // shifted coordinates), solve only the aliased pre-cursor lag, price the
            // shifted floor identically, and keep the geometry with the lower floor -
            // arbitration by the quantity the chain is actually priced with, no
            // threshold knob.
            int lockShift = 0;
            if (_frozenRelockActive && TurboFrozenPreCursor
                && tir.Lag >= 1 && tir.Lag < probePeriod / 2
                && probePeriod - tir.Lag <= Math.Min(fb, mode.U / 2))
            {
                int shift = tir.Lag;
                Complex32[] causalTaps = dfe.SnapshotTaps();
                bool adopted = false;
                if (AccumulateProbeRows(f, window, past, shift) == solveRows)
                {
                    Dfe.TirSolve sTir = dfe.SolveTrainingTir(
                        regularization: _trackRidge, ffNoisePower: GenieNoisePower(),
                        maxLag: Math.Min(fb, mode.U / 2), allowPair: false,
                        onlyLag: probePeriod - shift);
                    if (sTir.Lag == probePeriod - shift)
                    {
                        for (int j = 0; j < fb; j++)
                        {
                            past[j] = Complex32.Zero;
                        }

                        var shiftedRx = new Complex32[mode.U];
                        bool have = true;
                        for (int u = 0; u < mode.U; u++)
                        {
                            if (!HaveSamplesForChip(frameChip + u + shift + 2))
                            {
                                have = false;
                                break;
                            }

                            FillWindow(frameChip + u + shift, window);
                            shiftedRx[u] = dfe.Equalize(window, past);
                        }

                        if (have)
                        {
                            // Anchors + floor in the shifted geometry - the same
                            // construction as above; the folded subtraction
                            // probe[i − lag] is pre-cursor-correct through the
                            // periodic probe.
                            float altAcc = 0f;
                            int altRows = 0;
                            for (int p = 0; p < 2; p++)
                            {
                                Complex32[] probe = p == 0 ? precedingProbe : followingProbe;
                                long probeChip = p == 0 ? frameChip - mode.K : frameChip + mode.U;
                                float uBase = p == 0 ? -mode.K : mode.U;
                                var acc = Complex32.Zero;
                                int n = 0;
                                float posSum = 0f;
                                for (int i = sTir.Lag; i < mode.K; i++)
                                {
                                    if (!HaveSamplesForChip(probeChip + i + shift + 2))
                                    {
                                        continue;
                                    }

                                    FillWindow(probeChip + i + shift, window);
                                    Complex32 y = dfe.Equalize(window, past) - (sTir.Coefficient * probe[i - sTir.Lag]);
                                    probeY[(p * mode.K) + n] = y;
                                    probeIdx[(p * mode.K) + n] = i;
                                    acc += y * probe[i].Conj();
                                    posSum += uBase + i;
                                    n++;
                                }

                                anchorAlt[p] = n > 0 ? acc * (1f / n) : Complex32.Zero;
                                anchorPosAlt[p] = n > 0 ? posSum / n : (p == 0 ? -mode.K * 0.5f : mode.U + (mode.K * 0.5f));
                                for (int r = 0; r < n; r++)
                                {
                                    int i = probeIdx[(p * mode.K) + r];
                                    Complex32 resid = probeY[(p * mode.K) + r] - (anchorAlt[p] * probe[i]);
                                    altAcc += resid.MagnitudeSquared();
                                    altRows++;
                                }
                            }

                            float altVar = Math.Max(altRows > 0 ? 0.5f * altAcc / altRows : 1e-2f, 1e-6f);
                            bool adopt = altRows > 0 && altVar < TurboFrozenRelockMargin * noiseVar;
                            FrameDiagnostics?.Invoke(FormattableString.Invariant(
                                $"frozen-relock b{_blockIndex} f{f}: lag={shift} causal={noiseVar:E3} alt={altVar:E3} adopted={(adopt ? 1 : 0)}"));
                            if (adopt)
                            {
                                tir = sTir;
                                delay = Math.Max(1, tir.Lag);
                                h2Wire = tir.Coefficient;
                                rxWire = shiftedRx;
                                anchor[0] = anchorAlt[0];
                                anchor[1] = anchorAlt[1];
                                anchorPos[0] = anchorPosAlt[0];
                                anchorPos[1] = anchorPosAlt[1];
                                noiseVar = altVar;
                                lockShift = shift;
                                adopted = true;
                            }
                        }
                    }
                }

                if (!adopted)
                {
                    dfe.LoadTaps(causalTaps);
                }
            }

            // §B3.7 E1″(a) (Amendment 2): an accepted lag beyond half the probe base
            // period is the pre-cursor folded through the periodic probe - the causal
            // chain model at that lag is measurably worse than none (E1′ alias→0
            // class). The solve, FF, anchors and floor stand (they fit the true
            // response through the folded column); the CHAIN echo model is dropped and
            // the pre-cursor priced into the floor (unit-power symbols, per dimension).
            int chainDelay = delay;
            Complex32 h2Chain = h2Wire;
            bool aliasFrame = tir.Lag > probePeriod / 2;
            bool preCursorFrame = TurboFrozenPreCursor && aliasFrame && probePeriod - tir.Lag >= 1;
            if (preCursorFrame)
            {
                // §B3.7 E1″(b): exact pre-cursor chains - assembly below shifts the
                // observation by d = period − lag and swaps the tap roles.
                chainDelay = probePeriod - tir.Lag;
            }
            else if (TurboFrozenAliasNull && aliasFrame)
            {
                chainDelay = 1;
                h2Chain = Complex32.Zero;
                noiseVar += 0.5f * tir.Coefficient.MagnitudeSquared();
            }

            // Descrambled-domain assembly, mirroring TurboCore: h1 rides through the
            // derotation; the wire echo coefficient folds the rotor product.
            _scrambler.Reset();
            var rotors = new Complex32[mode.U];
            for (int u = 0; u < mode.U; u++)
            {
                rotors[u] = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
            }

            var rxDesc = new Complex32[mode.U];
            var h1Span = new Complex32[mode.U];
            var h2Span = new Complex32[mode.U];
            var preceding = new Complex32[chainDelay];
            for (int c = 0; c < chainDelay; c++)
            {
                preceding[c] = precedingProbe[(mode.K - chainDelay) + c];
            }

            float span = Math.Max(1f, anchorPos[1] - anchorPos[0]);
            if (preCursorFrame)
            {
                // §B3.7 E1″(b) assembly: o[u] = yWire[u−d] = h1(u−d)·xw[u−d] + c·xw[u].
                // Derotated by r̄(u): the pre-cursor coefficient rides the cursor slot
                // rotor-free; h1 (evaluated at wire position u−d) takes the echo slot
                // with the usual rotor fold, probe chips as the u < d sources. The
                // u < d observations are the preceding probe's last d positions,
                // equalized in the same feedback-free domain.
                int d = chainDelay;
                for (int u = 0; u < d; u++)
                {
                    // §B3.8 E3: on re-locked frames the whole geometry rides shifted
                    // windows (lockShift = d there, so these chips are frameChip + u).
                    long chip = frameChip - d + u + lockShift;
                    if (!HaveSamplesForChip(chip + 2))
                    {
                        dfe.LoadTaps(savedTaps);
                        dfe.BeginTraining();
                        return;
                    }

                    FillWindow(chip, window);
                    rxDesc[u] = dfe.Equalize(window, past) * rotors[u].Conj();
                }

                for (int u = d; u < mode.U; u++)
                {
                    rxDesc[u] = rxWire[u - d] * rotors[u].Conj();
                }

                for (int u = 0; u < mode.U; u++)
                {
                    h1Span[u] = tir.Coefficient;
                    float t = Math.Clamp(((u - d) - anchorPos[0]) / span, 0f, 1f);
                    Complex32 h1w = (anchor[0] * (1f - t)) + (anchor[1] * t);
                    h2Span[u] = u >= d
                        ? h1w * rotors[u - d] * rotors[u].Conj()
                        : h1w * rotors[u].Conj();
                }
            }
            else
            {
                for (int u = 0; u < mode.U; u++)
                {
                    float t = Math.Clamp((u - anchorPos[0]) / span, 0f, 1f);
                    h1Span[u] = (anchor[0] * (1f - t)) + (anchor[1] * t);
                    rxDesc[u] = rxWire[u] * rotors[u].Conj();
                    h2Span[u] = u >= chainDelay
                        ? h2Chain * rotors[u - chainDelay] * rotors[u].Conj()
                        : h2Chain * rotors[u].Conj();
                }
            }

            if (FrameDiagnostics is not null)
            {
                FrameDiagnostics.Invoke(FormattableString.Invariant(
                    $"frozen-frame b{_blockIndex} f{f}: lag={tir.Lag} |c|={Math.Sqrt(h2Wire.MagnitudeSquared()):F3} n={noiseVar:E3} rows={solveRows} h1a={anchor[0].Abs():F3}|{anchor[1].Abs():F3} sseN={tir.SseNull:E3} sse1={tir.SseTir:E3} a0={anchor[0].Re:F4},{anchor[0].Im:F4} a1={anchor[1].Re:F4},{anchor[1].Im:F4} shift={lockShift}"));
            }

            var frameLlrs = new float[mode.U * bitsPerSymbol];
            Ms110dChainBcjr.Equalize(
                rxDesc, h1Span, h2Span, chainDelay, noiseVar,
                constellation, labels, bitsPerSymbol, preceding, frameLlrs,
                default, noiseVarPerSymbol: default);
            for (int i = 0; i < frameLlrs.Length; i++)
            {
                AddLlr(frameLlrs[i]);
            }
        }

        dfe.LoadTaps(savedTaps);
        dfe.BeginTraining();
    }

    /// <summary>§B3.6 salvage (Amendment 1): on revert-at-cap, re-detect the block with
    /// the label-free frozen probe pass and run a fresh soft loop (same cap) from that
    /// seed. Returns true with <paramref name="info"/> holding the new fixed-point decode;
    /// returns false - <paramref name="info"/> scribbled, caller restores the first
    /// pass - when the pass aborts or the seeded loop finds no fixed point either.</summary>
    private bool TrySalvageRevert(byte[] info, byte[] prevInfo)
    {
        TurboFrozenProbePass();
        if (_blockLlrCount != _il!.SizeBits)
        {
            return false;
        }

        Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, info);
        for (int iter = 0; iter < 24; iter++)
        {
            if (iter == 0)
            {
                TurboReequalize(info);
            }
            else
            {
                TurboReequalizeSoft();
            }

            if (_blockLlrCount != _il.SizeBits)
            {
                return false;
            }

            Array.Copy(info, prevInfo, info.Length);
            Ms110dFraming.DecodeBlock(_viterbi!, _puncture!, _interleaver!, _blockLlrs, info);
            if (FrameDiagnostics is not null)
            {
                int diffs = 0;
                for (int i = 0; i < info.Length; i++)
                {
                    diffs += info[i] != prevInfo[i] ? 1 : 0;
                }

                FrameDiagnostics.Invoke($"salvage-iter b{_blockIndex} i{iter} decode-changes={diffs}");
            }

            if (info.AsSpan().SequenceEqual(prevInfo))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>§B3.8 Amendment 3: the second salvage rung. Only when the standard
    /// salvage fails does the frozen pass re-run with the late-lock offer active -
    /// blocks that converge anywhere along the existing path are structurally
    /// untouched (a ceiling block's fixed point can wobble a few bits under ANY seed
    /// change; no label-free arbitration can prefer one converged fixed point over
    /// another). Same contract as <see cref="TrySalvageRevert"/>.</summary>
    private bool TrySalvageRelock(byte[] info, byte[] prevInfo)
    {
        if (!TurboFrozenRelock)
        {
            return false;
        }

        _frozenRelockActive = true;
        try
        {
            return TrySalvageRevert(info, prevInfo);
        }
        finally
        {
            _frozenRelockActive = false;
        }
    }

    /// <summary>The turbo re-equalization machinery (§B2.3): per-frame FF batch-LS re-solve
    /// on the expected symbols, per-segment h1, scrambler-exact single-lag echo, chain-BCJR
    /// detection. <paramref name="expectedAll"/> holds one expected wire symbol per data
    /// position (frame-major); <paramref name="symbolVar"/> is the per-symbol prior variance
    /// 1−|E[x]|² for soft labels, or null for hard labels - null skips the variance terms
    /// entirely, keeping the hard/oracle path bit-identical to the pre-§B3.3 code.</summary>
    private void TurboCore(Complex32[] expectedAll, float[]? symbolVar, float[]? wireExtLlrs, bool allowPair, bool truthChannel = false)
    {
        var mode = _mode!;
        var dfe = _dfe!;

        // Save DFE state - turbo must not corrupt tracking for future blocks.
        Complex32[] savedTaps = dfe.SnapshotTaps();

        int fb = dfe.FbTaps;
        Span<Complex32> window = stackalloc Complex32[dfe.FfTaps];
        Span<Complex32> past = stackalloc Complex32[fb];
        Span<float> pastVar = stackalloc float[fb];
        bool genie = _genieRing is not null;
        Span<Complex32> estWindow = stackalloc Complex32[dfe.FfTaps];
        // 4 segments, measured: the banked #81 "16-segment h1 −10%" lever does NOT
        // compose with TIR - at 16 the correlation windows (16 symbols per segment at
        // U = 256, less after echo-lag guards) are too noisy for the pinned-echo model
        // and the WN7 corpse oracle regressed 209 → 495 with a lost convergence
        // (§B3.3 twolag note, step 3). That banked measurement was inversion-regime-
        // specific; under TIR the estimates need the averaging.
        const int Segments = 4;
        Span<Complex32> segH1 = stackalloc Complex32[Segments];
        Span<Complex32> segH2 = stackalloc Complex32[Segments];
        Span<Complex32> segH2b = stackalloc Complex32[Segments];
        Span<float> segCentre = stackalloc float[Segments];
        // §B4.1 floor-estimator instrument (diagnostics ONLY - pricing stays the frame
        // constant): the assembly residual bucketed on the same u/segLen partition as the
        // channel anchors, so the corpse can measure within-frame heteroscedasticity and
        // score candidate estimators. The banked §B3.3 pricing consumed these buckets;
        // here they only reach the turbo-frame line.
        Span<float> segResid = stackalloc float[Segments];
        Span<int> segResidCount = stackalloc int[Segments];
        Span<float> segNv = stackalloc float[Segments];
        Span<float> segPrice = stackalloc float[Segments];
        // W1/W2 truth-gauge scratch (slot layout {a1,b1, a2,b2, a2b,b2b, aPre,bPre,
        // aPost,bPost}; two parts for the V-split variant; hoisted out of the frame loop
        // for the stackalloc-in-loop rule). Zero cost when truthChannel is false.
        Span<Complex32> truthGauge = stackalloc Complex32[20];
        Span<Complex32> truthPhi = stackalloc Complex32[10];
        Span<Complex32> truthGram = stackalloc Complex32[100];
        Span<Complex32> truthRhs = stackalloc Complex32[10];
        Span<int> truthSlots = stackalloc int[10];
        int bitsPerSymbol = TurboBitsPerSymbol(mode.Modulation);
        _blockLlrCount = 0;
        int tirFrames = 0;
        long tirLagSum = 0;
        double tirCoeffSum = 0;
        int tirPairFrames = 0;
        double tirCoeff2Sum = 0;
        bool probeDiag = TurboProbeDiag && FrameDiagnostics is not null;
        double probePriceErr = 0;
        int probePriceRows = 0;

        (Complex32[] constellation, byte[] labels) = mode.Modulation switch
        {
            Ms110dModulation.Bpsk => (TurboBpsk, Array.Empty<byte>()),
            Ms110dModulation.Qpsk => (TurboQpsk, TurboQpskLabels),
            Ms110dModulation.Psk8 => (Ms110dTables.Psk8, SymbolToTribit8),
            // §B3.4: QAM16 runs the chains in the WIRE domain (the XOR scramble is a
            // label permutation with no geometric descrambled form): identity labels;
            // the scramble enters as a per-symbol prior permutation plus per-bit output
            // sign flips below.
            _ => (Ms110dTables.Qam16, Array.Empty<byte>()),
        };
        bool qamWire = mode.Modulation == Ms110dModulation.Qam16;

        // §B3.3 turbo priors: outer-code extrinsics become per-symbol log-priors on the
        // chain BCJR (descrambled labels - the scrambler never touches the bit→ring map).
        float[]? logPriors = wireExtLlrs is null ? null : new float[mode.U * constellation.Length];
        Span<float> lp0 = stackalloc float[4];
        Span<float> lp1 = stackalloc float[4];

        for (int f = 0; f < _il!.Frames; f++)
        {
            long frameChip = _blockFrameChips[f];
            ReadOnlySpan<Complex32> expected = expectedAll.AsSpan(f * mode.U, mode.U);
            ReadOnlySpan<float> expectedVar =
                symbolVar is null ? default : symbolVar.AsSpan(f * mode.U, mode.U);

            // §B3.4: per-symbol second moment E[|x|²] = |E[x]|² + Var for the QAM16
            // correlation denominators below - the PSK-ring E[|x|²] = 1 identity that
            // let them divide by symbol COUNTS does not hold across two rings. Bounded
            // below by the inner-ring energy 0.134, so the denominators stay
            // conditioned; PSK paths keep their count denominators bit-identically.
            float[]? x2 = null;
            if (qamWire)
            {
                x2 = new float[mode.U];
                for (int u = 0; u < mode.U; u++)
                {
                    x2[u] = expected[u].MagnitudeSquared() + (symbolVar is null ? 0f : expectedVar[u]);
                }
            }

            // The probe preceding this frame's data supplies the known symbol history for
            // the head rows of the shortening solve and the known echo sources for the
            // first `delay` chain-BCJR symbols (§B2.2). Frame f's preceding probe is the
            // one that FOLLOWED frame f - 1 (for the first frame of the burst, the
            // preamble-ending probe, never shifted).
            Complex32[] precedingProbe = MiniProbe.Get(mode.K, boundary: ProbeShiftedAfter(((long)_blockIndex * _il.Frames) + f - 1));

            // Batch-LS shortening solve (§B3.3 TIR): the feedback columns carry the
            // wire-domain symbol history (probe tail before u = 0), so the joint solve can
            // hand ISI at one lag to the chain BCJR instead of inverting it into a train.
            dfe.BeginTraining();
            for (int u = 0; u < mode.U; u++)
            {
                if (!HaveSamplesForChip(frameChip + u + 2))
                {
                    // Abort mid-block: restore taps AND leave a clean training
                    // accumulator - a half-filled Gram would poison the next probe solve.
                    dfe.LoadTaps(savedTaps);
                    dfe.BeginTraining();
                    return;
                }

                if (genie)
                {
                    FillWindowEst(frameChip + u, window); // training row: estimation read
                }
                else
                {
                    FillWindow(frameChip + u, window);
                }

                for (int j = 0; j < fb; j++)
                {
                    int idx = u - 1 - j;
                    past[j] = idx >= 0 ? expected[idx] : precedingProbe[mode.K + idx];
                }

                if (symbolVar is null)
                {
                    dfe.AddTrainingRow(window, past, expected[u], weight: 1.0f);
                }
                else
                {
                    for (int j = 0; j < fb; j++)
                    {
                        int idx = u - 1 - j;
                        pastVar[j] = idx >= 0 ? expectedVar[idx] : 0f;
                    }

                    dfe.AddTrainingRow(window, past, pastVar, expected[u], weight: 1.0f);
                }
            }

            // §B3.4 Amendment 1 (QAM16 only): the re-solve's data rows are LABEL rows,
            // and at a coin-flip first decode the solve has no anchor in truth -
            // measured as a strong initial pull that stalls into a wander plateau
            // (0c/11r, corpse w0/b0). The bounding mini-probes are label-free truth at
            // both ends of the frame: join their rows (those whose feedback history
            // lies wholly inside the probe) so the re-solved equalizer keeps a truth
            // floor at every iteration - probe-grade at coin-flip labels, oracle-grade
            // as labels improve.
            if (qamWire)
            {
                Complex32[] followingProbe = MiniProbe.Get(mode.K, boundary: ProbeShiftedAfter(((long)_blockIndex * _il.Frames) + f));
                for (int p = 0; p < 2; p++)
                {
                    Complex32[] probe = p == 0 ? precedingProbe : followingProbe;
                    long probeChip = p == 0 ? frameChip - mode.K : frameChip + mode.U;
                    for (int i = fb; i < mode.K; i++)
                    {
                        if (!HaveSamplesForChip(probeChip + i + 2))
                        {
                            continue; // burst-edge probe tail; the data rows above are complete
                        }

                        if (genie)
                        {
                            FillWindowEst(probeChip + i, window);
                        }
                        else
                        {
                            FillWindow(probeChip + i, window);
                        }

                        for (int j = 0; j < fb; j++)
                        {
                            past[j] = probe[i - 1 - j];
                        }

                        dfe.AddTrainingRow(window, past, probe[i], weight: 1.0f);
                    }
                }
            }

            // Solve with _trackRidge for all modes; the TIR acceptance margin keeps
            // echo-free frames on the full-inversion (null) candidate exactly.
            // Pair candidates only when the expected labels are trustworthy (§B3.3
            // label-trust gate): the oracle path (truth) and the soft iterations (E[x]
            // whose uncertainty the cancellation prices via the variance bump). The
            // shipped hard iteration 0 re-encodes a first decode that is up to ~49%
            // wrong on deep-start blocks, and cancelling the adjacent tap with those
            // labels injects unpriced error into the very observation the chains
            // equalize - measured flipping a marginal WN6 block out of convergence
            // (11.4k errors in one block, 146× the point's BER) while the corpse and
            // every trusted-label consumer improved.
            Dfe.TirSolve tir = dfe.SolveTrainingTir(
                regularization: _trackRidge, ffNoisePower: GenieNoisePower(),
                maxLag: Math.Min(fb, mode.U / 2), allowPair: allowPair);
            if (tir.Lag > 0)
            {
                tirFrames++;
                tirLagSum += tir.Lag;
                tirCoeffSum += Math.Sqrt(tir.Coefficient.MagnitudeSquared());
                if (tir.Lag2 > 0)
                {
                    tirPairFrames++;
                    tirCoeff2Sum += Math.Sqrt(tir.Coefficient2.MagnitudeSquared());
                }
            }

            // §B3.6 M1a: price the solved channel on the preceding probe's rows - known
            // symbols whose feedback history and TIR echo sources stay wholly inside the
            // probe, mirroring the solve's own probe-row construction (§B3.4 Amendment 1).
            // A channel refit to wrong labels explains its label rows by construction;
            // these rows it cannot launder.
            if (probeDiag)
            {
                long probeChip = frameChip - mode.K;
                int firstRow = Math.Max(fb, Math.Max(tir.Lag, tir.Lag2));
                for (int i = firstRow; i < mode.K; i++)
                {
                    if (!HaveSamplesForChip(probeChip + i + 2))
                    {
                        continue;
                    }

                    FillWindow(probeChip + i, window);
                    for (int j = 0; j < fb; j++)
                    {
                        past[j] = precedingProbe[i - 1 - j];
                    }

                    Complex32 model = precedingProbe[i];
                    if (tir.Lag > 0)
                    {
                        model += tir.Coefficient * precedingProbe[i - tir.Lag];
                        if (tir.Lag2 > 0)
                        {
                            model += tir.Coefficient2 * precedingProbe[i - tir.Lag2];
                        }
                    }

                    probePriceErr += (dfe.Equalize(window, past) - model).MagnitudeSquared();
                    probePriceRows++;
                }
            }

            // Chain-BCJR re-equalization for every mode (§B2.2/§B2.3; QAM16 since §B3.4).
            // Channel estimation runs in the WIRE domain: the legacy path estimated the
            // echo tap on DESCRAMBLED quantities, where the scrambler's rotor product
            // r(u−d)·r̄(u) phase-scrambles the lag correlation toward zero - the echo
            // model was scrambler-blind (issue #65). The scrambler re-enters the PSK
            // model exactly through the per-position h2 span below; QAM16 stays wire
            // end-to-end (nibbles permute priors and flip output LLR signs instead).
            _scrambler.Reset();
            var rotors = new Complex32[mode.U];
            int[]? nibbles = qamWire ? new int[mode.U] : null;
            for (int u = 0; u < mode.U; u++)
            {
                if (qamWire)
                {
                    nibbles![u] = _scrambler.NextQam(0, 4);
                }
                else
                {
                    rotors[u] = Ms110dTables.Psk8[_scrambler.NextPsk(0)];
                }
            }

            for (int j = 0; j < fb; j++)
            {
                past[j] = Complex32.Zero;
            }

            // Genie: the channel estimates read the clean stream through the SAME
            // re-solved taps; detection (the BCJR input and its noise floor) stays noisy.
            var rxWire = new Complex32[mode.U];
            Complex32[] estWire = genie ? new Complex32[mode.U] : rxWire;
            for (int u = 0; u < mode.U; u++)
            {
                FillWindow(frameChip + u, window);
                rxWire[u] = dfe.Equalize(window, past);
                if (genie)
                {
                    FillWindowEst(frameChip + u, estWindow);
                    estWire[u] = dfe.Equalize(estWindow, past);
                }
            }

            // Per-segment h1 anchors (§B2.1b: per-position h instead of block constants -
            // issue #65). The re-encoded symbols are mid-frame references the probes can
            // never provide, so the intra-frame trajectory is observable HERE - including
            // through fade nulls, where phase moves fastest and the probe-anchored
            // first-pass interpolation is weakest.
            int segLen = (mode.U + Segments - 1) / Segments;
            var h1Avg = Complex32.Zero;
            for (int s = 0; s < Segments; s++)
            {
                var acc = Complex32.Zero;
                float den = 0f;
                int start = s * segLen;
                int end = Math.Min(mode.U, start + segLen);
                for (int u = start; u < end; u++)
                {
                    acc += estWire[u] * expected[u].Conj();
                    if (qamWire)
                    {
                        den += x2![u];
                    }
                }

                segH1[s] = acc * (qamWire
                    ? 1f / Math.Max(1e-3f, den)
                    : 1f / Math.Max(1, end - start));
                segCentre[s] = 0.5f * (start + end - 1);
                h1Avg += segH1[s];
            }

            h1Avg *= 1f / Segments;

            int delay;
            Complex32 h2Avg;
            int delay2 = 0;
            var h2b = Complex32.Zero;
            bool segEcho = tir.Lag > 0;
            if (tir.Lag > 0)
            {
                // TIR accepted: the FF was solved to LEAVE the echo at this lag, so the
                // estimation is pinned there and the significance floor does not apply -
                // the acceptance margin already established the echo on U rows of
                // evidence, and the worst case would be an FF that deliberately left an
                // echo in with a BCJR told there is none.
                delay = tir.Lag;
                var acc = Complex32.Zero;
                float denAvg = 0f;
                for (int u = delay; u < mode.U; u++)
                {
                    acc += (estWire[u] - (h1Avg * expected[u])) * expected[u - delay].Conj();
                    if (qamWire)
                    {
                        denAvg += x2![u - delay];
                    }
                }

                h2Avg = acc * (qamWire
                    ? 1f / Math.Max(1e-3f, denAvg)
                    : 1f / (mode.U - delay));

                // Per-segment h2 on the same grid as h1 (§B2.1 applied to the echo path):
                // under TIR the echo coefficient is a real fading path, and a
                // frame-constant estimate misrepresents it exactly the way block-constant
                // h1 did before per-segment anchors. Segments starting before `delay`
                // (short U with a deep-lag echo) fall back to the frame estimate.
                for (int s = 0; s < Segments; s++)
                {
                    var segAcc = Complex32.Zero;
                    float segDen = 0f;
                    int start = Math.Max(delay, s * segLen);
                    int end = Math.Min(mode.U, (s * segLen) + segLen);
                    int count = end - start;
                    for (int u = start; u < end; u++)
                    {
                        segAcc += (estWire[u] - (h1Avg * expected[u])) * expected[u - delay].Conj();
                        if (qamWire)
                        {
                            segDen += x2![u - delay];
                        }
                    }

                    segH2[s] = count > 0
                        ? segAcc * (qamWire ? 1f / Math.Max(1e-3f, segDen) : 1f / count)
                        : h2Avg;
                }

                // §B3.3 straddle pair: the adjacent-lag coefficient, estimated on the
                // doubly-subtracted residual (h1 and the dominant echo removed) - the
                // scrambler decorrelates distinct lags on the PSK ring, so the direct
                // correlation is consistent. Cancelled softly from the observation in the
                // assembly loop below; the chain BCJR stays exact on the dominant lag.
                // Per-segment on the same grid as h1/h2: the fractional tap is the same
                // physical fading path, and a frame-constant estimate cancels with a
                // stale coefficient exactly where the fade moves fastest (measured: the
                // frame-constant form REGRESSED the b10 oracle 6 → 19).
                if (tir.Lag2 > 0)
                {
                    int start2 = Math.Max(delay, tir.Lag2);
                    var accB = Complex32.Zero;
                    float denB = 0f;
                    for (int u = start2; u < mode.U; u++)
                    {
                        Complex32 r = estWire[u] - (h1Avg * expected[u]) - (h2Avg * expected[u - delay]);
                        accB += r * expected[u - tir.Lag2].Conj();
                        if (qamWire)
                        {
                            denB += x2![u - tir.Lag2];
                        }
                    }

                    h2b = accB * (qamWire
                        ? 1f / Math.Max(1e-3f, denB)
                        : 1f / Math.Max(1, mode.U - start2));
                    delay2 = tir.Lag2;

                    for (int s = 0; s < Segments; s++)
                    {
                        var segAcc = Complex32.Zero;
                        float segDen = 0f;
                        int start = Math.Max(start2, s * segLen);
                        int end = Math.Min(mode.U, (s * segLen) + segLen);
                        int count = end - start;
                        for (int u = start; u < end; u++)
                        {
                            Complex32 r = estWire[u] - (h1Avg * expected[u]) - (h2Avg * expected[u - delay]);
                            segAcc += r * expected[u - tir.Lag2].Conj();
                            if (qamWire)
                            {
                                segDen += x2![u - tir.Lag2];
                            }
                        }

                        segH2b[s] = count > 0
                            ? segAcc * (qamWire ? 1f / Math.Max(1e-3f, segDen) : 1f / count)
                            : h2b;
                    }
                }
            }
            else
            {
                // Null (full-inversion) solve: today's free echo-delay search on wire-domain
                // residuals. The lag cap was 8 under the 2^delay trellis (issue #64's
                // ceiling); each chain here carries M states whatever the delay, so the cap
                // is physical, not computational: 24 symbols (10 ms) covers the D-LXV 9 ms
                // static spread, bounded by the probe length so the pre-block echo source
                // stays inside the known probe.
                int maxLag = Math.Min(Math.Min(24, mode.U / 2), mode.K);
                delay = 1;
                h2Avg = Complex32.Zero;
                for (int lag = 1; lag <= maxLag; lag++)
                {
                    var acc = Complex32.Zero;
                    float den = 0f;
                    for (int u = lag; u < mode.U; u++)
                    {
                        acc += (estWire[u] - (h1Avg * expected[u])) * expected[u - lag].Conj();
                        if (qamWire)
                        {
                            den += x2![u - lag];
                        }
                    }

                    Complex32 h2 = acc * (qamWire ? 1f / Math.Max(1e-3f, den) : 1f / (mode.U - lag));
                    if (h2.MagnitudeSquared() > h2Avg.MagnitudeSquared())
                    {
                        h2Avg = h2;
                        delay = lag;
                    }
                }

                // Significance floor: each noise-only lag estimate has variance ≈ σ²/U, so
                // the max over ≤24 lags sits near 2·ln24·σ²/U - at the worst gated point
                // (WN3, U=96, ~4 dB Es/N0) that is ≈ 0.027·|h1|². Below 0.04·|h1|² the
                // "echo" is a noise pick: run the chains echo-free (matched-filter mode).
                if (h2Avg.MagnitudeSquared() < 0.04f * h1Avg.MagnitudeSquared())
                {
                    h2Avg = Complex32.Zero;
                    delay = 1;
                }
            }

            // W1 true-channel injection (wn8-program): replace the segment-anchor TIME
            // BASIS with the recorded truth trajectories. Each model tap gets a static
            // per-frame two-basis gauge h(u) = a·g₁(u) + b·g₂(u); the gauge constants
            // are LS-fitted on this frame's true symbols, so labels supply only
            // 2·(1+echo+straddle) complex constants per frame while the fade motion is
            // per-symbol exact. The FF solve, rxWire, echo lags, pricing, and the chain
            // call are the oracle path's own - the estimator's TIME MODEL is the only
            // delta, which is precisely the W1 registration's question.
            Complex32[]? tg1 = null, tg2 = null;
            Complex32[]? truthFollow = null;
            bool truthFit = false;
            bool truthEcho = false;
            bool truthXtaps = false;
            int truthParts = 1;
            if (truthChannel && TruthGainsAtSample is not null)
            {
                tg1 = new Complex32[mode.U];
                tg2 = new Complex32[mode.U];
                for (int u = 0; u < mode.U; u++)
                {
                    (tg1[u], tg2[u]) = TruthGainsAtSample(2.0 * PositionOfChip(frameChip + u));
                }

                truthEcho = segEcho || h2Avg.MagnitudeSquared() > 0f;
                truthXtaps = TruthGaugeXtaps;
                truthParts = Math.Clamp(TruthGaugeSplit, 1, 2);
                truthFollow = MiniProbe.Get(mode.K, boundary: ProbeShiftedAfter(((long)_blockIndex * _il.Frames) + f));

                // Compact unknown list → gauge slots; inactive slots stay zero so the
                // assembly reads are branch-free on the layout.
                int n = 0;
                truthSlots[n++] = 0;
                truthSlots[n++] = 1;
                if (truthEcho)
                {
                    truthSlots[n++] = 2;
                    truthSlots[n++] = 3;
                }

                if (delay2 > 0)
                {
                    truthSlots[n++] = 4;
                    truthSlots[n++] = 5;
                }

                if (truthXtaps)
                {
                    truthSlots[n++] = 6;
                    truthSlots[n++] = 7;
                    truthSlots[n++] = 8;
                    truthSlots[n++] = 9;
                }

                truthGauge.Clear();
                truthFit = true;
                for (int part = 0; part < truthParts; part++)
                {
                    int uStart = part * mode.U / truthParts;
                    int uEnd = (part + 1) * mode.U / truthParts;
                    truthGram.Clear();
                    truthRhs.Clear();
                    for (int u = uStart; u < uEnd; u++)
                    {
                        truthPhi.Clear();
                        truthPhi[0] = tg1[u] * expected[u];
                        truthPhi[1] = tg2[u] * expected[u];
                        if (truthEcho)
                        {
                            Complex32 src = u >= delay
                                ? expected[u - delay]
                                : precedingProbe[(mode.K - delay) + u];
                            truthPhi[2] = tg1[u] * src;
                            truthPhi[3] = tg2[u] * src;
                        }

                        if (delay2 > 0)
                        {
                            Complex32 srcB = u >= delay2
                                ? expected[u - delay2]
                                : precedingProbe[mode.K + (u - delay2)];
                            truthPhi[4] = tg1[u] * srcB;
                            truthPhi[5] = tg2[u] * srcB;
                        }

                        if (truthXtaps)
                        {
                            Complex32 pre = u + 1 < mode.U ? expected[u + 1] : truthFollow[0];
                            Complex32 post = u >= 1 ? expected[u - 1] : precedingProbe[mode.K - 1];
                            truthPhi[6] = tg1[u] * pre;
                            truthPhi[7] = tg2[u] * pre;
                            truthPhi[8] = tg1[u] * post;
                            truthPhi[9] = tg2[u] * post;
                        }

                        for (int i = 0; i < n; i++)
                        {
                            Complex32 pi = truthPhi[truthSlots[i]].Conj();
                            truthRhs[i] += pi * estWire[u];
                            for (int j = 0; j < n; j++)
                            {
                                truthGram[(i * 10) + j] += pi * truthPhi[truthSlots[j]];
                            }
                        }
                    }

                    float ridge = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        ridge += truthGram[(i * 10) + i].Re;
                    }

                    ridge = Math.Max(1e-6f, 1e-4f * ridge / n);
                    for (int i = 0; i < n; i++)
                    {
                        truthGram[(i * 10) + i] += new Complex32(ridge, 0f);
                    }

                    truthFit &= SolveComplex(truthGram, truthRhs, n);
                    if (truthFit)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            truthGauge[(part * 10) + truthSlots[i]] = truthRhs[i];
                        }
                    }
                }

                if (truthFit && FrameDiagnostics is not null)
                {
                    float fitResid = 0f;
                    for (int u = 0; u < mode.U; u++)
                    {
                        Span<Complex32> tp = truthGauge.Slice(
                            truthParts == 2 && u >= mode.U / 2 ? 10 : 0, 10);
                        Complex32 model = ((tp[0] * tg1[u]) + (tp[1] * tg2[u])) * expected[u];
                        if (truthEcho)
                        {
                            Complex32 src = u >= delay
                                ? expected[u - delay]
                                : precedingProbe[(mode.K - delay) + u];
                            model += ((tp[2] * tg1[u]) + (tp[3] * tg2[u])) * src;
                        }

                        if (delay2 > 0)
                        {
                            Complex32 srcB = u >= delay2
                                ? expected[u - delay2]
                                : precedingProbe[mode.K + (u - delay2)];
                            model += ((tp[4] * tg1[u]) + (tp[5] * tg2[u])) * srcB;
                        }

                        if (truthXtaps)
                        {
                            Complex32 pre = u + 1 < mode.U ? expected[u + 1] : truthFollow[0];
                            Complex32 post = u >= 1 ? expected[u - 1] : precedingProbe[mode.K - 1];
                            model += ((tp[6] * tg1[u]) + (tp[7] * tg2[u])) * pre;
                            model += ((tp[8] * tg1[u]) + (tp[9] * tg2[u])) * post;
                        }

                        fitResid += (estWire[u] - model).MagnitudeSquared();
                    }

                    FrameDiagnostics.Invoke(
                        FormattableString.Invariant(
                            $"truth-frame b{_blockIndex} f{f}: lag={delay} lag2={delay2} split={truthParts} xtaps={(truthXtaps ? 1 : 0)} rms={0.5f * fitResid / mode.U:E3}") +
                        FormattableString.Invariant(
                            $" a1={truthGauge[0].Abs():F4} b1={truthGauge[1].Abs():F4} a2={truthGauge[2].Abs():F4} b2={truthGauge[3].Abs():F4}"));
                }
            }

            // Assemble the descrambled-domain model: z[u] = rxWire[u]·r̄(u) leaves h1
            // unchanged (piecewise-linear through the segment centres) and puts the
            // scrambler into the echo coefficient - h2·r(u−d)·r̄(u) for in-block echoes,
            // h2·r̄(u) against the known wire chip for the pre-block ones. QAM16 (§B3.4)
            // stays in the wire domain - no derotation, plain h2 - because its scramble
            // is a label permutation, handled at the priors and the output LLR signs.
            var rxDesc = new Complex32[mode.U];
            var h1Span = new Complex32[mode.U];
            var h2Span = new Complex32[mode.U];
            var preceding = new Complex32[delay];
            for (int c = 0; c < delay; c++)
            {
                preceding[c] = precedingProbe[(mode.K - delay) + c];
            }

            float residual = 0f;
            segResid.Clear();
            segResidCount.Clear();
            // §B4.1: per-position residual/|h1| capture for the oracle-pass reference
            // floor (label-true residuals; symbolVar is null and allowPair only on the
            // oracle instrument's call). Diagnostic path only - never the shipped loop.
            bool residDump = _turboFrameDiag && FrameDiagnostics is not null
                && symbolVar is null && allowPair;
            float[]? residPos = residDump ? new float[mode.U] : null;
            float[]? gainPos = residDump ? new float[mode.U] : null;
            for (int u = 0; u < mode.U; u++)
            {
                int sn = Math.Min(Segments - 1, u / segLen);
                float residBefore = residual;
                int s = sn;
                int ia, ib;
                float t;
                if (u <= segCentre[0] || Segments == 1)
                {
                    ia = ib = 0;
                    t = 0f;
                }
                else if (u >= segCentre[Segments - 1])
                {
                    ia = ib = Segments - 1;
                    t = 0f;
                }
                else
                {
                    if (u < segCentre[s])
                    {
                        s--;
                    }

                    ia = s;
                    ib = s + 1;
                    t = (u - segCentre[s]) / (segCentre[s + 1] - segCentre[s]);
                }

                Complex32 h1u = ia == ib ? segH1[ia] : (segH1[ia] * (1f - t)) + (segH1[ib] * t);
                Complex32 h2u = segEcho
                    ? (ia == ib ? segH2[ia] : (segH2[ia] * (1f - t)) + (segH2[ib] * t))
                    : h2Avg;
                if (truthFit)
                {
                    // W1/W2: the truth-gauge model replaces the segment interpolation -
                    // same taps, per-symbol-exact time variation (part-selected for the
                    // V-split variant).
                    Span<Complex32> tp = truthGauge.Slice(
                        truthParts == 2 && u >= mode.U / 2 ? 10 : 0, 10);
                    h1u = (tp[0] * tg1![u]) + (tp[1] * tg2![u]);
                    h2u = truthEcho ? (tp[2] * tg1[u]) + (tp[3] * tg2[u]) : Complex32.Zero;
                }

                // §B3.3 straddle pair: soft-cancel the adjacent-lag component before the
                // chains, at the segment-interpolated coefficient. x̂ is this iteration's
                // expected symbol (truth on the oracle path - exact cancellation; E[x] on
                // soft iterations, whose uncertainty enters the noise below); pre-block
                // sources are known probe chips.
                if (delay2 > 0)
                {
                    Complex32 h2bu = ia == ib ? segH2b[ia] : (segH2b[ia] * (1f - t)) + (segH2b[ib] * t);
                    if (truthFit)
                    {
                        Span<Complex32> tpb = truthGauge.Slice(
                            truthParts == 2 && u >= mode.U / 2 ? 10 : 0, 10);
                        h2bu = (tpb[4] * tg1![u]) + (tpb[5] * tg2![u]);
                    }

                    Complex32 srcB = u >= delay2 ? expected[u - delay2] : precedingProbe[mode.K + (u - delay2)];
                    rxWire[u] -= h2bu * srcB;
                }

                // W2 V-xtaps: exact cancellation of the fitted cursor±1 components,
                // the same pattern as the straddle above.
                if (truthFit && truthXtaps)
                {
                    Span<Complex32> tpx = truthGauge.Slice(
                        truthParts == 2 && u >= mode.U / 2 ? 10 : 0, 10);
                    Complex32 pre = u + 1 < mode.U ? expected[u + 1] : truthFollow![0];
                    Complex32 post = u >= 1 ? expected[u - 1] : precedingProbe[mode.K - 1];
                    rxWire[u] -= (((tpx[6] * tg1![u]) + (tpx[7] * tg2![u])) * pre)
                        + (((tpx[8] * tg1[u]) + (tpx[9] * tg2[u])) * post);
                }

                h1Span[u] = h1u;
                rxDesc[u] = qamWire ? rxWire[u] : rxWire[u] * rotors[u].Conj();
                Complex32 echoWire = u >= delay ? expected[u - delay] : preceding[u];
                h2Span[u] = qamWire
                    ? h2u
                    : u >= delay
                        ? h2u * rotors[u - delay] * rotors[u].Conj()
                        : h2u * rotors[u].Conj();
                Complex32 predicted = (h1u * expected[u]) + (h2u * echoWire);
                residual += (rxWire[u] - predicted).MagnitudeSquared();
                if (symbolVar is not null)
                {
                    // EM-consistent noise estimate for soft labels: E|z − h·x|² =
                    // |z − h·E[x]|² + |h|²·(1 − |E[x]|²). The preceding-probe echo
                    // sources (u < delay) are known exactly - variance 0. The cancelled
                    // adjacent tap contributes its own cancellation uncertainty.
                    residual += h1u.MagnitudeSquared() * expectedVar[u];
                    if (u >= delay)
                    {
                        residual += h2u.MagnitudeSquared() * expectedVar[u - delay];
                    }

                    if (delay2 > 0 && u >= delay2)
                    {
                        residual += h2b.MagnitudeSquared() * expectedVar[u - delay2];
                    }
                }

                segResid[sn] += residual - residBefore;
                segResidCount[sn]++;
                if (residDump)
                {
                    residPos![u] = residual - residBefore;
                    gainPos![u] = h1u.Abs();
                }
            }

            // MagnitudeSquared() sums both complex dimensions; the BCJR wants σ² per dimension, so
            // halve (the #65 2×-under-confidence lesson).
            float noiseVar = Math.Max(0.5f * residual / mode.U, 1e-6f);
            for (int s = 0; s < Segments; s++)
            {
                segNv[s] = segResidCount[s] > 0
                    ? Math.Max(0.5f * segResid[s] / segResidCount[s], 1e-6f)
                    : noiseVar;
            }

            // §B4.1 per-segment noise pricing (Amendment 2 variant ladder; SHIPPED
            // default = spikeup, "off" restores the frame constant). A segment's windowed
            // floor replaces the frame constant only beyond its own 3σ χ² band
            // (thr = exp(3/√count) - dof-derived, never tuned; WN2's flat-floor truth
            // cannot cross its 24-dof 2.4× band, so it stays frame-constant by
            // construction). "spikeup" engages upward only - pricing can de-confidence a
            // locally-bad span but never injects an over-confident low floor (the §B3.3
            // WN2 damage direction); "spike2s" engages both ways. Engaged values
            // interpolate through the segment centres like the channel spans (no cliffs).
            float[]? nvSpan = null;
            string nsegMode = _options.TurboNsegMode ?? "spikeup"; // null = shipped default
            if (nsegMode is "spikeup" or "spike2s")
            {
                bool twoSided = nsegMode == "spike2s";
                bool engaged = false;
                for (int s = 0; s < Segments; s++)
                {
                    float thr = segResidCount[s] > 0
                        ? MathF.Exp(3f / MathF.Sqrt(segResidCount[s])) : float.MaxValue;
                    bool spike = segNv[s] > noiseVar * thr
                        || (twoSided && segNv[s] < noiseVar / thr);
                    segPrice[s] = spike ? segNv[s] : noiseVar;
                    engaged |= spike;
                }

                if (engaged)
                {
                    nvSpan = new float[mode.U];
                    for (int u = 0; u < mode.U; u++)
                    {
                        int s = Math.Min(Segments - 1, u / segLen);
                        if (u <= segCentre[0] || Segments == 1)
                        {
                            nvSpan[u] = segPrice[0];
                        }
                        else if (u >= segCentre[Segments - 1])
                        {
                            nvSpan[u] = segPrice[Segments - 1];
                        }
                        else
                        {
                            if (u < segCentre[s])
                            {
                                s--;
                            }

                            float t = (u - segCentre[s]) / (segCentre[s + 1] - segCentre[s]);
                            nvSpan[u] = (segPrice[s] * (1f - t)) + (segPrice[s + 1] * t);
                        }
                    }
                }
            }

            if (_turboFrameDiag && FrameDiagnostics is not null)
            {
                var sb = new System.Text.StringBuilder(256);
                sb.Append(FormattableString.Invariant(
                    $"turbo-frame b{_blockIndex} f{f}: lag={delay} lag2={delay2} n={noiseVar:E3} nseg={segNv[0]:E2}|{segNv[1]:E2}|{segNv[2]:E2}|{segNv[3]:E2} ffE={dfe.FfEnergy:F3} sseN={tir.SseNull:F1} sseT={tir.SseTir:F1} h1="));
                for (int s = 0; s < Segments; s++)
                {
                    if (s > 0) { sb.Append('|'); }
                    sb.Append(FormattableString.Invariant($"{segH1[s].Re:F4},{segH1[s].Im:F4}"));
                }

                sb.Append(FormattableString.Invariant($" h2avg={h2Avg.Re:F4},{h2Avg.Im:F4}"));
                if (segEcho)
                {
                    sb.Append(" h2=");
                    for (int s = 0; s < Segments; s++)
                    {
                        if (s > 0) { sb.Append('|'); }
                        sb.Append(FormattableString.Invariant($"{segH2[s].Re:F4},{segH2[s].Im:F4}"));
                    }
                }

                if (delay2 > 0)
                {
                    sb.Append(" h2b=");
                    for (int s = 0; s < Segments; s++)
                    {
                        if (s > 0) { sb.Append('|'); }
                        sb.Append(FormattableString.Invariant($"{segH2b[s].Re:F4},{segH2b[s].Im:F4}"));
                    }
                }

                FrameDiagnostics.Invoke(sb.ToString());

                if (residDump)
                {
                    // §B4.1 oracle-pass reference field: label-true per-position squared
                    // residual and the interpolated |h1(u)| the trajectory candidate
                    // regresses on. One line per frame, oracle pass only.
                    var rb = new System.Text.StringBuilder(12 * mode.U);
                    rb.Append(FormattableString.Invariant($"turbo-resid b{_blockIndex} f{f}: r="));
                    for (int u = 0; u < mode.U; u++)
                    {
                        if (u > 0) { rb.Append(','); }
                        rb.Append(FormattableString.Invariant($"{residPos![u]:E2}"));
                    }

                    rb.Append(" g=");
                    for (int u = 0; u < mode.U; u++)
                    {
                        if (u > 0) { rb.Append(','); }
                        rb.Append(FormattableString.Invariant($"{gainPos![u]:F4}"));
                    }

                    FrameDiagnostics.Invoke(rb.ToString());
                }
            }

            int bitBase = f * mode.U * bitsPerSymbol;
            if (logPriors is not null)
            {
                int m = constellation.Length;
                for (int u = 0; u < mode.U; u++)
                {
                    for (int b = 0; b < bitsPerSymbol; b++)
                    {
                        // log P(bit=0) = −softplus(−L), log P(bit=1) = −softplus(L)
                        // under the positive-⇒-0 convention.
                        float ext = wireExtLlrs![bitBase + (u * bitsPerSymbol) + b];
                        lp0[b] = -Softplus(-ext);
                        lp1[b] = -Softplus(ext);
                    }

                    for (int s = 0; s < m; s++)
                    {
                        // QAM16: prior for WIRE symbol s = P(data nibble = s XOR n_u) -
                        // the extrinsics address DATA bits, so the label is permuted.
                        int label = labels.Length > 0 ? labels[s] : s;
                        if (qamWire)
                        {
                            label = s ^ nibbles![u];
                        }

                        float sum = 0f;
                        for (int b = 0; b < bitsPerSymbol; b++)
                        {
                            sum += ((label >> (bitsPerSymbol - 1 - b)) & 1) == 0 ? lp0[b] : lp1[b];
                        }

                        logPriors[(u * m) + s] = sum;
                    }
                }
            }

            var frameLlrs = new float[mode.U * bitsPerSymbol];
            Ms110dChainBcjr.Equalize(
                rxDesc, h1Span, h2Span, delay, noiseVar,
                constellation, labels, bitsPerSymbol, preceding, frameLlrs,
                logPriors is null ? default : logPriors,
                noiseVarPerSymbol: nvSpan is null ? default : nvSpan);

            // QAM16: the chains emitted WIRE-bit LLRs (identity labels over the wire
            // constellation); data bit i = wire bit i XOR scramble bit i, so scramble
            // bits flip signs - BEFORE the extrinsic subtraction, which lives in the
            // data domain.
            if (qamWire)
            {
                for (int u = 0; u < mode.U; u++)
                {
                    int nib = nibbles![u];
                    for (int b = 0; b < bitsPerSymbol; b++)
                    {
                        if (((nib >> (bitsPerSymbol - 1 - b)) & 1) != 0)
                        {
                            frameLlrs[(u * bitsPerSymbol) + b] = -frameLlrs[(u * bitsPerSymbol) + b];
                        }
                    }
                }
            }

            // With priors in, the BCJR emits full posteriors; hand the outer code detector
            // EXTRINSICS only (posterior − prior) - feeding its own opinion back to the
            // SISO would double-count and lock the loop.
            if (wireExtLlrs is not null)
            {
                for (int i = 0; i < frameLlrs.Length; i++)
                {
                    frameLlrs[i] -= wireExtLlrs[bitBase + i];
                }
            }

            for (int i = 0; i < frameLlrs.Length; i++)
            {
                AddLlr(frameLlrs[i]);
            }
        }

        if (probeDiag && probePriceRows > 0)
        {
            FrameDiagnostics!.Invoke(FormattableString.Invariant(
                $"turbo-probe b{_blockIndex}: rows={probePriceRows} resid={probePriceErr / probePriceRows:E3}"));
        }

        if (FrameDiagnostics is not null)
        {
            FrameDiagnostics.Invoke(FormattableString.Invariant(
                $"turbo-tir b{_blockIndex}: frames={_il.Frames} tir={tirFrames} meanLag={(tirFrames > 0 ? (double)tirLagSum / tirFrames : 0):F1} mean|c|={(tirFrames > 0 ? tirCoeffSum / tirFrames : 0):F3} pair={tirPairFrames} mean|c2|={(tirPairFrames > 0 ? tirCoeff2Sum / tirPairFrames : 0):F3}"));
        }

        // Restore DFE state and leave a clean Gram for the next frame.
        dfe.LoadTaps(savedTaps);
        dfe.BeginTraining();
    }

    private void CompleteBurst(Ms110dBurstEndReason reason)
    {
        EmitBurst([.. _burstBits], reason);
    }

    private void EmitBurst(byte[] payload, Ms110dBurstEndReason reason)
    {
        int blocks = _blockIndex;
        Ms110dLockInfo? lockInfo = _lock;
        EndBurst();
        BurstCompleted?.Invoke(new Ms110dBurst(payload, reason, blocks, lockInfo));
    }

    private void EndBurst()
    {
        _state = Ms110dRxState.Searching;
        _agcGain = 1.0f; // #101: next burst re-acquires and re-estimates its own level at unity
        _lock = null;
        _mode = null;
        _il = null;
        _dfe = null;
        _walsh = null;
        // W5b2: the next burst may lock on the other propagation path - the MFB
        // decoder's delay-profile window must be re-scanned per burst. The decoder
        // itself is mode-shaped, so a mode change rebuilds it.
        _mfb?.ResetBurst();
        _mfb = null;
        _trackingInitialized = false;
        _blockLlrCount = 0;
        _blockIndex = 0;
        _burstBits.Clear();
        // A burst ending mid-block (SignalLost/Terminate/EOM) must not leak this
        // block's frame positions or fading state into the next burst's turbo gate
        // and flat-channel classification.
        _blockFrameChips.Clear();
        _fadeFloor = 0;
        _fadeFloorSeeded = false;
        _framesSinceExcursion = int.MaxValue / 2;
        _fading = false;
        _fadingLatched = false;
        _bestMetric = 0;
        _bestStart = -1;
        _terminate = false;
    }
}
