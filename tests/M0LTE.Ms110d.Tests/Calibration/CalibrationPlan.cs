using System.Globalization;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests.Calibration;

/// <summary>One rung of the rate ladder: a waveform number and an interleaver.</summary>
internal sealed record Rung(int Wn, Ms110dInterleaverKind Il)
{
    /// <summary>The interleaver as the 5066 node's configuration spells it.</summary>
    public string IlName => Il switch
    {
        Ms110dInterleaverKind.UltraShort => "ultra_short",
        Ms110dInterleaverKind.Short => "short",
        Ms110dInterleaverKind.Medium => "medium",
        Ms110dInterleaverKind.Long => "long",
        _ => throw new ArgumentOutOfRangeException(nameof(Il)),
    };

    /// <summary>The rung as the output tables name it, for example <c>wn6-long</c>.</summary>
    public string Name => $"wn{Wn}-{IlName}";

    public Ms110dTxSettings Settings => new()
    {
        WaveformNumber = Wn,
        Interleaver = Il,
        ConstraintLength = 7,
        PreambleSuperframes = 3,
        AppendEom = true,
        AppendEot = true,
    };

    public Ms110dInterleaverParams Params => Ms110dInterleaverParams.Get3k(Wn, Il);

    public static Ms110dInterleaverKind ParseIl(string s) => s.Trim().ToLowerInvariant() switch
    {
        "ultra_short" or "ultrashort" or "us" => Ms110dInterleaverKind.UltraShort,
        "short" or "s" => Ms110dInterleaverKind.Short,
        "medium" or "m" => Ms110dInterleaverKind.Medium,
        "long" or "l" => Ms110dInterleaverKind.Long,
        _ => throw new ArgumentException($"unknown interleaver {s}"),
    };
}

/// <summary>The burst a rung sends in the sweep: a fixed number of blocks, and the D_PDU layout
/// the 5066 planner builds over them.</summary>
internal sealed record RungBurst(Rung Rung, int Blocks, int BytesPerBlock, double Seconds, double BlockSeconds,
    double DataRateBps, IReadOnlyList<(int Start, int Length)> Dpdus)
{
    /// <summary>Payload bytes: the blocks less the 32-bit EOM in the last one.</summary>
    public int PayloadBytes => (Blocks * BytesPerBlock) - 4;
}

/// <summary>A channel of the sweep.</summary>
internal sealed record CalChannel(string Name, WattersonPath[] Paths, bool Ssb, string Prior, bool Fading);

/// <summary>One (channel, set SNR) point: the rungs it measures and the cycle of bursts it sends.</summary>
internal sealed record CalPoint(CalChannel Channel, int SnrTenths, IReadOnlyList<Rung> Rungs, IReadOnlyList<Rung> Cycle, int Cycles);

/// <summary>One unit of work: one cycle of a point's schedule through its own continuous
/// channel realisation.</summary>
internal sealed record CalJob(CalPoint Point, int Segment, int ChannelSeed, int Rotation, double SignalSeconds)
{
    public string Id => string.Create(CultureInfo.InvariantCulture,
        $"{Point.Channel.Name}_{(Point.SnrTenths < 0 ? "m" : "p")}{Math.Abs(Point.SnrTenths):0000}_c{Segment:000}");

    /// <summary>The bursts of this job in order: the point's cycle, rotated so the pair lost at the
    /// start of each realisation is a different one in each segment.</summary>
    public IEnumerable<Rung> Bursts()
    {
        int n = Point.Cycle.Count;
        for (int i = 0; i < n; i++)
        {
            yield return Point.Cycle[(i + Rotation) % n];
        }
    }
}

/// <summary>
/// The P7-5 calibration sweep plan (M0LTE.S5066 docs/core-architecture.md section 9): the rungs,
/// channels, grid, per-point schedules, burst lengths, D_PDU layouts and jobs. Everything here is
/// a pure function of the settings, so a resumed run, a shard on another machine and the merge
/// step all rebuild the same plan.
/// </summary>
internal sealed class CalibrationPlan
{
    public const int SampleRate = 9600;

