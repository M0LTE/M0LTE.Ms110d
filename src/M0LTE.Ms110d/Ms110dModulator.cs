using M0LTE.Fec;
using M0LTE.Dsp;
using M0LTE.Ms110d.Fec;

namespace M0LTE.Ms110d;

/// <summary>
/// Appendix D 3 kHz serial-tone transmitter: 2400 Bd single carrier on an 1800 Hz audio
/// sub-carrier (Table D-I), SRRC 35 % pulse shaping (D.5.1.5), native 9600 Hz
/// (4 samples/symbol - design §4.3). TX construction is byte-exact to spec: preamble
/// (D.5.2.1) → initial mini-probe → [U data + K probe] frames with per-frame scrambler reset
/// (D.5.1.3) and the interleaver-boundary probe cyclically shifted (D.5.2.2); WN 0 sends
/// continuous Walsh channel symbols with no probes (D.5.2).
/// </summary>
public sealed class Ms110dModulator
{
    /// <summary>Native DSP rate: 9600 Hz = 4 samples/symbol at 2400 Bd.</summary>
    public const int NativeRate = 9600;

    /// <summary>SRRC roll-off D.5.1.5 recommends, 0.35, and the default for both the transmit
    /// shaping (<see cref="Ms110dTxSettings.RollOff"/>) and the receive matched filter
    /// (<see cref="Ms110dDemodOptions.RollOff"/>); the OBW tests pin it.</summary>
    public const double RollOff = 0.35;

    /// <summary>Narrowest roll-off either end accepts. Below it the pulse would need a span of
    /// hundreds of symbols to stay band-limited.</summary>
    public const double MinRollOff = 0.05;

    private const int SamplesPerSymbol = 4;

    private static readonly float[] DefaultPulse = DesignPulse(RollOff);

    private readonly Ms110dTxSettings _settings;
    private readonly Ms110dMode _mode;
    private readonly float[] _pulse;

    /// <summary>Creates a transmitter for one waveform configuration.</summary>
    public Ms110dModulator(Ms110dTxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _mode = Ms110dMode.Mode3k(settings.WaveformNumber);
        if (settings.ConstraintLength is not (7 or 9))
        {
            throw new ArgumentException("constraint length must be 7 or 9", nameof(settings));
        }

        ValidateRollOff(settings.RollOff, nameof(settings));
        _pulse = settings.RollOff == RollOff ? DefaultPulse : DesignPulse(settings.RollOff);
    }

    /// <summary>The mode row this transmitter is configured for.</summary>
    public Ms110dMode Mode => _mode;

    /// <summary>Test hook, not part of the public API: mark the shifted mini-probes with the
    /// D.5.2.2 standard rule (long interleaver boundary only) instead of the selected
    /// interleaver's boundary this transmitter sends by default.</summary>
    internal bool StandardProbePattern { get; set; }

    /// <summary>Modulates payload bits (0/1 bytes) into one complete burst at 9600 Hz.</summary>
    public float[] Modulate(ReadOnlySpan<byte> payloadBits)
    {
        Complex32[] symbols = BuildSymbols(payloadBits);
        return Shape(symbols, _settings.Amplitude, _pulse);
    }

    /// <summary>Builds the full burst symbol stream (unit-magnitude, one entry per 2400 Bd
    /// symbol) - exposed for hermetic tests that want the wire symbols pre-DSP.</summary>
    internal Complex32[] BuildSymbols(ReadOnlySpan<byte> payloadBits)
    {
        var preamble = new PreambleGenerator(_settings.TlcBlocks, _settings.PreambleSuperframes);
        byte[] preambleChips = preamble.Generate(
            _mode.Wn, _settings.Interleaver, _settings.ConstraintLength);

        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(_mode.Wn, _settings.Interleaver);
        byte[] bits = Ms110dFraming.BuildTxBits(payloadBits, _settings.AppendEom, il.InputBits);
        int blocks = bits.Length / il.InputBits;

        ConvolutionalCode code = _settings.ConstraintLength == 9 ? ConvolutionalCode.K9 : ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, _mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);

