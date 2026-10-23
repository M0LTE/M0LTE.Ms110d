using System.Globalization;
using M0LTE.Fec;
using M0LTE.Ofdm;
using M0LTE.Ms110d;
using M0LTE.Ms110d.Fec;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Env-gated corpse-reproduction rig for the B3 catastrophic-burst tail autopsy
/// (phase-b-plan §B3, issue #69). The mask harness's per-burst census
/// (<c>MS110D_MASK_BURST_LOG</c>) localizes which bursts die; this rig rebuilds ONE such
/// burst bit-exactly - same payload draw sequence, same channel seed, same settings as
/// <see cref="Ms110dMaskTests"/> - and dumps every first-pass equalized data symbol
/// NEXT TO ITS TRUE transmitted constellation point, so the death mechanism (rotation
/// lock, fade misalignment, timing walk-off…) is measurable instead of guessed.
/// Not a gate - an instrument. <c>MS110D_AUTOPSY=1</c>, <c>MS110D_AUTOPSY_WN</c>
/// (default 5), <c>MS110D_AUTOPSY_SNR</c> (default the WN's Poor mask),
/// <c>MS110D_AUTOPSY_SEED</c> (base seed, default 500+WN - add any census seed offset
/// yourself), <c>MS110D_AUTOPSY_WORKER</c> (default 0), <c>MS110D_AUTOPSY_BURST</c>
/// (default 0), <c>MS110D_AUTOPSY_OUT</c> (directory, default "."),
/// <c>MS110D_AUTOPSY_GENIE=1</c> for the genie-aided companion dump.
/// </summary>
public class Ms110dTailAutopsy
{
    private static readonly Dictionary<int, double> PoorSnr = new()
    {
        { 0, -1 }, { 1, 3 }, { 2, 5 }, { 3, 7 }, { 4, 10 }, { 5, 11 }, { 6, 14 },
        { 7, 19 }, { 8, 23 }, { 13, 11 },
    };

