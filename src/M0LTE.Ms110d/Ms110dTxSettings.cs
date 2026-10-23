namespace M0LTE.Ms110d;

/// <summary>
/// Transmit configuration for the Appendix D 3 kHz waveform - the TX half of the D.5.4.6
/// remote-control parameter list (bandwidth is fixed at 3 kHz in Phase A).
/// </summary>
public sealed record Ms110dTxSettings
{
    /// <summary>Waveform number (Phase A: 0-6 and 13). Default WN 6 (3200 bps QPSK).</summary>
    public int WaveformNumber { get; init; } = 6;

    /// <summary>Interleaver length option (WID d5d4). Default Short.</summary>
    public Ms110dInterleaverKind Interleaver { get; init; } = Ms110dInterleaverKind.Short;

    /// <summary>Convolutional constraint length, 7 or 9 (WID d3). Default 7.</summary>
    public int ConstraintLength { get; init; } = 7;

    /// <summary>Preamble super-frame repeats M, 1-32 (D.5.2.1.3). Default 3. The D.6
    /// performance tests use 20.</summary>
    public int PreambleSuperframes { get; init; } = 3;

    /// <summary>TLC section blocks N, 0-255 (D.5.2.1.2; 0 omits the section). Default 0 -
    /// the TLC exists for radio AGC/TGC settling, which loopback and simulation skip.</summary>
    public int TlcBlocks { get; init; }

    /// <summary>Append the 32-bit EOM (D.5.4.3) after the data. Default on - burst framing
    /// depends on it (the ARQ-off case).</summary>
    public bool AppendEom { get; init; } = true;

    /// <summary>Extend the final mini-probe by 32 symbols as an EOT marker
    /// (D.5.4.4). Transmitted but not required by the Phase A receiver. No-op for WN 0
    /// (no probes). Default on.</summary>
    public bool AppendEot { get; init; } = true;

    /// <summary>Linear scale applied to the unit-magnitude symbol stream before pulse
    /// shaping. The default keeps peaks comfortably inside ±1.</summary>
    public float Amplitude { get; init; } = 0.5f;

    /// <summary>SRRC pulse-shaping roll-off, 0.05 to 1. Default 0.35, the D.5.1.5
    /// recommendation: 2400 Bd x (1 + roll-off) edge to edge, so 3240 Hz at the default and
    /// 2640 Hz at 0.10. A narrower signal fits a narrower receive filter; the receiver's
    /// matched filter (<see cref="Ms110dDemodOptions.RollOff"/>) is set separately.</summary>
    public double RollOff { get; init; } = Ms110dModulator.RollOff;
}