        var symbols = new List<Complex32>(preambleChips.Length + (blocks * il.SizeBits));
        foreach (byte chip in preambleChips)
        {
            symbols.Add(Ms110dTables.Psk8[chip]);
        }

        if (_mode.Wn == 0)
        {
            var walsh = new Wid0WalshModem();
            for (int b = 0; b < blocks; b++)
            {
                byte[] fetched = Ms110dFraming.EncodeBlock(
                    code, puncture, interleaver, bits.AsSpan(b * il.InputBits, il.InputBits));
                walsh.Reset(); // scramble sequence resets at the interleaver boundary
                var chips = new byte[fetched.Length * 16];
                walsh.Modulate(fetched, chips);
                foreach (byte chip in chips)
                {
                    symbols.Add(Ms110dTables.Psk8[chip]);
                }
            }

            return [.. symbols];
        }

        // One probe ends the preamble (design §2.4), training the equalizer before the
        // first data block.
        AppendProbe(symbols, boundary: false, extend: false);

        var scrambler = new Ms110dScrambler();
        for (int b = 0; b < blocks; b++)
        {
            byte[] fetched = Ms110dFraming.EncodeBlock(
                code, puncture, interleaver, bits.AsSpan(b * il.InputBits, il.InputBits));
            int bit = 0;
            for (int f = 0; f < il.Frames; f++)
            {
                scrambler.Reset(); // "initialized to 1 at the start of each data frame"
                for (int u = 0; u < _mode.U; u++)
                {
                    if (_mode.Modulation == Ms110dModulation.Qam16)
                    {
                        int symbolNumber = Transcode(fetched, ref bit);
                        symbols.Add(Ms110dTables.Qam16[scrambler.NextQam(symbolNumber, 4)]);
                    }
                    else
                    {
                        int transcoded = Transcode(fetched, ref bit);
                        symbols.Add(Ms110dTables.Psk8[scrambler.NextPsk(transcoded)]);
                    }
                }

                // The probe after the second-to-last data block of each interleaver block
                // is cyclically shifted (D.5.2.2). With one frame per block every probe
                // precedes a boundary and is shifted.
                long frameNumber = ((long)b * il.Frames) + f;
                bool boundary = StandardProbePattern
                    ? ProbePattern.StandardRule(
                        frameNumber, Ms110dInterleaverParams.Get3k(_mode.Wn, Ms110dInterleaverKind.Long).Frames)
                    : (f + 2) % il.Frames == 0;
                bool last = b == blocks - 1 && f == il.Frames - 1;
                AppendProbe(symbols, boundary, extend: last && _settings.AppendEot);
            }
        }