    /// <summary>D_PDU overhead in bytes: a DATA-ONLY D_PDU with address size 3 (the T4 node
    /// addresses 0.0.1.1 and 0.0.1.2), header, header CRC and the 4-byte data CRC; measured with
    /// M0LTE.S5066.Core's DpduCodec.</summary>
    public const int DpduOverhead = 19;

    /// <summary>The largest segment between two of our nodes: MAX-SEGMENT 255 (1020 bytes), the
    /// value a receiver sends a variable-speed sender (ruling 9 of 2026-11-08).</summary>
    public const int MaxSegment = 1020;

    /// <summary>The longest transmission (C-0106).</summary>
    public const double MaxTransmissionSeconds = 127.5;

    /// <summary>Base burst length in seconds; rungs whose blocks are long get longer bursts so every
    /// rung meets its block and time minimums after the same number of cycles.</summary>
    public const double BaseBurstSeconds = 12;

    /// <summary>The sizing the burst lengths are balanced for (the calibration plan's minimums), fixed
    /// whatever profile runs, so the burst length per rung never changes between runs.</summary>
    private const int SizingMinBlocks = 300;
    private const double SizingMinSeconds = 20 * 60;

    /// <summary>Ladder offsets every point's schedule pairs: b within this many rungs of a, either
    /// way. The plan needs b from four below to two above; the symmetric range keeps the pair graph
    /// balanced, so one Eulerian circuit holds every pair exactly once.</summary>
    public const int PairReach = 4;

    /// <summary>Table D-LXIV 3 kHz rows, as Ms110dMaskTests: WN to (AWGN, Poor) SNR dB.</summary>
    public static readonly IReadOnlyDictionary<int, (double Awgn, double Poor)> MaskSnr = new Dictionary<int, (double, double)>
    {
        [1] = (-3, 3), [2] = (0, 5), [3] = (3, 7), [4] = (5, 10), [5] = (6, 11),
        [6] = (9, 14), [7] = (13, 19), [8] = (16, 23), [13] = (6, 11),
    };

    public static readonly int[] AllWns = [1, 2, 3, 4, 5, 6, 7, 8, 13];

    public static readonly Ms110dInterleaverKind[] AllIls =
        [Ms110dInterleaverKind.UltraShort, Ms110dInterleaverKind.Short, Ms110dInterleaverKind.Medium, Ms110dInterleaverKind.Long];

    public static readonly SsbPassband Passband = new(400, 2600);

    public static readonly CalChannel[] AllChannels =
    [
        new("AWGN", [], Ssb: false, Prior: "awgn", Fading: false),
        new("Poor", WattersonChannel.Poor, Ssb: false, Prior: "poor", Fading: true),
        new("Moderate", WattersonChannel.Moderate, Ssb: false, Prior: "moderate", Fading: true),
        new("AWGN-SSB", [], Ssb: true, Prior: "awgn", Fading: false),
        new("Poor-SSB", WattersonChannel.Poor, Ssb: true, Prior: "poor", Fading: true),
    ];

    public CalibrationPlan(CalibrationSettings settings)
    {
        Settings = settings;
        Rungs = settings.Wns.SelectMany(wn => settings.Ils.Select(il => new Rung(wn, il))).ToList();
        Ladder = AllWns.SelectMany(wn => AllIls.Select(il => new Rung(wn, il)))
            .OrderBy(r => PriorPoorDb(r)).ThenBy(r => DataRate(r)).ThenBy(r => r.Wn).ThenBy(r => r.Il).ToList();
        Bursts = Rungs.ToDictionary(r => r, BuildBurst);
        Points = settings.Channels.SelectMany(BuildPoints).ToList();
        Jobs = Points.SelectMany(BuildJobs).ToList();
    }

    public CalibrationSettings Settings { get; }

    /// <summary>The rungs measured.</summary>
    public IReadOnlyList<Rung> Rungs { get; }

