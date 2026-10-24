# Reference files (transmit pipeline)

`tools/M0LTE.Ms110d.RefGen` writes one directory of plain files per test case, one file per stage of the transmit pipeline, so a port in another language can check itself stage by stage without .NET. It is a tool, not part of the package: it is not packed to NuGet and uses the library only through its public API plus internal access for the symbol builder.

## Running it

```sh
# the standard set (83 cases, about 17 MiB), then again to prove it is deterministic
dotnet run -c Release --project tools/M0LTE.Ms110d.RefGen -- standard --out ref --verify

# one case
dotnet run -c Release --project tools/M0LTE.Ms110d.RefGen -- case --out ref --wn 6 --interleaver short --k 7 --seed 1 --bits 100
# optional: --superframes M (default 3), --tlc N (default 0), --git-commit SHA

# a hash over a whole reference tree
dotnet run -c Release --project tools/M0LTE.Ms110d.RefGen -- hash ref
```

Same seed and same package version give byte-identical output on a given machine. The only inputs are the case parameters and the library; there are no timestamps. `--verify` generates the set twice and compares the tree hashes. The manifest records the git commit, so reference files from two commits differ in that one line even when nothing else changed; pass `--git-commit` to pin it.

## Layout

```
ref/
  index.txt                      format_version, package_version, git_commit, then one case= line per case
  wn6_short_k7_s1_p100/          wn{WN}_{interleaver}_k{K}_s{seed}_p{payload bits}[_m{M}_t{N}]
    manifest.txt
    00_payload.bits ... 10_audio.f32
```

The `_m{M}_t{N}` suffix appears only when the preamble super-frame count M is not 3 or the TLC block count N is not 0.

## Formats (version 1)

Everything is a flat file with no header, so any language can read it with a few lines of code.

- **Bit files** (`*.bits`): one byte per bit, value 0 or 1, in the order the library sends them (for the EOM, leftmost bit first).
- **Symbol files** (`*.sym`): one byte per symbol. A value 0 to 7 is an 8PSK constellation index (Table D-VI, phase = index x 45 degrees); in 16-QAM data the value 0 to 15 is an index into the 16-QAM table (Table D-VII). The value 255 marks a mini-probe symbol; the probe values themselves are in `09_probes.cf32`, because the 48 symbol probe used by WN1 and WN2 is not on the 8PSK grid.
- **Complex file** (`09_probes.cf32`): little-endian float32 pairs, I then Q, exactly the library's unit-magnitude probe values.
- **Audio** (`10_audio.f32`): little-endian float32, 9600 Hz, 1800 Hz sub-carrier, exactly what `Ms110dModulator.Modulate` returns. The sample count is `audio_sample_count` in the manifest and equals the file size divided by 4.
- **Manifest** (`manifest.txt`): UTF-8 text, one `key=value` per line, LF line ends, no quoting. Values are integers, plain decimal numbers or bare words.

## Stages

| File | Stage | Length |
| --- | --- | --- |
| `00_payload.bits` | The input bits. A splitmix64 stream seeded with `seed` (bit i is bit i mod 64 of word i div 64; state += 0x9E3779B97F4A7A15 before each word), but this file is authoritative. | `payload_bits` |
| `01_framed.bits` | Payload, then the 32 bit EOM (`0x4B65A5B2`), zero filled to a whole number of interleaver input blocks (`Ms110dFraming.BuildTxBits`). | `blocks` x `block_input_bits` |
| `02_encoded.bits` | Tail-biting convolutional encode of each block, K7 or K9, mother code rate 1/2: two bits per input bit, the library's own bit order. | 2 x framed |
| `03_punctured.bits` | After Table D-L puncturing or repetition, block by block. | `blocks` x `block_interleaver_bits` |
| `04_interleaved.bits` | After the block interleaver: the wire-order bits, block by block. | same |
| `05_preamble.sym` | Preamble chips (TLC, then M super-frames of fixed, down-count and WID sections), 8PSK indices. | `preamble_symbol_count` |
| `06_transcoded.sym` | Data symbol numbers before scrambling: the bit-to-symbol transcode (Tables D-III, D-IV, D-V; 16-QAM is the four bits themselves). For WN0, the Walsh chip value (0 to 3) of each of the 32 chips of each channel symbol. | `data_symbol_count` |
| `07_scramble.sym` | The scrambler value applied to each data symbol (modulo-8 addend for PSK and WN0, XOR mask for 16-QAM). Scrambler resets: per frame for WN1 to WN13, per interleaver block for WN0. | `data_symbol_count` |
| `08_data.sym` | Scrambled data symbols only: transcoded combined with scramble. | `data_symbol_count` |
| `09_symbols.sym` | The whole burst, one entry per 2400 Bd symbol, before pulse shaping. | `burst_symbol_count` |
| `09_probes.cf32` | The probe symbols in burst order (see above). | number of 255 entries in `09_symbols.sym` |
| `10_audio.f32` | The final audio. | `audio_sample_count` |