    private static int EnvInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;
    }

    /// <summary>Mirror of the modulator's private Transcode (Tables D-III/D-IV/D-V):
    /// fetched wire bits at <paramref name="bit"/> → descrambled 8PSK ring index.</summary>
    private static int RingIndex(byte[] fetched, ref int bit, Ms110dModulation modulation)
    {
        if (modulation == Ms110dModulation.Bpsk)
        {
            return fetched[bit++] == 0 ? 0 : 4;
        }

        if (modulation == Ms110dModulation.Psk8)
        {
            int tribit = (fetched[bit++] << 2) | (fetched[bit++] << 1) | fetched[bit++];
            return Ms110dTables.Transcode8Psk[tribit];
        }

        int msb = fetched[bit++];
        int lsb = fetched[bit++];
        return ((msb << 1) | lsb) switch { 0 => 0, 1 => 2, 3 => 4, _ => 6 };
    }

    [Fact]
    public void Mask_Burst_Corpse_Dump()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_AUTOPSY") != "1",
            "set MS110D_AUTOPSY=1 for the tail-autopsy corpse dump");

        int wn = EnvInt("MS110D_AUTOPSY_WN", 5);
        double snrDb = double.TryParse(Environment.GetEnvironmentVariable("MS110D_AUTOPSY_SNR"), out double s)
            ? s : PoorSnr[wn];
        int baseSeed = EnvInt("MS110D_AUTOPSY_SEED", 500 + wn);
        int worker = EnvInt("MS110D_AUTOPSY_WORKER", 0);
        int burst = EnvInt("MS110D_AUTOPSY_BURST", 0);
        string outDir = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_OUT") ?? ".";
        bool genie = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_GENIE") == "1";

        // Bit-exact reproduction of Ms110dMaskTests.RunPointWorker's burst construction.
        int workerSeed = baseSeed + (worker * 1_000_000);
        var settings = new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Long,
            ConstraintLength = 7,
            PreambleSuperframes = 20,
        };
        var tx = new Ms110dModulator(settings);
        bool qam16 = tx.Mode.Modulation == Ms110dModulation.Qam16;
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long);
        double blockSeconds = wn == 0
            ? il.Frames * 32.0 / 2400
            : il.Frames * (tx.Mode.U + tx.Mode.K) / 2400.0;
        int blocksPerBurst = Math.Max(1, (int)(90 / blockSeconds));
        int payloadBitsPerBurst = (blocksPerBurst * il.InputBits) - 32;

        var random = new Random(workerSeed);
        var payload = new byte[payloadBitsPerBurst];
        for (int b = 0; b <= burst; b++)
        {
            for (int i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)random.Next(2); // prior bursts advance the draw sequence
            }
        }

        float[] audio = tx.Modulate(payload);
        int channelSeed = workerSeed + (1000 * burst) + 1;
        // MS110D_AUTOPSY_AWGN=1: the AWGN rig instead of D.6.1 Poor - the mask suite's
        // AWGN points (seed base 100 + wn, empty path set) reproduced burst-exactly, so
        // an AWGN census line becomes a corpse like any Poor one (G1d needed it: the
        // ensemble's one AWGN WN7 failure).
        bool awgnRig = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_AWGN") == "1";
        var channel = new WattersonChannel(9600, channelSeed, awgnRig ? [] : WattersonChannel.Poor)
        {
            RecordGains = true,
        };

        // MS110D_AUTOPSY_CLEAN=1: no channel at all - no fading, no noise, just the
        // lead-in/out padding. Isolates systematic first-pass error (equalizer bias,
        // probe/data geometry) from everything channel-induced.
        bool clean = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_CLEAN") == "1";
        float[] rx;
        if (clean)
        {
            rx = new float[2400 + audio.Length + 2400];
            audio.CopyTo(rx, 2400);
        }
        else
        {
            rx = channel.Apply(audio, snrDb, leadInSamples: 2400, leadOutSamples: 2400);
        }

        // Per-symbol truth: re-encode the TX stream to wire bits per block (the same
        // telemetry path as the mask harness) and transcode to descrambled ring points.
        byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, il.InputBits);
        int txBlocks = txBits.Length / il.InputBits;
        ConvolutionalCode code = ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, tx.Mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);
        int symbolsPerBlock = il.Frames * tx.Mode.U;
        var refRing = new int[txBlocks * symbolsPerBlock];
        var fetchedBlocks = new byte[txBlocks][];
        for (int b = 0; b < txBlocks; b++)
        {
            byte[] fetched = Ms110dFraming.EncodeBlock(
                code, puncture, interleaver, txBits.AsSpan(b * il.InputBits, il.InputBits));
            fetchedBlocks[b] = fetched;
            int bit = 0;
            if (qam16)
            {
                // QAM16 truth lives in the WIRE domain (the demod emits wire-domain y -
                // descrambling happens inside PushMaxLogLlrs via the XOR nibble): symbol
                // number = 4 fetched bits MSB-first, wire index = number XOR the scramble
                // nibble, register reset at each data frame start (D.5.1.3).
                var truthScrambler = new Ms110dScrambler();
                for (int f = 0; f < il.Frames; f++)
                {
                    truthScrambler.Reset();
                    for (int u = 0; u < tx.Mode.U; u++)
                    {
                        int nibble = (fetched[bit++] << 3) | (fetched[bit++] << 2)
                            | (fetched[bit++] << 1) | fetched[bit++];
                        refRing[(b * symbolsPerBlock) + (f * tx.Mode.U) + u] =
                            truthScrambler.NextQam(nibble, 4);
                    }
                }
            }
            else
            {
                for (int sym = 0; sym < symbolsPerBlock; sym++)
                {
                    refRing[(b * symbolsPerBlock) + sym] = RingIndex(fetched, ref bit, tx.Mode.Modulation);
                }
            }
        }

        bool oracle = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_ORACLE") == "1";

        // W1 (wn8-program) true-channel injection: the oracle-pass structure with the
        // channel's time variation from recorded Watterson truth instead of the
        // label-trained segment anchors (Ms110dDemodulator.TruthGainsAtSample). Implies
        // the oracle pass - the two bounds land side by side on every block.
        bool truth = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TRUTH") == "1";
        if (truth && clean)
        {
            throw new InvalidOperationException(
                "MS110D_AUTOPSY_TRUTH needs a channel (unset MS110D_AUTOPSY_CLEAN)");
        }

        oracle |= truth;

        // §B3.5b WN0 genie-gain oracle: "inst" | "pole" (evidence/2026-07-26-phase-
        // b35b-wn0genie). Implies genie feeding - the truth gains are read from the
        // clean stream.
        string? walshOracle = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_WALSH_ORACLE");
        genie |= walshOracle is not null;

        // §B3.6 M1b perturbed-restart instrument: "p,k" - flip each iteration-0 label
        // with probability p under a deterministic per-block xorshift seeded by k
        // (registered: p = 0.02, k = 1..8; evidence/2026-07-26-phase-b36-wn7loop).
        string? turboPerturb = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TURBO_PERTURB");

        // §B3.6 C2a stage instruments: the frozen (label-free) re-detection pass (M2a)
        // and the seed file that feeds a prior run's frozen decodes back in as
        // iteration-0 labels (M2b, the composed candidate).
        bool turboFrozen = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TURBO_FROZEN") == "1";
        string? turboSeedFile = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TURBO_SEED");
        if (turboPerturb is not null && turboSeedFile is not null)
        {
            throw new InvalidOperationException("TURBO_PERTURB and TURBO_SEED both set");
        }

        string tag = $"wn{wn}-w{worker}-b{burst}{(clean ? "-clean" : "")}" +
            $"{(genie ? "-genie" : "")}{(oracle ? "-oracle" : "")}{(truth ? "-truth" : "")}" +
            $"{(walshOracle is null ? "" : $"-wgo{walshOracle}")}" +
            $"{(turboPerturb is null ? "" : $"-tp{turboPerturb.Replace(',', '-')}")}" +
            $"{(turboFrozen ? "-tfz" : "")}{(turboSeedFile is null ? "" : "-tsd")}";
        using var symbols = new StreamWriter(Path.Combine(outDir, $"autopsy-symbols-{tag}.csv"));
        symbols.WriteLine("index,re,im,refIdx,refRe,refIm");
        using var frames = new StreamWriter(Path.Combine(outDir, $"autopsy-frames-{tag}.log"));
        using var bitErrs = new StreamWriter(Path.Combine(outDir, $"autopsy-biterrs-{tag}.csv"));
        bitErrs.WriteLine("block,symbolInBlock,bitInSymbol");
        using var oracleBitErrs = new StreamWriter(Path.Combine(outDir, $"autopsy-oracle-biterrs-{tag}.csv"));
        oracleBitErrs.WriteLine("block,symbolInBlock,bitInSymbol");
        using var frozenBitErrs = new StreamWriter(Path.Combine(outDir, $"autopsy-frozen-biterrs-{tag}.csv"));
        frozenBitErrs.WriteLine("block,symbolInBlock,bitInSymbol");
        using var llrStats = new StreamWriter(Path.Combine(outDir, $"autopsy-llrstats-{tag}.csv"));
        llrStats.WriteLine("pass,block,bits,errBits,sumAbsRight,sumAbsWrong");

        int symbolIndex = 0;
        int bitsPerSymbol = tx.Mode.BitsPerSymbol;
        long uncodedErrors = 0, uncodedBits = 0;
        var decoded = new List<byte>(payload.Length + 64);
        Ms110dBurstEndReason? endReason = null;
        var demod = new Ms110dDemodulator(new Ms110dDemodOptions
        {
            DisableTurbo = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_NOTURBO") == "1",
            TrackRidge = float.TryParse(
                Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TRACK_RIDGE"), out float tr)
                ? tr : null,
            // §B4.1 per-segment pricing variant ("spikeup"/"spike2s"); unset = shipped.
            TurboNsegMode = Environment.GetEnvironmentVariable("MS110D_TURBO_NSEG"),
        });
        demod.BurstCompleted += bu => endReason = bu.Reason;
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        demod.DataSymbolEqualized += y =>
        {
            int i = symbolIndex++;
            if (i < refRing.Length)
            {
                Cf p = qam16 ? Ms110dTables.Qam16[refRing[i]] : Ms110dTables.Psk8[refRing[i]];
                symbols.WriteLine($"{i},{y.Re:F5},{y.Im:F5},{refRing[i]},{p.Re:F5},{p.Im:F5}");
            }
            else
            {
                symbols.WriteLine($"{i},{y.Re:F5},{y.Im:F5},-1,0,0"); // post-EOM, no truth
            }
        };
        demod.FrameDiagnostics += line => frames.WriteLine(line);
        demod.FirstPassBlockLlrs += (blockIndex, llrs) =>
        {
            if (blockIndex >= txBlocks)
            {
                return;
            }

            byte[] fetched = fetchedBlocks[blockIndex];
            int compareBits = Math.Min(llrs.Length, fetched.Length);
            uncodedBits += compareBits;
            for (int i = 0; i < compareBits; i++)
            {
                if ((llrs[i] > 0 ? 0 : 1) != fetched[i])
                {
                    uncodedErrors++;
                    bitErrs.WriteLine($"{blockIndex},{i / bitsPerSymbol},{i % bitsPerSymbol}");
                }
            }
        };

        // §B3.2b instrument: per-block uncoded SER and signed-LLR-mass stats for the
        // first-pass LLR stream - the error-CONFIDENCE view the WN2 anchor-ridge autopsy
        // established (wrong-sign LLR mass is what the Viterbi actually pays, and the
        // ratio against a genie corpse locates a block relative to the decode cliff).
        void WriteLlrStats(string pass, int blockIndex, float[] llrs)
        {
            if (blockIndex >= txBlocks)
            {
                return;
            }

            byte[] fetched = fetchedBlocks[blockIndex];
            int compareBits = Math.Min(llrs.Length, fetched.Length);
            int errBits = 0;
            double sumRight = 0, sumWrong = 0;
            for (int i = 0; i < compareBits; i++)
            {
                if ((llrs[i] > 0 ? 0 : 1) != fetched[i])
                {
                    errBits++;
                    sumWrong += Math.Abs(llrs[i]);
                }
                else
                {
                    sumRight += Math.Abs(llrs[i]);
                }
            }

            llrStats.WriteLine($"{pass},{blockIndex},{compareBits},{errBits},{sumRight:F1},{sumWrong:F1}");
        }

        demod.FirstPassBlockLlrs += (blockIndex, llrs) => WriteLlrStats("first", blockIndex, llrs);

        // §B3.3 basin instrument: the FIRST DECODE's per-block info errors - the quantity
        // the basin boundary is measured in (llrstats `first` counts raw stream signs,
        // which per-frame LLR weighting cannot change; the Viterbi decode is what it
        // changes). Decoded rig-side so the demod's own pipeline is untouched.
        var firstViterbi = new TailBitingViterbiDecoder(code);
        var firstDecodeErrs = new List<string>();
        demod.FirstPassBlockLlrs += (blockIndex, llrs) =>
        {
            if (blockIndex >= txBlocks)
            {
                return;
            }

            var dec = new byte[il.InputBits];
            Ms110dFraming.DecodeBlock(firstViterbi, puncture, interleaver, llrs, dec);
            int errs = 0;
            for (int i = 0; i < dec.Length; i++)
            {
                errs += dec[i] != txBits[(blockIndex * il.InputBits) + i] ? 1 : 0;
            }

            firstDecodeErrs.Add($"b{blockIndex}:{errs}");
        };

        // §B3.3 basin instrument: the stream the turbo settled on - the missing middle of
        // the first→final→oracle walk. On reverted blocks this is the wander state at the
        // cap, which is exactly the view the basin mechanism analysis needs.
        demod.TurboBlockLlrs += (blockIndex, llrs) => WriteLlrStats("final", blockIndex, llrs);

        // §B3.4 instrument: the final stream's INFO decode - what the revert-at-cap
        // discards. PSK wander states measured worse-than-first (the revert is right);
        // the QAM16 climb-from-bootstrap may floor far better than its coin-flip first
        // decode, and this number decides whether the revert logic fits QAM16.
        var finalDecodeErrs = new List<string>();
        demod.TurboBlockLlrs += (blockIndex, llrs) =>
        {
            if (blockIndex >= txBlocks)
            {
                return;
            }

            var dec = new byte[il.InputBits];
            Ms110dFraming.DecodeBlock(firstViterbi, puncture, interleaver, llrs, dec);
            int errs = 0;
            for (int i = 0; i < dec.Length; i++)
            {
                errs += dec[i] != txBits[(blockIndex * il.InputBits) + i] ? 1 : 0;
            }

            finalDecodeErrs.Add($"b{blockIndex}:{errs}");
        };

        // §B3.3 oracle-labels ceiling (MS110D_AUTOPSY_ORACLE=1): the demod runs one
        // extra chain-BCJR turbo pass per block trained on the TRUE info bits - the
        // upper bound a converged soft-feedback turbo could reach with this channel
        // model. Per-block oracle coded errors land in the summary; the oracle LLR
        // stream lands in llrstats as pass "oracle".
        var oracleBlockErrs = new List<string>();
        if (oracle)
        {
            demod.OracleInfo = b => b < txBlocks
                ? txBits.AsSpan(b * il.InputBits, il.InputBits).ToArray()
                : null;
            demod.OracleBlockLlrs += (blockIndex, llrs, dec) =>
            {
                WriteLlrStats("oracle", blockIndex, llrs);

                // §B3.3 model-front instrument: WHERE the oracle stream is wrong - the
                // positions localize the residual (fade nulls vs echo regions vs uniform).
                byte[] fetchedTruth = fetchedBlocks[blockIndex];
                int cmp = Math.Min(llrs.Length, fetchedTruth.Length);
                for (int i = 0; i < cmp; i++)
                {
                    if ((llrs[i] > 0 ? 0 : 1) != fetchedTruth[i])
                    {
                        oracleBitErrs.WriteLine($"{blockIndex},{i / bitsPerSymbol},{i % bitsPerSymbol}");
                    }
                }
                int errs = 0;
                for (int i = 0; i < dec.Length; i++)
                {
                    errs += dec[i] != txBits[(blockIndex * il.InputBits) + i] ? 1 : 0;
                }

                oracleBlockErrs.Add($"b{blockIndex}:{errs}");
            };
        }

        // W1 truth wiring: the rig owns lead-in/gain-rate alignment (input sample →
        // channel-span position → 96 Hz trajectory index, linearly interpolated - the
        // same interpolation the channel itself applied between gain samples); the demod
        // owns chip→sample. The constant RX front-end group delay is deliberately NOT
        // compensated: a few ms against a 1 Hz fade is a sub-percent trajectory error,
        // priced into the fit residual (W1 registration).
        var truthBlockErrs = new List<string>();
        if (truth)
        {
            IReadOnlyList<Cf[]> pathGains = channel.LastPathGains
                ?? throw new InvalidOperationException("channel gains were not recorded");

            // W2 variants: V-offset shifts the truth lookup by a constant (the
            // front-end group-delay scan); V-split/V-xtaps are demod-side gauge knobs.
            double truthOffset = double.TryParse(
                Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TRUTH_OFFSET"),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double to) ? to : 0.0;
            demod.TruthGaugeSplit = EnvInt("MS110D_AUTOPSY_TRUTH_SPLIT", 1);
            demod.TruthGaugeXtaps =
                Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TRUTH_XTAPS") == "1";
            demod.TruthGainsAtSample = pos =>
            {
                double x = (pos - 2400.0 + truthOffset) / 100.0;
                return (InterpGain(pathGains[0], x), InterpGain(pathGains[1], x));
            };
            demod.TruthBlockLlrs += (blockIndex, llrs, dec) =>
            {
                WriteLlrStats("truth", blockIndex, llrs);
                if (blockIndex >= txBlocks)
                {
                    return;
                }

                int errs = 0;
                for (int i = 0; i < dec.Length; i++)
                {
                    errs += dec[i] != txBits[(blockIndex * il.InputBits) + i] ? 1 : 0;
                }

                truthBlockErrs.Add($"b{blockIndex}:{errs}");
            };
        }

        // §B3.5b: truth di-bits for the WN0 gain oracle from the same fetchedBlocks
        // truth the uncoded accounting uses (Modulate's MSB-first di-bit order); −1
        // (no truth / post-EOM) falls back to the shipped DD path per symbol.
        if (walshOracle is not null)
        {
            demod.WalshOraclePole = walshOracle == "pole";
            demod.WalshOracleDibit = (b, sym) =>
                b < txBlocks && (2 * sym) + 1 < fetchedBlocks[b].Length
                    ? (fetchedBlocks[b][2 * sym] << 1) | fetchedBlocks[b][(2 * sym) + 1]
                    : -1;
        }

        // §B3.6 C2a stage (M2a): arm the frozen pass; per-block coded errors reach the
        // summary and the decodes are dumped for M2b seeding.
        var frozenBlockErrs = new List<string>();
        var frozenDecodes = new Dictionary<int, byte[]>();
        if (turboFrozen)
        {
            demod.TurboFrozenProbe = true;
            demod.FrozenBlockLlrs += (blockIndex, llrs, dec) =>
            {
                WriteLlrStats("frozen", blockIndex, llrs);
                if (blockIndex >= txBlocks)
                {
                    return;
                }

                // §B3.7 M0: per-position frozen LLR-sign errors, same schema as the
                // first-pass biterrs CSV so scaffold.py applies to the frozen decode.
                byte[] fetched = fetchedBlocks[blockIndex];
                int compareBits = Math.Min(llrs.Length, fetched.Length);
                for (int i = 0; i < compareBits; i++)
                {
                    if ((llrs[i] > 0 ? 0 : 1) != fetched[i])
                    {
                        frozenBitErrs.WriteLine($"{blockIndex},{i / bitsPerSymbol},{i % bitsPerSymbol}");
                    }
                }

                int errs = 0;
                for (int i = 0; i < dec.Length; i++)
                {
                    errs += dec[i] != txBits[(blockIndex * il.InputBits) + i] ? 1 : 0;
                }

                frozenBlockErrs.Add($"b{blockIndex}:{errs}");
                frozenDecodes[blockIndex] = (byte[])dec.Clone();
            };
        }

        // §B3.6 M2b: a prior frozen run's decodes become this run's iteration-0 labels
        // (the composed C2a candidate, staged across two runs). Blocks absent from the
        // file keep their shipped start.
        if (turboSeedFile is not null)
        {
            var seeds = new Dictionary<int, byte[]>();
            foreach (string line in File.ReadAllLines(turboSeedFile))
            {
                int colon = line.IndexOf(':');
                if (colon <= 1 || !line.StartsWith('b'))
                {
                    continue;
                }

                int b = int.Parse(line[1..colon], CultureInfo.InvariantCulture);
                seeds[b] = line[(colon + 1)..].TrimEnd().Select(c => (byte)(c - '0')).ToArray();
            }

            demod.TurboStartOverride = (blockIndex, firstPass) =>
                seeds.TryGetValue(blockIndex, out byte[]? s) && s.Length == firstPass.Length
                    ? s : null;
        }

        // §B3.6 M1a probe-pricing diag (turbo-probe lines in the frames log) and M1b
        // perturbed restarts. The perturbation touches ONLY the iteration-0 labels; the
        // revert fallback stays the true first pass inside the demod.
        demod.TurboProbeDiag = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_TURBO_PROBEDIAG") == "1";
        // §B3.7 M1a: log-only straddle-pair solve per frozen frame (frozen-pair lines).
        demod.TurboFrozenPairDiag = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_FROZEN_PAIRDIAG") == "1";
        // §B3.7 E1′ (Amendment 1): burst-consensus constrained frozen solve.
        demod.TurboFrozenConsensus = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_FROZEN_CONSENSUS") == "1";
        // §B3.7 E1″(a) (Amendment 2): alias-priced null on pre-cursor frames.
        demod.TurboFrozenAliasNull = Environment.GetEnvironmentVariable("MS110D_AUTOPSY_FROZEN_ALIASNULL") == "1";
        // §B3.7 E1″(b), shipped default (Amendment 3): exact pre-cursor chains on
        // alias frames. "0" disables (the pre-B3.7 causal-alias seam).
        if (Environment.GetEnvironmentVariable("MS110D_AUTOPSY_FROZEN_PRECURSOR") == "0")
        {
            demod.TurboFrozenPreCursor = false;
        }
        // §B3.8 E3/Amendment 3, shipped default: late-lock second salvage rung.
        // "0" disables (the pre-B3.8 seam).
        if (Environment.GetEnvironmentVariable("MS110D_AUTOPSY_FROZEN_RELOCK") == "0")
        {
            demod.TurboFrozenRelock = false;
        }
        // §B3.8 Amendment 2: decisive-adoption margin (1 = adopt on any improvement).
        if (float.TryParse(Environment.GetEnvironmentVariable("MS110D_AUTOPSY_FROZEN_RELOCK_MARGIN"),
                out float relockMargin))
        {
            demod.TurboFrozenRelockMargin = relockMargin;
        }
        if (turboPerturb is not null)
        {
            string[] parts = turboPerturb.Split(',');
            float pFlip = float.Parse(parts[0], CultureInfo.InvariantCulture);
            int pSeed = int.Parse(parts[1], CultureInfo.InvariantCulture);
            demod.TurboStartOverride = (blockIndex, firstPass) =>
            {
                var perturbed = (byte[])firstPass.Clone();
                uint s = unchecked((uint)((pSeed * 1000003) + (blockIndex * 40503)) ^ 2463534242u);
                for (int i = 0; i < perturbed.Length; i++)
                {
                    s ^= s << 13;
                    s ^= s >> 17;
                    s ^= s << 5;
                    if (s * (1.0f / uint.MaxValue) < pFlip)
                    {
                        perturbed[i] ^= 1;
                    }
                }

                return perturbed;
            };
        }

        if (genie)
        {
            float[] genieRef = new WattersonChannel(9600, channelSeed, WattersonChannel.Poor).Apply(
                audio, double.PositiveInfinity, leadInSamples: 2400, leadOutSamples: 2400);
            for (int i = 0; i < rx.Length; i += 4800)
            {
                int length = Math.Min(4800, rx.Length - i);
                demod.WriteGenie(genieRef.AsSpan(i, length));
                demod.Process(rx.AsSpan(i, length));
            }
        }
        else
        {
            demod.Process(rx);
        }

        if (!clean && !awgnRig)
        {
            using var gainsOut = new StreamWriter(Path.Combine(outDir, $"autopsy-gains-{tag}.csv"));
            gainsOut.WriteLine("index,p0re,p0im,p1re,p1im");
            IReadOnlyList<Cf[]> gains = channel.LastPathGains!;
            int count = Math.Max(gains[0].Length, gains[1].Length);
            for (int i = 0; i < count; i++)
            {
                Cf g0 = gains[0][Math.Min(i, gains[0].Length - 1)];
                Cf g1 = gains[1][Math.Min(i, gains[1].Length - 1)];
                gainsOut.WriteLine($"{i},{g0.Re:F5},{g0.Im:F5},{g1.Re:F5},{g1.Im:F5}");
            }
        }

        long codedErrors = 0, firstErr = -1, lastErr = -1;
        int compared = Math.Min(decoded.Count, payload.Length);
        for (int i = 0; i < compared; i++)
        {
            if (decoded[i] != payload[i])
            {
                codedErrors++;
                if (firstErr < 0)
                {
                    firstErr = i;
                }

                lastErr = i;
            }
        }

        File.WriteAllText(
            Path.Combine(outDir, $"autopsy-decoded-{tag}.txt"),
            string.Concat(decoded.Select(b => b.ToString())) + "\n" +
            string.Concat(payload.Select(b => b.ToString())) + "\n");
        File.WriteAllText(
            Path.Combine(outDir, $"autopsy-summary-{tag}.txt"),
            $"WN{wn} @ {snrDb} dB baseSeed {baseSeed} worker {worker} burst {burst} " +
            $"(channelSeed {channelSeed}) genie={genie}\n" +
            $"payload {payload.Length} bits in {txBlocks} blocks ({symbolsPerBlock} symbols/block), " +
            $"decoded {decoded.Count}, coded errors {codedErrors} " +
            $"(first {firstErr}, last {lastErr}), uncoded {uncodedErrors}/{uncodedBits}, " +
            $"collapses {demod.CollapseResolves}, turbo {demod.TurboConverged}c/" +
            $"{demod.TurboReverted}r/{demod.TurboAborted}a/{demod.TurboSkipped}s/" +
            $"{demod.TurboSalvaged}v, " +
            $"end={endReason}, {symbolIndex} symbols dumped, " +
            $"lock={demod.Lock?.WaveformNumber}/{demod.Lock?.Interleaver}/K{demod.Lock?.ConstraintLength}" +
            $"@{demod.Lock?.CfoHz:F2}Hz (tx K{settings.ConstraintLength})\n" +
            $"first-decode errors per block: {string.Join(" ", firstDecodeErrs)}\n" +
            $"final-decode errors per block: {string.Join(" ", finalDecodeErrs)}\n" +
            (oracle ? $"oracle coded errors per block: {string.Join(" ", oracleBlockErrs)}\n" : "") +
            (demod.MfbOffered > 0 ? $"mfb ensemble: offered {demod.MfbOffered}, selected {demod.MfbSelected}\n" : "") +
            (truth ? $"truth coded errors per block: {string.Join(" ", truthBlockErrs)}\n" : "") +
            (turboFrozen ? $"frozen coded errors per block: {string.Join(" ", frozenBlockErrs)}\n" : ""));
        if (turboFrozen)
        {
            File.WriteAllLines(
                Path.Combine(outDir, $"autopsy-frozen-decode-{tag}.txt"),
                frozenDecodes.OrderBy(kv => kv.Key).Select(kv =>
                    $"b{kv.Key}:{string.Concat(kv.Value.Select(b => b.ToString()))}"));
        }
        decoded.Count.Should().BeGreaterThan(0,
            "the corpse must at least acquire for the dump to mean anything");
    }

    /// <summary>Truth-gain lookup at a fractional 96 Hz trajectory index - linear
    /// interpolation matching the channel's own gain application; endpoints clamped
    /// (only ever reached by lead-in/lead-out positions no data chip maps to).</summary>
    private static Cf InterpGain(Cf[] trajectory, double x)
    {
        if (trajectory.Length == 1)
        {
            return trajectory[0]; // static path: a single recorded constant
        }

        double clamped = Math.Clamp(x, 0, trajectory.Length - 1);
        int i0 = (int)clamped;
        int i1 = Math.Min(trajectory.Length - 1, i0 + 1);
        float frac = (float)(clamped - i0);
        return (trajectory[i0] * (1f - frac)) + (trajectory[i1] * frac);
    }
}
