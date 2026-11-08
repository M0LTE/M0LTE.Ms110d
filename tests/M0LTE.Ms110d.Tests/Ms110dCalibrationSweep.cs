using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using M0LTE.Ms110d.Tests.Calibration;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// The 5066 rate-ladder calibration sweep (M0LTE.S5066 phase 7 item P7-5; runbook in
/// docs/calibration-sweep.md). Block and D_PDU errors of every rung against the burst SNR
/// estimate, own burst and next burst, through M0LTE.HfChannel's continuous Watterson channel.
/// The heavy tests run only behind environment variables, as the SNR and mask sweeps do:
/// <c>MS110D_CAL_SWEEP=1</c> runs the sweep (resumable, shardable), <c>MS110D_CAL_MERGE=1</c>
/// merges the raw records into the aggregates, <c>MS110D_CAL_PLAN=1</c> prints the plan's size.
/// The plan-logic tests here run in CI.
/// </summary>
public class Ms110dCalibrationSweep(ITestOutputHelper output)
{
    private void Report(string line)
    {
        output.WriteLine(line);
        Console.Error.WriteLine(line);
    }

    private static string OutRoot()
    {
        string root = Environment.GetEnvironmentVariable("MS110D_CAL_OUT") is { Length: > 0 } o ? o : "/tmp/ms110d-cal";
        if (!Path.IsPathRooted(root))
        {
            throw new ArgumentException("MS110D_CAL_OUT must be an absolute path (the test host runs from its output directory)");
        }

        return root;
    }

    private static int Workers() =>
        int.TryParse(Environment.GetEnvironmentVariable("MS110D_MASK_WORKERS"), out int w) ? Math.Max(1, w) : Environment.ProcessorCount;

    private static (int Index, int Count) Shard()
    {
        string? s = Environment.GetEnvironmentVariable("MS110D_CAL_SHARD");
        if (string.IsNullOrEmpty(s))
        {
            return (0, 1);
        }

        string[] parts = s.Split('/');
        int i = int.Parse(parts[0], CultureInfo.InvariantCulture), n = int.Parse(parts[1], CultureInfo.InvariantCulture);
        if (n < 1 || i < 0 || i >= n)
        {
            throw new ArgumentException("MS110D_CAL_SHARD must be i/n with 0 <= i < n");
        }

        return (i, n);
    }

    // ---------------------------------------------------------------- gated runs

