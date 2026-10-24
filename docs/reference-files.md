# Reference files (transmit pipeline)

`tools/M0LTE.Ms110d.RefGen` writes one directory of plain files per test case, one file per stage of the transmit pipeline, so a port in another language can check itself stage by stage without .NET. It is a tool, not part of the package: it is not packed to NuGet.

## Running it

```sh
# the standard set (122 cases, about 40 MiB), then again to prove it is deterministic
dotnet run -c Release --project tools/M0LTE.Ms110d.RefGen -- standard --out ref --verify

# one case
dotnet run -c Release --project tools/M0LTE.Ms110d.RefGen -- case --out ref --wn 6 --interleaver short --k 7 --seed 1 --bits 100
# optional: --superframes M (default 3), --tlc N (default 0), --no-eom, --no-eot,
#           --amplitude A (default 0.5), --roll-off R (default 0.35), --git-commit SHA, --allow-dirty

# a hash over a whole reference tree
dotnet run -c Release --project tools/M0LTE.Ms110d.RefGen -- hash ref
```

`--interleaver` takes `ultrashort`, `short`, `medium` or `long` (WN0 has no ultra-short).

Same seed and same package version give byte-identical output on a given machine. The only inputs are the case parameters and the library; there are no timestamps. `--verify` generates the set twice and compares the tree hashes. The manifest records the git commit, so reference files from two commits differ in that one line even when nothing else changed; pass `--git-commit` to pin it.

The tool refuses to run from a git tree with uncommitted changes to tracked files, because the recorded commit would then not describe the code that made the files. Pass `--allow-dirty` to override; the manifest then says `dirty=true`. An explicit `--git-commit` skips the git check and records `dirty=false`.

## Layout

```
ref/
  index.txt                      format_version, package_version, git_commit, dirty, then one case= line per case
  wn6_short_k7_s1_p100/          wn{WN}_{interleaver}_k{K}_s{seed}_p{payload bits}[suffixes]
    manifest.txt
    00_payload.bits ... 13_audio.f32
```

Suffixes appear only for non-default options: `_m{M}_t{N}` (preamble super-frames not 3, or TLC blocks not 0), `_noeom`, `_noeot`, `_amp{A}`, `_ro{R}`.

## Formats

Everything is a flat file with no header, so any language can read it with a few lines of code.

- **Bit files** (`*.bits`): one byte per bit, value 0 or 1, in the order the library sends them (for the EOM, leftmost bit first).
- **Symbol files** (`*.sym`): one byte per symbol. A value 0 to 7 is an 8PSK constellation index (Table D-VI, phase = index x 45 degrees); in 16-QAM data the value 0 to 15 is an index into the 16-QAM table (Table D-VII). The value 255 marks a mini-probe symbol; the probe values themselves are in `10_probes.cf32`, because the 48 symbol probe used by WN1 and WN2 is not on the 8PSK grid.
- **Complex files** (`*.cf32`): little-endian float32 pairs, I then Q.
- **Real float files** (`*.f32`): little-endian float32.
- **Manifest** (`manifest.txt`): UTF-8 text, one `key=value` per line, LF line ends, no quoting. Values are integers, plain decimal numbers or bare words.

## Stages

| File | Stage | Length |
| --- | --- | --- |
| `00_payload.bits` | The input bits. A splitmix64 stream seeded with `seed` (bit i is bit i mod 64 of word i div 64; state += 0x9E3779B97F4A7A15 before each word), but this file is authoritative. May be empty. | `payload_bits` |
| `01_framed.bits` | Payload, then the 32 bit EOM (`0x4B65A5B2`, leftmost bit first) unless `append_eom=0`, zero filled to a whole number of interleaver input blocks, at least one block (`Ms110dFraming.BuildTxBits`). A payload whose EOM ends exactly on a block end gets no extra block. | `blocks` x `block_input_bits` |
| `02_encoded.bits` | Tail-biting rate 1/2 encode, each block on its own; see below. | 2 x framed |
| `03_punctured.bits` | After Table D-L puncturing or repetition, block by block. | `blocks` x `block_interleaver_bits` |
| `04_interleaved.bits` | After the block interleaver: the wire-order bits, block by block. | same |
| `05_preamble.sym` | Preamble chips (TLC, then M super-frames of fixed, down-count and WID sections), 8PSK indices. | `preamble_symbol_count` |
| `06_transcoded.sym` | Data symbol numbers before scrambling: the bit-to-symbol transcode (Tables D-III, D-IV, D-V; 16-QAM is the four bits themselves). For WN0, the Walsh chip value of each of the 32 chips of each channel symbol; this is an 8PSK index and is only ever 0 or 4 (the Walsh rows are binary). | `data_symbol_count` |
| `07_scramble.sym` | The scrambler value applied to each data symbol (modulo-8 addend for PSK and WN0, XOR mask for 16-QAM). Scrambler resets: per frame for WN1 to WN13, per interleaver block for WN0. | `data_symbol_count` |
| `08_data.sym` | Scrambled data symbols only: transcoded combined with scramble. | `data_symbol_count` |
| `09_symbols.sym` | The whole burst, one entry per 2400 Bd symbol, before pulse shaping. | `burst_symbol_count` |
| `10_probes.cf32` | The probe symbols in burst order (see above): the exact unit-magnitude values the library sends. | number of 255 entries in `09_symbols.sym` |
| `11_pulse.f32` | The pulse-shaping filter taps (unit energy root-raised-cosine, 4 samples per symbol), as the library uses them. | `pulse_tap_count` |
| `12_baseband.cf32` | The pulse-shaped complex baseband, before mixing to the sub-carrier. | `audio_sample_count` |
| `13_audio.f32` | The final audio, as `Ms110dModulator.Modulate` returns it. | `audio_sample_count` |

