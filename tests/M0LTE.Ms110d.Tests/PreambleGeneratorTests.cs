using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

public class PreambleGeneratorTests
{
    [Fact]
    public void Wid_Encoding_Round_Trips_For_Every_Configuration()
    {
        foreach (int wn in Enumerable.Range(0, 14))
        {
            foreach (Ms110dInterleaverKind il in Enum.GetValues<Ms110dInterleaverKind>())
            {
                foreach (int k in new[] { 7, 9 })
                {
                    byte[] dibits = PreambleGenerator.EncodeWid(wn, il, k);
                    PreambleGenerator.TryDecodeWid(dibits, out int wnOut, out Ms110dInterleaverKind ilOut, out int kOut)
                        .Should().BeTrue();
                    wnOut.Should().Be(wn);
                    ilOut.Should().Be(il);
                    kOut.Should().Be(k);
                }
            }
        }
    }

    [Fact]
    public void Wid_Checksum_Rejects_Any_Single_Dibit_Corruption()
    {
        byte[] good = PreambleGenerator.EncodeWid(4, Ms110dInterleaverKind.Short, 7);
        for (int position = 0; position < 5; position++)
        {
            for (int wrong = 0; wrong < 4; wrong++)
            {
                if (wrong == good[position])
                {
                    continue;
                }

                var corrupted = (byte[])good.Clone();
                corrupted[position] = (byte)wrong;
                bool ok = PreambleGenerator.TryDecodeWid(corrupted, out int wn, out Ms110dInterleaverKind il, out int k);
                (ok && wn == 4 && il == Ms110dInterleaverKind.Short && k == 7).Should().BeFalse(
                    $"corrupting w{4 - position} to {wrong} must not decode to the original WID");
            }
        }
    }

    [Fact]
    public void Wid_Encoding_Matches_The_Table_DXv_Bit_Layout()
    {
        // WN 4 = d9d8d7d6 0100 (Table D-XV: w4 = 01, w3 = 00); Long = d5d4 11 (D-XVI);
        // K=9 → d3 = 1 (D-XVII); checksum d2 = 0^1^0 = 1, d1 = 0^0^1 = 1, d0 = 1^1^1 = 1.
        PreambleGenerator.EncodeWid(4, Ms110dInterleaverKind.Long, 9)
            .Should().Equal(1, 0, 3, 3, 3);

        // WN 13 = 1101 (w4 = 11, w3 = 01); UltraShort = 00; K=7 → d3 = 0;
        // d2 = 1^1^0 = 0, d1 = 0^1^0 = 1, d0 = 0^0^0 = 0.
        PreambleGenerator.EncodeWid(13, Ms110dInterleaverKind.UltraShort, 7)
            .Should().Equal(3, 1, 0, 0, 2);
    }

    [Fact]
    public void Downcount_Round_Trips_With_Parity_For_All_Values()
    {
        for (int count = 0; count < 32; count++)
        {
            byte[] dibits = PreambleGenerator.EncodeCount(count);
            PreambleGenerator.TryDecodeCount(dibits, out int decoded).Should().BeTrue();
            decoded.Should().Be(count);
        }
    }

    [Fact]
    public void Downcount_Parity_Example_Is_Bit_Exact()
    {
        // count 19 = b4…b0 = 10011: b7 = b1^b2^b3 = 1^0^0 = 1, b6 = b2^b3^b4 = 0^0^1 = 1,
        // b5 = b0^b1^b2 = 1^1^0 = 0 → c3 = (1,1) = 3, c2 = (0,1) = 1, c1 = (0,0) = 0,
        // c0 = (1,1) = 3. Hand-derived from the D.5.2.1.3.1 prose.
        PreambleGenerator.EncodeCount(19).Should().Equal(3, 1, 0, 3);
    }

    [Fact]
    public void Wid_Section_Chips_Are_Walsh_Plus_WidPn_Modulo_8()
    {
        // Wire-side anchor (checklist L2): recompute the expected chips here with
        // independent expansion arithmetic - di-bit w4 of WN 6/Short/K7 (d9d8 = 01 → 1),
        // Walsh(1) = 0404 repeated 8×, plus widPN[0..31] mod 8.
        byte[] chips = PreambleGenerator.WidSectionChips(6, Ms110dInterleaverKind.Short, 7);
        chips.Length.Should().Be(5 * 32);

        byte[] widDibits = PreambleGenerator.EncodeWid(6, Ms110dInterleaverKind.Short, 7);
        byte[][] walshRows =
        [
            [0, 0, 0, 0], [0, 4, 0, 4], [0, 0, 4, 4], [0, 4, 4, 0],
        ];
        for (int j = 0; j < 5; j++)
        {
            for (int i = 0; i < 32; i++)
            {
                int expected = (walshRows[widDibits[j]][i % 4] + Ms110dTables.WidPn[(32 * j) + i]) % 8;
                chips[(32 * j) + i].Should().Be((byte)expected, $"WID symbol {j} chip {i}");
            }
        }
    }

