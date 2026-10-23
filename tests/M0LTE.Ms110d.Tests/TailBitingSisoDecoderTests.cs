using M0LTE.Fec;
using M0LTE.Ms110d.Fec;

namespace M0LTE.Ms110d.Tests;

public class TailBitingSisoDecoderTests
{
    private static byte[] RandomInfo(int n, int seed)
    {
        var random = new Random(seed);
        var info = new byte[n];
        for (int i = 0; i < n; i++)
        {
            info[i] = (byte)random.Next(2);
        }

        return info;
    }

    [Theory]
    [InlineData(7, 96)]
    [InlineData(7, 384)]
    [InlineData(9, 96)]
    [InlineData(9, 384)]
    public void Saturated_Clean_Input_Reproduces_The_Encoder_Output_At_Every_Position(int k, int n)
    {
        ConvolutionalCode code = k == 9 ? ConvolutionalCode.K9 : ConvolutionalCode.K7;
        byte[] info = RandomInfo(n, 41 + k + n);
        var coded = new byte[2 * n];
        TailBitingEncoder.Encode(code, info, coded);

        var llrs = new float[2 * n];
        for (int i = 0; i < llrs.Length; i++)
        {
            llrs[i] = coded[i] == 0 ? 20f : -20f;
        }

        var post = new float[2 * n];
        new TailBitingSisoDecoder(code).Decode(llrs, post);

        for (int i = 0; i < post.Length; i++)
        {
            (post[i] > 0f ? 0 : 1).Should().Be(coded[i], $"position {i}");
            MathF.Abs(post[i]).Should().BeGreaterThan(10f, $"position {i} is clean and saturated");
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(9)]
    public void Erased_Positions_Are_Filled_In_By_The_Code_On_Clean_Input(int k)
    {
        // Erase the 3/4-puncture pattern's holes (the live WN5-8 configuration) - the code's
        // posterior at an erased position comes purely from the trellis constraint and must
        // still carry the transmitted bit's sign everywhere.
        ConvolutionalCode code = k == 9 ? ConvolutionalCode.K9 : ConvolutionalCode.K7;
        PunctureSpec spec = Ms110dPuncture.Get(code, "3/4");
        const int N = 288; // integral 3-column mask cycles
        byte[] info = RandomInfo(N, 977 + k);
        var coded = new byte[2 * N];
        TailBitingEncoder.Encode(code, info, coded);

        var llrs = new float[2 * N];
        int len = spec.KeepT1.Length;
        int erased = 0;
        for (int p = 0; p < N; p++)
        {
            int col = p % len;
            llrs[2 * p] = spec.KeepT1[col] != 0 ? (coded[2 * p] == 0 ? 8f : -8f) : 0f;
            llrs[(2 * p) + 1] = spec.KeepT2[col] != 0 ? (coded[(2 * p) + 1] == 0 ? 8f : -8f) : 0f;
            erased += (spec.KeepT1[col] == 0 ? 1 : 0) + (spec.KeepT2[col] == 0 ? 1 : 0);
        }

        erased.Should().BeGreaterThan(0);
        var post = new float[2 * N];
        new TailBitingSisoDecoder(code).Decode(llrs, post);

        for (int i = 0; i < post.Length; i++)
        {
            (post[i] > 0f ? 0 : 1).Should().Be(coded[i], $"position {i}");
        }
    }

    [Theory]
    [InlineData(7, 0.7f)]
    [InlineData(9, 0.7f)]
    public void Posterior_Signs_Match_The_Transmitted_Bits_Where_The_Viterbi_Decodes_Cleanly(
        int k, float sigma)
    {
        // BPSK + AWGN at an SNR where the Viterbi recovers the block exactly; the SISO
        // posteriors must then agree with the transmitted coded stream at every position.
        ConvolutionalCode code = k == 9 ? ConvolutionalCode.K9 : ConvolutionalCode.K7;
        const int N = 384;
        byte[] info = RandomInfo(N, 1201 + k);
        var coded = new byte[2 * N];
        TailBitingEncoder.Encode(code, info, coded);

        var random = new Random(555 + k);
        var llrs = new float[2 * N];
        for (int i = 0; i < llrs.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double y = (coded[i] == 0 ? 1.0 : -1.0) + (sigma * gauss);
            llrs[i] = (float)(2.0 * y / (sigma * sigma));
        }

        var viterbiInfo = new byte[N];
        new TailBitingViterbiDecoder(code).Decode(llrs, viterbiInfo);
        viterbiInfo.Should().Equal(info, "the operating point must be clean for this comparison");

        var post = new float[2 * N];
        new TailBitingSisoDecoder(code).Decode(llrs, post);
        for (int i = 0; i < post.Length; i++)
        {
            (post[i] > 0f ? 0 : 1).Should().Be(coded[i], $"position {i}");
        }
    }

    [Fact]
    public void Posteriors_Sharpen_Over_The_Input_Where_The_Code_Agrees()
    {
        // On a decodable noisy block the code adds information: the mean posterior
        // magnitude must exceed the mean input magnitude, and every output stays finite.
        ConvolutionalCode code = ConvolutionalCode.K7;
        const int N = 384;
        const float Sigma = 1.0f;
        byte[] info = RandomInfo(N, 7001);
        var coded = new byte[2 * N];
        TailBitingEncoder.Encode(code, info, coded);

        var random = new Random(7002);
        var llrs = new float[2 * N];
        for (int i = 0; i < llrs.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble();
            double u2 = random.NextDouble();
            double gauss = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
            double y = (coded[i] == 0 ? 1.0 : -1.0) + (Sigma * gauss);
            llrs[i] = (float)(2.0 * y / (Sigma * Sigma));
        }

        var post = new float[2 * N];
        new TailBitingSisoDecoder(code).Decode(llrs, post);

        double meanIn = 0, meanOut = 0;
        for (int i = 0; i < post.Length; i++)
        {
            float.IsFinite(post[i]).Should().BeTrue($"position {i}");
            meanIn += MathF.Abs(llrs[i]);
            meanOut += MathF.Abs(post[i]);
        }

        meanOut.Should().BeGreaterThan(meanIn);
    }

    [Fact]
    public void Decoder_Instance_Is_Reusable_Across_Block_Sizes()
    {
        // The demodulator holds one decoder per burst and block sizes vary by interleaver
        // choice - a larger block after a smaller one must not read stale state.
        ConvolutionalCode code = ConvolutionalCode.K7;
        var siso = new TailBitingSisoDecoder(code);
        foreach (int n in new[] { 96, 384, 96, 768 })
        {
            byte[] info = RandomInfo(n, 8100 + n);
            var coded = new byte[2 * n];
            TailBitingEncoder.Encode(code, info, coded);
            var llrs = new float[2 * n];
            for (int i = 0; i < llrs.Length; i++)
            {
                llrs[i] = coded[i] == 0 ? 12f : -12f;
            }

            var post = new float[2 * n];
            siso.Decode(llrs, post);
            for (int i = 0; i < post.Length; i++)
            {
                (post[i] > 0f ? 0 : 1).Should().Be(coded[i], $"block {n}, position {i}");
            }
        }
    }
}
