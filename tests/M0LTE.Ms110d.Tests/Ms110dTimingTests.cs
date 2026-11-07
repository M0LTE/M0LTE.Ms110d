using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>Sample-index timing and the BurstStarted event (docs/design-5066-support.md section 3).</summary>
public class Ms110dTimingTests
{
    private const int LeadIn = 3000;
    private const int OneChip = 4; // input samples per 2400 Bd symbol

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

    private sealed record Capture(
        List<Ms110dBurstStart> Starts, List<Ms110dRxBlock> Blocks, List<Ms110dBurst> Bursts, List<string> Diagnostics,
        List<long> BlockSeen, List<long> StartSeen);

    private static Capture Receive(float[] audio)
    {
        var demod = new Ms110dDemodulator();
        var capture = new Capture([], [], [], [], [], []);
        demod.BurstStarted += s =>
        {
            capture.Starts.Add(s);
            capture.StartSeen.Add(demod.SamplesProcessed);
        };
        demod.BlockDecoded += b =>
        {
            capture.Blocks.Add(b);
            capture.BlockSeen.Add(demod.SamplesProcessed);
        };
        demod.BurstCompleted += capture.Bursts.Add;
        demod.FrameDiagnostics += capture.Diagnostics.Add;
        for (int i = 0; i < audio.Length; i += 192)
        {
            demod.Process(audio.AsSpan(i, Math.Min(192, audio.Length - i)));
        }

        return capture;
    }

