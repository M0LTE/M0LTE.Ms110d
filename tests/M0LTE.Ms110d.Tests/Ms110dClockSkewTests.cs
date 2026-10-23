using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Rung 1 sample-clock-skew rig (design §5.1, issue #67): TX audio is resampled by a
/// ppm-scale rate offset before RX, modelling mismatched soundcard oscillators. The
/// demodulator's documented mechanism is the slow per-probe timing tracker
/// (<see cref="Ms110dDemodulator"/> remarks; WN 0 has none, so it is out of scope here).
/// </summary>
/// <remarks>
/// Calibration, 2026-07-23 on the Phase A closeout equalizer (ff1d832), on exactly this
/// rig (clean channel, ~3.6-4 s bursts, default Short/K=7): WN2/WN5/WN6 all decode
/// bit-exact at ±2, ±5, ±10, ±20, ±50, ±150, ±300 and ±500 ppm; the first failures are
/// WN5/WN6 at +700 ppm (both still pass −700, WN2 solid through ±700); everything fails
/// by ±1000 ppm. Tolerance is duration-dependent - on 4× longer bursts (~11 s) all three
/// WNs pass ±200 ppm and fail from +300/±400 ppm. The pinned ±50 ppm is the §5.1
/// adversarial figure (the issue #67 aspiration - met), with ~14× margin to the breaking
/// point on this geometry and ≥4× against the long-burst breaking point.
/// </remarks>
public class Ms110dClockSkewTests
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

    /// <summary>Resamples <paramref name="input"/> as heard by a receiver whose sample
    /// clock runs (1 + ppm·1e-6) fast: output sample n is the windowed-sinc interpolation
    /// of the input at position n·(1 + ppm·1e-6). Same Hann-windowed-sinc kernel as
    /// <c>WattersonChannel.FractionalDelay</c> (±16 taps).</summary>
    private static float[] Resample(float[] input, double ppm)
    {
        double ratio = 1.0 + (ppm * 1e-6);
        int count = (int)((input.Length - 1) / ratio);
        var output = new float[count];
        const int half = 16;
        for (int n = 0; n < count; n++)
        {
            double pos = n * ratio;
            int i0 = (int)Math.Floor(pos);
            double frac = pos - i0;
            double acc = 0;
            for (int j = -half + 1; j <= half; j++)
            {
                int k = i0 + j;
                if (k < 0 || k >= input.Length)
                {
                    continue;
                }

                double u = j - frac;
                double w = Math.Abs(u) < 1e-9
                    ? 1.0
                    : Math.Sin(Math.PI * u) / (Math.PI * u) * (0.5 + (0.5 * Math.Cos(Math.PI * u / half)));
                acc += input[k] * w;
            }

            output[n] = (float)acc;
        }

        return output;
    }

    [Theory]
    [InlineData(2, -50)]
    [InlineData(2, 50)]
    [InlineData(5, -50)]
    [InlineData(5, 50)]
    [InlineData(6, -50)]
    [InlineData(6, 50)]
    public void Fifty_Ppm_Sample_Clock_Skew_Decodes_Bit_Exact(int wn, double ppm)
    {
        // Multi-block payloads (~2.5 s of data on air) so the skew has air time to
        // accumulate drift across many probes - a one-block burst would understate it.
        int bits = wn switch { 2 => 768, 5 => 3840, _ => 7680 };
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = wn });
        byte[] payload = RandomBits(bits, 500 + wn);
        float[] skewed = Resample(tx.Modulate(payload), ppm);

        var demod = new Ms110dDemodulator();
        Ms110dBurst? burst = null;
        demod.BurstCompleted += b => burst ??= b;
        demod.Process(new float[1500]);
        demod.Process(skewed);
        demod.Process(new float[6000]);

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        burst.PayloadBits.Should().Equal(payload);
    }
}
