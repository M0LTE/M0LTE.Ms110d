namespace M0LTE.Ms110d;

/// <summary>
/// What one transmitted burst will occupy, worked out from the mode tables alone: no
/// modulator instance and no audio. A caller uses it to size a transmission, to know how
/// many bytes fit in a block, and to know exactly how long the audio from
/// <see cref="Ms110dModulator.Modulate"/> will be.
/// </summary>
/// <remarks>
/// The library has no end-of-transmission ("transmit done") event, because it cannot know when
/// a sound card has played the audio. The host raises that itself, when the last of
/// <see cref="Samples"/> samples has left its output device plus its output latency, stamped
/// with the same host clock it uses for receive times (see
/// <see cref="Ms110dDemodulator.SamplesProcessed"/>). Key-up to that moment should equal
/// <see cref="Seconds"/> plus the output latency.
/// </remarks>
/// <param name="WaveformNumber">Echoed from the settings.</param>
/// <param name="Interleaver">Echoed from the settings.</param>
/// <param name="ConstraintLength">Echoed from the settings.</param>
/// <param name="InputBitsPerBlock">Information bits per interleaver block (Table D-XXXVII).</param>
/// <param name="BytesPerBlock"><paramref name="InputBitsPerBlock"/> divided by 8; every 3 kHz
/// value is a whole number of bytes.</param>
/// <param name="Blocks">Interleaver blocks the burst carries (at least 1).</param>
/// <param name="PayloadCapacityBits">The most payload bits this block count can carry, after
/// the 32-bit EOM when it is appended.</param>
/// <param name="PreambleSymbols">Symbols in the TLC section and preamble super-frames.</param>
/// <param name="DataSymbols">Symbols after the preamble: the initial mini-probe, the data
/// frames with their mini-probes, and the 32-symbol EOT extension when enabled (WN 0 has
/// neither probes nor an extension).</param>
/// <param name="AirSymbols"><paramref name="PreambleSymbols"/> plus <paramref name="DataSymbols"/>.</param>
/// <param name="Seconds">Time the signal occupies the air: <paramref name="AirSymbols"/> / 2400.</param>
/// <param name="Samples">Exactly the length of the array <see cref="Ms110dModulator.Modulate"/>
/// returns, which includes the pulse-shaping tail.</param>
/// <param name="BlockSeconds">Time one interleaver block occupies the air.</param>
public sealed record Ms110dBurstPlan(
    int WaveformNumber,
    Ms110dInterleaverKind Interleaver,
    int ConstraintLength,
    int InputBitsPerBlock,
    int BytesPerBlock,
    int Blocks,
    int PayloadCapacityBits,
    int PreambleSymbols,
    int DataSymbols,
    int AirSymbols,
    double Seconds,
    int Samples,
    double BlockSeconds)
{
    private const int EomBits = 32;
    private const int EotSymbols = 32;

    /// <summary>The plan for a burst carrying <paramref name="payloadBitCount"/> payload bits.
    /// Unlike <see cref="Ms110dModulator.BurstSeconds"/>, the result includes the EOT extension
    /// when <see cref="Ms110dTxSettings.AppendEot"/> is on, and the sample count includes the
    /// pulse tail.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The payload size is negative, the burst
    /// would be too long for one audio array, or the waveform number is not supported.</exception>
    /// <exception cref="ArgumentException">The constraint length, roll-off or interleaver is
    /// not valid for the settings.</exception>
    public static Ms110dBurstPlan Plan(Ms110dTxSettings settings, int payloadBitCount)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadBitCount);
        Ms110dInterleaverParams il = Validate(settings);
        long used = (long)payloadBitCount + (settings.AppendEom ? EomBits : 0);
        long blocks = Math.Max(1, (used + il.InputBits - 1) / il.InputBits);
        if (blocks > int.MaxValue / il.InputBits)
        {
            throw new ArgumentOutOfRangeException(nameof(payloadBitCount), payloadBitCount, "payload too large");
        }

        return Build(settings, il, (int)blocks);
    }

    /// <summary>The plan for a burst of exactly <paramref name="blocks"/> interleaver blocks,
    /// carrying <see cref="PayloadCapacityBits"/> payload bits.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The block count is less than 1, the burst
    /// would be too long for one audio array, or the waveform number is not supported.</exception>
    public static Ms110dBurstPlan ForBlocks(Ms110dTxSettings settings, int blocks)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(blocks, 1);
        Ms110dInterleaverParams il = Validate(settings);
        if (blocks > int.MaxValue / il.InputBits)
        {
            throw new ArgumentOutOfRangeException(nameof(blocks), blocks, "too many blocks");
        }

        return Build(settings, il, blocks);
    }

    private static Ms110dInterleaverParams Validate(Ms110dTxSettings settings)
    {
        if (settings.ConstraintLength is not (7 or 9))
        {
            throw new ArgumentException("constraint length must be 7 or 9", nameof(settings));
        }

        Ms110dModulator.ValidateRollOff(settings.RollOff, nameof(settings));
        return Ms110dInterleaverParams.Get3k(settings.WaveformNumber, settings.Interleaver);
    }

    private static Ms110dBurstPlan Build(Ms110dTxSettings settings, Ms110dInterleaverParams il, int blocks)
    {
        Ms110dMode mode = Ms110dMode.Mode3k(settings.WaveformNumber);
        int m = settings.PreambleSuperframes;
        int preamble = (settings.TlcBlocks * 32) + (m * ((m == 1 ? 10 : 18) * 32));
        int blockSymbols = mode.Wn == 0 ? il.Frames * 32 : il.Frames * (mode.U + mode.K);
        long dataLong = mode.Wn == 0
            ? (long)blocks * blockSymbols
            : mode.K + ((long)blocks * blockSymbols) + (settings.AppendEot ? EotSymbols : 0);
        int pulseLength = (Ms110dModulator.PulseSpanSymbols(settings.RollOff) * 4) + 1;
        long samples = ((preamble + dataLong) * 4) + pulseLength;
        if (samples > int.MaxValue)
        {
            // Modulate returns one float[], so a burst this long could never be produced.
            throw new ArgumentOutOfRangeException(nameof(settings), "burst too long for one audio array");
        }

        int data = (int)dataLong;
        int air = preamble + data;
        return new Ms110dBurstPlan(
            settings.WaveformNumber,
            settings.Interleaver,
            settings.ConstraintLength,
            il.InputBits,
            il.InputBits / 8,
            blocks,
            Math.Max(0, (blocks * il.InputBits) - (settings.AppendEom ? EomBits : 0)),
            preamble,
            data,
            air,
            air / (double)Ms110dTables.SymbolRate,
            (int)samples,
            blockSymbols / (double)Ms110dTables.SymbolRate);
    }
}