    /// <summary>All 36 rungs in the prior ladder order (Poor transition, then data rate): the
    /// order "ladder neighbours" and the pair offsets refer to.</summary>
    public IReadOnlyList<Rung> Ladder { get; }

    public IReadOnlyDictionary<Rung, RungBurst> Bursts { get; }

    public IReadOnlyList<CalPoint> Points { get; }

    public IReadOnlyList<CalJob> Jobs { get; }

    public double SignalHours => Jobs.Sum(j => j.SignalSeconds) / 3600.0;

    public static double DataRate(Rung r)
    {
        Ms110dBurstPlan p = Ms110dBurstPlan.ForBlocks(r.Settings, 1);
        return p.InputBitsPerBlock / p.BlockSeconds;
    }

    // ------------------------------------------------------------------ priors

    /// <summary>A prior guess of the set SNR (dB) at which the rung's block errors fall through
    /// their transition on Poor: the D-LXIV Poor figure, shifted by interleaver (a longer
    /// interleaver rides out fades better; the 0.6.0 SNR sweep saw about 7% block errors at the
    /// Poor mask point on Short and none on Long). Only orders the ladder and places the
    /// windows; the measurement decides the table.</summary>
    public static double PriorPoorDb(Rung r) => MaskSnr[r.Wn].Poor + r.Il switch
    {
        Ms110dInterleaverKind.UltraShort => 3,
        Ms110dInterleaverKind.Short => 0,
        Ms110dInterleaverKind.Medium => -1.5,
        _ => -3,
    };

    /// <summary>The set SNR window (dB) a rung is measured over on a channel: from where its block
    /// errors are expected near 100% to where they should vanish, with margin, on the 1.5 dB grid.
    /// AWGN: the D-LXIV AWGN figure (no block errors were seen at or above it in the 0.6.0 sweep)
    /// from 4.5 dB below to 3 dB above. Fading: the Poor prior from 6 dB below to 4.5 dB above,
    /// Moderate 1.5 dB easier. The SSB band-pass adds 1.5 dB at the top.</summary>
    public static (int LoTenths, int HiTenths) Window(Rung r, CalChannel c)
    {
        double lo, hi;
        if (c.Prior == "awgn")
        {
            double t = MaskSnr[r.Wn].Awgn + (r.Il == Ms110dInterleaverKind.UltraShort ? 1 : 0);
            lo = t - 4.5;
            hi = t + 3;
        }
        else
        {
            double t = PriorPoorDb(r) - (c.Prior == "moderate" ? 1.5 : 0);
            lo = t - 6;
            hi = t + 4.5;
        }

        if (c.Ssb)
        {
            hi += 1.5;
        }

        return ((int)(Math.Floor(lo / 1.5) * 15), (int)(Math.Ceiling(hi / 1.5) * 15));
    }

    // ------------------------------------------------------------------ bursts and layout

    private static RungBurst BuildBurst(Rung r)
    {
        Ms110dBurstPlan one = Ms110dBurstPlan.ForBlocks(r.Settings, 1);
        double tb = one.BlockSeconds;
        double target = BaseBurstSeconds * Math.Max(1.0, SizingMinBlocks * tb / SizingMinSeconds);
        int blocks = Math.Max(1, (int)Math.Ceiling((target - 1e-9) / tb));
        // Room for at least one full-size D_PDU, as a bulk transfer would send (WN 1 and 2).
        while ((blocks * one.BytesPerBlock) - 4 < DpduOverhead + MaxSegment)
        {
            blocks++;
        }

        while (blocks > 1 && Ms110dBurstPlan.ForBlocks(r.Settings, blocks).Seconds > MaxTransmissionSeconds)
        {
            blocks--;
        }

        Ms110dBurstPlan p = Ms110dBurstPlan.ForBlocks(r.Settings, blocks);
        return new RungBurst(r, blocks, p.BytesPerBlock, p.Seconds, tb, p.InputBitsPerBlock / tb,
            Layout(p.BytesPerBlock, (blocks * p.BytesPerBlock) - 4));
    }

