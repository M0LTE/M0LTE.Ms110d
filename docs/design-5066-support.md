# Design: what a STANAG 5066 data link needs from M0LTE.Ms110d

Status: design, not yet implemented. Written for [M0LTE/M0LTE.S5066#8](https://github.com/M0LTE/M0LTE.S5066/issues/8) (phase 2 of that plan). Nothing here changes the waveform; every addition is to what the library reports and what a caller can set. All additions are additive to the public API, and the C++ port ([ms110d-cpp](https://github.com/M0LTE/ms110d-cpp)) gets the same additions so the two stay level.

Read with: `docs/versioning.md` (this repo), `docs/plan.md` sections 5 to 7 in M0LTE.S5066, and `notes/modem-and-tests.md` sections A and D in the s5066-research notes. Line numbers below are for `main` at 87b5e91 (v0.4.1) and ms110d-cpp at 6e22ca2.

## Contents

1. What the 5066 stack asks for, and what exists
2. SNR estimator
3. Timing and events
4. Run-time maximum input data blocks
5. PlanBurst
6. EOT detection
7. API additions, C# and C
8. Versioning and consumer impact
9. Work breakdown
10. Open points and non-goals

## 1. What the 5066 stack asks for, and what exists

The 5066 plan's modem port (plan 5.1) needs, per received burst: a start time for carrier detect (CSMA, Annex K), decoded bytes per block, and an end event carrying the exit reason, the lock (WN, interleaver, K) and an SNR. Per transmission it needs a plan (duration, block count, bytes per block) for the EOT field and block alignment, and an end-of-our-own-burst input (`transmit_done`). At run time it needs to set the receiver's maximum input data block count (110D D.5.4.6 c).

What the library has today (notes D.2, D.3):

| Need | C# today | C++ today |
|---|---|---|
| Lock info (WN, interleaver, K, CFO) | `Ms110dBurst.Lock`, `Ms110dDemodulator.Lock` | event fields |
| Bytes per block | `BlockDecoded(Ms110dRxBlock)` | `BLOCK_DECODED` |
| End of burst with reason | `BurstCompleted(Ms110dBurst)` | `BURST_COMPLETED` |
| Carrier detect | `CarrierDetect` (polled) | `ms110d_rx_state` (polled) |
| Burst start event | none | `BURST_STARTED` with `sample` |
| Sample index on events | none | `sample` on every event |
| SNR | none | none |
| Duration of a planned burst | `BurstSeconds(bits)` | `ms110d_tx_audio_length` |
| Block capacity | `Ms110dInterleaverParams.Get3k(wn, kind).InputBits` | mode tables |
| Max input blocks | `Ms110dDemodOptions.MaxInputDataBlocks`, init only | `rx_options.max_input_data_blocks`, create only |
| Terminate receive | `Terminate()` | `ms110d_rx_terminate` |
| EOT (D.5.4.4) | transmitted, not detected | transmitted, not detected |

So the gaps are: SNR (both), timing in C# (and two extra timestamps in C++), run-time max blocks (both), a plan call (both), and optionally EOT detection (both).

## 2. SNR estimator

### 2.1 Definition

The library reports **SNR in dB, with the noise referred to a 3 kHz bandwidth**, the same definition as 110D Table D-LXIV [110D D.6.1, p.240: SNR measured in the specified bandwidth, here 3 kHz] and as the channel simulator the tests use (`M0LTE.HfChannel.WattersonChannel.Apply(..., snrDb, noiseBandwidthHz = 3000)`). That is the number the 5066 rate ladder compares against the D-LXIV table (plan 6.2), so no conversion is needed downstream.

Internally the receiver measures a per-symbol quantity at 2400 symbols/s: the ratio of coherent signal power to residual power at the equaliser output, call it `Es/N0_eq`. For a signal at 2400 Bd with noise density N0, SNR in 3 kHz is `S / (N0 x 3000)` and Es/N0 is `S / (N0 x 2400)`, so

    SNR_3k_dB = 10 log10(Es/N0_eq) + 10 log10(2400 / 3000) = 10 log10(Es/N0_eq) - 0.97 dB

on a flat channel with an ideal matched filter. Anything that departs from this (equaliser noise enhancement, tracking lag on fading, the interpolation and filter mismatch at high SNR) is a bias the validation (2.5) measures and the calibration table records. The report is clamped to [-10, +40] dB; a value at the clamp means "beyond what the estimator can resolve", not a measurement.

Three quantities are reported:

- **Per block** (`Ms110dRxBlock.SnrDb`): over the mini-probes of that interleaver block.
- **Per burst** (`Ms110dBurst.SnrDb`): over every mini-probe of the burst.
- **At lock** (`Ms110dBurstStart.PreambleSnrDb`): over the preamble symbols, available the moment the WID is read, for every waveform including WN 0. Coarser (0.72 s of signal with the default M = 3), but early.

All three are nullable (`double?` in C#, a `has_snr` flag in C): null when there was nothing to measure (a burst that ended before its first probe, or WN 0 in-burst, see 2.4).

### 2.2 Method: mini-probe residual at the equaliser output, before the probe's own solve

For WN 1 to 13 every data frame ends with a K-symbol mini-probe of known symbols [110D D.5.2.2, p.170; Table D-XII, p.164: K = 48 for WN 1 and 2, 32 for WN 3 to 10 and 13, 24 for WN 11 and 12]. The receiver already equalises each probe with the taps it carried into the frame, before it solves on that probe's rows, and accumulates two numbers over the probe's usable rows (`Ms110dDemodulator.ProcessFrame`, `Ms110dDemodulator.cs:1895-1915`):

- `probePhase = sum(y_i x conj(p_i))`, the coherent correlation of equalised output `y_i` against the known probe symbol `p_i`;
- `mse = sum(|y_i - p_i|^2)`;

over `statRows = K - FbTaps` rows (the first FbTaps probe symbols are skipped because their feedback history is data, not probe).

The estimator adds a third accumulator and computes, per probe:

    g   = probePhase / statRows                          complex gain of the equalised probe (nominally 1)
    S_p = |g|^2                                          coherent signal power
    N_p = (1 / (statRows - 1)) x sum(|y_i - g x p_i|^2)  residual power about the fitted gain, both dimensions

The `statRows - 1` divisor removes the bias from fitting one complex gain to the rows. `N_p` is the same residual the turbo pass already uses as its "probe-priced noise floor" (`Ms110dDemodulator.cs:3945-3959`, there halved to per-dimension for the BCJR); the first-pass version here runs on every frame, turbo or not, and is what the report uses so that `DisableTurbo` does not change the SNR.

Why pre-solve: the probe rows are also the training rows of the solve that follows (`AddTrainingRow(..., weight: 6f)` then `SolveTraining`), so the post-solve residual on the same rows is biased low by the fit (K - FbTaps rows against FfTaps + FbTaps unknowns, held off zero only by the ridge). The pre-solve residual is a prediction error of taps estimated from earlier data, which is unbiased for stationary noise and includes the equaliser's tracking lag on a fading channel. That lag is real: it is what the decoder sees, so an "effective SNR" that includes it is the right input to a rate choice.

Why not the preamble alone: it is short (M x 240 ms) and samples one fade state. Why not total received power against a noise floor measured between bursts: the inter-burst floor is not available on a half-duplex channel the stack keeps busy, and energy measures do not separate signal from co-channel interference.

Aggregation (all in linear power, converted to dB only at the end):

    block SNR  = sum_p S_p / sum_p N_p    over the probes of the block
    burst SNR  = sum_p S_p / sum_p N_p    over the probes of the burst
    SnrDb      = 10 log10(ratio) - 0.97

The ratio of sums (not the mean of per-probe ratios, and not a mean of dBs) is the mean signal power over the mean noise power, which on a fading channel is the "average SNR" sense of Table D-LXIV and is stable at low SNR where a single probe's ratio can go negative.

Which probe rows: every frame's probe, including the cyclically shifted boundary probe (the receiver knows its pattern, `ProbeShiftedAfter`), and excluding probes the receiver flags as bad for tracking (the `probeGain < max(0.10, 0.45 x _probeGainRef)` branch at `Ms110dDemodulator.cs:2336`). A bad probe during a deep fade would otherwise contribute S near 0 and N near the total power, which is correct physics but the receiver's own definition of that probe is "untrustworthy"; excluding it keeps the block number comparable with what the decoder was given. The burst count of excluded probes is reported alongside (`ProbesSkipped`), so a caller can see when a block's SNR stands on few probes.

Also recorded per block for the validation only (not public): the row count, so the expected standard error can be checked.

### 2.3 Expected accuracy

A single probe gives N_p from K - FbTaps rows: 20 for K = 32 (12 feedback taps) and 26 for K = 48 (22 feedback taps), per the tap table at `Ms110dDemodulator.cs:1743-1746`. The relative standard error of one probe's N_p is about 1/sqrt(20) = 22%, or 0.9 dB. Averaging over a block: Short interleaver, 4 frames (WN 5 to 10, 13) gives about 0.45 dB; Long, 64 frames, about 0.1 dB; WN 1 and 2 Short, 16 frames of K = 48 at about 0.2 dB. Per burst, better still. These are AWGN figures; on Poor the number also reflects the fade state during the block, which is the point.

Expected biases, to be measured rather than assumed:

- Below the WN's own mask SNR the equaliser does not track well, so N_p grows and the estimate reads low. Fine for the rate ladder (it means "slow down"), but the validation only gates from the mask SNR upward.
- At high SNR (above about 30 dB) the residual floor is the equaliser's misadjustment and the T/2 interpolation, not noise. The estimate saturates. The validation records where.
- On Poor the estimate is an effective post-equaliser SNR, expected to read below the simulator's set SNR by the fading and tracking loss. The validation records the offset per WN and that offset table is what the 5066 rate ladder's margins are calibrated against (plan 6.2 says those margins are calibrated in simulation anyway).

### 2.4 WN 0 and the preamble estimate

WN 0 has no mini-probes [110D D.5.2, p.163]. In the first release WN 0 bursts report `SnrDb = null` per block and per burst, and the preamble estimate stands in. A Walsh-correlator estimate (best-of-4 correlation power against the mean of the other three, per 32-chip symbol, from `Wid0WalshModem.DemodulateRake`) is possible later; it is biased by the max-of-4 selection at low SNR and needs its own validation, so it is item 9 in the breakdown and not in the way of anything.

The preamble estimate: once the WID is read, every preamble symbol is known (Fixed, downcount, WID: 9 + 4 + 5 Walsh symbols of 32 chips per super-frame [110D D.5.2.1, p.165]). The receiver already correlates known chips in 32-chip groups to refine the carrier (`RefineCarrier`, `Ms110dDemodulator.cs:1418-1450`) and to set the level (`Ms110dDemodulator.cs:1666-1714`). The estimate uses the same groups: per group, coherent power `|sum(y x conj(k))|^2 / 32^2` against the per-chip residual power about that fit, summed over the super-frames the receiver matched, converted as in 2.2. It is reported on the new burst-start event only. Its row count is small (a few hundred chips) and it sits before any equalisation, so it is a channel SNR without the equaliser's loss. Expect it to read above the probe estimate on Poor; that difference is recorded by the validation too.

### 2.5 Validation plan and acceptance criteria

Harness: the existing `RunPoint` in `tests/M0LTE.Ms110d.Tests/Ms110dMaskTests.cs`, which runs bursts through `WattersonChannel.Apply` at a set SNR in 3 kHz, AWGN (no paths) or `WattersonChannel.Poor` (two equal paths, 2 ms, 1 Hz). It gets one addition: it collects every `Ms110dBurst.SnrDb`, `Ms110dRxBlock.SnrDb` and `PreambleSnrDb` it sees, so a new `Ms110dSnrEstimateTests` class can report mean and standard deviation of the estimate against the set value, per WN and interleaver.

Points. For each WN in 1 to 8 and 13, Short and Long interleaver, M = 3 (the library default, what the stack will transmit; the masks use 20):

- AWGN at the WN's D-LXIV AWGN SNR, +5, +10 and +20 dB above it, and at +30 dB absolute. At least 20 bursts of 4 blocks per point.
- Poor at the WN's D-LXIV Poor SNR and +6 dB, at least 60 s of signal per point (the fade rate is 1 Hz; 60 s covers enough fades for a mean).

Acceptance (our design choice, not from any standard):

| Measure | AWGN | Poor |
|---|---|---|
| Burst SNR bias, mean over bursts | within 1.0 dB of the set SNR from the mask SNR to +20 dB above it | recorded per WN into `docs/snr-calibration.md`; no gate on the value, gate on repeatability: standard deviation across bursts under 2.0 dB |
| Block SNR standard deviation across blocks at one point | under 1.0 dB (Short) or 0.5 dB (Long) | not gated (fading dominates) |
| Monotonic | the mean estimate rises with each step of set SNR, every WN | the same |
| Saturation | the point where the mean stops rising is recorded; it must be above 30 dB | not gated |
| Preamble estimate | within 2.0 dB on AWGN at the same points, all WNs including 0 | recorded |
| Decode unchanged | the mask suites' bit counts are identical with the estimator on (the estimator reads, never writes, receiver state) | the same |

The AWGN gates run in CI (short, a few seconds per point at WN 6). The Poor points run behind `MS110D_MASKS_POOR=1` like the existing Poor masks, and their result goes into the calibration doc with the commit it was measured at.

C++ parity: the C++ receiver must produce the same per-block and per-burst values within 0.1 dB on the reference audio (the `tools/M0LTE.Ms110d.RefGen` set, `docs/reference-files.md`). The reference manifest gains the C# values per block and per burst; the C++ `reference_decode` test compares them with that tolerance rather than exactly, because the accumulations run in a different order and precision.

### 2.6 Hook points

C#, `src/M0LTE.Ms110d/Ms110dDemodulator.cs`:

- `ProcessFrame` at the pre-solve probe loop (`:1899-1915`): accumulate `S_p`, `N_p`, the row count; add to the block accumulators unless the frame is later judged bad (`:2336`), in which case count it as skipped.
- The block completion that raises `BlockDecoded` (`:3425`): convert the block accumulators, put the value on the `Ms110dRxBlock`, fold the accumulators into the burst's, reset the block's.
- `EmitBurst` (`:5279`): convert the burst accumulators onto the `Ms110dBurst`; `EndBurst` (`:5300`) clears them.
- `EnterTracking` (`:1250-1318`), after `RefineCarrier` has run over the matched super-frames: the preamble estimate, placed on the new `BurstStarted` event (section 3).
- `TrackWalsh` (`:2942`): untouched in the first release (WN 0 reports null in-burst).

C++, `src/receiver.cpp` and the per-frame equaliser it calls: the same three accumulation points, which exist in the port's frame loop because it decodes the same bits (the port's `docs/dev/receiver.md` maps them). The values travel on the event struct (section 7.2).

## 3. Timing and events

### 3.1 The clock

The library has no clock and reads none (the 5066 core is sans-IO, and the modem should be too). Time is an **input sample index at 9600 Hz**, counted from the first `Process` call after construction or `Reset`. C++ already has this: `ms110d_rx_event.sample` is "input samples taken when it was raised" (`ms110d.h:319`) and `ms110d_rx_stats.samples_taken`. C# gets the same counter, `Ms110dDemodulator.SamplesProcessed` (long), incremented per input sample in `Process` (`Ms110dDemodulator.cs:716`), before the T/2 decimation (the internal `_written` counts T/2 samples and is not exposed).

The host converts: `seconds = sample / 9600.0`, and anchors sample 0 to its own clock at the moment it started feeding audio, less its capture latency. The host, not the library, owns that anchor; pdn-soundmodem already resamples 48 kHz audio to 9600 Hz for this modem and knows its device latency.

Positions on the air (where a preamble or a frame sits in the input) are internal T/2 positions (`PositionOfChip`, `Ms110dDemodulator.cs:1559`: `_chip0 + 2 x chip + _tau`). Converting to an input sample index follows the formula the C++ port uses (`src/preamble_detector.cpp:652`): `sample = 2 x t2 + 1 - (taps - 1) / 2`, where `taps` is the receive filter length (`_rxPulse.Length`), the subtraction removing the filter's group delay. One chip is one 2400 Bd symbol, 4 input samples. The C# gets a private `SampleOfChip(double chip)` doing exactly this so the two ports agree to the sample.

### 3.2 Burst start

New C# event `BurstStarted(Ms110dBurstStart)`, raised at the same moment as the C++ `BURST_STARTED`: when the WID has been read and accepted, which is the `EnterTracking` call (`:1250`) that sets `Lock` and the state to `Tracking`. Not earlier. The `ReadingPreamble` state starts on a candidate peak that can still be rejected (the retry logic of #12), and a 5066 node must not treat a rejected candidate as a burst.

`CarrierDetect` keeps its current meaning (true from the candidate peak, `:632`), which is the earlier and more cautious DCD the 5066 plan chose for Annex K (plan 7.2). The stack polls it for sensing and uses the event for everything else.

`Ms110dBurstStart` carries:

- `LockSample`: `SamplesProcessed` when the event is raised (the C++ `sample`).
- `DataStartSample`: the input sample at which the first data chip arrived, from `_dataStartChip` (`:1316`) through `SampleOfChip`. This is a position on the air, in the past relative to `LockSample` by the filter delay and the super-frames read after the match.
- `FirstSeenSample`: the input sample of chip 0 of the earliest super-frame the receiver matched (`_earliestChip0` when `_trackFromEarliest`, else `_chip0`). The transmission may have started earlier (super-frames the receiver did not match, and any TLC section), so this is a lower bound on how long the signal has been on the air, not the keying instant. It is what a CSMA implementation gets for "when did this carrier appear".
- `Lock`: the `Ms110dLockInfo` (WN, interleaver, K, CFO at lock).
- `PreambleSnrDb`: section 2.4.

The C++ event gains `data_start_sample` and `first_seen_sample` (the port has `data_start_sample` internally at `preamble_detector.cpp:652`) and the preamble SNR.

### 3.3 Burst end

`Ms110dBurst` gains:

- `StartSample`: the `DataStartSample` of the burst (repeated so a consumer that only subscribes to `BurstCompleted` has both ends).
- `EndSample`: `SamplesProcessed` when the exit was decided. For `Eom` and `MaxInputDataBlocks` that is at the decode of the last block; for `Terminated`, `SignalLost`, `SignalAbsent` and `NewPreamble` it is when that exit fired. The C++ `BURST_COMPLETED.sample` is already this.
- `LastFrameEndSample` (nullable): the input sample at which the last decoded block's final mini-probe ended (its last chip plus one, through `SampleOfChip`), null when no block was decoded. This is the on-air end for a burst that ended by EOM or block count, without the receiver's decode latency, and is what the 5066 EOT bookkeeping compares against. For `SignalLost` it is the end of the last decoded block, which is before the signal actually stopped; the stack treats it as a lower bound.

Why both: a half-duplex turnaround timer should start from when the signal left the air (`LastFrameEndSample`), while a "the modem is free now" decision uses `EndSample`. They differ by the last block's interleaver span plus decode time, up to about 8 s for a Long interleaver.

### 3.4 `transmit_done`

The library produces a complete burst of audio (`Modulate`, or the C++ `ms110d_tx_read` loop ending with 0 written). It cannot know when the sound card has played it, so `transmit_done` is not a library event. The library's part is to make the burst's length exact and known before transmission starts: `Ms110dBurstPlan.Samples` (section 5) equals the length of the array `Modulate` returns, including the pulse-shaping tail; the C++ `ms110d_tx_audio_length` already gives this.

The host (pdn-soundmodem's `IModem`, or whatever plays the audio) raises `transmit_done` when the last of those samples has left its output device, plus its known output latency, and the 5066 station layer takes that as the end of its own burst. This document records that division so that nobody looks for the event in the modem. The pdn-soundmodem change is in section 8.3 as a follow-up outside this repo.

### 3.5 Per-block timing

`Ms110dRxBlock` gains `EndSample`: the input sample at which the block's last mini-probe ended (the same quantity `LastFrameEndSample` holds for the final block). A 5066 D_PDU sync search that runs per block can then timestamp each D_PDU to the block it came from. C++: `block_end_sample` on `BLOCK_DECODED`.

## 4. Run-time maximum input data blocks

110D D.5.4.5.3 ends a reception after a configured number of input data blocks, zero meaning unlimited, and says an ARQ protocol with fixed packet sizes sends the count to the far end, which passes it to its receiving modem over the remote control interface [110D D.5.4.5.3, p.239; D.5.4.6 c, p.239]. Today the count is fixed at construction (`Ms110dDemodOptions.MaxInputDataBlocks`, checked at `Ms110dDemodulator.cs:3437`; C++ `rx_options.max_input_data_blocks`).

Addition: a settable property `Ms110dDemodulator.MaxInputDataBlocks { get; set; }`, initialised from the option, and C `ms110d_rx_set_max_input_data_blocks(rx, n)` plus `ms110d_rx_get_max_input_data_blocks`. Semantics:

- 0 is unlimited; negative is `ArgumentOutOfRangeException` / `MS110D_ERR_INVALID_ARGUMENT`.
- The value applies to the burst in progress and to every later burst until changed. It is not reset by a burst ending or by `Reset()` (which restores the option's value, as construction does: `Reset` is "back to the start of a stream").
- The check runs where it does now, after each block is decoded (`:3437`). Setting a value at or below the number of blocks already decoded in the current burst ends that burst at once with `MaxInputDataBlocks` (the setter calls the same completion path), so the caller never has to wait for one more block to see the limit take effect.

The options record keeps `MaxInputDataBlocks` as the initial value; nothing existing changes.

## 5. PlanBurst

A pure function over the mode tables: no modulator instance, no audio. Both ports.

C#:

    public static Ms110dBurstPlan Ms110dBurstPlan.Plan(Ms110dTxSettings settings, int payloadBitCount)

returning a record with:

| Field | Meaning | Source |
|---|---|---|
| `WaveformNumber`, `Interleaver`, `ConstraintLength` | echoed from the settings | |
| `InputBitsPerBlock` | Table D-XXXVII input bits for (WN, interleaver) | `Ms110dInterleaverParams.Get3k(wn, kind).InputBits` |
| `BytesPerBlock` | `InputBitsPerBlock / 8` (every 3 kHz value is a multiple of 8; D.5.4.1.3 says blocks are byte multiples) | derived |
| `Blocks` | `max(1, ceil((payloadBits + (AppendEom ? 32 : 0)) / InputBitsPerBlock))` | the same arithmetic as `BurstSeconds`, `Ms110dModulator.cs:147-152` |
| `PayloadCapacityBits` | `Blocks x InputBitsPerBlock - (AppendEom ? 32 : 0)`: the most payload bits this block count can carry | derived |
| `PreambleSymbols` | `TlcBlocks x 32 + M x (M == 1 ? 10 : 18) x 32` | `Ms110dModulator.cs:153-154`; 110D D.5.2.1 |
| `DataSymbols` | WN 0: `Blocks x Frames x 32`; else `K + Blocks x Frames x (U + K) + (AppendEot ? 32 : 0)` | `Ms110dModulator.cs:155-157` plus the EOT extension at `:185-197` |
| `AirSymbols` | `PreambleSymbols + DataSymbols` | |
| `Seconds` | `AirSymbols / 2400.0`, the time the signal occupies the air | |
| `Samples` | `AirSymbols x 4 + pulseLength(RollOff)`: exactly the length `Modulate` returns (`Ms110dModulator.cs:203`) | |
| `BlockSeconds` | `Frames x (U + K) / 2400.0` (WN 0: `Frames x 32 / 2400.0`): one interleaver block on the air | derived, matches `docs/waveforms.md` |

Two things this fixes relative to `BurstSeconds`: it includes the 32-symbol EOT extension when `AppendEot` is on (`BurstSeconds` omits it, 13.3 ms), and it gives the sample count including the pulse tail. `BurstSeconds` itself is left exactly as it is in this release so pdn-mailcast's airtime sums do not move by 13 ms without that project choosing to; its doc comment gains a line pointing at `Plan`.

Also a helper the stack's block alignment wants: `Ms110dBurstPlan.ForBlocks(settings, blocks)`, the plan for a given block count (payload = `PayloadCapacityBits`), so a sender can ask "how long are N blocks" without first inventing a byte count.

Validation: for every supported (WN, interleaver, K, EOM, EOT, M in {1, 3, 20}) and payload sizes 0, 1, one block exactly, one block plus one bit: `Plan(...).Samples == Modulate(bits).Length`, `Plan(...).Blocks` equals the block count `BuildSymbols` produced, and `Plan(...).Seconds - BurstSeconds(bits)` is 0 with EOT off and 32/2400 with it on.

C: `ms110d_tx_plan(const ms110d_tx_settings*, uint32_t payload_bits, ms110d_tx_plan* out)` with a `struct_size`-first output struct holding the same fields as fixed-width integers and a `double seconds`; validated against `ms110d_tx_audio_length` and against the C# values in the reference manifest.

## 6. EOT detection (D.5.4.4)

The transmitter may extend the final mini-probe by 32 symbols as an end-of-transmission marker so a receiver can end reception without waiting for the EOM in the decoded data; receiver search for it is optional [110D D.5.4.4, pp.236-237]. We transmit it (`AppendEot`, default on); neither port looks for it.

What it would buy a 5066 node: nothing when the last block decodes, because the EOM ends the burst at that decode and the decode happens as soon as the block's last probe is in (plus the turbo pass). It helps in two cases: when the last block fails to decode (no EOM found), where today the receiver runs on until `SignalLost` (about 4 s of bad probes, `Ms110dDemodulator.cs:2346`) or `SignalAbsent` (16 s), both too slow for an ARQ turnaround; and when the sender inhibits EOM (plan 5.4 keeps it on). The 5066 stack has its own answers for the first case: the D_PDU EOT field predicts the end time from any earlier D_PDU it did decode, and fixed-size ARQ bursts can use the block count of section 4.

Decision: **later**, as the last work item (item 10), after the estimator and timing have landed and the stack's T3/T4 simulations (5066 plan 7.2) show how often a lost last block costs a turnaround. Design for when it is done, so the item is self-contained:

- After each mini-probe, correlate the 32 symbols that follow it against the probe's cyclic extension (`MiniProbe.Sequence(K)`, the same construction `AppendProbe` uses at `Ms110dModulator.cs:191-197`, shifted when the probe was a boundary probe) using the equaliser output. Data symbols are scrambled and uncorrelated with it; the extension gives a coherent sum of 32.
- Declare EOT when the normalised correlation exceeds a threshold set from the measured probe gain (the same reference `_probeGainRef` the bad-probe test uses), two conditions: the coherent gain is at least half the reference and the residual about it is below twice the running N_p. Thresholds come from a false-alarm measurement, not from here.
- On EOT: finish the current block if its last frame is complete (it is, by construction), raise `BurstCompleted` with a new reason `Ms110dBurstEndReason.Eot = 6` / `MS110D_END_EOT = 6`, and `LastFrameEndSample` at the end of the extension.
- Validation: on AWGN and Poor at each WN's mask SNR, zero false EOTs over the mask runs' total frame count (a false EOT truncates a burst, which the bit counts would show), and detection on at least 99% of bursts at mask SNR + 6 dB. If the false-alarm rate cannot be made zero at the mask SNR for a WN, detection stays off for that WN.
- Opt-in for a release: `Ms110dDemodOptions.DetectEot` (default false) until measured, then default true in a later minor.

## 7. API additions, C# and C

Everything below is additive. No existing signature changes. C# positional records gain **non-positional `init` properties** so the existing constructors keep their arity (a compiled consumer calling `new Ms110dBurst(bits, reason, blocks, lock)` keeps binding).

### 7.1 C# (`M0LTE.Ms110d`)

    public sealed record Ms110dBurstStart(
        long LockSample, long DataStartSample, long FirstSeenSample, Ms110dLockInfo Lock)
    {
        public double? PreambleSnrDb { get; init; }
    }

    public sealed record Ms110dRxBlock(int Index, byte[] Bits)
    {
        public double? SnrDb { get; init; }        // section 2, null for WN 0
        public int ProbesUsed { get; init; }
        public int ProbesSkipped { get; init; }
        public long EndSample { get; init; }       // section 3.5
    }

    public sealed record Ms110dBurst(byte[] PayloadBits, Ms110dBurstEndReason Reason, int Blocks, Ms110dLockInfo? Lock = null)
    {
        public double? SnrDb { get; init; }
        public int ProbesUsed { get; init; }
        public int ProbesSkipped { get; init; }
        public long StartSample { get; init; }
        public long EndSample { get; init; }
        public long? LastFrameEndSample { get; init; }
    }

    public sealed class Ms110dDemodulator
    {
        public long SamplesProcessed { get; }
        public int MaxInputDataBlocks { get; set; }           // section 4
        public event Action<Ms110dBurstStart>? BurstStarted;  // section 3.2
    }

    public sealed record Ms110dBurstPlan(...)                 // section 5
    {
        public static Ms110dBurstPlan Plan(Ms110dTxSettings settings, int payloadBitCount);
        public static Ms110dBurstPlan ForBlocks(Ms110dTxSettings settings, int blocks);
    }

    public enum Ms110dBurstEndReason { ..., Eot = 6 }         // item 10 only
    public sealed record Ms110dDemodOptions { public bool DetectEot { get; init; } }  // item 10 only

The approval test `tests/M0LTE.Ms110d.Tests/PublicApiTests.cs` moves `PublicApi.approved.txt` with each item, and the diff of that file is the review artefact for each PR, as `docs/versioning.md` requires.

### 7.2 C (`include/m0lte/ms110d/ms110d.h`)

The event struct grows past its published 64 bytes, which the `struct_size` rule allows without an ABI bump: the library writes only up to the caller's `struct_size`, old callers keep their 64-byte view (`docs/dev/c-interface.md` item "Event struct"). New fields, appended in this order so that every 8-byte field stays 8-byte aligned:

    typedef struct ms110d_rx_event {
        ...                             // the 64 published bytes, unchanged
        int64_t data_start_sample;      // BURST_STARTED, BURST_COMPLETED (as start)
        int64_t first_seen_sample;      // BURST_STARTED
        int64_t block_end_sample;       // BLOCK_DECODED
        int64_t last_frame_end_sample;  // BURST_COMPLETED, -1 when no block decoded
        int32_t has_snr;                // 0 or 1
        int32_t probes_used;
        int32_t probes_skipped;
        uint32_t reserved2;             // keeps snr_db at an 8-byte offset
        double snr_db;                  // BURST_STARTED: preamble; BLOCK_DECODED: block; BURST_COMPLETED: burst
    } ms110d_rx_event;                  // 112 bytes

    MS110D_API ms110d_status ms110d_rx_set_max_input_data_blocks(ms110d_rx* rx, int32_t blocks);
    MS110D_API ms110d_status ms110d_rx_get_max_input_data_blocks(const ms110d_rx* rx, int32_t* blocks);

    typedef struct ms110d_tx_plan {
        uint32_t struct_size;
        int32_t waveform_number;
        int32_t interleaver;
        int32_t constraint_length;
        int32_t input_bits_per_block;
        int32_t bytes_per_block;
        int32_t blocks;
        int32_t payload_capacity_bits;
        int32_t preamble_symbols;
        int32_t data_symbols;
        int32_t air_symbols;
        uint32_t samples;
        double seconds;
        double block_seconds;
    } ms110d_tx_plan;
    MS110D_API ms110d_status ms110d_tx_plan(const ms110d_tx_settings* settings, uint32_t payload_bits, ms110d_tx_plan* plan);
    MS110D_API ms110d_status ms110d_tx_plan_for_blocks(const ms110d_tx_settings* settings, uint32_t blocks, ms110d_tx_plan* plan);

    #define MS110D_END_EOT 6            // item 10 only; plus detect_eot in ms110d_rx_options (grows that struct, same rule)

Each new struct and the grown event go into `tests/c_layout_check.c` with `_Static_assert`s on size and the offsets of the 8-byte fields, on the host and the Cortex-M7 job, as the interface rules require. The C++ `RxEvent` and `RxOptions` get the same fields with the same names, and `tests/api/public-api.txt` moves per `docs/dev/api-lock.md`. `MS110D_ABI_VERSION` stays 1 for all of it (0.x, additions only); it would move only if the EOT option's growth of `ms110d_rx_options` were done any other way than by `struct_size`.

## 8. Versioning and consumer impact

### 8.1 M0LTE.Ms110d (C#)

By the letter of `docs/versioning.md` an additive change in 0.x is a PATCH. By the repo's practice it is a MINOR: v0.4.0 added one enum member (`NewPreamble`) and bumped the minor. This design follows the practice: the items ship as **0.5.0** (the first item that touches the snapshot bumps `Directory.Build.props` to 0.5.0; later items in the same release line keep 0.5.0 until it is tagged; items landing after the tag go to 0.6.0 if additive to the snapshot, 0.5.x if not). Item 10 (EOT), if it ever makes detection the default, is a behavioural change for a receiver fed EOT-extended bursts (a burst can now end `Eot` where it used to end `Eom` or `SignalLost`), and gets its own minor with that in the notes.

The estimator must not change decoded bits: the mask suites are the gate (2.5, "decode unchanged"). The receiver's internals hold the accumulators but never read them back into tracking or decoding.

### 8.2 ms110d-cpp

Additions only: MINOR per its `docs/versioning.md` (0.x: "a breaking change bumps MINOR, and an addition or a fix bumps PATCH", so by its letter PATCH; the same practice argument applies and the two libraries should move together, 0.x.0 for both). `MS110D_ABI_VERSION` stays 1. `reference/pin.txt` moves to the C# release that carries the SNR fields before the C++ SNR item is tested, in its own PR, as that repo's rules say.

### 8.3 Consumers

pdn-soundmodem (pins `M0LTE.Ms110d` 0.4.1 in `Directory.Packages.props`; wrapper `src/Packet.SoundModem/Modems/Ms110dModem.cs` on `origin/main`): nothing changes until it moves the pin. Every existing call it makes (`new Ms110dDemodulator(options)`, `BlockDecoded`, `BurstCompleted`, `Lock`, `CarrierDetect`, `BurstSeconds`) keeps its signature and behaviour. When it adopts, the useful follow-ups are: fill `FrameQuality.SnrDb` from `Ms110dBurst.SnrDb` (that field exists and is null for this modem today, `Modems/FrameQuality.cs:165`); expose a `TransmitDone` on `IModem` from the audio player for section 3.4; and extend KISS SETHW or a new control for max input blocks. None of that is in this repo.

pdn-mailcast (head end uses `Ms110dModulator.BurstSeconds` through `Ms110dAirtime`, `src/Mailcast.HeadEnd/Slot/Airtime.cs:66-85`; the receiver reads `Ms110dDemodulator.Lock` by reflection into pdn-soundmodem's private field, `src/Mailcast.Receiver/Audio/BurstWatch.cs:147`, and `Ms110dModulator.BuildSymbols` by reflection, `Audio/BurstReference.cs:24`): nothing changes. `BurstSeconds` is untouched, `BuildSymbols` keeps its name and signature, `Lock` keeps its type. When it adopts, its receiver can replace the reflection on `_rx` with the `BurstStarted` event if pdn-soundmodem forwards it, and show the SNR on its channel page.

M0LTE.S5066: consumes 0.5.0 from the first item that lands (it is the reason for the work), one feature at a time behind its own modem port.

## 9. Work breakdown

PR-sized, in order. Each has C# and C++ halves; the C++ half follows the C# release it tests against (pin bump first, as that repo requires). A reviewer per PR, merge on green, per the standing rules. Items 1 to 3 are independent of each other and could run in parallel on separate branches; 4 depends on 1 (it uses the block end sample for the row bookkeeping and the reference manifest format); 5 to 7 depend on 4.

| # | Item | C# | C++ | Tests |
|---|---|---|---|---|
| 1 | Sample counter and timing fields | `SamplesProcessed`; private `SampleOfChip`; `Ms110dRxBlock.EndSample`; `Ms110dBurst.StartSample`, `EndSample`, `LastFrameEndSample`; `Ms110dBurstStart` record and `BurstStarted` event | event grows by `data_start_sample`, `first_seen_sample`, `block_end_sample`, `last_frame_end_sample`; layout check | Loopback at every WN and interleaver: `BurstStarted.DataStartSample` within 4 samples (one chip) of the known preamble length in `Modulate`'s output offset; `LastFrameEndSample - DataStartSample` equals `Plan.DataSymbols x 4` within 4 samples; `EndSample >= LastFrameEndSample`; block end samples strictly increasing by `BlockSeconds x 9600`; `BurstStarted` not raised for a rejected candidate (the #12 retry fixtures). C++: same values within 4 samples on the reference audio. Mask bit counts unchanged. |
| 2 | Run-time max input data blocks | `MaxInputDataBlocks { get; set; }`, immediate completion rule | `ms110d_rx_set/get_max_input_data_blocks` | Set to N before a burst of N+2 blocks: ends after N with `MaxInputDataBlocks`; set to N mid-burst after N+1 decoded: ends at once; 0 restores unlimited; negative throws; `Reset()` restores the option; C++ the same through the C API and the C++ class. |
| 3 | PlanBurst | `Ms110dBurstPlan.Plan`, `ForBlocks`; doc line on `BurstSeconds` | `ms110d_tx_plan`, `ms110d_tx_plan_for_blocks`, layout check | Section 5's exhaustive comparison against `Modulate` length and `BurstSeconds`; a table test of `BytesPerBlock` against the D-XXXVII rows in `docs/waveforms.md`; C++ against `ms110d_tx_audio_length` and the C# values in the reference manifest. |
| 4 | SNR estimator, AWGN | per-probe accumulators in `ProcessFrame`; `Ms110dRxBlock.SnrDb`, `ProbesUsed/Skipped`; `Ms110dBurst.SnrDb`, `ProbesUsed/Skipped`; RefGen manifest gains the values | the same accumulators; event `has_snr`, `snr_db`, `probes_used`, `probes_skipped`; reference compare at 0.1 dB | Section 2.5 AWGN gates (bias, block spread, monotonic, saturation recorded), all WN 1 to 8 and 13, Short and Long; WN 0 reports null; mask bit counts unchanged with the estimator on; `DisableTurbo` leaves the SNR unchanged. |
| 5 | Preamble SNR at lock | `Ms110dBurstStart.PreambleSnrDb` from the matched super-frames | event `snr_db` on `BURST_STARTED` | AWGN within 2 dB for WN 0 to 8 and 13 at M = 3 and M = 20; null never on a locked burst. |
| 6 | SNR on Poor and the calibration table | none in code; `docs/snr-calibration.md` with per-WN offsets, spreads and the saturation points, measured at a named commit | the same runs on the C++ to confirm the 0.1 dB parity holds on Poor | Section 2.5 Poor gates behind `MS110D_MASKS_POOR=1`; the doc is the deliverable. |
| 7 | Release 0.5.0 and the C++ pin bump | version, approved API, release notes naming each addition | pin to v0.5.0, regenerate reference, release | The full suites on both; the interop tests on the new package. |
| 8 | Consumer follow-ups (other repos, not gating) | pdn-soundmodem: pin, `FrameQuality.SnrDb`, `TransmitDone`; pdn-mailcast: pin only | | Their own. |
| 9 | WN 0 in-burst SNR (optional) | Walsh-correlator estimate in `TrackWalsh` | the same | AWGN within 2 dB at -6, -1, +4, +14 dB; the WN 0 masks unchanged. |
| 10 | EOT detection (optional, section 6) | `DetectEot` option, `Eot` end reason, detector after each probe | the same, `MS110D_END_EOT`, option struct grows | Section 6's false-alarm and detection gates per WN. |

Ten items; items 1 to 7 are the phase-2 deliverable, 8 is in other repos, 9 and 10 are optional.

## 10. Open points and non-goals

- **Not in scope:** WN 9 to 12 (the 5066 rate ladder caps at WN 8 until they exist, plan 5.2 item 5); an Appendix A TCP interface; any socket or remote-control wire format (the 5066 modem port is in-process); a block "decode failed" flag. The last is attractive (notes D.5 item 2) but a tail-biting convolutional decode has no intrinsic failure signal; the probe SNR of the block is the honest proxy, and the stack's D_PDU CRCs are the real check.
- **Preamble length unknown to the receiver.** `FirstSeenSample` is a lower bound (3.2). If the stack ever needs the keying instant it must get it from the sender's own plan over the link, not from the receiver.
- **Bias table ownership.** The Poor offsets (item 6) live in this repo because they are properties of this receiver; the 5066 rate ladder's margins (its plan 6.2) are tuned on top of them in that repo. If the estimator changes, item 6 is re-run and the table's commit moves.
- **Interference.** The estimator's "noise" is everything the equaliser cannot explain: co-channel signals and impulses count as noise, which is what a rate choice wants. Nothing here separates them.
