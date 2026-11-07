using M0LTE.Ms110d;

namespace M0LTE.Ms110d.Tests;

/// <summary>PlanBurst (docs/design-5066-support.md section 5): the plan must agree with what the
/// modulator really produces.</summary>
public class Ms110dBurstPlanTests
{
    private static readonly int[] Waveforms = [0, 1, 2, 3, 4, 5, 6, 7, 8, 13];

    private static IEnumerable<Ms110dInterleaverKind> Interleavers(int wn) =>
        Enum.GetValues<Ms110dInterleaverKind>().Where(k => wn != 0 || k != Ms110dInterleaverKind.UltraShort);

    [Fact]
    public void Plan_matches_the_modulator_for_every_mode_and_option()
    {
        int checkedCount = 0;
        foreach (int wn in Waveforms)
        {
            foreach (Ms110dInterleaverKind kind in Interleavers(wn))
            {
                // The Long interleaver of the fast modes is tens of thousands of bits per
                // block; one block-boundary sweep per combination is plenty there.
                foreach (int k in new[] { 7, 9 })
                {
                    foreach (bool eom in new[] { true, false })
                    {
                        foreach (bool eot in new[] { true, false })
                        {
                            foreach (int m in new[] { 1, 3, 20 })
                            {
                                if (kind == Ms110dInterleaverKind.Long && (k == 9 || m == 20 || !eom || !eot))
                                {
                                    continue; // sampled: keep the suite fast
                                }

                                var settings = new Ms110dTxSettings
                                {
                                    WaveformNumber = wn,
                                    Interleaver = kind,
                                    ConstraintLength = k,
                                    AppendEom = eom,
                                    AppendEot = eot,
                                    PreambleSuperframes = m,
                                };
                                int inputBits = Ms110dInterleaverParams.Get3k(wn, kind).InputBits;
                                int eomBits = eom ? 32 : 0;
                                foreach (int payload in new[] { 0, 1, inputBits - eomBits, inputBits - eomBits + 1 }
                                    .Where(p => p >= 0).Distinct())
                                {
                                    AssertAgrees(settings, payload);
                                    checkedCount++;
                                }
                            }
                        }
                    }
                }
            }
        }

        checkedCount.Should().BeGreaterThan(500);
    }

    private static void AssertAgrees(Ms110dTxSettings settings, int payloadBits)
    {
        var modulator = new Ms110dModulator(settings);
        var bits = new byte[payloadBits];
        Ms110dBurstPlan plan = Ms110dBurstPlan.Plan(settings, payloadBits);
        string what = $"wn={settings.WaveformNumber} il={settings.Interleaver} k={settings.ConstraintLength} " +
            $"eom={settings.AppendEom} eot={settings.AppendEot} m={settings.PreambleSuperframes} bits={payloadBits}";

        Complex32[] symbols = modulator.BuildSymbols(bits);
        plan.AirSymbols.Should().Be(symbols.Length, what);
        plan.Samples.Should().Be(modulator.Modulate(bits).Length, what);
        plan.Seconds.Should().BeApproximately(plan.AirSymbols / 2400.0, 1e-12, what);

        int blockSymbols = settings.WaveformNumber == 0
            ? Ms110dInterleaverParams.Get3k(0, settings.Interleaver).Frames * 32
            : Ms110dInterleaverParams.Get3k(settings.WaveformNumber, settings.Interleaver).Frames *
              (modulator.Mode.U + modulator.Mode.K);
        plan.BlockSeconds.Should().BeApproximately(blockSymbols / 2400.0, 1e-12, what);

        double expectedDelta = settings.AppendEot && settings.WaveformNumber != 0 ? 32 / 2400.0 : 0;
        (plan.Seconds - modulator.BurstSeconds(payloadBits)).Should().BeApproximately(expectedDelta, 1e-9, what);

        plan.PayloadCapacityBits.Should().BeGreaterThanOrEqualTo(payloadBits, what);
        plan.WaveformNumber.Should().Be(settings.WaveformNumber);
        plan.Interleaver.Should().Be(settings.Interleaver);
        plan.ConstraintLength.Should().Be(settings.ConstraintLength);
    }

