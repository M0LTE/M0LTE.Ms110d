# Provenance

`M0LTE.Ms110d` is the MIL-STD-188-110D Appendix D modem from [pdn-soundmodem](https://github.com/packet-net/pdn-soundmodem), moved here unchanged apart from its namespace and a few members made public, at pdn-soundmodem commit `67941730e5e896b12d2f5f63f7109408f5f344ac` (2026-10-22). It was written for that project and carries its licence, AGPL-3.0-or-later.

## Spec-first, no external implementation

No open implementation of Appendix D and no public off-air recording of it were found, so none was used. Every wire constant (the PN sequences, Walsh maps, transcoding and constellations, mini-probe bases, the K7 and K9 polynomials 133/171 and 561/753, the Table D-L puncture masks, interleaver sizes and increments, WID and downcount checksums, EOM and EOT) is transcribed from MIL-STD-188-110D (29 Dec 2017, Distribution A). Each table was transcribed twice, independently, and the two compared with zero conflicts, then checked against the spec's own worked examples: the printed Trinomial(159,31) scrambler sequence, the Walsh combine row, the D.5.3.3.2 interleaver example and the puncture-mask rate consistency.

The equaliser and the soft tail-biting decoder are textbook DSP designed from the probe structure alone (Proakis chapters 9 and 10 for the DFE, Haykin for NLMS, wrap-extension for tail-biting decoding). No AGPL source, mars-suite or otherwise, was consulted.

## No codec2-derived dependency

M0LTE.Ms110d depends on no codec2-derived package. Since 0.2.0 it uses its own single-precision complex struct, `Complex32`, written fresh for this package from the members the modem uses and textbook complex arithmetic. It replaced the `Cf` type of `M0LTE.Ofdm`, which carries codec2 lineage, and the `M0LTE.Ofdm` reference (and `M0LTE.FecLdpc` with it) is gone from the shipped package. The switch was proven not to change anything on air: the standard reference file set (docs/reference-files.md) is byte-identical before and after. The test project still sees `M0LTE.Ofdm` transitively, only because the `M0LTE.HfChannel` test dependency exposes it; nothing shipped does.

## The design records

The source comments cite paths under `docs/dev/ms110d/`. Those are in the pdn-soundmodem repository, not this one: the transcribed tables and their verification ([docs/dev/ms110d/tables](https://github.com/packet-net/pdn-soundmodem/tree/main/docs/dev/ms110d/tables)), the [design](https://github.com/packet-net/pdn-soundmodem/blob/main/docs/dev/ms110d/design.md), and the standard itself. The per-waveform parameter table moved here as [docs/waveforms.md](docs/waveforms.md). Other `docs/dev/...` paths and `#N` issue numbers in comments refer to pdn-soundmodem too.

## Validation

Loopback, and the D-LXIV and D-LXV performance masks in simulation through the Watterson channel of Appendix E ([M0LTE.HfChannel](https://github.com/M0LTE/M0LTE.HfChannel)). This is spec-faithful and mask-passing, not interop-proven: it has only ever talked to itself.
