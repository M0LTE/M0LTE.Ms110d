# MS110D waveforms at a glance

The waveforms this package implements, and pdn-soundmodem's `ms110d-wn*` modes, are the 3 kHz serial-tone waveforms of MIL-STD-188-110D Appendix D. This page lists each one's parameters as the standard defines them, with a link to the table they come from. Every figure is the standard's 3 kHz column; pdn-soundmodem does not do the wider bandwidths.

**Names.** "WN" is our abbreviation, not the standard's. The number in `ms110d-wn3` is the standard's waveform number: its tables call it "Waveform Number" ([Table D-XV](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=173), p.168 (PDF 173)) and its text says "Waveform ID 3" or "WID 3" ([D.5.1.4](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=166), p.161 (PDF 166); [D.6.2](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=247), p.242 (PDF 247)). Search the standard or other MS110D material for WID 3, not WN3.

Spec references give the standard's own page number, then the page of the [PDF in the pdn-soundmodem repository](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf) (always 5 more). The links open the PDF at that page where the viewer allows it. The tables were typed in twice independently and checked against each other; the copies are in pdn-soundmodem's [docs/dev/ms110d/tables](https://github.com/packet-net/pdn-soundmodem/tree/main/docs/dev/ms110d/tables) and the story is in [its README](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/README.md).

## Common to every waveform

| Parameter | Value | Spec |
|---|---|---|
| Symbol rate | 2400 symbols/s | [Table D-I](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=148), p.143 (PDF 148) |
| Centre (sub-carrier) | 1800 Hz audio | [Table D-I](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=148), p.143 (PDF 148) |
| Pulse shape | Root raised cosine, roll-off 0.35 (the standard recommends it rather than requiring it), so 3240 Hz edge to edge (180 to 3420 Hz audio) | [D.5.1.5](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=167), p.162 (PDF 167) |
| Occupied bandwidth | 2.80 kHz holds 99% of the power (400 to 3200 Hz audio), the same for every waveform. Worked out from pdn-soundmodem's transmit filter; the standard's ideal filter gives the same. Width 26 dB down: 3.20 kHz | Definition in [section 3](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=10), p.5 (PDF 10); Appendix D sets no limit of its own |
| Frame | A block of data symbols, then a mini-probe of known symbols (none on WN0) | [D.5.2 and Figure D-7](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=168), p.163 (PDF 168) |
| Data scrambler | x^9 + x^4 + 1, restarted every data block | [D.5.1.3](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=165), p.160 (PDF 165) |
| Preamble | Optional TLC section, then M super-frames of 18 Walsh symbols, 32 chips each (240 ms per super-frame) | [D.5.2.1 and Figure D-8](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=170), pp.165 to 170 (PDF 170 to 175); [Table D-XIII](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=170), p.165 (PDF 170) |
| Waveform ID in the preamble | Waveform number, interleaver and constraint length, so a receiver sets itself up from the preamble (autobaud) | [Tables D-XV, D-XVI](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=173), p.168 (PDF 173); [D-XVII](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=174), p.169 (PDF 174) |
| Convolutional code | Rate 1/2 mother code, K=7 or K=9, punctured or repeated to the waveform's rate | [D.5.3.2](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=222), p.217 (PDF 222); [Figures D-9, D-10](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=224), pp.219 to 220 (PDF 224 to 225); [Table D-L](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=227), p.222 (PDF 227) |
| Interleaver | Ultra Short, Short, Medium or Long | [Table D-XXXVII](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=210), p.205 (PDF 210); [Table D-LI](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=228), p.223 (PDF 228) |
| Performance target | SNR in 3 kHz for a coded bit error rate of 1 in 100,000, on a steady channel (AWGN) and on the "Poor" channel (two equal fading paths, 2 ms apart, 1 Hz fading) | [Table D-LXIV](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=246), p.241 (PDF 246) |

pdn-soundmodem transmits with the Short interleaver, K=7 and 3 preamble super-frames, with no TLC section. Its receiver follows whatever the preamble announces, any interleaver and either code.

## Per waveform

| Waveform | Data rate | Modulation | Code rate | Data symbols per frame | Probe symbols per frame | Frame length | Interleaver span: Ultra Short / Short / Medium / Long | Target SNR: AWGN / Poor |
|---|---|---|---|---|---|---|---|---|
| <a id="wn0"></a>`ms110d-wn0` | 75 bps | Walsh | 1/2 | none | none | - | - / 533 ms / 1.92 s / 7.68 s | -6 / -1 dB |
| <a id="wn1"></a>`ms110d-wn1` | 150 bps | BPSK | 1/8 | 48 | 48 | 40 ms | 160 ms / 640 ms / 2.56 s / 10.24 s | -3 / 3 dB |
| <a id="wn2"></a>`ms110d-wn2` | 300 bps | BPSK | 1/4 | 48 | 48 | 40 ms | 160 ms / 640 ms / 2.56 s / 10.24 s | 0 / 5 dB |
| <a id="wn3"></a>`ms110d-wn3` | 600 bps | BPSK | 1/3 | 96 | 32 | 53.3 ms | 107 ms / 427 ms / 1.71 s / 6.83 s | 3 / 7 dB |
| <a id="wn4"></a>`ms110d-wn4` | 1200 bps | BPSK | 2/3 | 96 | 32 | 53.3 ms | 107 ms / 427 ms / 1.71 s / 6.83 s | 5 / 10 dB |
| <a id="wn5"></a>`ms110d-wn5` | 1600 bps | BPSK | 3/4 | 256 | 32 | 120 ms | 120 ms / 480 ms / 1.92 s / 7.68 s | 6 / 11 dB |
| <a id="wn6"></a>`ms110d-wn6` | 3200 bps | QPSK | 3/4 | 256 | 32 | 120 ms | 120 ms / 480 ms / 1.92 s / 7.68 s | 9 / 14 dB |
| <a id="wn7"></a>`ms110d-wn7` | 4800 bps | 8PSK | 3/4 | 256 | 32 | 120 ms | 120 ms / 480 ms / 1.92 s / 7.68 s | 13 / 19 dB |
| <a id="wn8"></a>`ms110d-wn8` | 6400 bps | 16QAM | 3/4 | 256 | 32 | 120 ms | 120 ms / 480 ms / 1.92 s / 7.68 s | 16 / 23 dB |
| <a id="wn13"></a>`ms110d-wn13` | 2400 bps | QPSK | 9/16 | 256 | 32 | 120 ms | 120 ms / 480 ms / 1.92 s / 7.68 s | 6 / 11 dB |
| Spec | [D-II](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=149), p.144 (PDF 149) | [D-II](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=149), p.144 (PDF 149) | [D-XLIX](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=223), p.218 (PDF 223) | [D-XI](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=169), p.164 (PDF 169) | [D-XII](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=169), p.164 (PDF 169) | from D-I, D-XI, D-XII | [D-XXXVII](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=210), p.205 (PDF 210) | [D-LXIV](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=246), p.241 (PDF 246) |

WN9 to WN12 (32QAM to 256QAM, 8000 to 16000 bps) are in the standard but not in pdn-soundmodem.

How the derived columns are worked out:

- **Frame length** is data plus probe symbols at 2400 symbols/s: 96 symbols for WN1 and WN2, 128 for WN3 and WN4, 288 for the rest.
- **Interleaver span** is the interleaver's input bits from Table D-XXXVII divided by the data rate. WN3 and WN4 share a frame shape and differ only in code rate, so their spans match.
- **Data rate** checks out from the other columns: 2400 x data / (data + probe) x bits per symbol x code rate. For WN4 that is 2400 x 96/128 x 1 x 2/3 = 1200 bps.
- **Target SNRs** are the standard's requirement, not what pdn-soundmodem achieves; the measured figures are in pdn-soundmodem's [mode-validation.md](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/mode-validation.md).

## Mini-probes

Each probe is a short base sequence repeated to fill the probe length. The probe after the second-to-last data block of each long-interleaver frame is sent cyclically shifted, whichever interleaver is in use, so a receiver can find that boundary.

| Probe length | Waveforms | Base sequence | Shift at interleaver boundary | Spec |
|---|---|---|---|---|
| 48 | WN1, WN2 | 25 symbols | 12 | [Table D-XXI](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=176), p.171 (PDF 176); [Table D-XXV](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=182), p.177 (PDF 182) |
| 32 | WN3 to WN8, WN13 | 16 symbols | 8 | [Table D-XXI](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=176), p.171 (PDF 176); [Table D-XXIII](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=180), p.175 (PDF 180) |

## Symbol mapping

| Modulation | Waveforms | Spec |
|---|---|---|
| Walsh (75 bps fallback) | WN0 | [D.5.1.4](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=166), p.161 (PDF 166) |
| BPSK | WN1 to WN5 | [Table D-III](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=150), p.145 (PDF 150) |
| QPSK | WN6, WN13 | [Table D-IV](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=151), p.146 (PDF 151) |
| 8PSK | WN7; BPSK and QPSK symbols are also sent as 8PSK points (Tables D-III, D-IV) | [Tables D-V, D-VI](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=151), p.146 (PDF 151) |
| 16QAM | WN8 | [Table D-VII](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/spec/MIL-STD-188-110D.pdf#page=154), p.149 (PDF 154) |