    /// <summary>
    /// The D_PDUs a bulk ARQ transmission to a known Ed4 peer lays over a burst, as M0LTE.S5066's
    /// TransmitQueue builds them (C-0553, C-0870 to C-0872, interp C-1155): one long C_PDU cut into
    /// segments of at most <see cref="MaxSegment"/> bytes, back to back from the start of the burst;
    /// a segment that would run past the capacity is cut to end at it, and one that would end inside
    /// a later block than it starts in is cut to end at the last block boundary it crosses, either
    /// only when a byte is left. So D_PDUs are block aligned where a block holds one, and straddle
    /// whole blocks where it does not (WN 1 Ultra Short, 3-byte blocks). The tail too short for a
    /// D_PDU (the planner's fill) is not counted.
    /// </summary>
    /// <param name="bytesPerBlock">Bytes per interleaver block.</param>
    /// <param name="capacity">Payload bytes of the burst.</param>
    /// <returns>(start byte, length in bytes) of each D_PDU.</returns>
    public static List<(int Start, int Length)> Layout(int bytesPerBlock, int capacity)
    {
        var result = new List<(int, int)>();
        long pos = 0;
        while (pos + DpduOverhead + 1 <= capacity)
        {
            int full = MaxSegment;
            long end = pos + DpduOverhead + full;
            long boundary = -1;
            if (end > capacity)
            {
                boundary = capacity;
            }
            else if (end % bytesPerBlock != 0 && (end - 1) / bytesPerBlock > pos / bytesPerBlock)
            {
                boundary = end / bytesPerBlock * bytesPerBlock;
            }

            int segment = full;
            if (boundary >= 0 && boundary - pos - DpduOverhead >= 1)
            {
                segment = (int)(boundary - pos - DpduOverhead);
            }

            if (pos + DpduOverhead + segment > capacity)
            {
                break;
            }

            result.Add(((int)pos, DpduOverhead + segment));
            pos += DpduOverhead + segment;
        }

        return result;
    }

    // ------------------------------------------------------------------ points and schedules

    private IEnumerable<CalPoint> BuildPoints(CalChannel c)
    {
        var windows = Rungs.ToDictionary(r => r, r => Window(r, c));
        if (windows.Count == 0)
        {
            yield break;
        }

        int lo = windows.Values.Min(w => w.LoTenths);
        int hi = windows.Values.Max(w => w.HiTenths);
        var ladderIndex = Ladder.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i);
        var points = new List<CalPoint>();
        for (int s = lo; s <= hi; s += 15)
        {
            var core = Rungs.Where(r => windows[r].LoTenths <= s && s <= windows[r].HiTenths).ToList();
            if (core.Count == 0)
            {
                continue;
            }

            // The rungs whose transition covers this SNR, plus their ladder neighbours (two each way).
            var set = new HashSet<Rung>(core);
            foreach (Rung r in core)
            {
                int i = ladderIndex[r];
                for (int d = -2; d <= 2; d++)
                {
                    if (i + d >= 0 && i + d < Ladder.Count && Rungs.Contains(Ladder[i + d]))
                    {
                        set.Add(Ladder[i + d]);
                    }
                }
            }

            List<Rung> members = set.OrderBy(r => ladderIndex[r]).ToList();
            List<Rung> cycle = Schedule(members, ladderIndex);
            points.Add(new CalPoint(c, s, members, cycle, CyclesNeeded(c, cycle)));
        }

        if (Settings.PointsPerChannel > 0 && points.Count > Settings.PointsPerChannel)
        {
            // Keep the middle of the grid (the smoke profile).
            int skip = (points.Count - Settings.PointsPerChannel) / 2;
            points = points.Skip(skip).Take(Settings.PointsPerChannel).ToList();
        }

