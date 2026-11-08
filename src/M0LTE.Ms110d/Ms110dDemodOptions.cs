namespace M0LTE.Ms110d;

/// <summary>Receiver state (design §2.6).</summary>
public enum Ms110dRxState
{
    /// <summary>Matched-filtering for the preamble Fixed subsection.</summary>
    Searching,

    /// <summary>Fixed-section peak found; reading downcount + WID symbols.</summary>
    ReadingPreamble,

    /// <summary>Autobaud complete; demodulating data frames.</summary>
    Tracking,
}

/// <summary>Why a burst ended (D.5.4.5 mandatory exits).</summary>
public enum Ms110dBurstEndReason
{
    /// <summary>The EOM marker was found in the decoded bits (D.5.4.5.1 - the receiver
    /// always scans).</summary>
    Eom,

    /// <summary>The configured input-data-block limit was reached (D.5.4.5.3).</summary>
    MaxInputDataBlocks,

    /// <summary>The terminate-receive command (D.5.4.5.2 / D.5.4.6.d).</summary>
    Terminated,

    /// <summary>Probe correlation collapsed / signal disappeared mid-burst.</summary>
    SignalLost,

    /// <summary>The mini-probes were indistinguishable from noise for a whole presence window
    /// (16 s): there is no signal under the lock, whatever the equalizer makes of it. The exit
    /// that lets go of a burst too weak to decode once it has ended (issue #553,
    /// docs/dev/ms110d/signal-absent.md). DFE modes only; WN 0 has no probes.</summary>
    SignalAbsent,

    /// <summary>A new preamble was found and read while this burst was still locked, so the
    /// receiver let go of this burst to receive the new one. A burst that lost its EOM no
    /// longer deafens the receiver to the next transmission.</summary>
    NewPreamble,
}

/// <summary>Autobaud result: what the WID + downcount announced.</summary>
/// <param name="WaveformNumber">Decoded waveform number.</param>
/// <param name="Interleaver">Decoded interleaver option.</param>
/// <param name="ConstraintLength">Decoded constraint length (7 or 9).</param>
/// <param name="CfoHz">Estimated carrier frequency offset at lock.</param>
public sealed record Ms110dLockInfo(
    int WaveformNumber, Ms110dInterleaverKind Interleaver, int ConstraintLength, double CfoHz);

/// <summary>One decoded input-data (interleaver) block.</summary>
/// <param name="Index">Block index within the burst, from 0.</param>
/// <param name="Bits">Decoded info bits (0/1 bytes).</param>
public sealed record Ms110dRxBlock(int Index, byte[] Bits)
{
    /// <summary>The input sample (9600 Hz, counted as <see cref="Ms110dDemodulator.SamplesProcessed"/>)
    /// at which the block's last mini-probe ended. For WN 0, which has no probes, the end of its
    /// last symbol.</summary>
    public long EndSample { get; init; }

    /// <summary>Signal-to-noise ratio in dB over this block's mini-probes, with signal and noise
    /// both in a 3 kHz bandwidth and the signal power the long-term average over data, probe and
    /// preamble symbols (110D C.6.1 convention). Measured from each probe's residual at the
    /// equaliser output before the probe's own solve, so on a fading channel it is an effective
    /// post-equaliser SNR, weighted towards the fades, which saturates well below the channel's
    /// average SNR (docs/snr-calibration.md). On AWGN it reads the channel SNR, calibrated at the
    /// default equaliser settings (not with <see cref="Ms110dDemodOptions.RlsForgettingFactor"/>
    /// or <see cref="Ms110dDemodOptions.TrackRidge"/> set). Clamped to [-10, +40] dB; a value at the
    /// clamp means beyond what the estimator resolves. Null for WN 0 (no probes) and in genie
    /// mode.</summary>
    public double? SnrDb { get; init; }

    /// <summary>Mini-probes behind <see cref="SnrDb"/>, including the bad ones.</summary>
    public int ProbesUsed { get; init; }

    /// <summary>How many of <see cref="ProbesUsed"/> the receiver flagged as bad for tracking
    /// (probe correlation below its signal-lost line, typically a deep fade).</summary>
    public int ProbesBad { get; init; }
}

