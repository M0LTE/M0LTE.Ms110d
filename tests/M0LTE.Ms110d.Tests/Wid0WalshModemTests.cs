using M0LTE.Ms110d;
using M0LTE.Ofdm;

namespace M0LTE.Ms110d.Tests;

public class Wid0WalshModemTests
{
    // D.5.1.4 printed worked example (text-layer verbatim): di-bit 01 → Walsh 0 4 0 4
    // repeated, combined modulo 8 with the first 32 scramble symbols:
    private static readonly byte[] PrintedCombineRow =
    [
        5, 2, 2, 5, 7, 7, 1, 5, 6, 4, 5, 0, 0, 3, 7, 4,
        5, 7, 1, 7, 3, 6, 2, 1, 5, 0, 7, 7, 5, 0, 3, 4,
    ];

    [Fact]
    public void Dibit_01_Reproduces_The_Printed_Combine_Row()
    {
        var modem = new Wid0WalshModem();
        var chips = new byte[32];
        modem.Modulate([0, 1], chips);

        chips.Should().Equal(PrintedCombineRow);
    }

    [Fact]
    public void Demodulate_Recovers_Modulated_Dibits_On_A_Clean_Channel()
    {
        var tx = new Wid0WalshModem();
        var rx = new Wid0WalshModem();
        var random = new Random(12345);
        var bits = new byte[2 * 200];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }

        var chips = new byte[bits.Length * 16];
        tx.Modulate(bits, chips);

        var rxChips = new Cf[32];
        var llrs = new float[2];
        for (int n = 0; n < bits.Length / 2; n++)
        {
            for (int i = 0; i < 32; i++)
            {
                rxChips[i] = Ms110dTables.Psk8[chips[(n * 32) + i]];
            }

            rx.Demodulate(rxChips, llrs, out int best, out Cf corr);
            best.Should().Be((bits[2 * n] << 1) | bits[(2 * n) + 1]);
            (llrs[0] > 0 ? 0 : 1).Should().Be(bits[2 * n]);
            (llrs[1] > 0 ? 0 : 1).Should().Be(bits[(2 * n) + 1]);
            corr.Re.Should().BeApproximately(32, 0.01f);
            corr.Im.Should().BeApproximately(0, 0.01f);
        }
    }

    [Fact]
    public void Reset_Realigns_Transmitter_And_Receiver_At_Interleaver_Boundaries()
    {
        var tx = new Wid0WalshModem();
        var rx = new Wid0WalshModem();
        var chips = new byte[32 * 2];
        tx.Modulate([1, 1, 0, 0], chips);

        tx.Reset();
        var afterReset = new byte[32];
        tx.Modulate([0, 1], afterReset);
        afterReset.Should().Equal(PrintedCombineRow);

        // The receiver descrambles with its own sequence; a matching Reset keeps it aligned.
        rx.Reset();
        var rxChips = new Cf[32];
        for (int i = 0; i < 32; i++)
        {
            rxChips[i] = Ms110dTables.Psk8[afterReset[i]];
        }

        var llrs = new float[2];
        rx.Demodulate(rxChips, llrs, out int best, out _);
        best.Should().Be(1);
    }
}
