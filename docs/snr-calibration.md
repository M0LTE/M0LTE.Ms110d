# SNR estimator calibration

What the SNR estimator (`docs/design-5066-support.md` section 2) reads against the channel simulator's set SNR, measured at commit 2d4db69 (0.6.0) on 2026-11-07. Re-run when the receiver's equaliser changes; the equaliser loss table in the code and the offsets here move together.

0.6.1 note: a burst that ends `NewPreamble` now leaves its trailing run of noise-only probes out of the burst figure, as `SignalLost` does ([M0LTE/M0LTE.S5066#36](https://github.com/M0LTE/M0LTE.S5066/issues/36)). The sweeps below end bursts on `Eom`, so the figures and offsets here are unchanged. The public API is unchanged.

The 5066 rate-ladder calibration, which measures block and D_PDU errors against this estimate on every rung, is in [calibration-sweep.md](calibration-sweep.md).

## How it was measured

`Ms110dSnrEstimateTests` runs bursts through `M0LTE.HfChannel.WattersonChannel.Apply` at a set SNR in 3 kHz, with a fresh demodulator per burst and M = 3 preamble super-frames (the library default). Every WN in 1 to 8 and 13, Short and Long interleaver.

- AWGN: the WN's D-LXIV AWGN SNR, +5, +10, +20 dB and 30 dB absolute; 20 bursts of 4 blocks per point.
- Poor (two equal Rayleigh paths, 2 ms, 1 Hz) and Moderate (two equal Rayleigh paths, 1 ms, 0.5 Hz): the WN's D-LXIV Poor SNR, +6 and +12 dB; bursts of about 10 s, at least 60 s of signal per point.

Commands (16-core box, 27 minutes in all):

```sh
MS110D_SNR_SWEEP=1 MS110D_SNR_BURSTS=20 MS110D_MASK_WORKERS=6 MS110D_SNR_LOG=$PWD/awgn.log \
  dotnet run -c Release --project tests/M0LTE.Ms110d.Tests -- -method M0LTE.Ms110d.Tests.Ms110dSnrEstimateTests.Sweep_Awgn
MS110D_MASKS_POOR=1 MS110D_SNR_SECONDS=60 MS110D_MASK_WORKERS=8 MS110D_SNR_LOG=$PWD/fading.log \
  dotnet run -c Release --project tests/M0LTE.Ms110d.Tests -- -method M0LTE.Ms110d.Tests.Ms110dSnrEstimateTests.Sweep_PoorAndModerate
```

`MS110D_SNR_WN` and `MS110D_SNR_IL` (comma lists) narrow a run, and `MS110D_SNR_SEED_OFFSET` moves every seed to a disjoint realisation set. Give `MS110D_SNR_LOG` an absolute path: the test host runs from its output directory, so a relative one lands there. The runs are seed-deterministic and independent of the worker count. Both tests passed their gates at this commit.

## Equaliser loss (in the code)

With the design's formula alone (section 2.2 and the 2400/3000 conversion), the probe estimate read low on AWGN by a fixed amount per waveform: constant from the mask SNR to 30 dB, and the same at Short and Long. A constant dB offset across 25 dB of SNR is excess noise at the equaliser output in proportion to the input noise, not a floor and not an estimator error, so it is added back: `Ms110dDemodulator.EqualiserLossDb`. The preamble estimate needs no such term (it reads within 0.5 dB of the set SNR up to 30 dB without one, which also confirms the bandwidth conversion).

Raw bias before the correction, over the +5, +10, +20 and 30 dB points of both interleavers (20 bursts each):

| WN | Mean raw bias (dB) | Range (dB) | Loss added back (dB) |
|---|---|---|---|
| 3 | -1.86 | -1.89 to -1.81 | 1.86 |
| 4 | -1.85 | -1.91 to -1.81 | 1.85 |
| 5 | -1.27 | -1.32 to -1.22 | 1.27 |
| 6 | -1.28 | -1.33 to -1.23 | 1.28 |
| 7 | -1.35 | -1.42 to -1.25 | 1.35 |
| 8 | -1.52 | -1.67 to -1.36 | 1.52 |
| 13 | -1.25 | -1.32 to -1.19 | 1.25 |

Out-of-sample check (review, same code): the AWGN sweep for WN 3, 4, 5, 7, 8 and 13 at both interleavers on a disjoint seed set (`MS110D_SNR_SEED_OFFSET=500000`) passes every gate, with burst bias within 0.25 dB above the mask point (worst WN 5 Short, -0.12 to -0.23 dB) and within 0.42 dB at it (WN 3 Long -0.42, WN 8 Short -0.34). So the table is not fitted to the realisations it was measured on. It does read 0.2 to 0.4 dB lower at the mask point than above it on WN 3, 4 and 8, where decision errors start to add to the equaliser's excess noise.

WN 1 and 2 (K = 48) read within 0.35 dB of the set SNR at their mask point without a correction and get none. Their bias grows with SNR instead (see below).

## AWGN

All gates pass for WN 3 to 8 and 13: burst bias within 0.40 dB from the mask SNR to 30 dB (within 0.3 dB above the mask point), block spread under 0.6 dB (Short) and 0.2 dB (Long), monotonic, no saturation up to 33 dB (WN 7) and 36 dB (WN 8). The preamble estimate is within 0.5 dB to 30 dB for every WN (WN 0 is covered by the CI test), reading about 1 dB low at 36 dB.

**WN 1 and 2 saturate.** Their K = 48 equaliser converges slowly under its ridge at high SNR (the probe gain climbs through the whole of a Short burst), so the residual is convergence error, not noise. The estimate saturates near 17.5 dB at the Short interleaver and near 27 dB at Long (longer bursts let it converge). That is the effective SNR this receiver really delivers on those bursts, so it is not corrected. The sweep's gate for WN 1 and 2 at Short stops at mask + 5 dB; at Long the full range passes. For a rate choice this is harmless: an estimate of 17 dB on WN 1 or 2 already says to go faster.

No block failed to decode at any AWGN point, so the AWGN 10% block-error crossings are below every point measured (not recorded).

Every AWGN point, with the correction:

| WN | Interleaver | Set SNR (dB) | Burst bias | Burst sd | Block sd | Preamble bias |
|---|---|---|---|---|---|---|
| 1 | Short | -3 | -0.21 | 0.28 | 0.57 | +0.23 |
| 1 | Short | 2 | -0.43 | 0.18 | 0.43 | +0.11 |
| 1 | Short | 7 | -0.70 | 0.13 | 0.58 | +0.06 |
| 1 | Short | 17 | -2.86 | 0.08 | 2.35 | +0.02 |
| 1 | Short | 30 | -12.50 | 0.08 | 4.21 | -0.14 |
| 1 | Long | -3 | -0.01 | 0.04 | 0.09 | +0.35 |
| 1 | Long | 2 | -0.24 | 0.03 | 0.07 | +0.20 |
| 1 | Long | 7 | -0.38 | 0.03 | 0.07 | +0.15 |
| 1 | Long | 17 | -0.65 | 0.03 | 0.25 | +0.11 |
| 1 | Long | 30 | -2.78 | 0.06 | 2.57 | -0.04 |
| 2 | Short | 0 | -0.32 | 0.23 | 0.47 | +0.16 |
| 2 | Short | 5 | -0.57 | 0.17 | 0.52 | +0.08 |
| 2 | Short | 10 | -1.02 | 0.15 | 1.03 | +0.05 |
| 2 | Short | 20 | -4.46 | 0.11 | 3.12 | +0.02 |
| 2 | Short | 30 | -12.57 | 0.10 | 4.32 | -0.13 |
| 2 | Long | 0 | -0.18 | 0.04 | 0.07 | +0.08 |
| 2 | Long | 5 | -0.34 | 0.03 | 0.06 | +0.01 |
| 2 | Long | 10 | -0.46 | 0.03 | 0.07 | -0.02 |
| 2 | Long | 20 | -0.82 | 0.02 | 0.46 | -0.05 |
| 2 | Long | 30 | -2.78 | 0.06 | 2.56 | -0.20 |
| 3 | Short | 3 | -0.39 | 0.28 | 0.49 | -0.01 |
| 3 | Short | 8 | -0.03 | 0.22 | 0.39 | -0.04 |
| 3 | Short | 13 | +0.03 | 0.19 | 0.36 | -0.05 |
| 3 | Short | 23 | +0.00 | 0.18 | 0.35 | -0.08 |
| 3 | Short | 30 | -0.02 | 0.18 | 0.35 | -0.21 |
| 3 | Long | 3 | -0.40 | 0.07 | 0.12 | +0.12 |
| 3 | Long | 8 | -0.02 | 0.05 | 0.09 | +0.08 |
| 3 | Long | 13 | +0.05 | 0.05 | 0.08 | +0.06 |
| 3 | Long | 23 | +0.02 | 0.05 | 0.08 | +0.01 |
| 3 | Long | 30 | -0.03 | 0.04 | 0.08 | -0.13 |
| 4 | Short | 5 | -0.21 | 0.28 | 0.40 | +0.04 |
| 4 | Short | 10 | +0.00 | 0.22 | 0.34 | +0.02 |
| 4 | Short | 15 | +0.01 | 0.19 | 0.33 | +0.01 |
| 4 | Short | 25 | -0.03 | 0.18 | 0.33 | -0.04 |
| 4 | Short | 30 | -0.06 | 0.18 | 0.33 | -0.16 |
| 4 | Long | 5 | -0.22 | 0.05 | 0.11 | +0.09 |
| 4 | Long | 10 | +0.03 | 0.04 | 0.09 | +0.05 |
| 4 | Long | 15 | +0.04 | 0.04 | 0.09 | +0.04 |
| 4 | Long | 25 | +0.01 | 0.04 | 0.09 | -0.02 |
| 4 | Long | 30 | -0.03 | 0.04 | 0.09 | -0.13 |
| 5 | Short | 6 | -0.06 | 0.26 | 0.48 | +0.01 |
| 5 | Short | 11 | +0.05 | 0.26 | 0.44 | +0.01 |
| 5 | Short | 16 | +0.03 | 0.28 | 0.44 | +0.01 |
| 5 | Short | 26 | -0.01 | 0.30 | 0.44 | -0.03 |
| 5 | Short | 30 | -0.04 | 0.30 | 0.44 | -0.13 |
| 5 | Long | 6 | -0.08 | 0.08 | 0.13 | +0.06 |
| 5 | Long | 11 | +0.04 | 0.07 | 0.12 | +0.04 |
| 5 | Long | 16 | +0.03 | 0.06 | 0.12 | +0.03 |
| 5 | Long | 26 | -0.02 | 0.06 | 0.13 | -0.04 |
| 5 | Long | 30 | -0.05 | 0.06 | 0.13 | -0.14 |
| 6 | Short | 9 | +0.03 | 0.31 | 0.52 | +0.03 |
| 6 | Short | 14 | +0.03 | 0.28 | 0.51 | +0.02 |
| 6 | Short | 19 | +0.02 | 0.26 | 0.50 | +0.01 |
| 6 | Short | 29 | -0.01 | 0.25 | 0.49 | -0.12 |
| 6 | Short | 30 | -0.02 | 0.25 | 0.49 | -0.15 |
| 6 | Long | 9 | +0.04 | 0.08 | 0.17 | +0.02 |
| 6 | Long | 14 | +0.05 | 0.07 | 0.16 | +0.00 |
| 6 | Long | 19 | +0.03 | 0.07 | 0.16 | -0.01 |
| 6 | Long | 29 | -0.04 | 0.07 | 0.16 | -0.13 |
| 6 | Long | 30 | -0.05 | 0.07 | 0.16 | -0.16 |
| 7 | Short | 13 | -0.06 | 0.29 | 0.45 | +0.00 |
| 7 | Short | 18 | -0.04 | 0.30 | 0.44 | +0.00 |
| 7 | Short | 23 | -0.04 | 0.30 | 0.44 | -0.02 |
| 7 | Short | 30 | -0.06 | 0.30 | 0.45 | -0.16 |
| 7 | Short | 33 | -0.07 | 0.29 | 0.45 | -0.32 |
| 7 | Long | 13 | +0.09 | 0.09 | 0.12 | +0.00 |
| 7 | Long | 18 | +0.10 | 0.09 | 0.11 | -0.02 |
| 7 | Long | 23 | +0.08 | 0.09 | 0.11 | -0.05 |
| 7 | Long | 30 | +0.02 | 0.09 | 0.11 | -0.19 |
| 7 | Long | 33 | +0.00 | 0.09 | 0.11 | -0.35 |
| 8 | Short | 16 | -0.29 | 0.33 | 0.50 | -0.25 |
| 8 | Short | 21 | -0.08 | 0.33 | 0.49 | -0.27 |
| 8 | Short | 26 | -0.09 | 0.33 | 0.49 | -0.33 |
| 8 | Short | 30 | -0.11 | 0.33 | 0.49 | -0.45 |
| 8 | Short | 36 | -0.15 | 0.33 | 0.49 | -1.01 |
| 8 | Long | 16 | -0.08 | 0.06 | 0.13 | -0.09 |
| 8 | Long | 21 | +0.16 | 0.06 | 0.13 | -0.10 |
| 8 | Long | 26 | +0.14 | 0.07 | 0.13 | -0.16 |
| 8 | Long | 30 | +0.11 | 0.07 | 0.13 | -0.27 |
| 8 | Long | 36 | +0.06 | 0.07 | 0.13 | -0.83 |
| 13 | Short | 6 | -0.07 | 0.33 | 0.59 | -0.01 |
| 13 | Short | 11 | +0.04 | 0.29 | 0.51 | -0.03 |
| 13 | Short | 16 | +0.00 | 0.28 | 0.49 | -0.03 |
| 13 | Short | 26 | -0.05 | 0.26 | 0.49 | -0.09 |
| 13 | Short | 30 | -0.07 | 0.26 | 0.49 | -0.19 |
| 13 | Long | 6 | -0.06 | 0.08 | 0.11 | +0.09 |
| 13 | Long | 11 | +0.06 | 0.08 | 0.11 | +0.08 |
| 13 | Long | 16 | +0.05 | 0.07 | 0.11 | +0.07 |
| 13 | Long | 26 | +0.00 | 0.07 | 0.12 | +0.00 |
| 13 | Long | 30 | -0.04 | 0.07 | 0.12 | -0.10 |

## Poor and Moderate

The offset (estimate minus set SNR) is **not a constant**. The burst estimate rises with set SNR, monotonically, but slowly, and for the U = 256 waveforms (WN 5 to 8 and 13) it is nearly flat above the mask SNR: on Poor it sits near 5 to 6 dB whatever the set SNR (WN 8 reads 5.03, 5.22 and 5.25 dB at 23, 29 and 35 dB), on Moderate near 9 to 10 dB. The faster-probing waveforms (WN 1 to 4, a probe every 40 to 53 ms) track further: on Poor WN 3 reads 3.4, 7.3 and 9.5 dB at 7, 13 and 19 dB.

This is the design's "ratio of sums behaves like a harmonic mean, weighted towards the fades" (section 2.2), stronger than that section expected: on a Rayleigh channel the fades and the equaliser's tracking lag set the residual, and above the mask SNR the noise hardly contributes. Two consequences for the 5066 rate ladder:

- "Estimated SNR minus a per-WN offset" cannot recover the set SNR on fading above the mask point: one offset per WN would be wrong by 6 dB or more across the three points. The estimate is a measure of how hard the channel is for the equaliser, and on fading it saturates at a level set mostly by the channel type and the waveform's probe spacing.
- Poor and Moderate differ by 2.4 to 4.6 dB at the same set SNR (WN 5 to 8 and 13), more than the offset moves between the Poor points. So the Moderate-to-Poor spread the design asks the ladder margin to cover is several dB.

Repeatability is good: the burst standard deviation is under 2 dB at every Poor point (gate passes), and block figures spread about 3 to 4 dB about their burst mean (fading, not gated).

The preamble estimate behaves as section 2.4 predicted on both channels: well below the probe estimate and nearly flat with set SNR.

Block errors were few at these points (with 60 s per point, the Long interleaver gives only 6 to 9 blocks a point), so the 10% block-error crossings are thin evidence. Where any were found, the crossing (estimated SNR, pooled over a WN's three points, 1 dB bins) was:

| WN | Short, Poor | Short, Moderate |
|---|---|---|
| 1 | -4 dB | -8 dB |
| 2 | -4 dB | -3 dB |
| 3 | -2 dB | -1 dB |
| 4 | 0 dB | 3 dB |
| 5 | -1 dB | 3 dB |
| 6 | 0 dB | 4 dB |
| 7 | -1 dB | 4 dB |
| 8 | -2 dB | 1 dB |
| 13 | 1 dB | 1 dB |

Long: no block errors at any fading point measured, so no crossing.

Every fading point:

| WN | Interleaver | Channel | Set SNR (dB) | Burst estimate (dB) | Offset | Burst sd | Preamble estimate (dB) | Bad probes | Block errors |
|---|---|---|---|---|---|---|---|---|---|
| 1 | Short | Poor | 3 | -0.65 | -3.65 | 0.99 | -2.78 | 335/1536 | 8/96 |
| 1 | Short | Poor | 9 | 2.09 | -6.91 | 1.15 | 0.39 | 219/1536 | 1/96 |
| 1 | Short | Poor | 15 | 3.47 | -11.53 | 1.57 | 2.10 | 204/1536 | 0/96 |
| 1 | Short | Moderate | 3 | 0.69 | -2.31 | 0.87 | -1.70 | 218/1536 | 2/96 |
| 1 | Short | Moderate | 9 | 4.14 | -4.86 | 0.85 | 1.33 | 172/1536 | 1/96 |
| 1 | Short | Moderate | 15 | 6.32 | -8.68 | 1.23 | 3.68 | 157/1536 | 1/96 |
| 1 | Long | Poor | 3 | -0.70 | -3.70 | 1.00 | -3.14 | 351/1536 | 0/6 |
| 1 | Long | Poor | 9 | 2.23 | -6.77 | 1.00 | -1.01 | 289/1536 | 0/6 |
| 1 | Long | Poor | 15 | 3.79 | -11.21 | 0.83 | 0.36 | 273/1536 | 0/6 |
| 1 | Long | Moderate | 3 | 0.83 | -2.17 | 1.07 | -1.47 | 209/1536 | 0/6 |
| 1 | Long | Moderate | 9 | 4.39 | -4.61 | 0.88 | 2.85 | 138/1536 | 0/6 |
| 1 | Long | Moderate | 15 | 6.35 | -8.65 | 1.90 | 5.85 | 118/1536 | 0/6 |
| 2 | Short | Poor | 5 | 0.95 | -4.05 | 0.48 | 0.63 | 280/1536 | 4/96 |
| 2 | Short | Poor | 11 | 3.75 | -7.25 | 0.36 | 3.17 | 256/1536 | 1/96 |
| 2 | Short | Poor | 17 | 4.64 | -12.36 | 0.91 | 4.27 | 231/1536 | 0/96 |
| 2 | Short | Moderate | 5 | 1.23 | -3.77 | 1.19 | -0.14 | 312/1616 | 13/101 |
| 2 | Short | Moderate | 11 | 4.41 | -6.59 | 1.17 | 1.17 | 161/1536 | 1/96 |
| 2 | Short | Moderate | 17 | 6.29 | -10.71 | 1.11 | 1.56 | 168/1536 | 0/96 |
| 2 | Long | Poor | 5 | 0.55 | -4.45 | 0.88 | 0.61 | 418/1536 | 0/6 |
| 2 | Long | Poor | 11 | 3.53 | -7.47 | 1.23 | 2.70 | 361/1536 | 0/6 |
| 2 | Long | Poor | 17 | 5.01 | -11.99 | 1.37 | 3.47 | 320/1536 | 0/6 |
| 2 | Long | Moderate | 5 | 2.13 | -2.87 | 0.48 | 0.23 | 232/1536 | 0/6 |
| 2 | Long | Moderate | 11 | 5.13 | -5.87 | 0.63 | 2.45 | 206/1536 | 0/6 |
| 2 | Long | Moderate | 17 | 6.69 | -10.31 | 0.98 | 3.37 | 216/1536 | 0/6 |
| 3 | Short | Poor | 7 | 3.44 | -3.56 | 0.65 | -1.67 | 109/1360 | 12/170 |
| 3 | Short | Poor | 13 | 7.29 | -5.71 | 0.62 | -0.01 | 21/1288 | 0/161 |
| 3 | Short | Poor | 19 | 9.52 | -9.48 | 0.60 | 0.62 | 20/1288 | 0/161 |
| 3 | Short | Moderate | 7 | 4.08 | -2.92 | 0.76 | 3.13 | 26/1288 | 7/161 |
| 3 | Short | Moderate | 13 | 8.58 | -4.42 | 1.16 | 5.57 | 13/1288 | 0/161 |
| 3 | Short | Moderate | 19 | 11.86 | -7.14 | 1.47 | 6.67 | 6/1288 | 0/161 |
| 3 | Long | Poor | 7 | 3.44 | -3.56 | 0.86 | 1.05 | 32/1024 | 0/8 |
| 3 | Long | Poor | 13 | 7.32 | -5.68 | 1.15 | 2.82 | 27/1024 | 0/8 |
| 3 | Long | Poor | 19 | 9.55 | -9.45 | 1.66 | 3.55 | 28/1024 | 0/8 |
| 3 | Long | Moderate | 7 | 4.52 | -2.48 | 1.40 | 0.77 | 20/1152 | 0/9 |
| 3 | Long | Moderate | 13 | 9.11 | -3.89 | 1.58 | 3.15 | 11/1152 | 0/9 |
| 3 | Long | Moderate | 19 | 12.54 | -6.46 | 1.86 | 4.31 | 1/1152 | 0/9 |
| 4 | Short | Poor | 10 | 5.55 | -4.45 | 0.64 | -0.11 | 104/1360 | 13/170 |
| 4 | Short | Poor | 16 | 8.44 | -7.56 | 0.63 | 0.86 | 28/1288 | 0/161 |
| 4 | Short | Poor | 22 | 9.69 | -12.31 | 0.68 | 2.84 | 28/1288 | 0/161 |
| 4 | Short | Moderate | 10 | 7.03 | -2.97 | 1.19 | 3.98 | 8/1288 | 5/161 |
| 4 | Short | Moderate | 16 | 11.17 | -4.83 | 1.12 | 7.04 | 3/1288 | 1/161 |
| 4 | Short | Moderate | 22 | 13.60 | -8.40 | 1.24 | 8.71 | 2/1288 | 0/161 |
| 4 | Long | Poor | 10 | 5.36 | -4.64 | 1.27 | 4.01 | 23/1152 | 0/9 |
| 4 | Long | Poor | 16 | 8.43 | -7.57 | 1.11 | 5.43 | 19/1152 | 0/9 |
| 4 | Long | Poor | 22 | 9.96 | -12.04 | 0.96 | 5.91 | 20/1152 | 0/9 |
| 4 | Long | Moderate | 10 | 6.63 | -3.37 | 1.02 | 3.92 | 0/1152 | 0/9 |
| 4 | Long | Moderate | 16 | 11.01 | -4.99 | 1.17 | 5.95 | 0/1152 | 0/9 |
| 4 | Long | Moderate | 22 | 13.91 | -8.09 | 1.53 | 6.90 | 0/1152 | 0/9 |
| 5 | Short | Poor | 11 | 3.61 | -7.39 | 0.45 | 1.52 | 62/504 | 4/126 |
| 5 | Short | Poor | 17 | 5.04 | -11.96 | 0.49 | 2.69 | 66/504 | 0/126 |
| 5 | Short | Poor | 23 | 5.53 | -17.47 | 0.53 | 3.12 | 65/504 | 0/126 |
| 5 | Short | Moderate | 11 | 6.37 | -4.63 | 0.97 | 1.39 | 9/504 | 2/126 |
| 5 | Short | Moderate | 17 | 9.04 | -7.96 | 0.94 | 2.73 | 7/504 | 1/126 |
| 5 | Short | Moderate | 23 | 10.11 | -12.89 | 0.95 | 3.33 | 8/504 | 0/126 |
| 5 | Long | Poor | 11 | 3.38 | -7.62 | 0.81 | -0.69 | 82/512 | 0/8 |
| 5 | Long | Poor | 17 | 4.96 | -12.04 | 0.91 | 0.69 | 92/512 | 0/8 |
| 5 | Long | Poor | 23 | 5.43 | -17.57 | 0.96 | 1.20 | 74/512 | 0/8 |
| 5 | Long | Moderate | 11 | 6.30 | -4.70 | 1.39 | 0.58 | 14/512 | 0/8 |
| 5 | Long | Moderate | 17 | 8.66 | -8.34 | 1.32 | 1.73 | 11/512 | 0/8 |
| 5 | Long | Moderate | 23 | 9.60 | -13.40 | 1.28 | 2.09 | 14/512 | 0/8 |
| 6 | Short | Poor | 14 | 4.57 | -9.43 | 0.84 | 4.11 | 83/536 | 17/134 |
| 6 | Short | Poor | 20 | 5.28 | -14.72 | 0.85 | 4.85 | 55/504 | 2/126 |
| 6 | Short | Poor | 26 | 5.40 | -20.60 | 0.87 | 5.08 | 56/504 | 1/126 |
| 6 | Short | Moderate | 14 | 7.92 | -6.08 | 1.66 | 2.03 | 9/504 | 3/126 |
| 6 | Short | Moderate | 20 | 9.33 | -10.67 | 1.83 | 2.72 | 12/504 | 0/126 |
| 6 | Short | Moderate | 26 | 9.77 | -16.23 | 1.89 | 2.92 | 11/504 | 0/126 |
| 6 | Long | Poor | 14 | 4.53 | -9.47 | 0.59 | 2.44 | 85/512 | 0/8 |
| 6 | Long | Poor | 20 | 5.46 | -14.54 | 0.70 | 3.26 | 89/512 | 0/8 |
| 6 | Long | Poor | 26 | 5.70 | -20.30 | 0.76 | 3.52 | 86/512 | 0/8 |
| 6 | Long | Moderate | 14 | 7.76 | -6.24 | 1.18 | 3.14 | 12/512 | 0/8 |
| 6 | Long | Moderate | 20 | 9.43 | -10.57 | 1.42 | 3.69 | 14/512 | 0/8 |
| 6 | Long | Moderate | 26 | 9.86 | -16.14 | 1.79 | 3.86 | 13/512 | 0/8 |
| 7 | Short | Poor | 19 | 5.46 | -13.54 | 0.59 | 0.24 | 63/504 | 2/126 |
| 7 | Short | Poor | 25 | 5.71 | -19.29 | 0.61 | 0.40 | 60/504 | 0/126 |
| 7 | Short | Poor | 31 | 5.77 | -25.23 | 0.62 | 0.44 | 61/504 | 2/126 |
| 7 | Short | Moderate | 19 | 8.18 | -10.82 | 1.19 | 4.52 | 23/504 | 6/126 |
| 7 | Short | Moderate | 25 | 8.78 | -16.22 | 1.20 | 4.77 | 22/504 | 0/126 |
| 7 | Short | Moderate | 31 | 8.98 | -22.02 | 1.10 | 4.84 | 25/504 | 0/126 |
| 7 | Long | Poor | 19 | 5.47 | -13.53 | 1.56 | 1.78 | 83/512 | 0/8 |
| 7 | Long | Poor | 25 | 5.78 | -19.22 | 1.61 | 1.94 | 89/512 | 0/8 |
| 7 | Long | Poor | 31 | 5.84 | -25.16 | 1.71 | 1.98 | 89/512 | 0/8 |
| 7 | Long | Moderate | 19 | 9.17 | -9.83 | 1.39 | 4.10 | 19/512 | 0/8 |
| 7 | Long | Moderate | 25 | 9.91 | -15.09 | 1.34 | 4.35 | 20/512 | 0/8 |
| 7 | Long | Moderate | 31 | 10.09 | -20.91 | 1.31 | 4.42 | 19/512 | 0/8 |
| 8 | Short | Poor | 23 | 5.03 | -17.97 | 1.05 | 0.82 | 56/504 | 1/126 |
| 8 | Short | Poor | 29 | 5.22 | -23.78 | 0.99 | 0.86 | 62/504 | 0/126 |
| 8 | Short | Poor | 35 | 5.25 | -29.75 | 0.99 | 0.87 | 61/504 | 0/126 |
| 8 | Short | Moderate | 23 | 9.12 | -13.88 | 0.76 | 5.25 | 25/504 | 1/126 |
| 8 | Short | Moderate | 29 | 9.47 | -19.53 | 0.90 | 5.52 | 26/504 | 0/126 |
| 8 | Short | Moderate | 35 | 9.55 | -25.45 | 0.96 | 5.60 | 26/504 | 0/126 |
| 8 | Long | Poor | 23 | 4.84 | -18.16 | 0.78 | 0.41 | 110/512 | 0/8 |
| 8 | Long | Poor | 29 | 4.98 | -24.02 | 0.78 | 0.47 | 110/512 | 0/8 |
| 8 | Long | Poor | 35 | 5.02 | -29.98 | 0.80 | 0.49 | 111/512 | 0/8 |
| 8 | Long | Moderate | 23 | 8.00 | -15.00 | 1.76 | 5.21 | 34/512 | 0/8 |
| 8 | Long | Moderate | 29 | 8.45 | -20.55 | 1.86 | 5.53 | 33/512 | 0/8 |
| 8 | Long | Moderate | 35 | 8.60 | -26.40 | 1.86 | 5.63 | 33/512 | 0/8 |
| 13 | Short | Poor | 11 | 3.52 | -7.48 | 0.78 | -2.42 | 77/504 | 8/126 |
| 13 | Short | Poor | 17 | 5.04 | -11.96 | 0.53 | -2.07 | 77/504 | 1/126 |
| 13 | Short | Poor | 23 | 5.53 | -17.47 | 0.51 | -2.10 | 78/504 | 2/126 |
| 13 | Short | Moderate | 11 | 5.95 | -5.05 | 0.99 | 5.39 | 17/504 | 6/126 |
| 13 | Short | Moderate | 17 | 8.27 | -8.73 | 1.23 | 7.17 | 16/504 | 1/126 |
| 13 | Short | Moderate | 23 | 9.25 | -13.75 | 1.33 | 7.84 | 15/504 | 0/126 |
| 13 | Long | Poor | 11 | 3.55 | -7.45 | 0.84 | 1.41 | 97/512 | 0/8 |
| 13 | Long | Poor | 17 | 4.96 | -12.04 | 0.94 | 2.68 | 99/512 | 0/8 |
| 13 | Long | Poor | 23 | 5.40 | -17.60 | 0.90 | 3.12 | 97/512 | 0/8 |
| 13 | Long | Moderate | 11 | 6.25 | -4.75 | 0.95 | 1.78 | 23/512 | 0/8 |
| 13 | Long | Moderate | 17 | 8.32 | -8.68 | 1.21 | 2.83 | 24/512 | 0/8 |
| 13 | Long | Moderate | 23 | 9.02 | -13.98 | 1.41 | 3.20 | 24/512 | 0/8 |

## What the estimate is, and what phase 7 must calibrate

On fading the estimate is an effective post-equaliser SNR: what the decoder of this waveform actually sees, fades and tracking error included. That is the right kind of quantity for a rate choice, because it is what decides whether a block decodes. It is not the channel's average SNR, and the measurements above show it cannot be turned back into one: above the mask SNR the fading, not the noise, sets it. So the design's plan of "estimate minus a per-WN Poor offset, compared with the D-LXIV row" (design section 2.5) does not work and is replaced by the following.

- **Calibrate decoding against the estimate, not the estimate against the set SNR.** Phase 7 measures block error rate (and then D_PDU error rate) as a function of the burst estimate, per WN and interleaver, and sets the ladder's thresholds where that curve crosses the target. The set SNR is only the knob that moves the simulation along the curve.
- **Measure across the transition.** The points here start at the mask SNR, where most WNs already decode almost every block. The thresholds live below it: phase 7 needs points from where block errors are near 100% up to where they vanish, with hundreds of blocks per point (the Long interleaver gave 6 to 9 blocks a point here, so it needs runs ten or more times longer, `MS110D_SNR_SECONDS=600` or more). The same applies to the burst spreads above, which come from 6 to 9 bursts a point.
- **Predict the next burst, not the same block.** The block-error crossings above pair each block's estimate with that block's own decode, so a deep fade lowers both together. A rate choice uses the estimate from one burst to pick the waveform of a later one. Phase 7 pairs the burst estimate with the block error rate of the following burst, at the turnaround the 5066 exchange really has.
- **Cross-waveform prediction is the hard part.** The estimate depends on the waveform it was measured on (probe spacing, frame length, equaliser): on Poor at 19 dB WN 3 reads 9.5 dB while WN 5 to 8 read 5 to 6 dB. The ladder needs, for the waveform it is on (a) and each waveform it might move to (b), the error rate of b given the estimate measured on a. That is a table per (a, b) pair, or a mapping from the estimate on a to the estimate on b measured on the same channels.
- **Going up on fading cannot rest on the estimate alone.** WN 5 on Poor reads 3.6, 5.0 and 5.5 dB at 11, 17 and 23 dB set SNR: 1.9 dB of movement over 12 dB, against a burst spread of about 0.5 dB. A WN 5 estimate cannot tell a channel where only WN 5 works from one where WN 8 (Poor mask 23 dB) would. Rate increases on fading need another input, such as a run of clean bursts or a trial at the higher rate, with the estimate mainly useful for going down.
- **Check whether the channel type drops out.** If block error rate against the estimate is the same curve on AWGN, Moderate and Poor, one table per waveform serves every channel and the ladder need not know the channel type. If not (the thin crossings above differ by up to 5 dB between Poor and Moderate), the spread between the curves is the margin the ladder must carry.

The equaliser loss term moves every probe figure by the same per-WN constant, on fading as on AWGN. It makes the AWGN figure the channel SNR; for a calibration against block error rate per WN it changes nothing.

## Rules for a consumer

- Compare an estimate only with calibration data measured on the same waveform and interleaver.
- On fading, treat the estimate as "how hard this channel is for this waveform", not as the channel SNR; do not subtract an offset to get a D-LXIV-comparable number.
- The equaliser loss table holds for the default equaliser settings. A caller that sets `RlsForgettingFactor` or `TrackRidge` changes the equaliser, and the AWGN figure is then uncalibrated.