        foreach (CalPoint p in points)
        {
            yield return p;
        }
    }

    /// <summary>
    /// The burst order of one cycle: an Eulerian circuit of the pair graph, whose edges are every
    /// (a, b) with b within <see cref="PairReach"/> ladder places of a (a itself included). The
    /// graph is symmetric, so every node has in-degree equal to out-degree and one circuit uses
    /// every edge exactly once: each needed pair follows once per cycle, where a plain cycle of the
    /// rungs would give each only one successor. Rungs near the ends of the set have fewer
    /// neighbours; extra self-pairs bring every rung to the same number of bursts per cycle.
    /// Rungs more than the reach apart in the ladder fall into separate components, whose circuits
    /// are sent one after the other.
    /// </summary>
    public static List<Rung> Schedule(IReadOnlyList<Rung> members, IReadOnlyDictionary<Rung, int> ladderIndex)
    {
        int n = members.Count;
        var adj = new List<int>[n];
        for (int a = 0; a < n; a++)
        {
            adj[a] = [];
            for (int b = 0; b < n; b++)
            {
                if (Math.Abs(ladderIndex[members[a]] - ladderIndex[members[b]]) <= PairReach)
                {
                    adj[a].Add(b);
                }
            }
        }

        int maxDegree = adj.Max(x => x.Count);
        for (int a = 0; a < n; a++)
        {
            // Spread the balancing self-pairs through the node's list rather than bunching them.
            int extra = maxDegree - adj[a].Count;
            for (int e = 0; e < extra; e++)
            {
                adj[a].Insert(Math.Min(adj[a].Count, (e * 2) + 1), a);
            }
        }

        var used = new int[n];
        var visited = new bool[n];
        var circuits = new List<List<int>>();
        for (int start = 0; start < n; start++)
        {
            if (visited[start])
            {
                continue;
            }

            // Hierholzer, iterative, deterministic.
            var stack = new Stack<int>();
            var circuit = new List<int>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                int v = stack.Peek();
                visited[v] = true;
                if (used[v] < adj[v].Count)
                {
                    stack.Push(adj[v][used[v]++]);
                }
                else
                {
                    circuit.Add(stack.Pop());
                }
            }

            circuit.Reverse();
            circuits.Add(circuit);
        }

        // A circuit returns to its start. With one component, drop the repeated start so cycles
        // chain into each other. With several, keep it, so each component's closing pair is still
        // sent; the components then join through one extra pair each.
        if (circuits.Count == 1)
        {
            circuits[0].RemoveAt(circuits[0].Count - 1);
        }

        return circuits.SelectMany(c => c.Select(i => members[i])).ToList();
    }

    private int CyclesNeeded(CalChannel c, IReadOnlyList<Rung> cycle)
    {
        int cycles = 1;
        foreach (IGrouping<Rung, Rung> g in cycle.GroupBy(r => r))
        {
            RungBurst b = Bursts[g.Key];
            int visits = g.Count();
            int forBlocks = (int)Math.Ceiling(Settings.MinBlocks / (double)(visits * b.Blocks));
            int forTime = c.Fading ? (int)Math.Ceiling(Settings.MinMinutesFading * 60 / (visits * b.Seconds)) : 0;
            cycles = Math.Max(cycles, Math.Max(forBlocks, forTime));
        }

        return Settings.MaxCycles > 0 ? Math.Min(cycles, Settings.MaxCycles) : cycles;
    }

    private IEnumerable<CalJob> BuildJobs(CalPoint p)
    {
        double seconds = p.Cycle.Sum(r => Bursts[r].Seconds + (Settings.GapMs / 1000.0)) + CalibrationRunner.LeadInSeconds
                         + CalibrationRunner.TailSeconds;
        int channelIndex = Array.FindIndex(AllChannels, c => c.Name == p.Channel.Name);
        for (int j = 0; j < p.Cycles; j++)
        {
            int seed = Mix(channelIndex, p.SnrTenths, j, Settings.SeedOffset);
            int rotation = (int)((long)j * 7919 % Math.Max(1, p.Cycle.Count));
            yield return new CalJob(p, j, seed, rotation, seconds);
        }
    }

    /// <summary>A deterministic 31-bit seed from integers (SplitMix64 finaliser).</summary>
    public static int Mix(params int[] values)
    {
        ulong h = 0x9E3779B97F4A7C15UL;
        foreach (int v in values)
        {
            h ^= (ulong)(uint)v;
            h += 0x9E3779B97F4A7C15UL;
            h = (h ^ (h >> 30)) * 0xBF58476D1CE4E5B9UL;
            h = (h ^ (h >> 27)) * 0x94D049BB133111EBUL;
            h ^= h >> 31;
        }

        return (int)(h & 0x7FFFFFFF);
    }
}

