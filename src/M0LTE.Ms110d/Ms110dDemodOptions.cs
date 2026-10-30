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
public sealed record Ms110dRxBlock(int Index, byte[] Bits);

/// <summary>A completed burst.</summary>
/// <param name="PayloadBits">Decoded bits up to (not including) the EOM, or all decoded
/// bits when no EOM was seen.</param>
/// <param name="Reason">Which D.5.4.5 exit ended the burst.</param>
/// <param name="Blocks">Input-data blocks decoded.</param>
/// <param name="Lock">What the preamble announced for the burst, if it got that far.</param>
public sealed record Ms110dBurst(
    byte[] PayloadBits, Ms110dBurstEndReason Reason, int Blocks, Ms110dLockInfo? Lock = null);

/// <summary>
/// Receiver options - the RX half of the D.5.4.6 remote-control parameter list. Any
/// real-world leniency discovered later becomes a named flag here (house rule).
/// </summary>
public sealed record Ms110dDemodOptions
{
    /// <summary>Stop after this many input-data blocks; 0 = unlimited (D.5.4.5.3).</summary>
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