    [Fact]
    public void Block_count_is_the_one_the_symbol_builder_used()
    {
        var settings = new Ms110dTxSettings { WaveformNumber = 4, Interleaver = Ms110dInterleaverKind.Short };
        int inputBits = Ms110dInterleaverParams.Get3k(4, settings.Interleaver).InputBits;
        int cap = inputBits - 32;
        Ms110dBurstPlan.Plan(settings, 0).Blocks.Should().Be(1);
        Ms110dBurstPlan.Plan(settings, cap).Blocks.Should().Be(1);
        Ms110dBurstPlan.Plan(settings, cap + 1).Blocks.Should().Be(2);
        Ms110dBurstPlan.Plan(settings, cap + inputBits).Blocks.Should().Be(2);
        Ms110dBurstPlan.Plan(settings, cap + inputBits + 1).Blocks.Should().Be(3);
        Ms110dBurstPlan.Plan(settings with { AppendEom = false }, inputBits).Blocks.Should().Be(1);
        Ms110dBurstPlan.Plan(settings with { AppendEom = false }, inputBits + 1).Blocks.Should().Be(2);
    }

    [Fact]
    public void ForBlocks_carries_exactly_its_payload_capacity()
    {
        foreach (int wn in new[] { 0, 2, 6, 13 })
        {
            var settings = new Ms110dTxSettings { WaveformNumber = wn, Interleaver = Ms110dInterleaverKind.Short };
            for (int blocks = 1; blocks <= 4; blocks++)
            {
                Ms110dBurstPlan forBlocks = Ms110dBurstPlan.ForBlocks(settings, blocks);
                forBlocks.Blocks.Should().Be(blocks);
                Ms110dBurstPlan.Plan(settings, forBlocks.PayloadCapacityBits).Should().Be(forBlocks);
                Ms110dBurstPlan.Plan(settings, forBlocks.PayloadCapacityBits + 1).Blocks.Should().Be(blocks + 1);
            }
        }
    }

    [Fact]
    public void Bytes_per_block_match_table_D_XXXVII()
    {
        // Information bits per block / 8, Ultra Short, Short, Medium, Long (WN 0 has no Ultra Short).
        var expected = new Dictionary<int, int[]>
        {
            [0] = [0, 5, 18, 72],
            [1] = [3, 12, 48, 192],
            [2] = [6, 24, 96, 384],
            [3] = [8, 32, 128, 512],
            [4] = [16, 64, 256, 1024],
            [5] = [24, 96, 384, 1536],
            [6] = [48, 192, 768, 3072],
            [7] = [72, 288, 1152, 4608],
            [8] = [96, 384, 1536, 6144],
            [13] = [36, 144, 576, 2304],
        };
        foreach ((int wn, int[] bytes) in expected)
        {
            foreach (Ms110dInterleaverKind kind in Interleavers(wn))
            {
                var plan = Ms110dBurstPlan.Plan(
                    new Ms110dTxSettings { WaveformNumber = wn, Interleaver = kind }, 0);
                plan.BytesPerBlock.Should().Be(bytes[(int)kind], $"wn={wn} {kind}");
                plan.InputBitsPerBlock.Should().Be(plan.BytesPerBlock * 8);
            }
        }
    }

    [Fact]
    public void BurstSeconds_is_unchanged_and_bad_input_is_rejected()
    {
        var settings = new Ms110dTxSettings();
        var modulator = new Ms110dModulator(settings);
        // 3 super-frames of 18 x 32 chips, one probe, 16 frames of (U + K) for one Short block.
        modulator.BurstSeconds(0).Should().BeApproximately(
            ((3 * 18 * 32) + modulator.Mode.K +
             (Ms110dInterleaverParams.Get3k(6, settings.Interleaver).Frames * (modulator.Mode.U + modulator.Mode.K))) / 2400.0,
            1e-12);

        FluentActions.Invoking(() => Ms110dBurstPlan.Plan(settings, -1)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => Ms110dBurstPlan.ForBlocks(settings, 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => Ms110dBurstPlan.Plan(settings with { WaveformNumber = 9 }, 0))
            .Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => Ms110dBurstPlan.Plan(settings with { ConstraintLength = 8 }, 0))
            .Should().Throw<ArgumentException>();
    }
}