        return [.. symbols];
    }

    /// <summary>Total burst duration for a payload, in seconds - preamble + frames.</summary>
    public double BurstSeconds(int payloadBitCount)
    {
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(_mode.Wn, _settings.Interleaver);
        int used = payloadBitCount + (_settings.AppendEom ? 32 : 0);
        int blocks = Math.Max(1, (used + il.InputBits - 1) / il.InputBits);
        int preambleChips = (_settings.TlcBlocks * 32) +
            (_settings.PreambleSuperframes * ((_settings.PreambleSuperframes == 1 ? 10 : 18) * 32));
        int dataSymbols = _mode.Wn == 0
            ? blocks * il.Frames * 32
            : _mode.K + (blocks * il.Frames * (_mode.U + _mode.K));
        return (preambleChips + dataSymbols) / (double)Ms110dTables.SymbolRate;
    }

    private int Transcode(byte[] fetched, ref int bit)
    {
        if (_mode.Modulation == Ms110dModulation.Bpsk)
        {
            return fetched[bit++] == 0 ? 0 : 4; // Table D-III
        }

        if (_mode.Modulation == Ms110dModulation.Psk8)
        {
            // Table D-V: 3 fetched bits MSB-first → tribit → 8PSK ring symbol.
            int tribit = (fetched[bit++] << 2) | (fetched[bit++] << 1) | fetched[bit++];
            return Ms110dTables.Transcode8Psk[tribit];
        }

        if (_mode.Modulation == Ms110dModulation.Qam16)
        {
            // No transcode table for QAM: 4 fetched bits MSB-first ARE the symbol number.
            return (fetched[bit++] << 3) | (fetched[bit++] << 2) | (fetched[bit++] << 1) | fetched[bit++];
        }

        // Table D-IV, first fetched bit = MSB (D.5.1.2.1.2 / D.5.3.1): 00→0, 01→2, 11→4, 10→6.
        int msb = fetched[bit++];
        int lsb = fetched[bit++];
        return ((msb << 1) | lsb) switch { 0 => 0, 1 => 2, 3 => 4, _ => 6 };
    }

    private void AppendProbe(List<Complex32> symbols, bool boundary, bool extend)
    {
        Complex32[] probe = MiniProbe.Get(_mode.K, boundary);
        symbols.AddRange(probe);
        if (extend)
        {
            // EOT (D.5.4.4): cyclic extension of the final mini-probe by 32 symbols.
            (Complex32[] baseSeq, int shift) = MiniProbe.Sequence(_mode.K);
            int offset = boundary ? shift : 0;
            for (int i = _mode.K; i < _mode.K + 32; i++)
            {
                symbols.Add(baseSeq[(i + offset) % baseSeq.Length]);
            }
        }
    }

    private static float[] Shape(Complex32[] symbols, float amplitude, float[] pulse)
    {
        int length = (symbols.Length * SamplesPerSymbol) + pulse.Length;
        var re = new float[length];
        var im = new float[length];
        for (int n = 0; n < symbols.Length; n++)
        {
            Complex32 s = symbols[n] * amplitude;
            int start = n * SamplesPerSymbol;
            for (int k = 0; k < pulse.Length; k++)
            {
                re[start + k] += s.Re * pulse[k];
                im[start + k] += s.Im * pulse[k];
            }
        }

        // Mix to the 1800 Hz sub-carrier: 1800/9600 = 3/16 cycles per sample.
        var audio = new float[length];
        for (int i = 0; i < length; i++)
        {
            double phase = 2.0 * Math.PI * 3.0 * i / 16.0;
            audio[i] = (float)((re[i] * Math.Cos(phase)) - (im[i] * Math.Sin(phase)));
        }

        return audio;
    }

    /// <summary>Throws unless <paramref name="rollOff"/> is in [<see cref="MinRollOff"/>, 1].</summary>
    internal static void ValidateRollOff(double rollOff, string paramName)
    {
        if (!(rollOff >= MinRollOff && rollOff <= 1.0))
        {
            throw new ArgumentException(
                $"SRRC roll-off must be between {MinRollOff} and 1 (got {rollOff})", paramName);
        }
    }

    /// <summary>Pulse span in symbols for a roll-off. 16 (+-8 symbols) at the standard's 0.35 and
    /// wider, so the default waveform is unchanged; a narrower roll-off has a slower-decaying
    /// pulse tail, so the span grows in proportion (capped at 64) to keep the truncation
    /// sidelobes at roughly the 0.35 pulse's level instead of spilling outside the band.</summary>
    public static int PulseSpanSymbols(double rollOff)
    {
        if (rollOff >= RollOff)
        {
            return 16;
        }

        int span = (int)Math.Ceiling(16 * RollOff / rollOff);
        span += span & 1;
        return Math.Min(span, 64);
    }

    /// <summary>The unit-energy SRRC pulse at 4 samples/symbol for a roll-off; shared by the
    /// receiver's matched filter.</summary>
    internal static float[] DesignPulse(double rollOff)
    {
        int taps = (PulseSpanSymbols(rollOff) * SamplesPerSymbol) + 1;
        var pulse = new float[taps];
        double centre = (taps - 1) / 2.0;
        double energy = 0;
        for (int i = 0; i < taps; i++)
        {
            double t = (i - centre) / SamplesPerSymbol;
            pulse[i] = (float)FilterDesign.RootRaisedCosine(t, rollOff);
            energy += pulse[i] * pulse[i];
        }

        float norm = (float)(1.0 / Math.Sqrt(energy));
        for (int i = 0; i < taps; i++)
        {
            pulse[i] *= norm;
        }

        return pulse;
    }
}