### Encoded bits (02)

Each interleaver input block is encoded separately by the library's `TailBitingEncoder` (MIL-STD-188-110D D.5.3.2.3), a rate 1/2 feed-forward encoder. For an N bit block the output is 2N bits: for each input bit in turn, T1 first and then T2.

- Generators (MSB = oldest tap, the highest power of x): K7 T1 = `1011011`, T2 = `1111001`; K9 T1 = `101110001`, T2 = `111101011`. The output bit is the parity (XOR) of the register ANDed with the generator.
- The register holds the K most recent input bits, newest in the least significant bit. It starts empty (all zero), is preloaded with the first K-1 input bits with no output, and then each of the N steps shifts in input bit `n` for n = K-1, K, ..., N-1 and then, wrapping, bits 0 to K-2 again (the same first K-1 bits, earliest first), emitting a T1/T2 pair at every step. So the encoder ends in the state it started in, and output pair i is the pair produced when input bit (i + K - 1) mod N enters the register.
- Blocks are at least K bits long (all are).

The puncture rule for the waveform's code rate is Table D-L; `03_punctured.bits` is the result. For each pair p of the (optionally repeated) stream, mask column `p mod columns` decides whether T1 and T2 of that pair are kept, T1 emitted first. Rates below 1/2 repeat each (T1,T2) pair adjacently by the repeat factor before masking. The masks below are those of `Ms110dPuncture.Get`, keyed by `code_rate` in the manifest (and by constraint length where K7 and K9 differ). Each is written as the T1 row over the T2 row, 1 = keep; "repeat" is the pair repeat factor applied before masking.

| code_rate | repeat | K7 T1 / T2 | K9 T1 / T2 |
| --- | --- | --- | --- |
| 9/10 | 1 | `111101110` / `100010001` | `111000101` / `100111010` |
| 8/9 | 1 | `11110100` / `10001011` | `11100000` / `10011111` |
| 5/6 | 1 | `11010` / `10101` | `10110` / `11001` |
| 4/5 | 1 | `1111` / `1000` | `1101` / `1010` |
| 3/4 | 1 | `110` / `101` | `111` / `100` |
| 2/3 | 1 | `11` / `10` | same |
| 4/7 | 1 | `1111` / `0111` | same |
| 9/16 | 1 | `111101111` / `111111011` | same |
| 1/2 | 1 | `1` / `1` | same |
| 2/5 | 2 | `1110` / `1010` | same |
| 1/3 | 2 | `11` / `10` | same |
| 2/7 | 2 | `1111` / `0111` | same |
| 1/4 | 2 | `1` / `1` | same |
| 1/6 | 3 | `1` / `1` | same |
| 1/8 | 4 | `1` / `1` | same |
| 1/12 | 6 | `1` / `1` | same |
| 1/16 | 8 | `1` / `1` | same |

### Burst layout (09)

For WN0: the preamble, then every data chip of every block, with no probes and no EOT. For the other waveforms: the preamble, an initial mini-probe of `probe_symbols` symbols, then for each block `frames_per_block` frames of `frame_data_symbols` data symbols each followed by a mini-probe; the last probe is followed by `eot_extension_symbols` extra symbols (32, or 0 when EOT is off) which are also marked 255. The probe before an interleaver boundary is the cyclically shifted one; its values are in the probes file, so a port compares against them rather than re-deriving the shift.

