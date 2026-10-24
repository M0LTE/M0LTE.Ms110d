using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using M0LTE.Fec;
using M0LTE.Ms110d.Fec;
using M0LTE.Ofdm;

namespace M0LTE.Ms110d.RefGen;

/// <summary>Writes the per-stage transmit reference files for one case. See
/// docs/reference-files.md for the format.</summary>
public static class RefWriter
{
    /// <summary>Bump when the file layout or the meaning of any stage changes.</summary>
    public const int FormatVersion = 2;

    /// <summary>The library version, without any "+commit" suffix.</summary>
    public static string PackageVersion()
    {
        string v = typeof(Ms110dModulator).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
        int plus = v.IndexOf('+', StringComparison.Ordinal);
        return plus >= 0 ? v[..plus] : v;
    }

    /// <summary>HEAD of the git checkout containing the current directory and whether the
    /// tracked files have uncommitted changes; ("unknown", false) outside a checkout.</summary>
    public static (string Commit, bool Dirty) GitState()
    {
        string? head = Git("rev-parse HEAD");
        if (head is null)
        {
            return ("unknown", false);
        }

        string? status = Git("status --porcelain --untracked-files=no");
        return (head, !string.IsNullOrEmpty(status));
    }

    private static string? Git(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>splitmix64, bit i of the payload = bit (i mod 64) of word (i div 64).
    /// Specified here so a port can regenerate payloads; 00_payload.bits is authoritative.</summary>
    public static byte[] Payload(ulong seed, int bits)
    {
        var result = new byte[bits];
        ulong state = seed;
        ulong word = 0;
        for (int i = 0; i < bits; i++)
        {
            if (i % 64 == 0)
            {
                state += 0x9E3779B97F4A7A15UL;
                ulong z = state;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                word = z ^ (z >> 31);
            }

            result[i] = (byte)((word >> (i % 64)) & 1);
        }

        return result;
    }

    /// <summary>Writes one case into <paramref name="caseDir"/> (created if missing).</summary>
    /// <param name="corruptReferenceForTest">Test hook: mutates the tool's own symbol stream before
    /// the cross-check against the library, to prove the check fires. Leave null.</param>
    public static void WriteCase(
        RefCase c, string caseDir, string gitCommit, bool dirty = false,
        Action<List<Cf>>? corruptReferenceForTest = null)
    {
        Directory.CreateDirectory(caseDir);
        Ms110dTxSettings settings = c.Settings;
        var modulator = new Ms110dModulator(settings);
        Ms110dMode mode = modulator.Mode;
        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(mode.Wn, settings.Interleaver);
        ConvolutionalCode code = settings.ConstraintLength == 9 ? ConvolutionalCode.K9 : ConvolutionalCode.K7;
        PunctureSpec puncture = Ms110dPuncture.Get(code, mode.CodeRate);
        var interleaver = new Ms110dInterleaver(il.SizeBits, il.Increment);

        // Stages 0 and 1: payload, then EOM appended and zero-filled to whole blocks.
        byte[] payload = Payload(c.Seed, c.PayloadBits);
        byte[] framed = Ms110dFraming.BuildTxBits(payload, settings.AppendEom, il.InputBits);
        int blocks = framed.Length / il.InputBits;
        int blocksExpected = Math.Max(1, (c.PayloadBits + (settings.AppendEom ? 32 : 0) + il.InputBits - 1) / il.InputBits);
        if (blocks != blocksExpected)
        {
            throw new InvalidOperationException("framed length disagrees with the block-count rule");
        }

        // Stages 2 to 4, block by block, using the same calls EncodeBlock makes.
        var encoded = new List<byte>();
        var punctured = new List<byte>();
        var interleaved = new List<byte>();
        for (int b = 0; b < blocks; b++)
        {
            ReadOnlySpan<byte> info = framed.AsSpan(b * il.InputBits, il.InputBits);
            var coded = new byte[2 * info.Length];
            TailBitingEncoder.Encode(code, info, coded);
            var punc = new byte[puncture.OutputLength(info.Length)];
            Ms110dPuncture.Apply(puncture, coded, punc);
            var fetched = new byte[il.SizeBits];
            interleaver.Interleave(punc, fetched);
            if (!fetched.AsSpan().SequenceEqual(Ms110dFraming.EncodeBlock(code, puncture, interleaver, info)))
            {
                throw new InvalidOperationException("stage split disagrees with Ms110dFraming.EncodeBlock");
            }

            encoded.AddRange(coded);
            punctured.AddRange(punc);
            interleaved.AddRange(fetched);
        }

        // Stage 5: the preamble chips (8PSK indices 0-7).
        var preamble = new PreambleGenerator(settings.TlcBlocks, settings.PreambleSuperframes);
        byte[] preambleChips = preamble.Generate(mode.Wn, settings.Interleaver, settings.ConstraintLength);

        // Stages 6 to 8 and 9: transcoded symbol numbers, scramble values, scrambled data
        // symbols, and the whole burst, rebuilt here from indices.
        var transcoded = new List<byte>();
        var scramble = new List<byte>();
        var data = new List<byte>();
        var burst = new List<byte>(preambleChips);
        var probes = new List<Cf>();
        var reference = new List<Cf>();
        foreach (byte chip in preambleChips)
        {
            reference.Add(Ms110dTables.Psk8[chip]);
        }

        void AddProbe(List<Cf> values)
        {
            foreach (Cf v in values)
            {
                burst.Add(ProbeMarker);
                probes.Add(v);
                reference.Add(v);
            }
        }

        int initialProbe = 0;
        int eotExtension = 0;
        if (mode.Wn == 0)
        {
            var tri = new Trinomial159Scrambler();
            for (int b = 0; b < blocks; b++)
            {
                tri.Reset();
                for (int n = b * il.SizeBits; n < (b + 1) * il.SizeBits; n += 2)
                {
                    int dibit = (interleaved[n] << 1) | interleaved[n + 1];
                    byte[] walsh = Ms110dTables.Walsh[dibit];
                    for (int i = 0; i < 32; i++)
                    {
                        int w = walsh[i & 3];
                        int s = tri.Next();
                        transcoded.Add((byte)w);
                        scramble.Add((byte)s);
                        data.Add((byte)((w + s) & 7));
                    }
                }
            }

            burst.AddRange(data);
            foreach (byte d in data)
            {
                reference.Add(Ms110dTables.Psk8[d]);
            }
        }
        else
        {
            initialProbe = mode.K;
            AddProbe(ProbeValues(mode.K, boundary: false, extend: false, out _));
            var scrambler = new Ms110dScrambler();
            int qamBits = 4;
            for (int b = 0; b < blocks; b++)
            {
                int bit = b * il.SizeBits;
                for (int f = 0; f < il.Frames; f++)
                {
                    scrambler.Reset();
                    for (int u = 0; u < mode.U; u++)
                    {
                        int t = Transcode(mode, interleaved, ref bit);
                        transcoded.Add((byte)t);
                        if (mode.Modulation == Ms110dModulation.Qam16)
                        {
                            int sv = scrambler.NextQam(0, qamBits);
                            scramble.Add((byte)sv);
                            data.Add((byte)(t ^ sv));
                        }
                        else
                        {
                            int sv = scrambler.NextPsk(0);
                            scramble.Add((byte)sv);
                            data.Add((byte)((t + sv) & 7));
                        }
                    }

                    int dataStart = data.Count - mode.U;
                    for (int u = 0; u < mode.U; u++)
                    {
                        byte d = data[dataStart + u];
                        burst.Add(d);
                        reference.Add(mode.Modulation == Ms110dModulation.Qam16 ? Ms110dTables.Qam16[d] : Ms110dTables.Psk8[d]);
                    }

                    bool boundary = (f + 2) % il.Frames == 0;
                    bool last = b == blocks - 1 && f == il.Frames - 1;
                    bool extend = last && settings.AppendEot;
                    AddProbe(ProbeValues(mode.K, boundary, extend, out int ext));
                    eotExtension += ext;
                }
            }
        }

        corruptReferenceForTest?.Invoke(reference);

        // Cross-check against the library's own builder and modulator before writing anything.
        Cf[] libSymbols = modulator.BuildSymbols(payload);
        if (libSymbols.Length != burst.Count)
        {
            throw new InvalidOperationException(
                $"symbol count mismatch: library {libSymbols.Length}, reference {burst.Count}");
        }

        // Independent count: BurstSeconds is computed from sizes alone, and excludes the EOT extension.
        long expectedSymbols = (long)Math.Round(modulator.BurstSeconds(payload.Length) * Ms110dTables.SymbolRate) + eotExtension;
        if (libSymbols.Length != expectedSymbols || libSymbols.Length != burst.Count)
        {
            throw new InvalidOperationException(
                $"symbol count: library {libSymbols.Length}, BurstSeconds rule {expectedSymbols}, reference {burst.Count}");
        }

        for (int i = 0; i < reference.Count; i++)
        {
            if (reference[i].Re != libSymbols[i].Re || reference[i].Im != libSymbols[i].Im)
            {
                throw new InvalidOperationException($"symbol {i} differs from the library");
            }
        }

        float[] audio = modulator.Modulate(payload);

        // Pulse shaping then mixing, repeated here so the baseband can be written separately.
        float[] pulse = Ms110dModulator.DesignPulse(settings.RollOff);
        int length = (libSymbols.Length * 4) + pulse.Length;
        var bbRe = new float[length];
        var bbIm = new float[length];
        for (int n = 0; n < libSymbols.Length; n++)
        {
            Cf sym = libSymbols[n] * settings.Amplitude;
            for (int t = 0; t < pulse.Length; t++)
            {
                bbRe[(n * 4) + t] += sym.Re * pulse[t];
                bbIm[(n * 4) + t] += sym.Im * pulse[t];
            }
        }

        if (audio.Length != length)
        {
            throw new InvalidOperationException("audio length disagrees with 4 x symbols + pulse taps");
        }

        for (int i = 0; i < length; i++)
        {
            double phase = 2.0 * Math.PI * 3.0 * i / 16.0;
            float mixed = (float)((bbRe[i] * Math.Cos(phase)) - (bbIm[i] * Math.Sin(phase)));
            if (mixed != audio[i])
            {
                throw new InvalidOperationException($"baseband mix differs from Modulate at sample {i}");
            }
        }

        // Write the files.
        var files = new List<(string Stage, string Name, byte[] Bytes, int Count)>();
        void Add(string stage, string name, byte[] bytes, int count) => files.Add((stage, name, bytes, count));
        Add("payload_bits", "00_payload.bits", payload, payload.Length);
        Add("framed_bits", "01_framed.bits", framed, framed.Length);
        Add("encoded_bits", "02_encoded.bits", [.. encoded], encoded.Count);
        Add("punctured_bits", "03_punctured.bits", [.. punctured], punctured.Count);
        Add("interleaved_bits", "04_interleaved.bits", [.. interleaved], interleaved.Count);
        Add("preamble_symbols", "05_preamble.sym", preambleChips, preambleChips.Length);
        Add("transcoded_symbols", "06_transcoded.sym", [.. transcoded], transcoded.Count);
        Add("scramble_symbols", "07_scramble.sym", [.. scramble], scramble.Count);
        Add("data_symbols", "08_data.sym", [.. data], data.Count);
        Add("burst_symbols", "09_symbols.sym", [.. burst], burst.Count);
        var probeBytes = new byte[probes.Count * 8];
        for (int i = 0; i < probes.Count; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(probeBytes.AsSpan(i * 8), probes[i].Re);
            BinaryPrimitives.WriteSingleLittleEndian(probeBytes.AsSpan((i * 8) + 4), probes[i].Im);
        }

        Add("probe_values_cf32le", "10_probes.cf32", probeBytes, probes.Count);
        var pulseBytes = new byte[pulse.Length * 4];
        for (int i = 0; i < pulse.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(pulseBytes.AsSpan(i * 4), pulse[i]);
        }

        Add("pulse_taps_f32le", "11_pulse.f32", pulseBytes, pulse.Length);
        var bbBytes = new byte[length * 8];
        for (int i = 0; i < length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bbBytes.AsSpan(i * 8), bbRe[i]);
            BinaryPrimitives.WriteSingleLittleEndian(bbBytes.AsSpan((i * 8) + 4), bbIm[i]);
        }

        Add("baseband_cf32le", "12_baseband.cf32", bbBytes, length);
        var audioBytes = new byte[audio.Length * 4];
        for (int i = 0; i < audio.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(audioBytes.AsSpan(i * 4), audio[i]);
        }

        Add("audio_f32le", "13_audio.f32", audioBytes, audio.Length);

        var m = new StringBuilder();
        void Kv(string k, object v) => m.Append(k).Append('=').Append(Convert.ToString(v, CultureInfo.InvariantCulture)).Append('\n');
        Kv("format_version", FormatVersion);
        Kv("package_version", PackageVersion());
        Kv("git_commit", gitCommit);
        Kv("dirty", dirty ? "true" : "false");
        Kv("case", c.DirectoryName);
        Kv("seed", c.Seed);
        Kv("wn", c.WaveformNumber);
        Kv("interleaver", c.Interleaver.ToString().ToLowerInvariant());
        Kv("constraint_length", c.ConstraintLength);
        Kv("payload_bits", c.PayloadBits);
        Kv("preamble_superframes", c.PreambleSuperframes);
        Kv("tlc_blocks", c.TlcBlocks);
        Kv("append_eom", settings.AppendEom ? 1 : 0);
        Kv("append_eot", settings.AppendEot ? 1 : 0);
        Kv("amplitude", settings.Amplitude);
        Kv("roll_off", settings.RollOff);
        Kv("sample_rate", Ms110dModulator.NativeRate);
        Kv("modulation", mode.Modulation.ToString().ToLowerInvariant());
        Kv("code_rate", mode.CodeRate);
        Kv("blocks", blocks);
        Kv("block_input_bits", il.InputBits);
        Kv("block_interleaver_bits", il.SizeBits);
        // WN0 has no frames: its blocks are counted in 32-chip Walsh channel symbols instead.
        Kv("frames_per_block", mode.Wn == 0 ? 0 : il.Frames);
        Kv("walsh_symbols_per_block", mode.Wn == 0 ? il.Frames : 0);
        Kv("frame_data_symbols", mode.U);
        Kv("probe_symbols", mode.K);
        Kv("preamble_symbol_count", preambleChips.Length);
        Kv("initial_probe_symbols", initialProbe);
        Kv("eot_extension_symbols", eotExtension);
        Kv("data_symbol_count", data.Count);
        Kv("burst_symbol_count", burst.Count);
        Kv("audio_sample_count", audio.Length);
        Kv("pulse_tap_count", pulse.Length);
        foreach (var (stage, name, bytes, count) in files)
        {
            File.WriteAllBytes(Path.Combine(caseDir, name), bytes);
            Kv("file." + stage, name);
            Kv("count." + stage, count);
            Kv("sha256." + stage, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        }

        File.WriteAllText(Path.Combine(caseDir, "manifest.txt"), m.ToString(), new UTF8Encoding(false));
    }

    private static int Transcode(Ms110dMode mode, List<byte> bits, ref int bit)
    {
        switch (mode.Modulation)
        {
            case Ms110dModulation.Bpsk:
                return bits[bit++] == 0 ? 0 : 4;
            case Ms110dModulation.Psk8:
                {
                    int tribit = (bits[bit++] << 2) | (bits[bit++] << 1) | bits[bit++];
                    return Ms110dTables.Transcode8Psk[tribit];
                }

            case Ms110dModulation.Qam16:
                return (bits[bit++] << 3) | (bits[bit++] << 2) | (bits[bit++] << 1) | bits[bit++];
            default:
                {
                    int msb = bits[bit++];
                    int lsb = bits[bit++];
                    return ((msb << 1) | lsb) switch { 0 => 0, 1 => 2, 3 => 4, _ => 6 };
                }
        }
    }

    /// <summary>Marks a probe position in the integer symbol streams; the probe values live in
    /// the probes file because the 48 symbol probe is not on the 8PSK grid.</summary>
    public const byte ProbeMarker = 255;

    // The probe symbols exactly as the library sends them, plus any EOT extension.
    private static List<Cf> ProbeValues(int k, bool boundary, bool extend, out int extension)
    {
        var result = new List<Cf>(MiniProbe.Get(k, boundary));
        extension = 0;
        if (extend)
        {
            (Cf[] baseSeq, int shift) = MiniProbe.Sequence(k);
            int offset = boundary ? shift : 0;
            for (int i = k; i < k + 32; i++)
            {
                result.Add(baseSeq[(i + offset) % baseSeq.Length]);
                extension++;
            }
        }

        return result;
    }

    /// <summary>Writes a whole case list under <paramref name="outDir"/> plus index.txt.</summary>
    public static void WriteSet(IReadOnlyList<RefCase> cases, string outDir, string gitCommit, bool dirty = false)
    {
        Directory.CreateDirectory(outDir);
        var index = new StringBuilder();
        index.Append("format_version=").Append(FormatVersion).Append('\n');
        index.Append("package_version=").Append(PackageVersion()).Append('\n');
        index.Append("git_commit=").Append(gitCommit).Append('\n');
        index.Append("dirty=").Append(dirty ? "true" : "false").Append('\n');
        foreach (RefCase c in cases)
        {
            WriteCase(c, Path.Combine(outDir, c.DirectoryName), gitCommit, dirty);
            index.Append("case=").Append(c.DirectoryName).Append('\n');
        }

        File.WriteAllText(Path.Combine(outDir, "index.txt"), index.ToString(), new UTF8Encoding(false));
    }

    /// <summary>One SHA-256 over every file under <paramref name="dir"/> (sorted relative
    /// paths with forward slashes, each followed by the file's own hash).</summary>
    public static string TreeHash(string dir)
    {
        var sb = new StringBuilder();
        foreach (string f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/'))
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            sb.Append(f).Append(' ')
              .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, f)))).ToLowerInvariant())
              .Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }
}