Burst layout (`09_symbols.sym`). For WN0: the preamble, then every data chip of every block, no probes, no EOT. For the other waveforms: the preamble, an initial mini-probe of `probe_symbols` symbols, then for each block `frames_per_block` frames of `frame_data_symbols` data symbols followed by a mini-probe; the last probe is followed by `eot_extension_symbols` extra symbols (32, or 0 when EOT is off) which are also marked 255. The probe before an interleaver boundary is the cyclically shifted one; its values are in the probes file, so a port compares against them rather than re-deriving the shift.

Stage boundaries are the ones the C# code has: the library builds the whole burst inside one internal method, so the tool repeats that method's steps using the same public pieces (`Ms110dFraming.BuildTxBits`, `TailBitingEncoder`, `Ms110dPuncture`, `Ms110dInterleaver`, `Ms110dScrambler`, `Trinomial159Scrambler`, `PreambleGenerator`, `MiniProbe`). It then asserts that its encode/puncture/interleave output equals `Ms110dFraming.EncodeBlock` and that its symbol stream equals the modulator's `BuildSymbols` exactly, and writes the audio straight from `Modulate`. A drift between the tool and the library therefore fails generation rather than writing wrong files.

## Manifest keys

`format_version`, `package_version`, `git_commit` (with `-dirty` appended for an unclean tree, `unknown` outside git), `case`, `seed`, `wn`, `interleaver`, `constraint_length`, `payload_bits`, `preamble_superframes`, `tlc_blocks`, `append_eom`, `append_eot`, `amplitude`, `roll_off`, `sample_rate`, `modulation`, `code_rate`, `blocks`, `block_input_bits`, `block_interleaver_bits`, `frames_per_block`, `frame_data_symbols`, `probe_symbols`, `preamble_symbol_count`, `initial_probe_symbols`, `eot_extension_symbols`, `data_symbol_count`, `burst_symbol_count`, `audio_sample_count`, and for each stage `file.<stage>`, `count.<stage>` and `sha256.<stage>` (stages: `payload_bits`, `framed_bits`, `encoded_bits`, `punctured_bits`, `interleaved_bits`, `preamble_symbols`, `transcoded_symbols`, `scramble_symbols`, `data_symbols`, `burst_symbols`, `probe_values_cf32le`, `audio_f32le`).

## Versioning

`format_version` changes whenever a file's layout or meaning changes. `package_version` and `git_commit` say which C# produced the files; a port pins the reference set it was tested against. Audio samples use `Math.Cos` and single-precision arithmetic, so compare them with a small tolerance (a few ULP) rather than for bit equality; every other file is exact.

## Standard set

`standard` writes, for each of WN0 to WN8 and WN13 and both K7 and K9:

- short and long interleaver with seed 1 and a 100 bit payload;
- short interleaver with seeds 2 and 3 and a payload that, with the EOM, fills exactly two and a half interleaver blocks;
- plus three cases with non-default preamble settings (WN6 M=1 N=2, WN0 M=5 N=1, WN8 M=2 N=3).
