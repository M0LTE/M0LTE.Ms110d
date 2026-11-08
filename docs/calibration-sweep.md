# 5066 rate calibration sweep (runbook)

The sweep behind the M0LTE.S5066 rate ladder (phase 7 item P7-5, `docs/core-architecture.md` section 9 there). For every rung (WN 1 to 8 and 13, each of the four interleavers) it counts block errors and D_PDU errors against the burst SNR estimate, for a burst on its own and for the burst after one on another rung, through M0LTE.HfChannel's continuous Watterson channel. The output is a set of small tab-separated tables that a script in s5066-conformance turns into `drc.table`.

The code is `tests/M0LTE.Ms110d.Tests/Ms110dCalibrationSweep.cs` and `tests/M0LTE.Ms110d.Tests/Calibration/`. Nothing heavy runs in CI: the sweep, the merge and the plan printout each sit behind an environment variable, as the SNR and mask sweeps do.

## What it measures

- **Channels**: AWGN, Poor, Moderate, AWGN with the SSB band-pass and Poor with the SSB band-pass (400 to 2600 Hz at -6 dB, on both the sending and the receiving radio).
- **Grid**: set SNR in 1.5 dB steps. Each rung is measured over a window around its expected error transition (from the D-LXIV figures, see `CalibrationPlan.Window`), plus the points where its two ladder neighbours either side are measured. About 20 to 27 points per channel.
- **SNR reference**: on air. The set SNR is the signal power at the channel input, after the transmit passband where there is one, over the noise in 3 kHz added at the receiving antenna (before the receive passband). Every burst is scaled to the same on-air power, so each rung meets the set SNR (WN 8's QAM would otherwise sit 0.8 dB lower).
- **Schedule**: each point sends a cycle of bursts with a 6 s gap between them (the 5066 turnaround until P7-8 measures it). The cycle is an Eulerian circuit of the pairs (a, b) with b up to eight places either side of a in the prior order of all 36 rungs, so every pair follows exactly once a cycle and every rung is sent the same number of times. Eight, not the plan's two above and a few below, because the final ladder drops the rungs a faster rung beats, so its neighbours can be several prior places apart (WN 13 Long to WN 6 Long is eight). Pairs further apart come from `own.tsv` by set SNR: the fading decorrelates in well under a second (1 Hz and 0.5 Hz Doppler spread), so across the 6 s gap one burst's channel is independent of the one before, and `next.tsv` carries the set SNR so the near pairs can check that. A point runs as many cycles as its slowest rung needs for at least 300 blocks and, on fading, at least 20 minutes of signal. Each cycle is one job with its own continuous channel realisation and one demodulator running throughout.
- **Bursts**: a fixed number of blocks per rung (about 12 s, longer for rungs with long blocks, and at least one full D_PDU, so about 56 s on WN 1 and 29 s on WN 2), listed in `meta.json`.
- **D_PDUs**: laid over each burst as M0LTE.S5066's planner lays a bulk transfer to an Ed4 peer: segments of up to 1020 bytes (MAX-SEGMENT 255), 19 bytes of overhead, block aligned where a block holds a D_PDU and straddling whole blocks where it does not. A D_PDU counts as an error if any bit in it is wrong or missing.

## Output

Under `MS110D_CAL_OUT` (an absolute path):

| Path | What | Copy back? |
|---|---|---|
| `plan.json` | Everything that fixes the results. A resumed run or a shard refuses to run against a different plan | No (it is inside `meta.json`) |
| `raw/<job>.tsv` | One line per burst sent. Written whole when the job finishes, so a killed job just runs again | No, unless asked |
| `progress.log` | One line per finished job, with the running speed and time to go | No |
| `ms110d-cs-<version>/own.tsv` | channel, rung, set SNR, estimate bin (5 tenths), bursts, blocks, block errors, D_PDUs, D_PDU errors, clean bursts (no D_PDU error) | Yes |
| `ms110d-cs-<version>/next.tsv` | channel, rung a, rung b, set SNR, gap, bin of the estimate on a, then pairs, D_PDUs, D_PDU errors, blocks, block errors and clean bursts of the burst on b. The plan's columns are these summed over set SNR | Yes |
| `ms110d-cs-<version>/meta.json` | Modem implementation, version and commit; HfChannel version; SSB edges; seeds; burst length and D_PDU layout per rung; every point's schedule; the grid; whether every job merged | Yes |
| `ms110d-cs-<version>/audit-sample.tsv` | 1% of the raw lines, chosen by hash | Yes |

The directory name says which modem the numbers belong to (`ms110d-cs-0.6.1` is this C# modem at 0.6.1). A table measured here does not apply to another modem implementation.

## Settings

| Variable | Meaning |
|---|---|
| `MS110D_CAL_SWEEP=1` | Run the sweep (test `Run`) |
| `MS110D_CAL_MERGE=1` | Merge the raw records into the tables (test `Merge`) |
| `MS110D_CAL_PLAN=1` | Print the points, burst lengths, job count and signal-hours (test `Plan`) |
| `MS110D_CAL_PILOT=1` | Check the windows: a few bursts of each rung alone at the bottom and top of its window on each channel, into `pilot.tsv` (test `Pilot`; `MS110D_CAL_PILOT_DRY=1` prints the size only) |
| `MS110D_CAL_OUT` | Output directory, absolute. Default `/tmp/ms110d-cal` |
| `MS110D_CAL_PROFILE` | `full` (default) or `smoke`: two channels, WN 3 and 6 at Short and Long, two points each, one cycle |
| `MS110D_MASK_WORKERS` | Parallel jobs. Default: one per core |
| `MS110D_CAL_SHARD` | `i/n` to run every n-th job from i, for several machines into separate directories; merge from one directory holding all the `raw/` files |
| `MS110D_CAL_CHANNELS`, `MS110D_CAL_WN`, `MS110D_CAL_IL`, `MS110D_CAL_POINTS` | Narrow a run (comma lists; points keeps the middle n of each channel's grid) |
| `MS110D_CAL_MIN_BLOCKS`, `MS110D_CAL_MIN_MINUTES`, `MS110D_CAL_MAX_CYCLES` | The per-rung minimums (300 blocks, 20 fading minutes) and a cap on cycles per point |
| `MS110D_CAL_GAP_MS` | Gap between bursts, default 6000 |
| `MS110D_CAL_SEED_OFFSET` | Moves every realisation, for an out-of-sample repeat |

The merge must run with the same settings and commit as the run.

## Smoke run (local, a couple of minutes)

```sh
MS110D_CAL_PROFILE=smoke MS110D_CAL_SWEEP=1 MS110D_CAL_OUT=/tmp/cal-smoke \
  dotnet run -c Release --project tests/M0LTE.Ms110d.Tests -- -method M0LTE.Ms110d.Tests.Ms110dCalibrationSweep.Run
MS110D_CAL_PROFILE=smoke MS110D_CAL_MERGE=1 MS110D_CAL_OUT=/tmp/cal-smoke \
  dotnet run -c Release --project tests/M0LTE.Ms110d.Tests -- -method M0LTE.Ms110d.Tests.Ms110dCalibrationSweep.Merge
```

## Full run on one spot instance

Measured speed and size: see "Timing" below. Memory is small (about 20 MB a worker).

1. Instance: 192 vCPUs, Linux x64 (for example c7a.48xlarge). Put `MS110D_CAL_OUT` on a disk that survives a reboot.
2. Install .NET 10 and set `DOTNET_ROOT`, or the test host will not start:

   ```sh
   curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
   export DOTNET_ROOT=$HOME/.dotnet PATH=$HOME/.dotnet:$PATH
   ```

3. Check out the commit to measure and build:

   ```sh
   git clone https://github.com/M0LTE/M0LTE.Ms110d && cd M0LTE.Ms110d && git checkout <commit>
   dotnet build -c Release tests/M0LTE.Ms110d.Tests
   ```

4. Start the sweep detached, all cores:

   ```sh
   mkdir -p /data/cal
   MS110D_CAL_SWEEP=1 MS110D_CAL_OUT=/data/cal MS110D_MASK_WORKERS=192 nohup \
     dotnet run -c Release --no-build --project tests/M0LTE.Ms110d.Tests -- \
     -method M0LTE.Ms110d.Tests.Ms110dCalibrationSweep.Run > /data/cal/run.log 2>&1 &
   ```

5. Watch `tail -f /data/cal/progress.log`. Each line gives the jobs done, the core-seconds per signal-second so far and the hours to go. The longest jobs start first.
6. If the instance is interrupted or the run stops, run the same command again: finished jobs are skipped. A job that failed is listed at the end of `run.log` and runs again on the next start.
7. Merge, check and pack only the tables:

   ```sh
   MS110D_CAL_MERGE=1 MS110D_CAL_OUT=/data/cal \
     dotnet run -c Release --no-build --project tests/M0LTE.Ms110d.Tests -- -method M0LTE.Ms110d.Tests.Ms110dCalibrationSweep.Merge
   grep -A3 '"merge"' /data/cal/ms110d-cs-*/meta.json   # complete: true
   tar czf /data/cal-ms110d-cs.tar.gz -C /data/cal ms110d-cs-0.6.1
   ```

   Copy back only that tarball (a few hundred kB; egress is billed). Leave `raw/` on the instance until the tables have been checked, then delete it with the instance.

## Timing

Measured on 2026-11-08 at commit ae8ab35 (0.6.1) on a shared 16-core box that was oversubscribed by other work at the time (load about 20), 6 workers:

| Run | Signal | Speed |
|---|---|---|
| Smoke profile, 4 jobs | 0.3 h | 0.10 (AWGN) to 0.27 (Poor-SSB) core-seconds per signal-second |
| Full rung mix, AWGN and Poor, 3 middle points each, one cycle | 4 jobs, 6.3 h | 0.25 core-seconds per signal-second |

The full profile is 1712 jobs and about 1590 signal-hours (`MS110D_CAL_PLAN=1` prints it), so about 400 core-hours at 0.25: about 2 to 2.5 hours on 192 cores, the longest single job (2.3 signal-hours, started first) about 35 minutes. Allow 3 hours. That is more signal than the calibration plan's 850 signal-hours (the 6 s gaps, and the 56 s WN 1 bursts that one full D_PDU needs) but fewer core-hours than its 600, because the demodulator runs faster than the 0.35 to 0.7 the plan assumed.
