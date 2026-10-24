using System.Globalization;
using M0LTE.Dsp;
using M0LTE.Fec;
using M0LTE.Ms110d;
using M0LTE.Ms110d.Fec;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// W1b matched-filter bound (wn8-program-plan §4, evidence 2026-07-31-wn8-w1b): coded
/// BER of matched-filter detection on the EXACT recorded channel with every other
/// symbol's interference cancelled exactly - an upper bound on any receiver-only
/// architecture at the operating point. No demodulator involvement at all: the corpse
/// is rebuilt bit-exactly (the autopsy rig's construction), the noiseless faded signal
/// comes from a same-seed <see cref="WattersonChannel"/> at SNR=∞ (gains are drawn
/// before noise - the B0 genie precedent), and the per-symbol statistic is bias-free by
/// construction: ŷ_u = x_u + the noise projected onto the symbol's exact-channel
/// templates, whitened by the per-symbol 2×2 Gram (exact elliptical pricing - fades
/// price as honest erasures). The envelope LPF inside the channel is not modelled, so
/// the projection is very slightly mismatched - a PESSIMISTIC bound, the safe
/// direction for both registered reads. Not a gate - an instrument.
/// <c>MS110D_MFB=1</c>, <c>MS110D_MFB_WN</c> (default 8), <c>MS110D_MFB_SEED</c>
/// (default 500+WN), <c>MS110D_MFB_WORKER</c>/<c>MS110D_MFB_BURST</c> (default 0),
/// <c>MS110D_MFB_OUT</c> (default ".").
/// </summary>
public class Ms110dMfbBound
{
    private static readonly Dictionary<int, double> PoorSnr = new()
    {
        { 2, 5 }, { 5, 11 }, { 6, 14 }, { 7, 19 }, { 8, 23 }, { 13, 11 },
    };

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;
    }

    [Fact]
    public void Matched_Filter_Bound_On_The_Exact_Channel()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_MFB") != "1",
            "set MS110D_MFB=1 for the W1b matched-filter bound");

        // W3: the trajectory-source knob. "truth" = the banked W1b anchor; "noise" =
        // truth + complex white perturbation at MS110D_MFB_TRAJNOISE relative rms (maps
        // the ceiling-vs-NMSE curve); "probes" = label-free probe-anchor LS on each
        // probe's ISI-clean interior, linearly interpolated across the burst.
        string traj = Environment.GetEnvironmentVariable("MS110D_MFB_TRAJ") ?? "truth";
        double trajNoise = double.TryParse(
            Environment.GetEnvironmentVariable("MS110D_MFB_TRAJNOISE"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double tn) ? tn : 0.0;

        int wn = EnvInt("MS110D_MFB_WN", 8);
        double snrDb = double.TryParse(Environment.GetEnvironmentVariable("MS110D_MFB_SNR"),
            out double s) ? s : PoorSnr[wn];
        int baseSeed = EnvInt("MS110D_MFB_SEED", 500 + wn);
        int worker = EnvInt("MS110D_MFB_WORKER", 0);
        int burst = EnvInt("MS110D_MFB_BURST", 0);
        string outDir = Environment.GetEnvironmentVariable("MS110D_MFB_OUT") ?? ".";

        // Bit-exact corpse reconstruction (the Ms110dTailAutopsy construction).
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
        Complex32[] wireSymbols = tx.BuildSymbols(payload);
        int channelSeed = workerSeed + (1000 * burst) + 1;
        var noisy = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor) { RecordGains = true };
        float[] rx = noisy.Apply(audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400);

        // The exact noiseless faded signal: a same-seed channel at SNR=∞ draws the
        // identical gain realization (gains before noise) and skips the noise.
        var exact = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor) { RecordGains = true };
        float[] noiseless = exact.Apply(audio, double.PositiveInfinity);
        IReadOnlyList<Complex32[]> gains = exact.PathGains()!;
        IReadOnlyList<Complex32[]> gainsNoisy = noisy.PathGains()!;
        for (int k = 0; k < gains.Count; k++)
        {
            (gains[k][0] - gainsNoisy[k][0]).MagnitudeSquared().Should().Be(0f,
                "the same-seed ∞-SNR channel must reproduce the identical gain draw");
        }

        // r = rx − R over the channel span is exactly the additive noise.
        int n = audio.Length;
        var r = new float[n];
        double noisePower = 0;
        for (int i = 0; i < n; i++)
        {
            r[i] = rx[2400 + i] - noiseless[i];
            noisePower += (double)r[i] * r[i];
        }

        double sigma2 = noisePower / n; // per real sample

        // TX truth per block (the autopsy's own construction): fetched wire bits and
        // the scramble nibbles that map symbol numbers to wire constellation points.
        byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, il.InputBits);
        int txBlocks = txBits.Length / il.InputBits;
        ConvolutionalCode code = ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, tx.Mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);
        var viterbi = new TailBitingViterbiDecoder(code);
        int u256 = tx.Mode.U;
        int k32 = tx.Mode.K;

        // Global symbol index of data symbol (b, f, u): preamble, the preamble-ending
        // probe, then [U data + K probe] frames, then the 32-chip EOT probe extension
        // (settings default - Ms110dModulator.BuildSymbols/AppendProbe). Verified
        // exactly below against the wire symbol stream.
        int preambleLen = wireSymbols.Length - (txBlocks * il.Frames * (u256 + k32)) - k32 - 32;
        preambleLen.Should().BeGreaterThan(0);
        int DataIndex(int b, int f, int u) =>
            preambleLen + k32 + (((b * il.Frames) + f) * (u256 + k32)) + u;

        var scrambleNibbles = new int[il.Frames * u256];
        var scrambler = new Ms110dScrambler();
        for (int f = 0; f < il.Frames; f++)
        {
            scrambler.Reset();
            for (int u = 0; u < u256; u++)
            {
                scrambleNibbles[(f * u256) + u] = scrambler.NextQam(0, 4);
            }
        }

        // The channel-static pulse templates: the modulator's SRRC (unit energy, 65
        // taps, symbol n's pulse starting at audio sample 4n - Ms110dModulator.Shape)
        // times the TX amplitude, fractionally delayed per path with the channel's own
        // windowed-sinc kernel. Real-valued; complex time variation rides on g_k.
        float[] basePulse = DesignPulse(settings.Amplitude);
        var pulses = new (float[] Taps, int First)[2];
        double[] delays = [0.0, 2.0 * 9600 / 1000.0];
        for (int k = 0; k < 2; k++)
        {
            pulses[k] = DelayPulse(basePulse, delays[k]);
        }

        float pathScale = (float)(1.0 / Math.Sqrt(2.0));

        // W3: the working trajectory the receiver BELIEVES. Estimation error enters the
        // statistic honestly (the true signal leaks through the estimated basis as a
        // per-symbol 2×2 distortion; pricing uses the believed Gram).
        Complex32[][] estGains;
        double[] nmse = [0.0, 0.0];
        if (traj == "probes")
        {
            estGains = EstimateFromProbes(
                rx, wireSymbols, preambleLen, u256, k32, il.Frames * txBlocks, pulses, pathScale, n);
        }
        else if (traj == "noise" && trajNoise > 0)
        {
            var perturb = new Random(channelSeed + 777);
            estGains = new Complex32[2][];
            for (int k = 0; k < 2; k++)
            {
                double ms = 0;
                foreach (Complex32 g in gains[k])
                {
                    ms += g.MagnitudeSquared();
                }

                float std = (float)(Math.Sqrt(ms / gains[k].Length) * trajNoise / Math.Sqrt(2.0));
                estGains[k] = new Complex32[gains[k].Length];
                for (int i = 0; i < gains[k].Length; i++)
                {
                    estGains[k][i] = gains[k][i] + new Complex32(
                        (float)(std * Gaussian(perturb)), (float)(std * Gaussian(perturb)));
                }
            }
        }
        else
        {
            estGains = [gains[0], gains[1]];
        }

        // Trajectory NMSE vs truth over the data span (pooled per path).
        for (int k = 0; k < 2; k++)
        {
            double err = 0, pow = 0;
            int lo = (preambleLen + k32) * 4 / 100;
            int hi = Math.Min(gains[k].Length,
                (preambleLen + k32 + (txBlocks * il.Frames * (u256 + k32))) * 4 / 100);
            for (int i = lo; i < hi; i++)
            {
                Complex32 e = InterpGain(estGains[k], i) - gains[k][i];
                err += e.MagnitudeSquared();
                pow += gains[k][i].MagnitudeSquared();
            }

            nmse[k] = err / Math.Max(pow, 1e-12);
        }

        // Calibration lane (i): rebuild the noiseless faded signal from the wire
        // symbols + templates and measure the model residual. Validates layout, pulse
        // model, carrier convention, and gain alignment in one label-exact shot.
        var rebuilt = new float[n];
        for (int sym = 0; sym < wireSymbols.Length; sym++)
        {
            AddSymbol(rebuilt, wireSymbols[sym], sym, pulses, gains, pathScale);
        }

        double refPower = 0, residPower = 0;
        for (int i = 0; i < n; i++)
        {
            refPower += (double)noiseless[i] * noiseless[i];
            residPower += ((double)rebuilt[i] - noiseless[i]) * (rebuilt[i] - noiseless[i]);
        }

        double modelResidual = Math.Sqrt(residPower / refPower);

        // The MFB per data symbol: whiten the projected noise by the 2×2 template Gram,
        // add the true symbol back (genie-exact interference cancellation), price each
        // candidate by the per-symbol Mahalanobis metric, decode through the outer code.
        var blockErrs = new List<string>();
        long codedErrors = 0;
        long sliceErrors = 0, sliceSymbols = 0;
        double predSum = 0, measSum = 0;
        var llrs = new float[il.SizeBits];
        var dec = new byte[il.InputBits];
        Span<float> metric = stackalloc float[16];
        string trajTag = traj == "truth" && trajNoise == 0
            ? ""
            : FormattableString.Invariant($"-{traj}{(traj == "noise" ? trajNoise.ToString("0.###", CultureInfo.InvariantCulture) : "")}");
        using var summary = new StreamWriter(Path.Combine(outDir,
            FormattableString.Invariant($"mfb-summary-wn{wn}-w{worker}-b{burst}-seed{baseSeed}{trajTag}.txt")));
        for (int b = 0; b < txBlocks; b++)
        {
            byte[] fetched = Ms110dFraming.EncodeBlock(
                code, puncture, interleaver, txBits.AsSpan(b * il.InputBits, il.InputBits));
            int bit = 0;
            for (int f = 0; f < il.Frames; f++)
            {
                for (int u = 0; u < u256; u++)
                {
                    int number = (fetched[bit] << 3) | (fetched[bit + 1] << 2)
                        | (fetched[bit + 2] << 1) | fetched[bit + 3];
                    int nib = scrambleNibbles[(f * u256) + u];
                    int sym = DataIndex(b, f, u);
                    Complex32 wire = Ms110dTables.Qam16[number ^ nib];
                    (wireSymbols[sym] - wire).MagnitudeSquared().Should().Be(0f,
                        "the data-index layout must match the modulator's wire stream");

                    (Complex32 y, float m11, float m12, float m22) =
                        Project(r, wire, sym, pulses, gains, estGains, pathScale);

                    // Predicted vs measured statistic-noise check (lane iii).
                    float det = (m11 * m22) - (m12 * m12);
                    predSum += sigma2 * (m11 + m22) / Math.Max(det, 1e-12f);
                    measSum += (y - wire).MagnitudeSquared();

                    int best = 0;
                    float bestMetric = float.MaxValue;
                    for (int cand = 0; cand < 16; cand++)
                    {
                        Complex32 d = y - Ms110dTables.Qam16[cand ^ nib];
                        // Mahalanobis under noise covariance σ²·M⁻¹: dᵀ·M·d / 2σ².
                        float q = (m11 * d.Re * d.Re) + (2f * m12 * d.Re * d.Im)
                            + (m22 * d.Im * d.Im);
                        metric[cand] = q / (float)(2.0 * sigma2);
                        if (metric[cand] < bestMetric)
                        {
                            bestMetric = metric[cand];
                            best = cand;
                        }
                    }

                    sliceSymbols++;
                    sliceErrors += best == number ? 0 : 1;
                    for (int bb = 0; bb < 4; bb++)
                    {
                        float m0 = float.MaxValue, m1 = float.MaxValue;
                        for (int cand = 0; cand < 16; cand++)
                        {
                            bool one = ((cand >> (3 - bb)) & 1) != 0;
                            if (one)
                            {
                                m1 = Math.Min(m1, metric[cand]);
                            }
                            else
                            {
                                m0 = Math.Min(m0, metric[cand]);
                            }
                        }

                        llrs[bit + bb] = m1 - m0; // positive ⇒ bit 0, the house convention
                    }

                    bit += 4;
                }
            }

            Ms110dFraming.DecodeBlock(viterbi, puncture, interleaver, llrs, dec);
            int errs = 0;
            for (int i = 0; i < dec.Length; i++)
            {
                errs += dec[i] != txBits[(b * il.InputBits) + i] ? 1 : 0;
            }

            codedErrors += errs;
            blockErrs.Add($"b{b}:{errs}");
        }

        long totalInfo = (long)txBlocks * il.InputBits;
        summary.WriteLine(FormattableString.Invariant(
            $"WN{wn} @ {snrDb} dB baseSeed {baseSeed} worker {worker} burst {burst} (channelSeed {channelSeed}) MFB"));
        summary.WriteLine(FormattableString.Invariant(
            $"trajectory {traj}{(trajNoise > 0 ? $" rel={trajNoise:0.###}" : "")}: NMSE p0 {10.0 * Math.Log10(Math.Max(nmse[0], 1e-12)):F1} dB, p1 {10.0 * Math.Log10(Math.Max(nmse[1], 1e-12)):F1} dB"));
        summary.WriteLine(FormattableString.Invariant(
            $"model residual RMS(rebuilt-exact)/RMS(exact) = {modelResidual:E3} (envelope-LPF unmodelled; pessimistic)"));
        summary.WriteLine(FormattableString.Invariant(
            $"noise sigma2/sample {sigma2:E4}; statistic noise predicted {predSum / sliceSymbols:E4} vs measured {measSum / sliceSymbols:E4}"));
        summary.WriteLine(FormattableString.Invariant(
            $"MF slicer SER {(double)sliceErrors / sliceSymbols:E3} ({sliceErrors}/{sliceSymbols})"));
        summary.WriteLine(FormattableString.Invariant(
            $"MFB coded errors {codedErrors}/{totalInfo} = {(double)codedErrors / totalInfo:E3}"));
        summary.WriteLine($"MFB coded errors per block: {string.Join(" ", blockErrs)}");
    }

    private static void AddSymbol(
        float[] output, Complex32 symbol, int symbolIndex,
        (float[] Taps, int First)[] pulses, IReadOnlyList<Complex32[]> gains, float pathScale)
    {
        int basePos = symbolIndex * 4;
        for (int k = 0; k < pulses.Length; k++)
        {
            (float[] taps, int first) = pulses[k];
            for (int m = 0; m < taps.Length; m++)
            {
                int i = basePos + first + m;
                if (i < 0 || i >= output.Length)
                {
                    continue;
                }

                Complex32 g = InterpGain(gains[k], i / 100.0);
                Complex32 env = symbol * (taps[m] * pathScale) * g;
                double phase = 2.0 * Math.PI * 3.0 * i / 16.0;
                output[i] += (float)((env.Re * Math.Cos(phase)) - (env.Im * Math.Sin(phase)));
            }
        }
    }

    /// <summary>The per-symbol statistic under a BELIEVED trajectory: the receiver's
    /// templates ê come from <paramref name="estGains"/>, so the true signal (templates
    /// from <paramref name="gains"/>) leaks through the estimated basis as a per-symbol
    /// 2×2 distortion T - ŷ = T·x + M̂⁻¹·⟨r, ê⟩ - while pricing uses the believed Gram
    /// M̂ (noise covariance σ²·M̂⁻¹). With estGains ≡ gains, T = I exactly and this is
    /// the banked W1b matched-filter statistic.</summary>
    private static (Complex32 Y, float M11, float M12, float M22) Project(
        float[] r, Complex32 truth, int symbolIndex,
        (float[] Taps, int First)[] pulses, IReadOnlyList<Complex32[]> gains, Complex32[][] estGains,
        float pathScale)
    {
        int basePos = symbolIndex * 4;
        int first = Math.Min(pulses[0].First, pulses[1].First);
        int last = Math.Max(pulses[0].First + pulses[0].Taps.Length,
            pulses[1].First + pulses[1].Taps.Length);
        double m11 = 0, m12 = 0, m22 = 0, v1 = 0, v2 = 0;
        double c1e1 = 0, c2e1 = 0, c1e2 = 0, c2e2 = 0;
        for (int m = first; m < last; m++)
        {
            int i = basePos + m;
            if (i < 0 || i >= r.Length)
            {
                continue;
            }

            var cTrue = Complex32.Zero;
            var cEst = Complex32.Zero;
            for (int k = 0; k < pulses.Length; k++)
            {
                int t = m - pulses[k].First;
                if (t >= 0 && t < pulses[k].Taps.Length)
                {
                    float w = pulses[k].Taps[t] * pathScale;
                    cTrue += InterpGain(gains[k], i / 100.0) * w;
                    cEst += InterpGain(estGains[k], i / 100.0) * w;
                }
            }

            double phase = 2.0 * Math.PI * 3.0 * i / 16.0;
            double cos = Math.Cos(phase);
            double sin = Math.Sin(phase);
            double e1t = (cTrue.Re * cos) - (cTrue.Im * sin);
            double e2t = -((cTrue.Im * cos) + (cTrue.Re * sin));
            double f1 = (cEst.Re * cos) - (cEst.Im * sin);
            double f2 = -((cEst.Im * cos) + (cEst.Re * sin));
            m11 += f1 * f1;
            m12 += f1 * f2;
            m22 += f2 * f2;
            v1 += r[i] * f1;
            v2 += r[i] * f2;
            c1e1 += f1 * e1t;
            c2e1 += f2 * e1t;
            c1e2 += f1 * e2t;
            c2e2 += f2 * e2t;
        }

        double det = Math.Max((m11 * m22) - (m12 * m12), 1e-12);
        double t11 = ((m22 * c1e1) - (m12 * c2e1)) / det;
        double t21 = ((m11 * c2e1) - (m12 * c1e1)) / det;
        double t12 = ((m22 * c1e2) - (m12 * c2e2)) / det;
        double t22 = ((m11 * c2e2) - (m12 * c1e2)) / det;
        double n1 = ((m22 * v1) - (m12 * v2)) / det;
        double n2 = ((m11 * v2) - (m12 * v1)) / det;
        double y1 = (t11 * truth.Re) + (t12 * truth.Im) + n1;
        double y2 = (t21 * truth.Re) + (t22 * truth.Im) + n2;
        return (new Complex32((float)y1, (float)y2), (float)m11, (float)m12, (float)m22);
    }

    /// <summary>Label-free probe-anchor trajectory estimate: per probe, a 4-real-unknown
    /// LS for the two path gains over the probe's ISI-clean interior (samples ≥96 past
    /// the probe start, so no data pulse - direct or delayed - reaches a row; the gains
    /// are ~constant over the 3.3 ms window), linearly interpolated between probe
    /// centres onto the 96 Hz grid. Sees only rx and the known probe chips.</summary>
    private static Complex32[][] EstimateFromProbes(
        float[] rx, Complex32[] wireSymbols, int preambleLen, int u, int k32, int framesTotal,
        (float[] Taps, int First)[] pulses, float pathScale, int envLength)
    {
        int gridLength = ((envLength - 1) / 100) + 1;
        var anchorX = new List<double>();
        var anchorG = new List<Complex32[]>();
        Span<double> a = stackalloc double[16];
        Span<double> b = stackalloc double[4];
        Span<double> f = stackalloc double[4];
        for (int p = 0; p <= framesTotal; p++)
        {
            int s = preambleLen + (p * (u + k32));
            int rowLo = 4 * (s + 24);
            int rowHi = 4 * (s + k32);
            if (rowHi >= envLength)
            {
                break;
            }

            a.Clear();
            b.Clear();
            for (int t = rowLo; t < rowHi; t++)
            {
                var c1 = Complex32.Zero;
                var c2 = Complex32.Zero;
                for (int c = s; c < s + k32; c++)
                {
                    int m = t - (4 * c);
                    for (int k = 0; k < 2; k++)
                    {
                        int tap = m - pulses[k].First;
                        if (tap >= 0 && tap < pulses[k].Taps.Length)
                        {
                            Complex32 add = wireSymbols[c] * (pulses[k].Taps[tap] * pathScale);
                            if (k == 0)
                            {
                                c1 += add;
                            }
                            else
                            {
                                c2 += add;
                            }
                        }
                    }
                }

                double phase = 2.0 * Math.PI * 3.0 * t / 16.0;
                double cos = Math.Cos(phase);
                double sin = Math.Sin(phase);
                f[0] = (c1.Re * cos) - (c1.Im * sin);
                f[1] = -((c1.Im * cos) + (c1.Re * sin));
                f[2] = (c2.Re * cos) - (c2.Im * sin);
                f[3] = -((c2.Im * cos) + (c2.Re * sin));
                double row = rx[2400 + t];
                for (int i = 0; i < 4; i++)
                {
                    b[i] += row * f[i];
                    for (int j = 0; j < 4; j++)
                    {
                        a[(i * 4) + j] += f[i] * f[j];
                    }
                }
            }

            if (!Solve4(a, b))
            {
                continue;
            }

            anchorX.Add((rowLo + rowHi) / 2.0 / 100.0);
            anchorG.Add([new Complex32((float)b[0], (float)b[1]), new Complex32((float)b[2], (float)b[3])]);
        }

        var est = new Complex32[2][];
        for (int k = 0; k < 2; k++)
        {
            est[k] = new Complex32[gridLength];
            for (int i = 0; i < gridLength; i++)
            {
                double x = i;
                int hi = anchorX.FindIndex(ax => ax >= x);
                if (hi < 0)
                {
                    est[k][i] = anchorG[^1][k];
                }
                else if (hi == 0)
                {
                    est[k][i] = anchorG[0][k];
                }
                else
                {
                    double frac = (x - anchorX[hi - 1]) / (anchorX[hi] - anchorX[hi - 1]);
                    est[k][i] = (anchorG[hi - 1][k] * (float)(1 - frac)) + (anchorG[hi][k] * (float)frac);
                }
            }
        }

        return est;
    }

    private static bool Solve4(Span<double> a, Span<double> b)
    {
        for (int col = 0; col < 4; col++)
        {
            int pivot = col;
            for (int r2 = col + 1; r2 < 4; r2++)
            {
                if (Math.Abs(a[(r2 * 4) + col]) > Math.Abs(a[(pivot * 4) + col]))
                {
                    pivot = r2;
                }
            }

            if (Math.Abs(a[(pivot * 4) + col]) < 1e-12)
            {
                return false;
            }

            if (pivot != col)
            {
                for (int c = 0; c < 4; c++)
                {
                    (a[(col * 4) + c], a[(pivot * 4) + c]) = (a[(pivot * 4) + c], a[(col * 4) + c]);
                }

                (b[col], b[pivot]) = (b[pivot], b[col]);
            }

            for (int r2 = col + 1; r2 < 4; r2++)
            {
                double factor = a[(r2 * 4) + col] / a[(col * 4) + col];
                for (int c = col; c < 4; c++)
                {
                    a[(r2 * 4) + c] -= factor * a[(col * 4) + c];
                }

                b[r2] -= factor * b[col];
            }
        }

        for (int r2 = 3; r2 >= 0; r2--)
        {
            double acc = b[r2];
            for (int c = r2 + 1; c < 4; c++)
            {
                acc -= a[(r2 * 4) + c] * b[c];
            }

            b[r2] = acc / a[(r2 * 4) + r2];
        }

        return true;
    }

    private static double Gaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private static Complex32 InterpGain(Complex32[] trajectory, double x)
    {
        if (trajectory.Length == 1)
        {
            return trajectory[0];
        }

        double clamped = Math.Clamp(x, 0, trajectory.Length - 1);
        int i0 = (int)clamped;
        int i1 = Math.Min(trajectory.Length - 1, i0 + 1);
        float frac = (float)(clamped - i0);
        return (trajectory[i0] * (1f - frac)) + (trajectory[i1] * frac);
    }

    /// <summary>The modulator's own SRRC (Ms110dModulator.DesignPulse: 65 taps, unit
    /// energy) scaled by the TX amplitude.</summary>
    private static float[] DesignPulse(float amplitude)
    {
        const int taps = (16 * 4) + 1;
        var pulse = new float[taps];
        double centre = (taps - 1) / 2.0;
        double energy = 0;
        for (int i = 0; i < taps; i++)
        {
            double t = (i - centre) / 4.0;
            pulse[i] = (float)FilterDesign.RootRaisedCosine(t, Ms110dModulator.RollOff);
            energy += pulse[i] * pulse[i];
        }

        float norm = (float)(amplitude / Math.Sqrt(energy));
        for (int i = 0; i < taps; i++)
        {
            pulse[i] *= norm;
        }

        return pulse;
    }

    /// <summary>Fractionally delays the pulse with the channel's own windowed-sinc
    /// kernel (WattersonChannel.FractionalDelay, half = 16). Returns the taps and the
    /// index of the first tap relative to the symbol's base sample (4·symbolIndex).</summary>
    private static (float[] Taps, int First) DelayPulse(float[] pulse, double delaySamples)
    {
        if (delaySamples == 0)
        {
            return (pulse, 0);
        }

        int whole = (int)Math.Floor(delaySamples);
        double frac = delaySamples - whole;
        const int half = 16;
        int first = whole - half;
        var taps = new float[pulse.Length + (2 * half) + 1];
        for (int m = 0; m < taps.Length; m++)
        {
            double acc = 0;
            for (int j = -half + 1; j <= half; j++)
            {
                int k = (first + m) - whole - j;
                if (k < 0 || k >= pulse.Length)
                {
                    continue;
                }

                double u = j - frac;
                double w = Math.Abs(u) < 1e-9
                    ? 1.0
                    : Math.Sin(Math.PI * u) / (Math.PI * u) * (0.5 + (0.5 * Math.Cos(Math.PI * u / half)));
                acc += pulse[k] * w;
            }

            taps[m] = (float)acc;
        }

        return (taps, first);
    }
}
