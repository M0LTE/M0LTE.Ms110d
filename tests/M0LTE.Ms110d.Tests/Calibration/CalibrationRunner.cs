using System.Globalization;
using System.Text;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests.Calibration;

/// <summary>What happened to one transmitted burst.</summary>
internal sealed record BurstRecord(
    string Job, string Channel, int SnrTenths, int Segment, int Index, Rung Rung, Rung? Previous, int GapMs,
    int? EstimateTenths, bool Decoded, string Reason, int Blocks, int BlockErrors, int Dpdus, int DpduErrors)
{
    public const string Header =
        "job\tchannel\tset_snr_tenth\tsegment\tindex\trung\tprev_rung\tgap_ms\test_tenth\tdecoded\treason\tblocks\tblock_errors\tdpdus\tdpdu_errors";

    public string ToLine() => string.Create(CultureInfo.InvariantCulture,
        $"{Job}\t{Channel}\t{SnrTenths}\t{Segment}\t{Index}\t{Rung.Name}\t{Previous?.Name ?? "-"}\t{GapMs}\t" +
        $"{(EstimateTenths is { } e ? e.ToString(CultureInfo.InvariantCulture) : "-")}\t{(Decoded ? 1 : 0)}\t{Reason}\t" +
        $"{Blocks}\t{BlockErrors}\t{Dpdus}\t{DpduErrors}");

    public static BurstRecord Parse(string line)
    {
        string[] f = line.Split('\t');
        return new BurstRecord(
            f[0], f[1], int.Parse(f[2], CultureInfo.InvariantCulture), int.Parse(f[3], CultureInfo.InvariantCulture),
            int.Parse(f[4], CultureInfo.InvariantCulture), ParseRung(f[5]), f[6] == "-" ? null : ParseRung(f[6]),
            int.Parse(f[7], CultureInfo.InvariantCulture), f[8] == "-" ? null : int.Parse(f[8], CultureInfo.InvariantCulture),
            f[9] == "1", f[10], int.Parse(f[11], CultureInfo.InvariantCulture), int.Parse(f[12], CultureInfo.InvariantCulture),
            int.Parse(f[13], CultureInfo.InvariantCulture), int.Parse(f[14], CultureInfo.InvariantCulture));
    }

    public static Rung ParseRung(string name)
    {
        // wn<N>-<interleaver>
        int dash = name.IndexOf('-', StringComparison.Ordinal);
        return new Rung(int.Parse(name[2..dash], CultureInfo.InvariantCulture), Rung.ParseIl(name[(dash + 1)..]));
    }
}

/// <summary>
/// Runs one calibration job: one cycle of a point's burst schedule, through one continuous
/// channel realisation (M0LTE.HfChannel's ContinuousWattersonChannel) into one demodulator that
/// runs the whole time, as a real receiver would, with the turnaround gap of silence (channel noise
/// only) between bursts. Each burst carries a known pseudo-random payload, so every block and
/// every D_PDU of the planner's layout is checked exactly.
/// </summary>
internal static class CalibrationRunner
{
    /// <summary>Noise before the first burst: one default gap, so the first burst of a job meets a
    /// demodulator with the same quiet history as every later one.</summary>
    public const double LeadInSeconds = 6;

    /// <summary>Noise after the last burst, so a burst still decoding completes.</summary>
    public const double TailSeconds = 10;

    private const int Chunk = 4800;

    private static readonly Lazy<(IReadOnlyDictionary<string, double> Plain, IReadOnlyDictionary<string, double> Ssb)> Powers =
        new(MeasurePowers);

    /// <summary>The mean power (mean square) of each rung's burst at the transmitter output, and the
    /// same after the transmit SSB passband, keyed by rung name; all 36 rungs, whatever the profile.</summary>
    public static (IReadOnlyDictionary<string, double> Plain, IReadOnlyDictionary<string, double> Ssb) RungPowers => Powers.Value;

    /// <summary>
    /// The signal power the channel's SNR is set against: the mean over all 36 rungs of each rung's
    /// burst power, on air. The SNR reference is the on-air signal: on the channels without the SSB
    /// band-pass, the transmitter output; with it, the signal after the sending radio's passband (so
    /// the filter's loss does not count as a lower SNR, and the SSB points compare with the plain ones
    /// at the same on-air SNR). Each burst is scaled to exactly this power (<see cref="BurstGain"/>),
    /// so every rung meets the set SNR, as WattersonChannel.Apply calibrates each burst in the SNR
    /// sweep, and every burst in one realisation shares one noise floor.
    /// </summary>
    public static double SignalPower(CalChannel c)
    {
        var map = c.Ssb ? RungPowers.Ssb : RungPowers.Plain;
        return map.Values.Average();
    }