/// <summary>A completed burst.</summary>
/// <param name="PayloadBits">Decoded bits up to (not including) the EOM, or all decoded
/// bits when no EOM was seen.</param>
/// <param name="Reason">Which D.5.4.5 exit ended the burst.</param>
/// <param name="Blocks">Input-data blocks decoded.</param>
/// <param name="Lock">What the preamble announced for the burst, if it got that far.</param>
public sealed record Ms110dBurst(
    byte[] PayloadBits, Ms110dBurstEndReason Reason, int Blocks, Ms110dLockInfo? Lock = null)
{
    /// <summary>The burst's <see cref="Ms110dBurstStart.DataStartSample"/>, repeated so a
    /// consumer that only listens for completed bursts has both ends. 0 when the burst never
    /// locked.</summary>
    public long StartSample { get; init; }

    /// <summary>The value of <see cref="Ms110dDemodulator.SamplesProcessed"/> when the exit was
    /// decided: at the decode of the last block for <see cref="Ms110dBurstEndReason.Eom"/> and
    /// <see cref="Ms110dBurstEndReason.MaxInputDataBlocks"/>, otherwise when that exit fired.</summary>
    public long EndSample { get; init; }

    /// <summary>The input sample at which the last decoded block's final mini-probe ended, or
    /// null when no block was decoded. The on-air end of a burst that finished by EOM or block
    /// count, without the receiver's decode latency. For <see cref="Ms110dBurstEndReason.SignalLost"/>
    /// it is the end of the last decoded block, which is before the signal actually stopped, so
    /// treat it as a lower bound.</summary>
    public long? LastFrameEndSample { get; init; }

    /// <summary>Signal-to-noise ratio in dB over every mini-probe of the burst, on the same
    /// definition as <see cref="Ms110dRxBlock.SnrDb"/>. For a
    /// <see cref="Ms110dBurstEndReason.SignalLost"/> or <see cref="Ms110dBurstEndReason.NewPreamble"/>
    /// exit the trailing run of bad probes (noise after the signal stopped) is left out. Null when there was no probe
    /// to measure (WN 0, a burst that ended before its first probe) and in genie mode.</summary>
    public double? SnrDb { get; init; }

    /// <summary>Mini-probes behind <see cref="SnrDb"/>, including the bad ones.</summary>
    public int ProbesUsed { get; init; }

    /// <summary>How many of <see cref="ProbesUsed"/> the receiver flagged as bad for tracking.</summary>
    public int ProbesBad { get; init; }
}

/// <summary>Raised when a burst has been locked: the preamble's WID has been read and accepted.
/// A rejected candidate never raises it. Sample values count input samples at 9600 Hz since the
/// first <see cref="Ms110dDemodulator.Process"/> call after construction or
/// <see cref="Ms110dDemodulator.Reset"/>; the host converts them to its own clock.</summary>
/// <param name="LockSample"><see cref="Ms110dDemodulator.SamplesProcessed"/> when the event is
/// raised.</param>
/// <param name="DataStartSample">The input sample at which the first data chip arrived. A
/// position on the air, earlier than <paramref name="LockSample"/>.</param>
/// <param name="FirstSeenSample">The input sample of chip 0 of the earliest preamble super-frame
/// the receiver matched. The transmission may have started earlier, so this is a lower bound on
/// how long the signal has been on the air, not the keying instant.</param>
/// <param name="Lock">What the preamble announced.</param>
public sealed record Ms110dBurstStart(
    long LockSample, long DataStartSample, long FirstSeenSample, Ms110dLockInfo Lock)
{
    /// <summary>Signal-to-noise ratio in dB from the matched preamble super-frame, on the same
    /// definition as <see cref="Ms110dRxBlock.SnrDb"/>, available at lock for every waveform
    /// including WN 0. Measured before any equalisation with one complex gain per 32 chips, so
    /// it is a good figure on a flat channel and reads low on any channel with delay spread
    /// (every echo counts as noise; on the Poor channel it stays within a few dB of 0 dB
    /// whatever the SNR, docs/snr-calibration.md). An early, coarse
    /// number, not an input to a rate choice on its own. Clamped to [-10, +40] dB; null in genie
    /// mode.</summary>
    public double? PreambleSnrDb { get; init; }
}