/// <summary>The sweep's settings, from the environment (the same pattern as the SNR and mask
/// sweeps). See docs/calibration-sweep.md.</summary>
internal sealed record CalibrationSettings
{
    public required string Profile { get; init; }
    public required IReadOnlyList<CalChannel> Channels { get; init; }
    public required IReadOnlyList<int> Wns { get; init; }
    public required IReadOnlyList<Ms110dInterleaverKind> Ils { get; init; }
    public int MinBlocks { get; init; } = 300;
    public double MinMinutesFading { get; init; } = 20;
    public int MaxCycles { get; init; }
    public int PointsPerChannel { get; init; }
    public int GapMs { get; init; } = 6000;
    public int SeedOffset { get; init; }

    public static CalibrationSettings FromEnvironment()
    {
        string profile = Env("MS110D_CAL_PROFILE") ?? "full";
        CalibrationSettings s = profile switch
        {
            "full" => new CalibrationSettings
            {
                Profile = profile,
                Channels = CalibrationPlan.AllChannels,
                Wns = CalibrationPlan.AllWns,
                Ils = CalibrationPlan.AllIls,
            },
            "smoke" => new CalibrationSettings
            {
                Profile = profile,
                Channels = CalibrationPlan.AllChannels.Where(c => c.Name is "AWGN" or "Poor-SSB").ToList(),
                Wns = [3, 6],
                Ils = [Ms110dInterleaverKind.Short, Ms110dInterleaverKind.Long],
                MinBlocks = 1,
                MinMinutesFading = 0,
                MaxCycles = 1,
                PointsPerChannel = 2,
            },
            _ => throw new ArgumentException($"MS110D_CAL_PROFILE must be full or smoke, not {profile}"),
        };

        if (Env("MS110D_CAL_CHANNELS") is { } ch)
        {
            var names = ch.Split(',').Select(x => x.Trim()).ToList();
            s = s with { Channels = names.Select(n => CalibrationPlan.AllChannels.Single(c => c.Name == n)).ToList() };
        }

        if (Env("MS110D_CAL_WN") is { } wn)
        {
            s = s with { Wns = wn.Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList() };
        }

        if (Env("MS110D_CAL_IL") is { } il)
        {
            s = s with { Ils = il.Split(',').Select(Rung.ParseIl).ToList() };
        }

        if (Env("MS110D_CAL_MIN_BLOCKS") is { } mb)
        {
            s = s with { MinBlocks = int.Parse(mb, CultureInfo.InvariantCulture) };
        }

        if (Env("MS110D_CAL_MIN_MINUTES") is { } mm)
        {
            s = s with { MinMinutesFading = double.Parse(mm, CultureInfo.InvariantCulture) };
        }

        if (Env("MS110D_CAL_MAX_CYCLES") is { } mc)
        {
            s = s with { MaxCycles = int.Parse(mc, CultureInfo.InvariantCulture) };
        }

        if (Env("MS110D_CAL_POINTS") is { } pts)
        {
            s = s with { PointsPerChannel = int.Parse(pts, CultureInfo.InvariantCulture) };
        }

        if (Env("MS110D_CAL_GAP_MS") is { } gap)
        {
            s = s with { GapMs = int.Parse(gap, CultureInfo.InvariantCulture) };
        }

        if (Env("MS110D_CAL_SEED_OFFSET") is { } so)
        {
            s = s with { SeedOffset = int.Parse(so, CultureInfo.InvariantCulture) };
        }

        return s;
    }

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