    /// <summary>The amplitude scale that brings a rung's burst to <see cref="SignalPower"/> on air
    /// (WN 8's QAM is about 0.8 dB below the PSK waveforms at the same peak amplitude).</summary>
    public static float BurstGain(CalChannel c, Rung r)
    {
        var map = c.Ssb ? RungPowers.Ssb : RungPowers.Plain;
        return (float)Math.Sqrt(SignalPower(c) / map[r.Name]);
    }

    private static (IReadOnlyDictionary<string, double>, IReadOnlyDictionary<string, double>) MeasurePowers()
    {
        var plain = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var ssb = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (int wn in CalibrationPlan.AllWns)
        {
            foreach (Ms110dInterleaverKind il in CalibrationPlan.AllIls)
            {
                var r = new Rung(wn, il);
                int blocks = Math.Max(1, (int)Math.Ceiling(4.0 / Ms110dBurstPlan.ForBlocks(r.Settings, 1).BlockSeconds));
                byte[] payload = PayloadBits(r, blocks, 12345);
                float[] audio = new Ms110dModulator(r.Settings).Modulate(payload);
                plain[r.Name] = MeanSquare(audio);
                ssb[r.Name] = MeanSquare(new SsbFilter(CalibrationPlan.SampleRate, CalibrationPlan.Passband).Apply(audio));
            }
        }

        return (plain, ssb);
    }

    private static double MeanSquare(ReadOnlySpan<float> x)
    {
        double sum = 0;
        foreach (float v in x)
        {
            sum += (double)v * v;
        }

        return sum / x.Length;
    }

    /// <summary>The payload bits of one burst: blocks x input bits less the 32-bit EOM.</summary>
    public static byte[] PayloadBits(Rung r, int blocks, int seed)
    {
        var random = new Random(seed);
        var bits = new byte[(blocks * r.Params.InputBits) - 32];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }

        return bits;
    }

    private sealed class Decoded
    {
        public long StartSample;
        public Ms110dLockInfo? Lock;
        public readonly Dictionary<int, byte[]> Blocks = [];
        public double? SnrDb;
        public string Reason = "none";
    }

    private sealed record Sent(int Index, Rung Rung, long Start, long End, byte[] Payload, byte[] TxBits);

    public static List<BurstRecord> Run(CalibrationPlan plan, CalJob job)
    {
        CalPoint p = job.Point;
        CalChannel c = p.Channel;
        var channel = new ContinuousWattersonChannel(
            CalibrationPlan.SampleRate, job.ChannelSeed, p.SnrTenths / 10.0, SignalPower(c), c.Paths)
        {
            TransmitPassband = c.Ssb ? CalibrationPlan.Passband : null,
            ReceivePassband = c.Ssb ? CalibrationPlan.Passband : null,
        };

        var demod = new Ms110dDemodulator(new Ms110dDemodOptions());
        var decoded = new List<Decoded>();
        Decoded? current = null;
        demod.BurstStarted += s => current = new Decoded { StartSample = s.DataStartSample, Lock = s.Lock };
        demod.BlockDecoded += b =>
        {
            if (current is not null)
            {
                current.Blocks.TryAdd(b.Index, b.Bits);
            }
        };
        demod.BurstCompleted += b =>
        {
            if (current is null)
            {
                return;
            }

            current.SnrDb = b.SnrDb;
            current.Reason = b.Reason.ToString();
            if (b.StartSample != 0)
            {
                current.StartSample = b.StartSample;
            }

            decoded.Add(current);
            current = null;
        };

        var modulators = new Dictionary<Rung, Ms110dModulator>();
        var sent = new List<Sent>();
        long position = 0;
        var buffer = new float[Chunk];

        void Feed(ReadOnlySpan<float> input)
        {
            for (int i = 0; i < input.Length; i += Chunk)
            {
                int n = Math.Min(Chunk, input.Length - i);
                channel.Process(input.Slice(i, n), buffer);
                demod.Process(buffer.AsSpan(0, n));
            }

            position += input.Length;
        }

        void Silence(double seconds)
        {
            var zeros = new float[Chunk];
            long remaining = (long)Math.Round(seconds * CalibrationPlan.SampleRate);
            while (remaining > 0)
            {
                int n = (int)Math.Min(Chunk, remaining);
                Feed(zeros.AsSpan(0, n));
                remaining -= n;
            }
        }

        Silence(LeadInSeconds);
        int index = 0;
        foreach (Rung r in job.Bursts())
        {
            RungBurst rb = plan.Bursts[r];
            if (!modulators.TryGetValue(r, out Ms110dModulator? tx))
            {
                tx = new Ms110dModulator(r.Settings);
                modulators[r] = tx;
            }

            byte[] payload = PayloadBits(r, rb.Blocks, CalibrationPlan.Mix(job.ChannelSeed, index));
            byte[] txBits = Ms110dFraming.BuildTxBits(payload, appendEom: true, r.Params.InputBits);
            float[] audio = tx.Modulate(payload);
            float gain = BurstGain(c, r);
            for (int i = 0; i < audio.Length; i++)
            {
                audio[i] *= gain;
            }

            long start = position;
            Feed(audio);
            sent.Add(new Sent(index, r, start, position, payload, txBits));
            Silence(plan.Settings.GapMs / 1000.0);
            index++;
        }

        Silence(TailSeconds);

        // Attribute each decoded burst to the transmitted burst its data started in.
        long latency = channel.LatencySamples;
        var match = new Decoded?[sent.Count];
        foreach (Decoded d in decoded)
        {
            if (d.Lock is null)
            {
                continue;
            }

            int k = sent.FindIndex(s => d.StartSample >= s.Start + latency && d.StartSample < s.End + latency);
            if (k >= 0 && match[k] is null)
            {
                match[k] = d;
            }
        }

        var records = new List<BurstRecord>(sent.Count);
        for (int k = 0; k < sent.Count; k++)
        {
            records.Add(Score(plan, job, sent[k], k > 0 ? sent[k - 1].Rung : null, match[k]));
        }

        return records;
    }

    private static BurstRecord Score(CalibrationPlan plan, CalJob job, Sent s, Rung? previous, Decoded? d)
    {
        RungBurst rb = plan.Bursts[s.Rung];
        int inputBits = s.Rung.Params.InputBits;
        bool locked = d?.Lock is { } l && l.WaveformNumber == s.Rung.Wn && l.Interleaver == s.Rung.Il;
        var rx = new byte[rb.Blocks * inputBits];
        Array.Fill(rx, (byte)2); // 2: not received
        int blockErrors = 0;
        for (int i = 0; i < rb.Blocks; i++)
        {
            bool ok = false;
            if (locked && d!.Blocks.TryGetValue(i, out byte[]? bits) && bits.Length == inputBits)
            {
                Array.Copy(bits, 0, rx, i * inputBits, inputBits);
                ok = bits.AsSpan().SequenceEqual(s.TxBits.AsSpan(i * inputBits, inputBits));
            }

            blockErrors += ok ? 0 : 1;
        }

        int dpduErrors = 0;
        foreach ((int start, int length) in rb.Dpdus)
        {
            ReadOnlySpan<byte> got = rx.AsSpan(start * 8, length * 8);
            ReadOnlySpan<byte> want = s.Payload.AsSpan(start * 8, length * 8);
            dpduErrors += got.SequenceEqual(want) ? 0 : 1;
        }

        int? estimate = locked && d!.SnrDb is { } snr ? (int)Math.Round(snr * 10, MidpointRounding.AwayFromZero) : null;
        string reason = d is null ? "missed" : locked ? d.Reason : "wrong_lock";
        return new BurstRecord(job.Id, job.Point.Channel.Name, job.Point.SnrTenths, job.Segment, s.Index, s.Rung, previous,
            plan.Settings.GapMs, estimate, locked, reason, rb.Blocks, blockErrors, rb.Dpdus.Count, dpduErrors);
    }

    /// <summary>Writes a job's records atomically: a complete file or none, so a run killed mid-job
    /// redoes that job on resume.</summary>
    public static void Write(string path, IEnumerable<BurstRecord> records)
    {
        var sb = new StringBuilder();
        sb.Append(BurstRecord.Header).Append('\n');
        foreach (BurstRecord r in records)
        {
            sb.Append(r.ToLine()).Append('\n');
        }

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, sb.ToString());
        File.Move(tmp, path, overwrite: true);
    }
}
