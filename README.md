# M0LTE.Ms110d

A managed **MIL-STD-188-110D Appendix D** 3 kHz serial-tone HF modem for .NET, extracted from the [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem) packet-radio modem. Appendix D's NATO counterpart is STANAG 5069; this package implements most of its 3 kHz waveforms (WN0 to WN8 and WN13; not WN9 to WN12 or the wider bandwidths). It is not the older 3 kHz waveform of Appendix C and STANAG 4539. See [Related standards](docs/waveforms.md#related-standards). Give it bits and it gives you 9600 Hz audio on an 1800 Hz sub-carrier; give it audio and it gives you the bits back.

| Type | What it is |
| --- | --- |
| `Ms110dModulator` | Bits to audio: autobaud preamble, scrambled data frames with mini-probes, EOM and EOT. Configured by `Ms110dTxSettings` (waveform number, short or long interleaver, K7 or K9 code, level, roll-off). |
| `Ms110dDemodulator` | Audio to bits: preamble search, waveform identification, probe-trained decision-feedback equalisation with iterative turbo decoding. Raises `BlockDecoded` per interleaver block and `BurstCompleted` when a burst ends. |
| `Ms110dMode`, `Ms110dTables` | The waveform table (WN0 to WN8 and WN13: Walsh, BPSK, QPSK, 8PSK, 16QAM) and the interop constants, transcribed from the standard. |
| `Ms110dFraming`, `Ms110dInterleaver`, `Ms110dPuncture` | The bit pipeline on its own: EOM, tail-biting encode, puncture, interleave. Useful for scoring a received burst against what was sent. |

- Targets `net10.0`. Depends on [M0LTE.Dsp](https://www.nuget.org/packages/M0LTE.Dsp), [M0LTE.Fec](https://www.nuget.org/packages/M0LTE.Fec) and [M0LTE.Ofdm](https://www.nuget.org/packages/M0LTE.Ofdm) (for its complex type).
- [docs/waveforms.md](docs/waveforms.md) lists every waveform's parameters (modulation, code rate, data rate, frame and probe lengths, interleaver spans, target SNRs) with page references into the standard.
- Spec-first: every wire constant comes from the standard. Validated in simulation against the standard's own AWGN and Poor-channel performance masks, measured through [M0LTE.HfChannel](https://github.com/M0LTE/M0LTE.HfChannel). Not yet proven against another implementation on air.
- The public API is locked by a test and the package follows [Semantic Versioning](https://semver.org/); see [docs/versioning.md](docs/versioning.md).

## Install

```sh
dotnet add package M0LTE.Ms110d
```

## Quick start

```csharp
using M0LTE.Ms110d;

// Transmit: WN6 (QPSK, rate 1/2), short interleaver. Bits are bytes holding 0 or 1.
var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 6 });
float[] audio = tx.Modulate(payloadBits); // 9600 Hz, centred on 1800 Hz

// Receive: feed audio in any chunk size; decoded blocks arrive as events.
var rx = new Ms110dDemodulator();
rx.BlockDecoded += block => Consume(block.Bits);
rx.BurstCompleted += burst => Console.WriteLine($"burst over: {burst.Reason}");
rx.Process(audio);
```

The modem works at 9600 Hz only. Resample at the edges if your audio runs at another rate.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE) and [PROVENANCE.md](PROVENANCE.md).
