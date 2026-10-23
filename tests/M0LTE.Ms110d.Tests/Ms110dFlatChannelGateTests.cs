using M0LTE.Ms110d;
using M0LTE.HfChannel;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Quick diagnostic: verify the flat-channel turbo gate works at mask SNRs.
/// Not gated by env vars - runs in normal CI.
/// </summary>
public class Ms110dFlatChannelGateTests
{
    private static byte[] RandomBits(int count, int seed)
    {
        var random = new Random(seed);
        var bits = new byte[count];
        for (int i = 0; i < bits.Length; i++)
        {
            bits[i] = (byte)random.Next(2);
        }
        return bits;
    }

    [Theory]
    [InlineData(0, -6)]  // BPSK 1/8, AWGN mask
    [InlineData(1, -3)]  // BPSK 1/4, AWGN mask
    [InlineData(2, 0)]   // BPSK 1/3, AWGN mask
    [InlineData(3, 3)]   // BPSK 1/3, AWGN mask
    [InlineData(4, 5)]   // BPSK 2/3, AWGN mask
    [InlineData(5, 6)]   // BPSK 3/4, AWGN mask
    [InlineData(6, 9)]   // QPSK 3/4, AWGN mask
    [InlineData(7, 13)]  // 8PSK 3/4, AWGN mask
    [InlineData(13, 6)]  // QPSK 9/16, AWGN mask
    public void Awgn_At_Mask_Snr_Decodes_Cleanly(int wn, double snrDb)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            PreambleSuperframes = 5,
        });

        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long);
        int payloadBits = il.InputBits * 2;
        byte[] payload = RandomBits(payloadBits, seed: 42 + wn);

        var channel = new WattersonChannel(9600, seed: 77);
        float[] audio = channel.Apply(
            tx.Modulate(payload), snrDb, leadInSamples: 2400, leadOutSamples: 4800);

        var decoded = new List<byte>();
        var demod = new Ms110dDemodulator();
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        demod.Process(audio);

        decoded.Count.Should().BeGreaterThanOrEqualTo(payloadBits,
            $"WN{wn} must decode at least 2 blocks at {snrDb} dB AWGN");

        int errors = 0;
        int compared = Math.Min(decoded.Count, payload.Length);
        for (int i = 0; i < compared; i++)
        {
            if (decoded[i] != payload[i]) errors++;
        }

        errors.Should().Be(0,
            $"WN{wn} at {snrDb} dB AWGN must decode bit-exact (flat-channel gate should skip turbo)");
    }

    [Theory]
    [InlineData(4, 10)]  // BPSK 2/3, Poor mask
    public void Poor_At_Mask_Snr_Still_Uses_Turbo(int wn, double snrDb)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            PreambleSuperframes = 5,
        });

        Ms110dInterleaverParams il = Ms110dInterleaverParams.Get3k(wn, Ms110dInterleaverKind.Long);
        int payloadBits = il.InputBits * 2;
        byte[] payload = RandomBits(payloadBits, seed: 99 + wn);

        var channel = new WattersonChannel(9600, seed: 55, WattersonChannel.Poor);
        float[] audio = channel.Apply(
            tx.Modulate(payload), snrDb, leadInSamples: 2400, leadOutSamples: 4800);

        var decoded = new List<byte>();
        var demod = new Ms110dDemodulator();
        demod.BlockDecoded += b => decoded.AddRange(b.Bits);
        demod.Process(audio);

        // #67: the test asserts what its name claims - the Poor channel classifies as
        // fading and the turbo path actually runs - and that the decode is correct,
        // not merely present (fixed seeds make this deterministic).
        (demod.TurboConverged + demod.TurboReverted + demod.TurboAborted).Should().BeGreaterThan(0,
            $"WN{wn} on Poor must take the turbo path (fading classification)");
        decoded.Count.Should().BeGreaterThanOrEqualTo(payloadBits,
            $"WN{wn} must decode at least 2 blocks at {snrDb} dB Poor");
        int errors = 0;
        for (int i = 0; i < payloadBits; i++)
        {
            if (decoded[i] != payload[i])
            {
                errors++;
            }
        }

        // Measured on this fixed realization: 229/8192 (2.8 %) - a deep fade this short
        // two-block burst cannot interleave over, the exact gap Phase B closes (WN4 Poor
        // baseline 2.36E-5 vs the 1E-5 mask). The bound asserts "degraded, not broken";
        // tighten to 0 when B3 lands WN4 at mask.
        ((double)errors / payloadBits).Should().BeLessThan(0.05,
            $"WN{wn} at {snrDb} dB Poor must not be catastrophic (measured 2.8 % on this realization)");
    }
}