In WN0 manifests `frames_per_block` is 0 (there are no frames) and `walsh_symbols_per_block` gives the number of 32-chip channel symbols per block (the library's table calls this the frame count for WN0). It is 0 for the other waveforms.

### Audio (11 to 13)

- Baseband length, and audio length, is exactly `4 x burst_symbol_count + pulse_tap_count` samples. Symbol n is scaled by `amplitude`, multiplied into the pulse (4 samples per symbol) and added at sample offset `4 n`, so the first pulse tap lines up with the first symbol and the last symbol's pulse ends 4 samples before the end of the buffer (the buffer is 4 samples longer than the last pulse needs).
- The pulse length depends on the roll-off: 65 taps (16 symbols x 4 + 1) at 0.35 and wider, longer for narrower roll-offs (`Ms110dModulator.PulseSpanSymbols`; 0.10 gives 225).
- Audio is baseband mixed up: `audio[i] = re[i] cos(2 pi 3 i / 16) - im[i] sin(2 pi 3 i / 16)` (1800 Hz at 9600 Hz), phase from sample 0 of the burst.
- The tool checks that its own shaping and mixing reproduce `Modulate` bit for bit before it writes, so the three files are consistent with each other on the generating machine.
- Tolerance for a port: compare audio and baseband with an absolute error of at most 1e-5 per sample. Samples are well within +-1 (the measured peak is about 0.47 at the default amplitude of 0.5), a float32 sum of the roughly 17 pulses overlapping any sample carries rounding error well under 1e-6 whatever the summation order, and 1e-5 (about -100 dB against full scale) leaves a tenfold margin without hiding any real fault. If the port computes its own pulse taps, compare them to `11_pulse.f32` at 1e-6 first. Every other file is exact.

## What the tool uses

The library builds the whole burst inside one internal method, so the tool repeats that method's steps using library pieces: the public `Ms110dFraming.BuildTxBits`, `Ms110dPuncture`, `Ms110dInterleaver`, `Ms110dScrambler`, `Trinomial159Scrambler`, `PreambleGenerator.Generate`, `Ms110dMode` and `Ms110dTables`, `M0LTE.Fec`'s `TailBitingEncoder`, and (through `InternalsVisibleTo`, which does not change the public API) the internal `MiniProbe.Sequence`, `Ms110dModulator.BuildSymbols` and `Ms110dModulator.DesignPulse`. It then asserts that its encode, puncture and interleave output equals `Ms110dFraming.EncodeBlock`, that its symbol count equals both `BuildSymbols` and the size-only `BurstSeconds` rule (plus the EOT extension), that its symbol stream equals `BuildSymbols` exactly, and that its shaping and mixing equal `Modulate`. A drift between the tool and the library therefore fails generation rather than writing wrong files.

## Manifest keys

`format_version`, `package_version`, `git_commit`, `dirty` (`true` or `false`; `git_commit` is `unknown` outside git), `case`, `seed`, `wn`, `interleaver`, `constraint_length`, `payload_bits`, `preamble_superframes`, `tlc_blocks`, `append_eom`, `append_eot`, `amplitude`, `roll_off`, `sample_rate`, `modulation`, `code_rate`, `blocks`, `block_input_bits`, `block_interleaver_bits`, `frames_per_block`, `walsh_symbols_per_block`, `frame_data_symbols`, `probe_symbols`, `preamble_symbol_count`, `initial_probe_symbols`, `eot_extension_symbols`, `data_symbol_count`, `burst_symbol_count`, `audio_sample_count`, `pulse_tap_count`, and for each stage `file.<stage>`, `count.<stage>` and `sha256.<stage>` (stages: `payload_bits`, `framed_bits`, `encoded_bits`, `punctured_bits`, `interleaved_bits`, `preamble_symbols`, `transcoded_symbols`, `scramble_symbols`, `data_symbols`, `burst_symbols`, `probe_values_cf32le`, `pulse_taps_f32le`, `baseband_cf32le`, `audio_f32le`).

## Versioning and history

`format_version` changes whenever a file's layout or meaning changes. `package_version`, `git_commit` and `dirty` say which C# produced the files; a port pins the reference set it was tested against.

- **2**: stage files renumbered so every number is unique (probes is now 10, audio 13); added the pulse taps (11) and the pre-mixing baseband (12); added `dirty`, `walsh_symbols_per_block` and `pulse_tap_count` to the manifest; WN0 `frames_per_block` is now 0; added options for EOM off, EOT off, amplitude and roll-off, and the ultra-short and medium interleavers to the standard set.
- **1**: first version.

## Standard set

`standard` writes (122 cases, about 40 MiB):

- every valid waveform (WN0 to WN8 and WN13) and interleaver (ultra-short, short, medium, long; WN0 has no ultra-short) with seed 1 and a 100 bit payload, with both K7 and K9 except long, where each waveform gets one code, alternating down the list;
- short interleaver with seeds 2 and 3 and a payload that, with the EOM, fills exactly two and a half interleaver blocks, both codes;
- two multi-block medium cases (WN6 K7, WN3 K9);
- on WN6 short K7 (and WN1 short K9 for the block-end case): EOM off, EOT off, amplitude 0.25, roll-off 0.10, a payload whose EOM ends exactly on the block end, a payload that fills a block exactly (so the EOM spills into a second block), an empty payload, and an empty payload with EOM off;
- three cases with non-default preamble settings (WN6 M=1 N=2, WN0 M=5 N=1, WN8 M=2 N=3).
