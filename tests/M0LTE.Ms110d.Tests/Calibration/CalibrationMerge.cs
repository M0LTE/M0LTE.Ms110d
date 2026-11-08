using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests.Calibration;

/// <summary>
/// The sweep's description (<c>plan.json</c>, then <c>meta.json</c>) and the merge of the raw
/// per-burst records into the fixed-format aggregates of the calibration plan
/// (M0LTE.S5066 docs/core-architecture.md section 9): <c>own.tsv</c>, <c>next.tsv</c>,
/// <c>meta.json</c> and a 1% audit sample of the raw records.
/// </summary>
internal static class CalibrationMerge
{
    public const int BinTenths = 5;

    /// <summary>The modem the calibration was measured on: implementation, version and commit,
    /// from the M0LTE.Ms110d assembly the tests ran against.</summary>
    public static (string Implementation, string Version, string Commit) Modem()
    {
        string info = typeof(Ms110dDemodulator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? "unknown";
        int plus = info.IndexOf('+', StringComparison.Ordinal);
        return ("ms110d-cs", plus < 0 ? info : info[..plus], plus < 0 ? "unknown" : info[(plus + 1)..]);
    }

    /// <summary>The output directory name: the modem implementation and version
    /// (core-architecture.md section 9 design shape item 6), for example <c>ms110d-cs-0.6.1</c>.</summary>
    public static string ResultName()
    {
        (string impl, string version, _) = Modem();
        return $"{impl}-{version}";
    }

    public static string HfChannelVersion()
    {
        string info = typeof(ContinuousWattersonChannel).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        int plus = info.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? info : info[..plus];
    }

    public static int Bin(int estimateTenths) => (int)Math.Floor(estimateTenths / (double)BinTenths) * BinTenths;

    /// <summary>Everything that fixes the sweep's results, as JSON. A run writes it as
    /// <c>plan.json</c> and refuses to resume into a directory whose plan differs.</summary>
    public static JsonObject Describe(CalibrationPlan plan)
    {
        (string impl, string version, string commit) = Modem();
        CalibrationSettings s = plan.Settings;
        var powers = CalibrationRunner.RungPowers;
        var o = new JsonObject
        {
            ["format"] = "M0LTE.S5066 P7-5 modem calibration, version 1",
            ["modem"] = new JsonObject { ["implementation"] = impl, ["package"] = "M0LTE.Ms110d", ["version"] = version, ["commit"] = commit },
            ["hfchannel"] = new JsonObject { ["package"] = "M0LTE.HfChannel", ["version"] = HfChannelVersion(), ["model"] = "ContinuousWattersonChannel" },
            ["profile"] = s.Profile,
            ["sample_rate_hz"] = CalibrationPlan.SampleRate,
            ["snr"] = new JsonObject
            {
                ["reference"] = "on-air: the set SNR is the signal power at the channel input, after the transmit SSB passband where there is one (so the filter's loss does not count against the SNR), over the noise in 3000 Hz added at the receiver's antenna, before the receive passband",
                ["noise_bandwidth_hz"] = 3000,
                ["signal_power"] = Round(CalibrationRunner.SignalPower(CalibrationPlan.AllChannels[0])),
                ["signal_power_after_tx_passband"] = Round(CalibrationRunner.SignalPower(CalibrationPlan.AllChannels[3])),
                ["signal_power_is"] = "the mean over all 36 rungs of each rung's burst mean square (on air); each burst is scaled to exactly this power, so every rung meets the set SNR and every burst in one realisation shares one noise floor",
                ["rung_power"] = Map(powers.Plain),
                ["rung_power_after_tx_passband"] = Map(powers.Ssb),
            },
            ["ssb_passband"] = new JsonObject
            {
                ["low_hz_minus_6db"] = CalibrationPlan.Passband.LowHz,
                ["high_hz_minus_6db"] = CalibrationPlan.Passband.HighHz,
                ["transition_hz"] = SsbFilter.TransitionHz,
                ["applied"] = "sending radio and receiving radio, on the -SSB channels only",
            },
            ["channels"] = new JsonArray(s.Channels.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["ssb"] = c.Ssb,
                ["paths"] = new JsonArray(c.Paths.Select(p => (JsonNode)new JsonObject
                {
                    ["delay_ms"] = p.DelayMs, ["fading"] = p.Fading, ["doppler_spread_hz"] = p.DopplerSpreadHz, ["doppler_shift_hz"] = p.DopplerShiftHz,
                }).ToArray()),
            }).ToArray()),
            ["seeds"] = new JsonObject
            {
                ["seed_offset"] = s.SeedOffset,
                ["channel_seed"] = "SplitMix64 finaliser over (channel index in AWGN, Poor, Moderate, AWGN-SSB, Poor-SSB; set SNR in tenths; segment; seed offset), one realisation per job",
                ["payload_seed"] = "SplitMix64 finaliser over (channel seed, burst index in the job); bits from System.Random(seed).Next(2)",
                ["power_payload_seed"] = 12345,
            },
            ["gap_ms"] = s.GapMs,
            ["lead_in_s"] = CalibrationRunner.LeadInSeconds,
            ["tail_s"] = CalibrationRunner.TailSeconds,
            ["modem_settings"] = "constraint length 7, 3 preamble super-frames, EOM and EOT appended, default demodulator options; one demodulator per job, running throughout",
            ["dpdu_layout"] = new JsonObject
            {
                ["overhead_bytes"] = CalibrationPlan.DpduOverhead,
                ["overhead_is"] = "DATA-ONLY, address size 3 (nodes 0.0.1.1 and 0.0.1.2), header, header CRC and data CRC, from M0LTE.S5066.Core DpduCodec",
                ["max_segment_bytes"] = CalibrationPlan.MaxSegment,
                ["rule"] = "one long ARQ C_PDU to a known Ed4 peer, as M0LTE.S5066 TransmitQueue cuts it (C-0553, C-0870 to C-0872, interp C-1155): block aligned where a block holds a D_PDU, straddling whole blocks where it does not; the fill tail is not counted. A D_PDU is in error when any of its bits is wrong or was not received",
            },
            ["estimate"] = new JsonObject
            {
                ["source"] = "Ms110dBurst.SnrDb of the decoded burst (the burst estimate), rounded to tenths of a dB",
                ["bin_width_tenths"] = BinTenths,
                ["bin_is"] = "the lower edge, floor(estimate / 5) * 5 tenths; - when the burst gave no estimate (not detected, wrong waveform locked, or no probe)",
            },
            ["grid"] = new JsonObject
            {
                ["step_tenths"] = 15,
                ["min_blocks_per_rung_and_point"] = s.MinBlocks,
                ["min_minutes_per_rung_and_fading_point"] = s.MinMinutesFading,
                ["max_cycles"] = s.MaxCycles,
                ["points_per_channel_limit"] = s.PointsPerChannel,
                ["windows"] = "per rung and channel, CalibrationPlan.Window: AWGN from the D-LXIV AWGN SNR (+1 dB Ultra Short) -4.5 to +3 dB; Poor from the Poor prior -6 to +4.5 dB, Moderate 1.5 dB lower; SSB +1.5 dB at the top; each point adds the ladder neighbours (two each way) of the rungs whose window covers it",
            },
            ["ladder_prior"] = new JsonArray(plan.Ladder.Select(r => (JsonNode)r.Name).ToArray()),
            ["ladder_prior_is"] = "the D-LXIV Poor SNR shifted by interleaver (Ultra Short +3, Short 0, Medium -1.5, Long -3 dB), then data rate; orders the pairs and neighbours only",
            ["pair_reach"] = CalibrationPlan.PairReach,
            ["pairs_are"] = "every (a, b) with b within pair_reach places of a in ladder_prior, a itself included, both members of the point; each once per cycle (an Eulerian circuit), every rung the same number of bursts per cycle. Pairs further apart: from own.tsv by set SNR (the fading decorrelates in well under a second, so across the gap a burst's channel is independent of the one before; next.tsv carries the set SNR so near pairs can check this)",
            ["tables"] = new JsonObject
            {
                ["own.tsv"] = "per channel, rung, set SNR and estimate bin of the burst itself: bursts, blocks, block errors, D_PDUs, D_PDU errors, clean bursts (no D_PDU error) and half-failed bursts (D_PDU errors x 2 > D_PDUs, the corpus-format 6.2 halving test)",
                ["next.tsv"] = "per channel, rung a, rung b, set SNR, gap and estimate bin on a: pairs (a burst on b straight after one on a), then the burst on b's D_PDUs, D_PDU errors, blocks, block errors, clean bursts and half-failed bursts. Sum over set SNR for the plan's columns",
                ["no_estimate_bin"] = "-",
            },
            ["rungs"] = new JsonArray(plan.Rungs.Select(r =>
            {
                RungBurst b = plan.Bursts[r];
                return (JsonNode)new JsonObject
                {
                    ["rung"] = r.Name,
                    ["data_rate_bps"] = Math.Round(b.DataRateBps, 1),
                    ["bytes_per_block"] = b.BytesPerBlock,
                    ["block_s"] = Math.Round(b.BlockSeconds, 4),
                    ["blocks_per_burst"] = b.Blocks,
                    ["burst_s"] = Math.Round(b.Seconds, 4),
                    ["payload_bytes"] = b.PayloadBytes,
                    ["dpdu_bytes"] = new JsonArray(b.Dpdus.Select(d => (JsonNode)d.Length).ToArray()),
                };
            }).ToArray()),
            ["points"] = new JsonArray(plan.Points.Select(p => (JsonNode)new JsonObject
            {
                ["channel"] = p.Channel.Name,
                ["set_snr_tenth"] = p.SnrTenths,
                ["cycles"] = p.Cycles,
                ["rungs"] = new JsonArray(p.Rungs.Select(r => (JsonNode)r.Name).ToArray()),
                ["cycle"] = string.Join(' ', p.Cycle.Select(r => r.Name)),
            }).ToArray()),
            ["jobs"] = plan.Jobs.Count,
            ["signal_hours"] = Math.Round(plan.SignalHours, 2),
        };
        return o;
    }

    private static double Round(double v) => Math.Round(v, 8);

    private static JsonObject Map(IReadOnlyDictionary<string, double> m)
    {
        var o = new JsonObject();
        foreach ((string k, double v) in m)
        {
            o[k] = Round(v);
        }

        return o;
    }

    public static string ToJson(JsonNode node) =>
        node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>Merges every raw job file under <paramref name="root"/> into the aggregates.</summary>
    /// <returns>The result directory and the ids of planned jobs with no raw file.</returns>
    public static (string Directory, List<string> Missing) Merge(CalibrationPlan plan, string root)
    {
        string rawDir = Path.Combine(root, "raw");
        var byJob = new Dictionary<string, List<BurstRecord>>(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (CalJob job in plan.Jobs)
        {
            string path = Path.Combine(rawDir, job.Id + ".tsv");
            if (!File.Exists(path))
            {
                missing.Add(job.Id);
                continue;
            }

            byJob[job.Id] = File.ReadLines(path).Skip(1).Where(l => l.Length > 0).Select(BurstRecord.Parse)
                .OrderBy(r => r.Index).ToList();
        }

        var channelOrder = CalibrationPlan.AllChannels.Select((c, i) => (c.Name, i)).ToDictionary(x => x.Name, x => x.i);
        var rungOrder = plan.Ladder.Select((r, i) => (r.Name, i)).ToDictionary(x => x.Name, x => x.i);

        // own.tsv: channel, rung, set SNR, estimate bin.
        var own = new SortedDictionary<(int, int, int, int), long[]>();
        // next.tsv: channel, rung a, rung b, set SNR, gap, bin of the estimate on a.
        var next = new SortedDictionary<(int, int, int, int, int, int), long[]>();
        var audit = new List<BurstRecord>();
        foreach (List<BurstRecord> records in byJob.Values)
        {
            for (int k = 0; k < records.Count; k++)
            {
                BurstRecord r = records[k];
                int bin = r.EstimateTenths is { } e ? Bin(e) : int.MinValue;
                var key = (channelOrder[r.Channel], rungOrder[r.Rung.Name], r.SnrTenths, bin);
                if (!own.TryGetValue(key, out long[]? a))
                {
                    own[key] = a = new long[7];
                }

                a[0]++;
                a[1] += r.Blocks;
                a[2] += r.BlockErrors;
                a[3] += r.Dpdus;
                a[4] += r.DpduErrors;
                a[5] += r.DpduErrors == 0 ? 1 : 0;
                a[6] += (long)r.DpduErrors * 2 > r.Dpdus ? 1 : 0;

                if (k > 0 && records[k - 1].Index == r.Index - 1)
                {
                    BurstRecord prev = records[k - 1];
                    int prevBin = prev.EstimateTenths is { } pe ? Bin(pe) : int.MinValue;
                    var nkey = (channelOrder[r.Channel], rungOrder[prev.Rung.Name], rungOrder[r.Rung.Name], r.SnrTenths, r.GapMs, prevBin);
                    if (!next.TryGetValue(nkey, out long[]? b))
                    {
                        next[nkey] = b = new long[7];
                    }

                    b[0]++;
                    b[1] += r.Dpdus;
                    b[2] += r.DpduErrors;
                    b[3] += r.Blocks;
                    b[4] += r.BlockErrors;
                    b[5] += r.DpduErrors == 0 ? 1 : 0;
                    b[6] += (long)r.DpduErrors * 2 > r.Dpdus ? 1 : 0;
                }

                if (Fnv(r.Job, r.Index) % 100 == 0)
                {
                    audit.Add(r);
                }
            }
        }

        string channelName(int i) => CalibrationPlan.AllChannels[i].Name;
        string rungName(int i) => plan.Ladder[i].Name;
        string binText(int b) => b == int.MinValue ? "-" : b.ToString(CultureInfo.InvariantCulture);

        string dir = Path.Combine(root, ResultName());
        Directory.CreateDirectory(dir);

        var sb = new StringBuilder("channel\trung\tset_snr_tenth\test_bin_tenth\tbursts\tblocks\tblock_errors\tdpdus\tdpdu_errors\tclean_bursts\thalf_failed_bursts\n");
        foreach (((int c, int r, int snr, int bin), long[] v) in own)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{channelName(c)}\t{rungName(r)}\t{snr}\t{binText(bin)}\t{v[0]}\t{v[1]}\t{v[2]}\t{v[3]}\t{v[4]}\t{v[5]}\t{v[6]}\n");
        }

        File.WriteAllText(Path.Combine(dir, "own.tsv"), sb.ToString());

        sb = new StringBuilder("channel\trung_a\trung_b\tset_snr_tenth\tgap_ms\test_bin_a_tenth\tpairs\tdpdus\tdpdu_errors\tblocks\tblock_errors\tclean_bursts\thalf_failed_bursts\n");
        foreach (((int c, int ra, int rb, int snr, int gap, int bin), long[] v) in next)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"{channelName(c)}\t{rungName(ra)}\t{rungName(rb)}\t{snr}\t{gap}\t{binText(bin)}\t{v[0]}\t{v[1]}\t{v[2]}\t{v[3]}\t{v[4]}\t{v[5]}\t{v[6]}\n");
        }

        File.WriteAllText(Path.Combine(dir, "next.tsv"), sb.ToString());

        sb = new StringBuilder(BurstRecord.Header).Append('\n');
        foreach (BurstRecord r in audit.OrderBy(x => x.Job, StringComparer.Ordinal).ThenBy(x => x.Index))
        {
            sb.Append(r.ToLine()).Append('\n');
        }

        File.WriteAllText(Path.Combine(dir, "audit-sample.tsv"), sb.ToString());

        JsonObject meta = Describe(plan);
        meta["merge"] = new JsonObject
        {
            ["complete"] = missing.Count == 0,
            ["jobs_merged"] = byJob.Count,
            ["missing_jobs"] = new JsonArray(missing.Select(m => (JsonNode)m).ToArray()),
            ["bursts"] = byJob.Values.Sum(v => v.Count),
            ["audit_sample"] = "audit-sample.tsv: the raw records whose FNV-1a hash of (job, index) is 0 mod 100",
        };
        File.WriteAllText(Path.Combine(dir, "meta.json"), ToJson(meta));
        return (dir, missing);
    }

    private static uint Fnv(string job, int index)
    {
        uint h = 2166136261;
        foreach (char ch in job + "#" + index.ToString(CultureInfo.InvariantCulture))
        {
            h ^= ch;
            h *= 16777619;
        }

        return h;
    }
}
