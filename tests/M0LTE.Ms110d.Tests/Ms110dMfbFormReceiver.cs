using System.Globalization;
using System.Numerics;
using M0LTE.Fec;
using M0LTE.Ms110d;
using M0LTE.Ms110d.Fec;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// W5a: the MFB-form receiver prototyped in the demodulator's own ring domain
/// (wn8-program-plan §4, evidence 2026-07-31-wn8-w5a). Everything a real receiver has,
/// nothing it doesn't: the shipped acquisition supplies CFO/timing; per-probe LS
/// estimates the composite T/2 impulse response (TX pulse ⊗ channel ⊗ RX front-end, no
/// parametric path knowledge) on ISI-clean probe interiors; per-tap linear
/// interpolation carries it between probes; detection is per-symbol matched projection
/// with iterative reconstruction-and-cancellation seeded from its own decodes. Truth is
/// used ONLY to score, plus one labelled calibration lane (truth-reconstruction
/// residual) that validates templates and alignment without touching the honest rungs.
/// Not a gate - an instrument. <c>MS110D_MFBRX=1</c>, <c>MS110D_MFBRX_SEED</c>
/// (default 508), <c>MS110D_MFBRX_WORKER/BURST</c> (default 0), <c>MS110D_MFBRX_ITERS</c>
/// (default 3), <c>MS110D_MFBRX_OUT</c> (default ".").
/// </summary>
public class Ms110dMfbFormReceiver
{
    // Wide delay-profile scan bounds, T/2 half-chips. Acquisition centres the chip
    // clock on whichever path is stronger during the preamble (the B3.5b lesson - the
    // W5a anatomy caught the disjoint specimen locked on the ECHO, putting the direct
    // path at −9.6 and outside a fixed one-sided window, with exactly half the signal
    // power unexplained). The scan finds the per-burst peaks inside these bounds and a
    // WinLen-wide detection window is placed over them.
    private const int ScanMin = -18;
    private const int ScanMax = 19;
    private const int ScanLen = ScanMax - ScanMin;
    private const int WinLen = 26;

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;
    }

    [Fact]
    public void Mfb_Form_Receiver_On_The_Ring()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MFBRX") != "1",
            "set MS110D_MFBRX=1 for the W5a MFB-form receiver prototype");

        int wn = EnvInt("MS110D_MFBRX_WN", 8);
        double snrDb = double.TryParse(Environment.GetEnvironmentVariable("MS110D_MFBRX_SNR"),
            out double s) ? s : 23;
        int baseSeed = EnvInt("MS110D_MFBRX_SEED", 500 + wn);
        int worker = EnvInt("MS110D_MFBRX_WORKER", 0);
        int burst = EnvInt("MS110D_MFBRX_BURST", 0);
        int iters = EnvInt("MS110D_MFBRX_ITERS", 3);
        bool soft = Environment.GetEnvironmentVariable("MS110D_MFBRX_SOFT") == "1";
        // Schedule variant: soft cancellation for rungs < SOFTUNTIL, hard after - soft's
        // fast wide-basin descent handing over to hard's exact fixed-point tail.
        int softUntil = EnvInt("MS110D_MFBRX_SOFTUNTIL", soft ? int.MaxValue : 0);
        // Decision-directed anchor re-fit: mid-frame anchors fitted on decision rows
        // (E[x]) triple the anchor density, attacking the trajectory-interpolation
        // error through deep fades - "oracle-grade as labels improve", label-free by
        // construction (its labels are the loop's own). MS110D_MFBRX_REFITAT is a
        // comma list of rungs (default: the soft→hard handover when REFIT=1).
        bool refit = Environment.GetEnvironmentVariable("MS110D_MFBRX_REFIT") == "1";
        var refitRungs = new HashSet<int>();
        string? refitAt = Environment.GetEnvironmentVariable("MS110D_MFBRX_REFITAT");
        if (refitAt is not null)
        {
            foreach (string part in refitAt.Split(','))
            {
                if (int.TryParse(part, out int rr))
                {
                    refitRungs.Add(rr);
                }
            }
        }
        else if (refit)
        {
            refitRungs.Add(softUntil);
        }
        string outDir = Environment.GetEnvironmentVariable("MS110D_MFBRX_OUT") ?? ".";
        // G2c: the MMSE cold rung (per-symbol linear MMSE over the response window at R0,
        // known probes subtracted, neighbouring data chips as unit-power unknowns, the
        // anchor-fit residual as the noise estimate) instead of the ISI-inclusive matched
        // projection. Off by default: the G2b summaries must reproduce byte-identically.
        bool mmse0 = Environment.GetEnvironmentVariable("MS110D_MFBRX_MMSE0") == "1";

        // Bit-exact corpse reconstruction (the banked construction).
        int workerSeed = baseSeed + (worker * 1_000_000);
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Long,
            ConstraintLength = 7,
            PreambleSuperframes = 20,
        };
        var tx = new Ms110dModulator(settings);
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long);
        double blockSeconds = il.Frames * (tx.Mode.U + tx.Mode.K) / 2400.0;
        int blocksPerBurst = Math.Max(1, (int)(90 / blockSeconds));
        int payloadBitsPerBurst = (blocksPerBurst * il.InputBits) - 32;
        var random = new Random(workerSeed);
        var payload = new byte[payloadBitsPerBurst];
        for (int b = 0; b <= burst; b++)
        {
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)random.Next(2);
            }
        }

        float[] audio = tx.Modulate(payload);
        int channelSeed = workerSeed + (1000 * burst) + 1;
        var channel = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor);
        float[] rx = channel.Apply(audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400);

        byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, il.InputBits);
        int txBlocks = txBits.Length / il.InputBits;
        ConvolutionalCode code = ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, tx.Mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);
        var viterbi = new TailBitingViterbiDecoder(code);
        int u256 = tx.Mode.U;
        int k32 = tx.Mode.K;
        int frames = il.Frames;

        var fetchedBlocks = new byte[txBlocks][];
        for (int b = 0; b < txBlocks; b++)
        {
            fetchedBlocks[b] = Ms110dFraming.EncodeBlock(
                code, puncture, interleaver, txBits.AsSpan(b * il.InputBits, il.InputBits));
        }

        // The symbol map (G1: modulation-generic - the W5 prototype spoke QAM16 only). A
        // fetched number is bps bits MSB-first; the per-frame scramble sequence comes from
        // the register reset at each data frame (identical every frame): QAM16 XORs the
        // nibble (NextQam), 8PSK transcodes the tribit through Table D-V and adds the
        // scrambler tribit modulo 8 (NextPsk), exactly as Ms110dModulator does. The QAM16
        // arithmetic is unchanged operation for operation, so the W5 numbers must
        // reproduce byte-identically (calibration lane (i) of the G1 registration).
        bool qam = tx.Mode.Modulation == Ms110dModulation.Qam16;
        (tx.Mode.Modulation is Ms110dModulation.Qam16 or Ms110dModulation.Psk8).Should().BeTrue(
            "the MFB-form prototype speaks the two Phase B constellations");
        int bps = tx.Mode.BitsPerSymbol;
        int points = 1 << bps;
        var scr = new int[u256];
        var scrambler = new Ms110dScrambler();
        scrambler.Reset();
        for (int u = 0; u < u256; u++)
        {
            scr[u] = qam ? scrambler.NextQam(0, 4) : scrambler.NextPsk(0);
        }

        byte[] transcode8 = Ms110dTables.Transcode8Psk.ToArray();
        Complex Wire(int number, int u)
        {
            Complex32 w = qam
                ? Ms110dTables.Qam16[number ^ scr[u]]
                : Ms110dTables.Psk8[(transcode8[number] + scr[u]) & 7];
            return new Complex(w.Re, w.Im);
        }

        Complex Point(int q)
        {
            Complex32 w = qam ? Ms110dTables.Qam16[q] : Ms110dTables.Psk8[q];
            return new Complex(w.Re, w.Im);
        }

        int Number(byte[] bits, ref int idx)
        {
            int number = 0;
            for (int i = 0; i < bps; i++)
            {
                number = (number << 1) | bits[idx++];
            }

            return number;
        }

        // The demod runs first: its acquisition supplies CFO/timing, and the block-ready
        // seam lets us pull each block's ring span while it is resident.
        var demod = new Ms110dDemodulator(new Ms110dDemodOptions());
        var spans = new List<(int Block, long[] FrameChips, long Hc0, Complex32[] Ring)>();
        demod.InstrumentBlockReady = (blockIndex, frameChips) =>
        {
            if (blockIndex >= txBlocks)
            {
                return;
            }

            long hc0 = (2 * (frameChips[0] - k32)) + (2 * ScanMin);
            long hcEnd = (2 * (frameChips[^1] + u256 + k32)) + (2 * ScanMax);
            var ring = new Complex32[hcEnd - hc0];
            for (long hc = hc0; hc < hcEnd; hc++)
            {
                ring[hc - hc0] = demod.RingReadT2(hc);
            }

            spans.Add((blockIndex, frameChips, hc0, ring));
        };
        demod.Process(rx);
        spans.Count.Should().Be(txBlocks, "every block must acquire and be pulled");

        // Per-burst delay-profile scan: ridged wide LS on each of the first 8 probes of
        // block 0, tap ENERGIES accumulated across probes (robust to fading - phases
        // decorrelate, delay positions don't). The detection window is placed over the
        // energy support.
        int lMin;
        {
            (int B0, long[] Fc0, long Hc00, Complex32[] Ring0) = spans[0];
            var profile = new double[ScanLen];
            var wGram = new Complex[ScanLen, ScanLen];
            var wRhs = new Complex[ScanLen];
            for (int p = 0; p < 8; p++)
            {
                long ps = Fc0[p] - k32;
                // Block 0's preceding probe is the preamble's closing probe, unshifted (see
                // Ms110dMfbBlockDecoder.ProbeIsBoundary); B0 == 0 here by construction.
                bool boundary = p != 0 && (p + 1) % frames == 0;
                Complex32[] probe = MiniProbe.Get(k32, boundary);
                Array.Clear(wRhs);
                for (int i = 0; i < ScanLen; i++)
                {
                    for (int j = 0; j < ScanLen; j++)
                    {
                        wGram[i, j] = Complex.Zero;
                    }
                }

                long rowLo = (2 * ps) + ScanMax;
                long rowHi = (2 * (ps + k32)) + ScanMin;
                for (long hc = rowLo; hc < rowHi; hc++)
                {
                    var row = new Complex(Ring0[hc - Hc00].Re, Ring0[hc - Hc00].Im);
                    for (int i = 0; i < ScanLen; i++)
                    {
                        long src = hc - (ScanMin + i);
                        if ((src & 1) != 0)
                        {
                            continue;
                        }

                        Complex32 x = probe[(src / 2) - ps];
                        var phiI = new Complex(x.Re, x.Im);
                        wRhs[i] += Complex.Conjugate(phiI) * row;
                        for (int j = i; j < ScanLen; j++)
                        {
                            long srcJ = hc - (ScanMin + j);
                            if ((srcJ & 1) != 0)
                            {
                                continue;
                            }

                            Complex32 xj = probe[(srcJ / 2) - ps];
                            Complex g = Complex.Conjugate(phiI) * new Complex(xj.Re, xj.Im);
                            wGram[i, j] += g;
                            if (j != i)
                            {
                                wGram[j, i] += Complex.Conjugate(g);
                            }
                        }
                    }
                }

                double trace = 0;
                for (int i = 0; i < ScanLen; i++)
                {
                    trace += wGram[i, i].Real;
                }

                double ridge = Math.Max(1e-9, 3e-2 * trace / ScanLen);
                for (int i = 0; i < ScanLen; i++)
                {
                    wGram[i, i] += ridge;
                }

                var hw = (Complex[])wRhs.Clone();
                if (!SolveHermitian(wGram, hw))
                {
                    continue;
                }

                for (int i = 0; i < ScanLen; i++)
                {
                    profile[i] += (hw[i].Real * hw[i].Real) + (hw[i].Imaginary * hw[i].Imaginary);
                }
            }

            double peak = profile.Max();
            int pkLo = Array.FindIndex(profile, e => e >= 0.1 * peak);
            int pkHi = Array.FindLastIndex(profile, e => e >= 0.1 * peak);
            lMin = ScanMin + pkLo - 7;
            if (ScanMin + pkHi + 8 > lMin + WinLen)
            {
                lMin = (ScanMin + pkHi + 8) - WinLen;
            }
        }

        int lLen = WinLen;
        int lMax = lMin + lLen;

        using var summary = new StreamWriter(Path.Combine(outDir,
            FormattableString.Invariant($"mfbrx-summary-wn{wn}-w{worker}-b{burst}-seed{baseSeed}.txt")));
        summary.WriteLine(FormattableString.Invariant(
            $"WN{wn} @ {snrDb} dB baseSeed {baseSeed} worker {worker} burst {burst} (channelSeed {channelSeed}) MFB-form receiver, window [{lMin},{lMax})"));

        // G1c selection-ceiling instrument: MS110D_MFBRX_CANDIDATE names an autopsy
        // `autopsy-decoded-*.txt` from the shipped receiver (the line whose length is the
        // burst's tx bit count is its decode). At the final rung every block prices its own
        // decode and the candidate's by the same label-free MFB reconstruction residual and
        // reports which the lower residual would select - the ensemble question asked of
        // the instrument before anything is built.
        byte[]? candidateBits = null;
        string? candidatePath = Environment.GetEnvironmentVariable("MS110D_MFBRX_CANDIDATE");
        if (candidatePath is not null)
        {
            foreach (string line in File.ReadLines(candidatePath))
            {
                string t = line.Trim();
                if (t.Length == txBits.Length)
                {
                    candidateBits = t.Select(ch => (byte)(ch == '1' ? 1 : 0)).ToArray();
                }
            }

            candidateBits.Should().NotBeNull($"{candidatePath} must carry a {txBits.Length}-bit decode line");
        }

        var selectLines = new List<string>();
        var rungTotals = new long[iters + 1];
        Span<double> metric = stackalloc double[16];
        Span<double> p0 = stackalloc double[4];
        var siso = new TailBitingSisoDecoder(code);
        var softPunctured = new float[il.SizeBits];
        var softMother = new float[2 * il.InputBits];
        var softMotherPost = new float[2 * il.InputBits];
        var softWire = new float[il.SizeBits];
        var truthResidBlocks = new List<string>();
        var anchorResidBlocks = new List<string>();
        var finalResidBlocks = new List<string>(); // G2e: the last decode's reconstruction residual
        var rungLines = new List<string>[iters + 1];
        for (int rung = 0; rung <= iters; rung++)
        {
            rungLines[rung] = new List<string>();
        }

        foreach ((int b, long[] frameChips, long hc0, Complex32[] ringCf) in spans)
        {
            int n = ringCf.Length;
            var ring = new Complex[n];
            for (int i = 0; i < n; i++)
            {
                ring[i] = new Complex(ringCf[i].Re, ringCf[i].Im);
            }

            // --- Probe-anchor composite-FIR estimation (label-free) -----------------
            // Anchors: the preceding probe of every frame plus the following probe of
            // the last frame. Rows are the probe's ISI-clean interior (every chip whose
            // response reaches the row lies inside the probe).
            var anchorChip = new List<double>();
            var anchorH = new List<Complex[]>();
            double anchorResidSum = 0;
            long anchorResidRows = 0;
            var gram = new Complex[lLen, lLen];
            var rhs = new Complex[lLen];
            for (int p = 0; p <= frames; p++)
            {
                bool preceding = p < frames;
                long ps = preceding ? frameChips[p] - k32 : frameChips[^1] + u256;
                bool boundary = !(p == 0 && b == 0) && (preceding
                    ? (p + 1) % frames == 0
                    : (frames + 1) % frames == 0);
                Complex32[] probe = MiniProbe.Get(k32, boundary);

                Array.Clear(rhs);
                for (int i = 0; i < lLen; i++)
                {
                    for (int j = 0; j < lLen; j++)
                    {
                        gram[i, j] = Complex.Zero;
                    }
                }

                long rowLo = (2 * ps) + lMax;
                long rowHi = (2 * (ps + k32)) + lMin;
                for (long hc = rowLo; hc < rowHi; hc++)
                {
                    Complex row = ring[hc - hc0];
                    for (int i = 0; i < lLen; i++)
                    {
                        long src = hc - (lMin + i);
                        if ((src & 1) != 0)
                        {
                            continue; // chips sit on even half-positions only
                        }

                        long c = src / 2;
                        Complex32 x = probe[c - ps];
                        var phiI = new Complex(x.Re, x.Im);
                        rhs[i] += Complex.Conjugate(phiI) * row;
                        for (int j = i; j < lLen; j++)
                        {
                            long srcJ = hc - (lMin + j);
                            if ((srcJ & 1) != 0)
                            {
                                continue;
                            }

                            Complex32 xj = probe[(srcJ / 2) - ps];
                            Complex g = Complex.Conjugate(phiI) * new Complex(xj.Re, xj.Im);
                            gram[i, j] += g;
                            if (j != i)
                            {
                                gram[j, i] += Complex.Conjugate(g);
                            }
                        }
                    }
                }

                double trace = 0;
                for (int i = 0; i < lLen; i++)
                {
                    trace += gram[i, i].Real;
                }

                double ridge = Math.Max(1e-9, 1e-3 * trace / lLen);
                for (int i = 0; i < lLen; i++)
                {
                    gram[i, i] += ridge;
                }

                var h = (Complex[])rhs.Clone();
                if (!SolveHermitian(gram, h))
                {
                    continue;
                }

                // Anchor-fit residual on its own rows (the label-free noise estimate).
                for (long hc = rowLo; hc < rowHi; hc++)
                {
                    Complex model = Complex.Zero;
                    for (int i = 0; i < lLen; i++)
                    {
                        long src = hc - (lMin + i);
                        if ((src & 1) != 0)
                        {
                            continue;
                        }

                        Complex32 x = probe[(src / 2) - ps];
                        model += h[i] * new Complex(x.Re, x.Im);
                    }

                    Complex d = ring[hc - hc0] - model;
                    anchorResidSum += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                    anchorResidRows++;
                }

                anchorChip.Add(ps + (k32 / 2.0));
                anchorH.Add(h);
            }

            double sigmaAnchor = anchorResidSum / Math.Max(1, anchorResidRows);
            anchorResidBlocks.Add(FormattableString.Invariant($"b{b}:{sigmaAnchor:E2}"));

            if (b == 0)
            {
                // First-block anatomy for the anchor lane: ring power, per-anchor h
                // energy, and the demod's lock view - enough to localize a bad fit.
                double ringPower = 0;
                for (int i = 0; i < n; i++)
                {
                    ringPower += (ring[i].Real * ring[i].Real) + (ring[i].Imaginary * ring[i].Imaginary);
                }

                var hEnergy = new List<string>();
                for (int p2 = 0; p2 < Math.Min(4, anchorH.Count); p2++)
                {
                    double e = 0;
                    int peak = 0;
                    for (int i = 0; i < lLen; i++)
                    {
                        double m = (anchorH[p2][i].Real * anchorH[p2][i].Real)
                            + (anchorH[p2][i].Imaginary * anchorH[p2][i].Imaginary);
                        e += m;
                        if (m > ((anchorH[p2][peak].Real * anchorH[p2][peak].Real)
                            + (anchorH[p2][peak].Imaginary * anchorH[p2][peak].Imaginary)))
                        {
                            peak = i;
                        }
                    }

                    hEnergy.Add(FormattableString.Invariant($"a{p2}:E={e:E2}@pk{lMin + peak}"));
                }

                summary.WriteLine(
                    FormattableString.Invariant(
                        $"b0 anatomy: mean|ring|^2={ringPower / n:E2} anchors={anchorH.Count} {string.Join(" ", hEnergy)}") +
                    FormattableString.Invariant(
                        $" lock={demod.Lock?.WaveformNumber}/{demod.Lock?.Interleaver}/K{demod.Lock?.ConstraintLength}@{demod.Lock?.CfoHz:F2}Hz"));
            }

            // Per-chip interpolated response h(t) - per-tap linear between anchors.
            Complex[] HAt(double chip)
            {
                int hi = anchorChip.FindIndex(ax => ax >= chip);
                if (hi < 0)
                {
                    return anchorH[^1];
                }

                if (hi == 0)
                {
                    return anchorH[0];
                }

                double frac = (chip - anchorChip[hi - 1]) / (anchorChip[hi] - anchorChip[hi - 1]);
                var h = new Complex[lLen];
                for (int i = 0; i < lLen; i++)
                {
                    h[i] = (anchorH[hi - 1][i] * (1 - frac)) + (anchorH[hi][i] * frac);
                }

                return h;
            }

            // Wire truth for this block (scoring + the truth-recon calibration lane).
            byte[] fetched = fetchedBlocks[b];
            var truthWire = new Complex[frames * u256];
            var dataChip = new long[frames * u256];
            int bit = 0;
            for (int f = 0; f < frames; f++)
            {
                for (int u = 0; u < u256; u++)
                {
                    truthWire[(f * u256) + u] = Wire(Number(fetched, ref bit), u);
                    dataChip[(f * u256) + u] = frameChips[f] + u;
                }
            }

            // Reconstruction of the whole span from a wire-symbol assignment: probes
            // always known, data from the given assignment (null entries contribute 0).
            Complex[] Reconstruct(Complex[]? dataWire)
            {
                var recon = new Complex[n];
                void AddChip(long c, Complex x)
                {
                    Complex[] h = HAt(c);
                    for (int i = 0; i < lLen; i++)
                    {
                        long hc = (2 * c) + lMin + i;
                        if (hc >= hc0 && hc < hc0 + n)
                        {
                            recon[hc - hc0] += h[i] * x;
                        }
                    }
                }

                for (int p = 0; p <= frames; p++)
                {
                    bool preceding = p < frames;
                    long ps = preceding ? frameChips[p] - k32 : frameChips[^1] + u256;
                    bool boundary = !(p == 0 && b == 0) && (preceding ? (p + 1) % frames == 0 : (frames + 1) % frames == 0);
                    Complex32[] probe = MiniProbe.Get(k32, boundary);
                    for (int c = 0; c < k32; c++)
                    {
                        AddChip(ps + c, new Complex(probe[c].Re, probe[c].Im));
                    }
                }

                if (dataWire is not null)
                {
                    for (int d = 0; d < dataWire.Length; d++)
                    {
                        AddChip(dataChip[d], dataWire[d]);
                    }
                }

                return recon;
            }

            // Calibration lane: residual of the TRUTH reconstruction over the data
            // region - validates templates, alignment, and the interpolated h(t) in one
            // label-exact number (compare against the anchor-fit noise floor).
            {
                Complex[] recon = Reconstruct(truthWire);
                double resid = 0;
                long rows = 0;
                for (long hc = 2 * frameChips[0]; hc < 2 * (frameChips[^1] + u256); hc++)
                {
                    Complex d = ring[hc - hc0] - recon[hc - hc0];
                    resid += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                    rows++;
                }

                truthResidBlocks.Add(FormattableString.Invariant($"b{b}:{resid / rows:E2}"));
            }

            // --- The detection rung ladder -----------------------------------------
            var llrs = new float[il.SizeBits];
            var dec = new byte[il.InputBits];
            var probeAnchorChip = anchorChip.ToList();
            var probeAnchorH = anchorH.ToList();
            Complex[]? decisions = null; // wire-symbol assignment from the last decode

            for (int rung = 0; rung <= iters; rung++)
            {
                if (refitRungs.Contains(rung) && decisions is not null)
                {
                    // Two mid-frame decision anchors per frame ([16,112) and [144,240)
                    // chips in), fitted on rows whose every source is a decided data
                    // chip - the anchor grid triples and the interpolation gap through
                    // deep fades drops from ~120 ms to ~40 ms. Each refit rebuilds from
                    // the probe anchors so repeated refits replace, not accumulate.
                    anchorChip = probeAnchorChip.ToList();
                    anchorH = probeAnchorH.ToList();
                    var phiRow = new Complex[lLen];
                    for (int f2 = 0; f2 < frames; f2++)
                    {
                        for (int half = 0; half < 2; half++)
                        {
                            long start = frameChips[f2] + 16 + (half * 128);
                            long end = start + 96;
                            Array.Clear(rhs);
                            for (int i = 0; i < lLen; i++)
                            {
                                for (int j = 0; j < lLen; j++)
                                {
                                    gram[i, j] = Complex.Zero;
                                }
                            }

                            for (long hc = (2 * start) + lMax; hc < (2 * end) + lMin; hc++)
                            {
                                Complex rowV = ring[hc - hc0];
                                for (int i = 0; i < lLen; i++)
                                {
                                    long src = hc - (lMin + i);
                                    if ((src & 1) != 0)
                                    {
                                        phiRow[i] = Complex.Zero;
                                        continue;
                                    }

                                    long c = src / 2;
                                    long rel = ((long)f2 * u256) + (c - frameChips[f2]);
                                    phiRow[i] = decisions[rel];
                                }

                                for (int i = 0; i < lLen; i++)
                                {
                                    Complex pi = Complex.Conjugate(phiRow[i]);
                                    rhs[i] += pi * rowV;
                                    for (int j = 0; j < lLen; j++)
                                    {
                                        gram[i, j] += pi * phiRow[j];
                                    }
                                }
                            }

                            double trace2 = 0;
                            for (int i = 0; i < lLen; i++)
                            {
                                trace2 += gram[i, i].Real;
                            }

                            double ridge2 = Math.Max(1e-9, 1e-3 * trace2 / lLen);
                            for (int i = 0; i < lLen; i++)
                            {
                                gram[i, i] += ridge2;
                            }

                            var h2 = (Complex[])rhs.Clone();
                            if (SolveHermitian(gram, h2))
                            {
                                anchorChip.Add((start + end) / 2.0);
                                anchorH.Add(h2);
                            }
                        }
                    }

                    var merged = anchorChip.Zip(anchorH).OrderBy(t => t.First).ToList();
                    anchorChip = merged.Select(t => t.First).ToList();
                    anchorH = merged.Select(t => t.Second).ToList();
                }

                Complex[]? recon = decisions is null ? null : Reconstruct(decisions);
                double sigma2;
                if (recon is null)
                {
                    sigma2 = 0; // set per-symbol below for R0
                }
                else
                {
                    double resid = 0;
                    long rows = 0;
                    for (long hc = 2 * frameChips[0]; hc < 2 * (frameChips[^1] + u256); hc++)
                    {
                        Complex d = ring[hc - hc0] - recon[hc - hc0];
                        resid += (d.Real * d.Real) + (d.Imaginary * d.Imaginary);
                        rows++;
                    }

                    sigma2 = resid / rows;
                }

                // R0's crude global price: the median squared distance of the raw
                // projections to their nearest constellation point (ISI-inclusive).
                var proj = new Complex[frames * u256];
                var gramT = new double[frames * u256];
                var r0Dist = recon is null ? new List<double>(frames * u256) : null;
                if (recon is null && mmse0)
                {
                    // The MMSE cold rung. y_data = ring minus the exact probe reconstruction;
                    // for symbol d at chip c the rows 2c+lMin..2c+lMax-1 see the neighbouring
                    // data chips |c'-c| <= (lLen-1)/2 through their own interpolated responses.
                    // R = Es H H^H + sigma^2 I; z = R^-1 h_c; mu = Es h_c^H z; xhat/mu = x + v,
                    // var(v) = Es (1-mu)/mu, so the LLR metric weight is mu / (Es (1-mu)).
                    Complex[] probesOnly = Reconstruct(null);
                    double es = 0;
                    for (int q = 0; q < points; q++)
                    {
                        Complex pt = Point(q);
                        es += (pt.Real * pt.Real) + (pt.Imaginary * pt.Imaginary);
                    }

                    es /= points;
                    double sigmaNoise = Math.Max(sigmaAnchor, 1e-12);
                    int half = (lLen - 1) / 2;
                    var dataAt = new Dictionary<long, int>(proj.Length);
                    for (int d = 0; d < proj.Length; d++)
                    {
                        dataAt[dataChip[d]] = d;
                    }

                    var rMat = new Complex[lLen, lLen];
                    var zVec = new Complex[lLen];
                    var hCol = new Complex[lLen];
                    var hN = new Complex[lLen];
                    for (int d = 0; d < proj.Length; d++)
                    {
                        long c = dataChip[d];
                        Complex[] hc = HAt(c);
                        for (int i = 0; i < lLen; i++)
                        {
                            for (int j = 0; j < lLen; j++)
                            {
                                rMat[i, j] = Complex.Zero;
                            }

                            rMat[i, i] = sigmaNoise;
                            hCol[i] = hc[i];
                        }

                        for (long cn = c - half; cn <= c + half; cn++)
                        {
                            if (!dataAt.ContainsKey(cn))
                            {
                                continue; // a probe chip (already subtracted) or outside the block
                            }

                            Complex[] hn = cn == c ? hc : HAt(cn);
                            int off = (int)(2 * (cn - c));
                            for (int i = 0; i < lLen; i++)
                            {
                                int k = i - off;
                                hN[i] = k >= 0 && k < lLen ? hn[k] : Complex.Zero;
                            }

                            for (int i = 0; i < lLen; i++)
                            {
                                if (hN[i] == Complex.Zero)
                                {
                                    continue;
                                }

                                for (int j = 0; j < lLen; j++)
                                {
                                    rMat[i, j] += es * hN[i] * Complex.Conjugate(hN[j]);
                                }
                            }
                        }

                        Array.Copy(hCol, zVec, lLen);
                        Complex mu = Complex.Zero;
                        Complex xhat = Complex.Zero;
                        if (SolveHermitian(rMat, zVec))
                        {
                            for (int i = 0; i < lLen; i++)
                            {
                                long hc2 = (2 * c) + lMin + i;
                                Complex y = hc2 >= hc0 && hc2 < hc0 + n
                                    ? ring[hc2 - hc0] - probesOnly[hc2 - hc0]
                                    : Complex.Zero;
                                mu += Complex.Conjugate(hCol[i]) * zVec[i];
                                xhat += Complex.Conjugate(zVec[i]) * y;
                            }

                            mu *= es;
                            xhat *= es;
                        }

                        double muR = Math.Clamp(mu.Real, 1e-6, 1 - 1e-6);
                        proj[d] = xhat / muR;
                        gramT[d] = muR / (es * (1 - muR));
                    }

                    sigma2 = 1.0;
                    r0Dist = null;
                }

                for (int d = 0; d < proj.Length; d++)
                {
                    if (recon is null && mmse0)
                    {
                        break; // projections and prices already set by the MMSE cold rung
                    }

                    long c = dataChip[d];
                    Complex[] h = HAt(c);
                    double g2 = 0;
                    Complex acc = Complex.Zero;
                    for (int i = 0; i < lLen; i++)
                    {
                        long hc = (2 * c) + lMin + i;
                        if (hc < hc0 || hc >= hc0 + n)
                        {
                            continue;
                        }

                        Complex row = recon is null
                            ? ring[hc - hc0]
                            : ring[hc - hc0] - recon[hc - hc0] + (h[i] * decisions![d]);
                        acc += Complex.Conjugate(h[i]) * row;
                        g2 += (h[i].Real * h[i].Real) + (h[i].Imaginary * h[i].Imaginary);
                    }

                    proj[d] = acc / Math.Max(g2, 1e-12);
                    gramT[d] = g2;
                    if (r0Dist is not null)
                    {
                        double bestD = double.MaxValue;
                        for (int q = 0; q < points; q++)
                        {
                            Complex cq = Point(q);
                            double dr = proj[d].Real - cq.Real;
                            double di = proj[d].Imaginary - cq.Imaginary;
                            bestD = Math.Min(bestD, (dr * dr) + (di * di));
                        }

                        r0Dist.Add(bestD * g2);
                    }
                }

                if (r0Dist is not null)
                {
                    r0Dist.Sort();
                    sigma2 = Math.Max(r0Dist[r0Dist.Count / 2], 1e-9);
                }

                // LLRs and decode.
                for (int d = 0; d < proj.Length; d++)
                {
                    int u = d % u256;
                    for (int q = 0; q < points; q++)
                    {
                        Complex cq = Wire(q, u);
                        double dr = proj[d].Real - cq.Real;
                        double di = proj[d].Imaginary - cq.Imaginary;
                        metric[q] = ((dr * dr) + (di * di)) * gramT[d] / sigma2;
                    }

                    for (int bb = 0; bb < bps; bb++)
                    {
                        double m0 = double.MaxValue, m1 = double.MaxValue;
                        for (int q = 0; q < points; q++)
                        {
                            if (((q >> (bps - 1 - bb)) & 1) != 0)
                            {
                                m1 = Math.Min(m1, metric[q]);
                            }
                            else
                            {
                                m0 = Math.Min(m0, metric[q]);
                            }
                        }

                        llrs[(d * bps) + bb] = (float)(m1 - m0);
                    }
                }

                Ms110dFraming.DecodeBlock(viterbi, puncture, interleaver, llrs, dec);
                int errs = 0;
                for (int i = 0; i < dec.Length; i++)
                {
                    errs += dec[i] != txBits[(b * il.InputBits) + i] ? 1 : 0;
                }

                rungTotals[rung] += errs;
                rungLines[rung].Add($"b{b}:{errs}");

                if (candidateBits is not null && rung == iters)
                {
                    double Residual(ReadOnlySpan<byte> blockBits)
                    {
                        byte[] enc = Ms110dFraming.EncodeBlock(code, puncture, interleaver, blockBits);
                        var wire = new Complex[frames * u256];
                        int idx = 0;
                        for (int f2 = 0; f2 < frames; f2++)
                        {
                            for (int u2 = 0; u2 < u256; u2++)
                            {
                                wire[(f2 * u256) + u2] = Wire(Number(enc, ref idx), u2);
                            }
                        }

                        Complex[] rc = Reconstruct(wire);
                        double r = 0;
                        long rows = 0;
                        for (long hc = 2 * frameChips[0]; hc < 2 * (frameChips[^1] + u256); hc++)
                        {
                            Complex dd = ring[hc - hc0] - rc[hc - hc0];
                            r += (dd.Real * dd.Real) + (dd.Imaginary * dd.Imaginary);
                            rows++;
                        }

                        return r / rows;
                    }

                    ReadOnlySpan<byte> cand = candidateBits.AsSpan(b * il.InputBits, il.InputBits);
                    int candErrs = 0;
                    for (int i = 0; i < cand.Length; i++)
                    {
                        candErrs += cand[i] != txBits[(b * il.InputBits) + i] ? 1 : 0;
                    }

                    double ownResid = Residual(dec);
                    double candResid = Residual(cand);
                    bool pickOwn = ownResid <= candResid;
                    selectLines.Add(FormattableString.Invariant(
                        $"b{b}: own {errs}@{ownResid:E3} cand {candErrs}@{candResid:E3} -> {(pickOwn ? "own" : "cand")} = {(pickOwn ? errs : candErrs)}"));
                }

                if (rung < softUntil)
                {
                    // W5b1 soft cancellation: SISO per-bit posteriors → per-symbol E[x]
                    // (independent-bit approximation over the 16 numbers, nib-permuted).
                    // Wrong decisions self-attenuate toward zero in the reconstruction -
                    // the B3.3 lesson in this architecture. The hard decode above still
                    // scores and detects convergence.
                    interleaver.Deinterleave(llrs, softPunctured);
                    Ms110dPuncture.Depuncture(puncture, softPunctured, softMother);
                    siso.Decode(softMother, softMotherPost);
                    Ms110dPuncture.Apply(puncture, softMotherPost, softPunctured);
                    interleaver.Interleave(softPunctured, softWire);
                    decisions = new Complex[frames * u256];
                    for (int d = 0; d < decisions.Length; d++)
                    {
                        int u = d % u256;
                        for (int bb = 0; bb < bps; bb++)
                        {
                            double l = Math.Clamp(softWire[(d * bps) + bb], -30f, 30f);
                            p0[bb] = 1.0 / (1.0 + Math.Exp(-l)); // positive ⇒ bit 0
                        }

                        Complex ex = Complex.Zero;
                        for (int m = 0; m < points; m++)
                        {
                            double pm = 1.0;
                            for (int bb = 0; bb < bps; bb++)
                            {
                                bool one = ((m >> (bps - 1 - bb)) & 1) != 0;
                                pm *= one ? 1.0 - p0[bb] : p0[bb];
                            }

                            ex += pm * Wire(m, u);
                        }

                        decisions[d] = ex;
                    }
                }
                else
                {
                    // Re-encode the decode as the next rung's cancellation assignment.
                    byte[] reFetched = Ms110dFraming.EncodeBlock(code, puncture, interleaver, dec);
                    decisions = new Complex[frames * u256];
                    int rbit = 0;
                    for (int f = 0; f < frames; f++)
                    {
                        for (int u = 0; u < u256; u++)
                        {
                            decisions[(f * u256) + u] = Wire(Number(reFetched, ref rbit), u);
                        }
                    }
                }
            }

            // G2e: price the block's final decode by the same label-free residual the shipped
            // decoder's cycle-accept and the G1d ensemble use.
            {
                byte[] encFinal = Ms110dFraming.EncodeBlock(code, puncture, interleaver, dec);
                var wireFinal = new Complex[frames * u256];
                int fi = 0;
                for (int f2 = 0; f2 < frames; f2++)
                {
                    for (int u2 = 0; u2 < u256; u2++)
                    {
                        wireFinal[(f2 * u256) + u2] = Wire(Number(encFinal, ref fi), u2);
                    }
                }

                Complex[] rcFinal = Reconstruct(wireFinal);
                double rf = 0;
                long rowsF = 0;
                for (long hc = 2 * frameChips[0]; hc < 2 * (frameChips[^1] + u256); hc++)
                {
                    Complex dd = ring[hc - hc0] - rcFinal[hc - hc0];
                    rf += (dd.Real * dd.Real) + (dd.Imaginary * dd.Imaginary);
                    rowsF++;
                }

                finalResidBlocks.Add(FormattableString.Invariant($"b{b}:{rf / rowsF:E4}"));
            }
        }

        if (selectLines.Count > 0)
        {
            summary.WriteLine($"selection (own vs candidate, by MFB residual): {string.Join(" | ", selectLines)}");
        }

        summary.WriteLine($"final-residual per block: {string.Join(" ", finalResidBlocks)}");
        summary.WriteLine($"anchor-fit residual per block: {string.Join(" ", anchorResidBlocks)}");
        summary.WriteLine($"truth-recon residual per block: {string.Join(" ", truthResidBlocks)}");
        for (int rung = 0; rung <= iters; rung++)
        {
            summary.WriteLine(FormattableString.Invariant(
                $"R{rung} coded errors {rungTotals[rung]}/{(long)txBlocks * il.InputBits}: {string.Join(" ", rungLines[rung])}"));
        }
    }

    private static bool SolveHermitian(Complex[,] a, Complex[] b)
    {
        int n = b.Length;
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