/// <summary>
/// Receiver options - the RX half of the D.5.4.6 remote-control parameter list. Any
/// real-world leniency discovered later becomes a named flag here (house rule).
/// </summary>
public sealed record Ms110dDemodOptions
{
    /// <summary>Stop after this many input-data blocks; 0 = unlimited (D.5.4.5.3). The initial
    /// value of <see cref="Ms110dDemodulator.MaxInputDataBlocks"/>, which can be changed at run time.</summary>
    public int MaxInputDataBlocks { get; init; }

    /// <summary>Normalized matched-filter threshold for preamble detection (house number,
    /// characterized by the acquisition tests).</summary>
    public double SyncThreshold { get; init; } = 0.32;

    /// <summary>RLS forgetting factor λ override. Null (the default) keeps the measured
    /// frame-tied policy λ = 1 − ln10/U - a DOCUMENTED DEVIATION from design §2.5's fixed
    /// 0.995 (see the comment at the BeginRls call, issue #64). The §2.5 value ties RLS
    /// memory to the 1 Hz coherence time instead of the frame; the Phase B RLS-vs-NLMS
    /// A/B (phase-b-plan §B2.4) measures both through this knob and the report decides
    /// which becomes the default.</summary>
    public float? RlsForgettingFactor { get; init; }

    /// <summary>Skip the chain-BCJR turbo re-equalization pass (§B2.3), decoding from
    /// first-pass LLRs alone. An equalizer-complexity control in the D.5.4.6 spirit, and
    /// the §B3 instrument for attributing decode damage to the turbo pass (the first-pass
    /// telemetry is pre-turbo, so a poisoned turbo is invisible to it - issue #69).</summary>
    public bool DisableTurbo { get; init; }

    /// <summary>Per-probe anchored-solve ridge override (all modes). Null (the default)
    /// keeps the measured per-K values. The anchor ridge IS the equalizer's cross-frame
    /// memory (a Kalman-style prior toward the current taps), so this knob trades solve
    /// noise against tracking lag - the §B3.2 A/B instrument for the flat estimation-noise
    /// tax the WN2 genie pair measured (issue #69). Report evidence only, never a gate
    /// default without a full-budget A/B.</summary>
    public float? TrackRidge { get; init; }

    /// <summary>§B4.1 per-segment BCJR noise pricing variant. Null (the default) runs the
    /// SHIPPED form, <c>"spikeup"</c>: a segment's windowed floor replaces the frame
    /// constant only where it exceeds it by the segment's own 3σ χ² band (exp(3/√count))
    /// - upward-only, so pricing can de-confidence locally-bad spans but can never inject
    /// an over-confident low floor (the §B3.3 WN2 damage direction; WN2's flat-floor
    /// truth cannot cross its own 24-dof 2.4× band, so it prices frame-constant by
    /// construction - its battery censuses are bit-identical either way). <c>"off"</c>
    /// forces the pre-B4.1 frame-constant floor; <c>"spike2s"</c> is the two-sided
    /// measurement variant. Thresholds are derived from the segment dof, never tuned
    /// (evidence/2026-07-26-phase-b41-wn6floor/, ship decision in Amendment 2's ladder:
    /// WN6 6M 57/57 → 35/39 both families with every other point held).</summary>
    public string? TurboNsegMode { get; init; }

    /// <summary>Roll-off of the SRRC receive matched filter, 0.05 to 1. Default 0.35, the
    /// D.5.1.5 value every MS110D transmitter is assumed to use. A receiver that knows its
    /// transmitter shapes narrower can match it here; left at 0.35 it still decodes a narrower
    /// signal, the equalizer absorbing the mismatch (docs/dev/ms110d/evidence/2026-10-04-filter-width/).</summary>
    public double RollOff { get; init; } = Ms110dModulator.RollOff;

    /// <summary>Instrument knob (issue #553 tuning): never end a burst as
    /// <see cref="Ms110dBurstEndReason.SignalAbsent"/>, so the receiver behaves exactly as it
    /// did before that exit existed while the statistic is still computed and traced.</summary>
    public bool PresenceReleaseOff { get; init; }
}