    public static IEnumerable<object[]> Modes()
    {
        foreach (int wn in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 13 })
        {
            foreach (Ms110dInterleaverKind kind in new[]
                { Ms110dInterleaverKind.UltraShort, Ms110dInterleaverKind.Short, Ms110dInterleaverKind.Medium })
            {
                if (wn == 0 && kind == Ms110dInterleaverKind.UltraShort)
                {
                    continue;
                }

                // Medium blocks of the fast modes run to tens of seconds; cover them on WN 0-4 only.
                if (kind == Ms110dInterleaverKind.Medium && wn > 4 && wn != 13)
                {
                    continue;
                }

                if (kind == Ms110dInterleaverKind.Medium && wn == 13)
                {
                    continue;
                }

                yield return [wn, kind];
            }
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void Loopback_timing_matches_the_transmitted_burst(int wn, Ms110dInterleaverKind kind)
    {
        var settings = new Ms110dTxSettings { WaveformNumber = wn, Interleaver = kind };
        Ms110dBurstPlan plan = Ms110dBurstPlan.ForBlocks(settings, kind == Ms110dInterleaverKind.Medium ? 2 : 3);
        byte[] payload = RandomBits(plan.PayloadCapacityBits, 100 + wn);
        float[] tx = new Ms110dModulator(settings).Modulate(payload);
        var audio = new float[LeadIn + tx.Length + 9600];
        tx.CopyTo(audio, LeadIn);

        Capture capture = Receive(audio);

        capture.Bursts.Should().ContainSingle().Which.Reason.Should().Be(Ms110dBurstEndReason.Eom);
        capture.Bursts[0].PayloadBits.Should().Equal(payload);
        Ms110dBurstStart start = capture.Starts.Should().ContainSingle().Subject;
        Ms110dBurst burst = capture.Bursts[0];

        // The first data chip arrives after the preamble, delayed by the transmit pulse's group delay.
        int txGroupDelay = (((Ms110dModulator.PulseSpanSymbols(settings.RollOff)) * OneChip + 1) - 1) / 2;
        long expectedDataStart = LeadIn + (plan.PreambleSymbols * OneChip) + txGroupDelay;
        start.DataStartSample.Should().BeCloseTo(expectedDataStart, OneChip);
        start.LockSample.Should().BeGreaterThanOrEqualTo(start.DataStartSample);
        capture.StartSeen[0].Should().Be(start.LockSample, "LockSample is SamplesProcessed when the event is raised");
        start.FirstSeenSample.Should().BeLessThanOrEqualTo(start.DataStartSample);
        start.FirstSeenSample.Should().BeGreaterThanOrEqualTo(LeadIn - (2 * OneChip));
        start.Lock.WaveformNumber.Should().Be(wn);
        burst.StartSample.Should().Be(start.DataStartSample);

        // The last decoded block's final mini-probe ends where the plan says the data ends,
        // less the 32-symbol EOT extension that follows it.
        int eot = wn == 0 ? 0 : 32;
        burst.LastFrameEndSample.Should().NotBeNull();
        (burst.LastFrameEndSample!.Value - burst.StartSample).Should().BeCloseTo(
            (plan.DataSymbols - eot) * OneChip, OneChip);
        burst.EndSample.Should().BeGreaterThanOrEqualTo(burst.LastFrameEndSample.Value);
        burst.EndSample.Should().Be(capture.BlockSeen[^1], "EOM ends the burst at the decode of the last block");

        capture.Blocks.Should().HaveCount(plan.Blocks);
        capture.Blocks[^1].EndSample.Should().Be(burst.LastFrameEndSample.Value);
        for (int i = 0; i < capture.Blocks.Count; i++)
        {
            capture.Blocks[i].EndSample.Should().BeLessThanOrEqualTo(capture.BlockSeen[i]);
            if (i > 0)
            {
                (capture.Blocks[i].EndSample - capture.Blocks[i - 1].EndSample).Should().BeCloseTo(
                    (long)Math.Round(plan.BlockSeconds * 9600), 2 * OneChip, $"block {i}");
            }
        }
    }

    [Fact]
    public void Noise_alone_raises_no_burst_start()
    {
        var random = new Random(5);
        var audio = new float[96000];
        for (int i = 0; i < audio.Length; i++)
        {
            audio[i] = (float)((random.NextDouble() - 0.5) * 0.4);
        }

        Capture capture = Receive(audio);

        capture.Starts.Should().BeEmpty();
        capture.Bursts.Should().BeEmpty();
    }

    [Fact]
    public void A_rejected_read_raises_no_burst_start_and_the_retried_lock_raises_one()
    {
        // The issue #8 fixture: the read at the earliest echo is rejected and retried at the
        // strongest peak. Only the accepted read is a burst start.
        const double early = 0.62;
        const int delay = 40;
        var tx = new Ms110dModulator(new Ms110dTxSettings { WaveformNumber = 5, PreambleSuperframes = 2 });
        byte[] payload = RandomBits(800, 80);
        float[] x = tx.Modulate(payload);
        var audio = new float[3000 + x.Length + delay + 9600];
        for (int i = 0; i < x.Length; i++)
        {
            audio[3000 + i] += (float)(early * x[i]);
            audio[3000 + i + delay] += x[i];
        }

        double sum = 0;
        for (int i = 3000; i < 3000 + x.Length; i++)
        {
            sum += audio[i] * audio[i];
        }

        double sigma = Math.Sqrt(sum / x.Length) * Math.Pow(10, -2.0 / 20);
        var random = new Random(1003);
        for (int i = 0; i < audio.Length; i++)
        {
            double u1 = 1.0 - random.NextDouble(), u2 = random.NextDouble();
            audio[i] += (float)(sigma * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        Capture capture = Receive(audio);

        capture.Diagnostics.Should().Contain(l => l.StartsWith("retry@", StringComparison.Ordinal));
        capture.Starts.Should().ContainSingle();
        capture.Bursts.Should().ContainSingle().Which.PayloadBits.Should().Equal(payload);
    }

    [Fact]
    public void SamplesProcessed_counts_input_samples_and_restarts_on_reset()
    {
        var demod = new Ms110dDemodulator();
        demod.SamplesProcessed.Should().Be(0);
        demod.Process(new float[1001]);
        demod.SamplesProcessed.Should().Be(1001);
        demod.Process(new float[999]);
        demod.SamplesProcessed.Should().Be(2000);
        demod.Reset();
        demod.SamplesProcessed.Should().Be(0);
    }

    [Fact]
    public void A_terminated_burst_reports_its_timing()
    {
        var settings = new Ms110dTxSettings { WaveformNumber = 4, Interleaver = Ms110dInterleaverKind.Short };
        float[] tx = new Ms110dModulator(settings).Modulate(RandomBits(4000, 9));
        var audio = new float[LeadIn + tx.Length];
        tx.CopyTo(audio, LeadIn);
        var demod = new Ms110dDemodulator();
        Ms110dBurst? burst = null;
        var starts = new List<Ms110dBurstStart>();
        demod.BurstStarted += starts.Add;
        demod.BurstCompleted += b => burst = b;
        demod.BlockDecoded += _ =>
        {
            if (demod.State == Ms110dRxState.Tracking)
            {
                demod.Terminate();
            }
        };
        for (int i = 0; i < audio.Length; i += 192)
        {
            demod.Process(audio.AsSpan(i, Math.Min(192, audio.Length - i)));
        }

        burst.Should().NotBeNull();
        burst!.Reason.Should().Be(Ms110dBurstEndReason.Terminated);
        burst.StartSample.Should().Be(starts.Should().ContainSingle().Subject.DataStartSample);
        burst.LastFrameEndSample.Should().NotBeNull();
        burst.EndSample.Should().BeGreaterThan(burst.LastFrameEndSample!.Value);
    }
}