    [Fact]
    public void Plan()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_CAL_PLAN") != "1",
            "set MS110D_CAL_PLAN=1 to print the calibration sweep's plan");
        var plan = new CalibrationPlan(CalibrationSettings.FromEnvironment());
        foreach (IGrouping<string, CalPoint> g in plan.Points.GroupBy(p => p.Channel.Name))
        {
            Report(string.Create(CultureInfo.InvariantCulture,
                $"[cal] {g.Key}: {g.Count()} points, {g.Min(p => p.SnrTenths) / 10.0:F1} to {g.Max(p => p.SnrTenths) / 10.0:F1} dB, " +
                $"{g.Sum(p => p.Cycles)} jobs, {plan.Jobs.Where(j => j.Point.Channel.Name == g.Key).Sum(j => j.SignalSeconds) / 3600:F1} signal-hours"));
        }

        foreach (CalPoint p in plan.Points)
        {
            Report(string.Create(CultureInfo.InvariantCulture,
                $"[cal]   {p.Channel.Name} {p.SnrTenths / 10.0:F1} dB: {p.Rungs.Count} rungs, {p.Cycle.Count} bursts a cycle, {p.Cycles} cycles"));
        }

        foreach (RungBurst b in plan.Bursts.Values.OrderBy(b => b.Rung.Wn).ThenBy(b => b.Rung.Il))
        {
            Report(string.Create(CultureInfo.InvariantCulture,
                $"[cal]   {b.Rung.Name}: {b.Blocks} blocks of {b.BytesPerBlock} bytes, {b.Seconds:F2} s, D_PDUs {string.Join(",", b.Dpdus.Select(d => d.Length))}"));
        }

        Report(string.Create(CultureInfo.InvariantCulture,
            $"[cal] profile {plan.Settings.Profile}: {plan.Jobs.Count} jobs, {plan.SignalHours:F1} signal-hours, longest job {plan.Jobs.Max(j => j.SignalSeconds) / 3600:F2} h"));
    }

    [Fact]
    public void Run()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_CAL_SWEEP") != "1",
            "set MS110D_CAL_SWEEP=1 for the P7-5 calibration sweep (docs/calibration-sweep.md)");
        var plan = new CalibrationPlan(CalibrationSettings.FromEnvironment());
        string root = OutRoot();
        string rawDir = Path.Combine(root, "raw");
        Directory.CreateDirectory(rawDir);

        // The plan fixes the results: a resume or another shard must run the same one.
        string planPath = Path.Combine(root, "plan.json");
        string planJson = CalibrationMerge.ToJson(CalibrationMerge.Describe(plan));
        if (File.Exists(planPath))
        {
            File.ReadAllText(planPath).Should().Be(planJson,
                $"{planPath} was written by a different plan; use a new MS110D_CAL_OUT or the same settings and commit");
        }
        else
        {
            File.WriteAllText(planPath, planJson);
        }

        (int shard, int shards) = Shard();
        var todo = plan.Jobs.Where((j, i) => i % shards == shard)
            .Where(j => !File.Exists(Path.Combine(rawDir, j.Id + ".tsv")))
            .OrderByDescending(j => j.SignalSeconds).ThenBy(j => j.Id, StringComparer.Ordinal)
            .ToList();
        int workers = Workers();
        double totalSignal = todo.Sum(j => j.SignalSeconds);
        Report(string.Create(CultureInfo.InvariantCulture,
            $"[cal] {plan.Settings.Profile}: shard {shard}/{shards}, {todo.Count} jobs to run ({plan.Jobs.Count} planned), " +
            $"{totalSignal / 3600:F1} signal-hours, {workers} workers, out {root}"));

        var clock = Stopwatch.StartNew();
        int done = 0;
        double signalDone = 0, coreSeconds = 0;
        object gate = new();
        var errors = new ConcurrentBag<string>();
        string progress = Path.Combine(root, "progress.log");
        Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(todo, System.Collections.Concurrent.EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            job =>
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    List<BurstRecord> records = CalibrationRunner.Run(plan, job);
                    CalibrationRunner.Write(Path.Combine(rawDir, job.Id + ".tsv"), records);
                }
                catch (Exception e)
                {
                    errors.Add($"{job.Id}: {e.GetType().Name}: {e.Message}");
                    return;
                }

                double took = watch.Elapsed.TotalSeconds;
                string line;
                lock (gate)
                {
                    done++;
                    signalDone += job.SignalSeconds;
                    coreSeconds += took;
                    double ratio = coreSeconds / signalDone;
                    double etaHours = (totalSignal - signalDone) * ratio / Math.Min(workers, Math.Max(1, todo.Count - done)) / 3600;
                    line = string.Create(CultureInfo.InvariantCulture,
                        $"[cal] {done}/{todo.Count} {job.Id}: {job.SignalSeconds:F0} s of signal in {took:F0} s; " +
                        $"{ratio:F3} core-s per signal-s so far; elapsed {clock.Elapsed.TotalHours:F2} h, about {etaHours:F2} h to go");
                    File.AppendAllText(progress, line + "\n");
                }

                Report(line);
            });

        Report(string.Create(CultureInfo.InvariantCulture,
            $"[cal] finished {done} jobs in {clock.Elapsed.TotalHours:F2} h: {(signalDone > 0 ? coreSeconds / signalDone : 0):F3} core-s per signal-s, " +
            $"{coreSeconds / 3600:F2} core-hours"));
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Merge()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_CAL_MERGE") != "1",
            "set MS110D_CAL_MERGE=1 to merge the calibration sweep's raw records");
        var plan = new CalibrationPlan(CalibrationSettings.FromEnvironment());
        string root = OutRoot();
        string planPath = Path.Combine(root, "plan.json");
        File.Exists(planPath).Should().BeTrue($"{planPath} is written by the run");
        File.ReadAllText(planPath).Should().Be(CalibrationMerge.ToJson(CalibrationMerge.Describe(plan)),
            "the merge must run with the settings and commit of the run");
        (string dir, List<string> missing) = CalibrationMerge.Merge(plan, root);
        Report($"[cal] merged into {dir}; {plan.Jobs.Count - missing.Count}/{plan.Jobs.Count} jobs" +
               (missing.Count > 0 ? $"; missing {string.Join(" ", missing.Take(20))}{(missing.Count > 20 ? " ..." : "")}" : ""));
    }

    /// <summary>
    /// Checks the per-rung windows before the full run: for every channel and rung of the profile,
    /// a few bursts of that rung alone at the bottom and at the top of its window
    /// (<see cref="CalibrationPlan.Window"/>). The bottom should show near-total D_PDU errors (at
    /// least 90%) and the top almost none (at most 2%); a window that does not bracket the waterfall is listed as
    /// WIDEN. Writes <c>pilot.tsv</c> under <c>MS110D_CAL_OUT</c>. <c>MS110D_CAL_PILOT_DRY=1</c>
    /// prints the size only.
    /// </summary>
    [Fact]
    public void Pilot()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("MS110D_CAL_PILOT") != "1",
            "set MS110D_CAL_PILOT=1 to check the calibration windows with a short pilot");
        var plan = new CalibrationPlan(CalibrationSettings.FromEnvironment());
        var jobs = new List<(CalJob Job, Rung Rung, string Edge)>();
        foreach (CalChannel c in plan.Settings.Channels)
        {
            int channelIndex = Array.FindIndex(CalibrationPlan.AllChannels, x => x.Name == c.Name);
            foreach (Rung r in plan.Rungs)
            {
                (int lo, int hi) = CalibrationPlan.Window(r, c);
                RungBurst b = plan.Bursts[r];
                foreach ((string edge, int snr) in new[] { ("bottom", lo), ("top", hi) })
                {
                    // Bottom: enough blocks to see the rate is near total. Top: enough to see errors
                    // have (nearly) gone, and on fading two minutes of channel.
                    int minBlocks = edge == "top" && c.Fading ? 60 : 24;
                    double minSeconds = edge == "top" && c.Fading ? 120 : 0;
                    int maxBursts = edge == "top" && c.Fading ? 6 : 3;
                    int n = Math.Max((int)Math.Ceiling(minBlocks / (double)b.Blocks), (int)Math.Ceiling(minSeconds / b.Seconds));
                    n = Math.Clamp(n, 1, maxBursts);
                    var cycle = Enumerable.Repeat(r, n).ToList();
                    var point = new CalPoint(c, snr, [r], cycle, 1);
                    double seconds = cycle.Sum(x => plan.Bursts[x].Seconds + (plan.Settings.GapMs / 1000.0))
                                     + CalibrationRunner.LeadInSeconds + CalibrationRunner.TailSeconds;
                    int seed = CalibrationPlan.Mix(channelIndex, snr, r.Wn, (int)r.Il, 0x5107, plan.Settings.SeedOffset);
                    jobs.Add((new CalJob(point, edge == "bottom" ? 0 : 1, seed, 0, seconds), r, edge));
                }
            }
        }

        double hours = jobs.Sum(j => j.Job.SignalSeconds) / 3600;
        Report(string.Create(CultureInfo.InvariantCulture, $"[pilot] {jobs.Count} jobs, {hours:F1} signal-hours, {Workers()} workers"));
        if (Environment.GetEnvironmentVariable("MS110D_CAL_PILOT_DRY") == "1")
        {
            return;
        }

        string root = OutRoot();
        Directory.CreateDirectory(root);
        var results = new ConcurrentBag<string>();
        var clock = Stopwatch.StartNew();
        int done = 0;
        Parallel.ForEach(
            Partitioner.Create(jobs.OrderByDescending(j => j.Job.SignalSeconds).ToList(), EnumerablePartitionerOptions.NoBuffering),
            new ParallelOptions { MaxDegreeOfParallelism = Workers() },
            j =>
            {
                List<BurstRecord> recs = CalibrationRunner.Run(plan, j.Job);
                int blocks = recs.Sum(x => x.Blocks), blockErrors = recs.Sum(x => x.BlockErrors);
                int dpdus = recs.Sum(x => x.Dpdus), dpduErrors = recs.Sum(x => x.DpduErrors);
                var est = recs.Where(x => x.EstimateTenths is not null).Select(x => x.EstimateTenths!.Value).OrderBy(x => x).ToList();
                double fer = dpdus > 0 ? dpduErrors / (double)dpdus : 1;
                string verdict = j.Edge == "bottom" ? (fer >= 0.9 ? "ok" : "WIDEN") : (fer <= 0.02 ? "ok" : "WIDEN");
                string line = string.Create(CultureInfo.InvariantCulture,
                    $"{j.Job.Point.Channel.Name}\t{j.Rung.Name}\t{j.Edge}\t{j.Job.Point.SnrTenths}\t{recs.Count}\t{blocks}\t{blockErrors}\t" +
                    $"{dpdus}\t{dpduErrors}\t{(est.Count > 0 ? est[est.Count / 2].ToString(CultureInfo.InvariantCulture) : "-")}\t{verdict}");
                results.Add(line);
                int k = Interlocked.Increment(ref done);
                if (k % 20 == 0)
                {
                    Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[pilot] {k}/{jobs.Count} after {clock.Elapsed.TotalMinutes:F1} min"));
                }
            });

        var sorted = results.OrderBy(l => l, StringComparer.Ordinal).ToList();
        File.WriteAllText(Path.Combine(root, "pilot.tsv"),
            "channel\trung\tedge\tset_snr_tenth\tbursts\tblocks\tblock_errors\tdpdus\tdpdu_errors\test_median_tenth\tverdict\n" +
            string.Join('\n', sorted) + "\n");
        foreach (string l in sorted.Where(l => l.EndsWith("WIDEN", StringComparison.Ordinal)))
        {
            Report("[pilot] " + l);
        }

        Report(string.Create(CultureInfo.InvariantCulture,
            $"[pilot] {sorted.Count(l => l.EndsWith("WIDEN", StringComparison.Ordinal))} of {sorted.Count} edges to widen; " +
            $"{clock.Elapsed.TotalMinutes:F1} min"));
    }

    // ---------------------------------------------------------------- plan logic (CI)

    [Fact]
    public void LayoutIsBlockAlignedWhereABlockHoldsADpdu()
    {
        // WN 8 Long: 6144-byte blocks, so whole D_PDUs, then one cut to end at the block boundary.
        List<(int Start, int Length)> d = CalibrationPlan.Layout(6144, (2 * 6144) - 4);
        int full = CalibrationPlan.DpduOverhead + CalibrationPlan.MaxSegment;
        d[0].Should().Be((0, full));
        d.Should().Contain(x => x.Start + x.Length == 6144);
        d.Should().AllSatisfy(x => (x.Start / 6144).Should().Be((x.Start + x.Length - 1) / 6144));
        (d[^1].Start + d[^1].Length).Should().Be((2 * 6144) - 4);
    }

    [Fact]
    public void LayoutStraddlesWholeBlocksWhereABlockIsSmall()
    {
        // WN 1 Ultra Short: 3-byte blocks. A full D_PDU would end inside a block, so it is cut to
        // the last boundary it crosses.
        List<(int Start, int Length)> d = CalibrationPlan.Layout(3, (400 * 3) - 4);
        d[0].Start.Should().Be(0);
        (d[0].Length % 3).Should().Be(0);
        d[0].Length.Should().BeLessThanOrEqualTo(CalibrationPlan.DpduOverhead + CalibrationPlan.MaxSegment);
        d[0].Length.Should().BeGreaterThan(CalibrationPlan.DpduOverhead + CalibrationPlan.MaxSegment - 3);
        d.Zip(d.Skip(1)).Should().AllSatisfy(p => p.Second.Start.Should().Be(p.First.Start + p.First.Length));
    }

    [Fact]
    public void EveryNeededPairFollowsEquallyOftenInACycle()
    {
        var settings = new CalibrationSettings
        {
            Profile = "test",
            Channels = [CalibrationPlan.AllChannels[1]],
            Wns = CalibrationPlan.AllWns,
            Ils = CalibrationPlan.AllIls,
            MaxCycles = 1,
        };
        var plan = new CalibrationPlan(settings);
        var ladderIndex = plan.Ladder.Select((r, i) => (r, i)).ToDictionary(x => x.r, x => x.i);
        plan.Points.Should().NotBeEmpty();
        foreach (CalPoint p in plan.Points)
        {
            // Count successor pairs around the closed cycle.
            var pairs = new Dictionary<(Rung, Rung), int>();
            for (int i = 0; i < p.Cycle.Count; i++)
            {
                var key = (p.Cycle[i], p.Cycle[(i + 1) % p.Cycle.Count]);
                pairs[key] = pairs.GetValueOrDefault(key) + 1;
            }

            bool connected = p.Rungs.Zip(p.Rungs.Skip(1)).All(x => ladderIndex[x.Second] - ladderIndex[x.First] <= CalibrationPlan.PairReach);
            foreach (Rung a in p.Rungs)
            {
                foreach (Rung b in p.Rungs)
                {
                    int d = Math.Abs(ladderIndex[b] - ladderIndex[a]);
                    if (d <= CalibrationPlan.PairReach && a != b)
                    {
                        pairs.GetValueOrDefault((a, b)).Should().Be(1, $"{p.Channel.Name} {p.SnrTenths}: {a.Name} then {b.Name}");
                    }
                }
            }

            // Every rung is sent the same number of times a cycle (one component; with several,
            // each component's start is sent once more).
            if (connected)
            {
                p.Cycle.GroupBy(r => r).Select(g => g.Count()).Distinct().Should().ContainSingle();
            }
        }
    }

    [Fact]
    public void PlanIsDeterministicAndCoversTheGrid()
    {
        var settings = new CalibrationSettings
        {
            Profile = "full",
            Channels = CalibrationPlan.AllChannels,
            Wns = CalibrationPlan.AllWns,
            Ils = CalibrationPlan.AllIls,
        };
        var a = new CalibrationPlan(settings);
        var b = new CalibrationPlan(settings);
        a.Jobs.Select(j => (j.Id, j.ChannelSeed, j.Rotation)).Should().Equal(b.Jobs.Select(j => (j.Id, j.ChannelSeed, j.Rotation)));
        a.Jobs.Select(j => j.Id).Should().OnlyHaveUniqueItems();
        a.Rungs.Should().HaveCount(36);
        a.Points.Select(p => p.Channel.Name).Distinct().Should().HaveCount(5);
        // Every rung is measured on every channel, at at least six points.
        foreach (CalChannel c in CalibrationPlan.AllChannels)
        {
            foreach (Rung r in a.Rungs)
            {
                a.Points.Count(p => p.Channel == c && p.Rungs.Contains(r)).Should().BeGreaterThanOrEqualTo(6, $"{c.Name} {r.Name}");
            }
        }

        // Bursts fit one transmission and hold at least one D_PDU.
        a.Bursts.Values.Should().AllSatisfy(x =>
        {
            x.Seconds.Should().BeLessThanOrEqualTo(CalibrationPlan.MaxTransmissionSeconds);
            x.Dpdus.Should().NotBeEmpty();
        });
    }
}