    [Fact]
    public void Tlc_Chips_Are_The_Complex_Conjugate_Of_FixedPn()
    {
        var generator = new PreambleGenerator(tlcBlocks: 2, superframes: 2);
        byte[] chips = generator.Generate(2, Ms110dInterleaverKind.Short, 7);
        for (int i = 0; i < 64; i++)
        {
            chips[i].Should().Be((byte)((8 - Ms110dTables.FixedPn[i % 256]) & 7));
        }
    }

    [Fact]
    public void Preamble_Has_The_Figure_D8_Geometry_And_Counts_Down()
    {
        const int m = 3;
        var generator = new PreambleGenerator(tlcBlocks: 0, superframes: m);
        byte[] chips = generator.Generate(6, Ms110dInterleaverKind.Short, 7);
        chips.Length.Should().Be(m * 18 * 32); // 9 fixed + 4 count + 5 WID channel symbols

        byte[] fixedSection = generator.FixedSectionChips();
        fixedSection.Length.Should().Be(288);
        byte[] wid = PreambleGenerator.WidSectionChips(6, Ms110dInterleaverKind.Short, 7);
        for (int rep = 0; rep < m; rep++)
        {
            int start = rep * 576;
            chips.Skip(start).Take(288).Should().Equal(fixedSection, $"fixed section, superframe {rep}");
            chips.Skip(start + 288).Take(128).Should().Equal(
                PreambleGenerator.CountSectionChips(m - 1 - rep), $"count section, superframe {rep}");
            chips.Skip(start + 416).Take(160).Should().Equal(wid, $"WID section, superframe {rep}");
        }
    }

    [Fact]
    public void Single_Superframe_Uses_The_One_Symbol_Fixed_Subsection_With_Dibit_3()
    {
        var generator = new PreambleGenerator(tlcBlocks: 0, superframes: 1);
        byte[] chips = generator.Generate(1, Ms110dInterleaverKind.Long, 9);
        chips.Length.Should().Be(10 * 32);

        byte[][] walshRows = [[0, 0, 0, 0], [0, 4, 0, 4], [0, 0, 4, 4], [0, 4, 4, 0]];
        for (int i = 0; i < 32; i++)
        {
            int expected = (walshRows[3][i % 4] + Ms110dTables.FixedPn[i]) % 8;
            chips[i].Should().Be((byte)expected);
        }
    }

    [Fact]
    public void Fixed_Section_Pn_Wraps_At_256_Chips()
    {
        // O-1: chips 256-287 of the 288-chip Fixed subsection reuse fixedPN[0..31] (at
        // 3 kHz the wrap and per-symbol-restart readings coincide; see PreambleGenerator).
        var generator = new PreambleGenerator(0, 2);
        byte[] fixedSection = generator.FixedSectionChips();
        byte[][] walshRows = [[0, 0, 0, 0], [0, 4, 0, 4], [0, 0, 4, 4], [0, 4, 4, 0]];
        for (int i = 256; i < 288; i++)
        {
            // Chips 256-287 belong to the 9th fixed symbol (di-bit 3, the last transmitted).
            int expected = (walshRows[3][i % 4] + Ms110dTables.FixedPn[i - 256]) % 8;
            fixedSection[i].Should().Be((byte)expected);
        }
    }

    [Fact]
    public void Has3k_Rejects_Combinations_Table_Dxxxvii_Does_Not_Define()
    {
        // A noise-corrupted WID can pass its 3-dibit checksum yet name (WN 0, UltraShort),
        // which has no Table D-XXXVII row; acquisition pre-validates with Has3k instead of
        // letting Get3k throw out of the receive path - the un-validated combination
        // crashed the receiver mid-burst on the Poor WN0 mask run (−1 dB, seed 500).
        Ms110dInterleaverParams.Has3k(0, Ms110dInterleaverKind.UltraShort).Should().BeFalse();
        Ms110dInterleaverParams.Has3k(9, Ms110dInterleaverKind.Long).Should().BeFalse();
        Ms110dInterleaverParams.Has3k(0, Ms110dInterleaverKind.Short).Should().BeTrue();
        Ms110dInterleaverParams.Has3k(0, Ms110dInterleaverKind.Long).Should().BeTrue();
        foreach (int wn in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 13 })
        {
            foreach (Ms110dInterleaverKind kind in Enum.GetValues<Ms110dInterleaverKind>())
            {
                Ms110dInterleaverParams.Has3k(wn, kind).Should().BeTrue(
                    $"WN {wn} has all four Table D-XXXVII rows");
            }
        }
    }
}
