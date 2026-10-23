using M0LTE.Dsp;
using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>
/// Design §5.4: D.5.1.5 only recommends SRRC α = 0.35, so the spectrum gate is ours and
/// absolute - analytic 99 % OBW of the shaped 2400 Bd waveform ≈ 2.89 kHz about 1800 Hz.
/// Pinned: 99 % OBW within [2700, 2950] Hz, −30 dB extent inside 170-3450 Hz, spectral
/// centroid 1800 ± 15 Hz.
/// </summary>
public class Ms110dObwTests
{
    [Theory]
    [InlineData(0)]   // Walsh chips
    [InlineData(1)]   // BPSK
    [InlineData(6)]   // QPSK
    [InlineData(7)]   // 8PSK
    [InlineData(8)]   // 16QAM
    public void Occupied_Bandwidth_Is_Pinned(int wn)
    {
        float[] steady = SteadyModulation(wn);

        (double lo, double hi, double width, _) = OccupiedBandwidth.Measure(steady, 9600);
        width.Should().BeInRange(2700, 2950, $"WN {wn} 99 % OBW");
        lo.Should().BeGreaterThan(170);
        hi.Should().BeLessThan(3450);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Minus_30_dB_Extent_And_Centroid_Are_Pinned(int wn)
    {
        float[] steady = SteadyModulation(wn);
        double[] psd = WelchPsd(steady, 4096);
        double binHz = 9600.0 / 4096;

        double peak = psd.Max();
        double centroidNum = 0, centroidDen = 0;
        for (int k = 0; k < psd.Length; k++)
        {
            double f = k * binHz;
            centroidNum += f * psd[k];
            centroidDen += psd[k];
            if (psd[k] > peak / 1000.0)
            {
                f.Should().BeInRange(170, 3450, $"WN {wn}: −30 dB extent must stay in band");
            }
        }

        (centroidNum / centroidDen).Should().BeApproximately(1800, 20, $"WN {wn} spectral centroid");
    }

    private static float[] SteadyModulation(int wn)
    {
        var tx = new Ms110dModulator(new Ms110dTxSettings
        {
            WaveformNumber = wn,
            Interleaver = Ms110dInterleaverKind.Short,
            PreambleSuperframes = 2,
        });
        var random = new Random(wn + 313);
        var payload = new byte[wn == 0 ? 300 : 4000];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)random.Next(2);
        }

        float[] audio = tx.Modulate(payload);

        // Skip the preamble + a guard so only steady data modulation is measured.
        int skip = (2 * 576 * 4) + 512;
        return audio.Skip(skip).Take(audio.Length - skip - 1024).ToArray();
    }

    private static double[] WelchPsd(float[] samples, int fftSize)
    {
        var window = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
        {
            window[i] = 0.5f - (0.5f * (float)Math.Cos(2 * Math.PI * i / (fftSize - 1)));
        }

        var power = new double[fftSize / 2];
        var re = new float[fftSize];
        var im = new float[fftSize];
        for (int offset = 0; offset + fftSize <= samples.Length; offset += fftSize / 2)
        {
            for (int i = 0; i < fftSize; i++)
            {
                re[i] = samples[offset + i] * window[i];
                im[i] = 0;
            }

            Fft.Forward(re, im);
            for (int k = 0; k < power.Length; k++)
            {
                power[k] += (re[k] * (double)re[k]) + (im[k] * (double)im[k]);
            }
        }

        return power;
    }
}
