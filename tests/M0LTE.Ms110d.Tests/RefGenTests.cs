using M0LTE.Ms110d.RefGen;

namespace M0LTE.Ms110d.Tests;

/// <summary>The reference-file generator (tools/M0LTE.Ms110d.RefGen): output is deterministic
/// and agrees with the library's own modulator.</summary>
public class RefGenTests
{
    [Theory]
    [InlineData(6, Ms110dInterleaverKind.Short, 7)]
    [InlineData(0, Ms110dInterleaverKind.Short, 9)]
    [InlineData(1, Ms110dInterleaverKind.Short, 7)]
    [InlineData(8, Ms110dInterleaverKind.Short, 9)]
    public void Same_case_twice_is_byte_identical_and_matches_the_modulator(
        int wn, Ms110dInterleaverKind il, int k)
    {
        string root = Path.Combine(Path.GetTempPath(), "refgen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var c = new RefCase(wn, il, k, 42, 150);
            RefWriter.WriteCase(c, Path.Combine(root, "a", c.DirectoryName), "test");
            RefWriter.WriteCase(c, Path.Combine(root, "b", c.DirectoryName), "test");
            RefWriter.TreeHash(Path.Combine(root, "a")).Should().Be(RefWriter.TreeHash(Path.Combine(root, "b")));

            // The audio file is exactly what Modulate returns, as little-endian float32.
            var tx = new Ms110dModulator(c.Settings);
            float[] expected = tx.Modulate(RefWriter.Payload(42, 150));
            byte[] audio = File.ReadAllBytes(Path.Combine(root, "a", c.DirectoryName, "13_audio.f32"));
            audio.Length.Should().Be(expected.Length * 4);
            for (int i = 0; i < expected.Length; i++)
            {
                BitConverter.ToSingle(audio, i * 4).Should().Be(expected[i]);
            }

            string manifest = File.ReadAllText(Path.Combine(root, "a", c.DirectoryName, "manifest.txt"));
            manifest.Should().Contain("format_version=2\n");
            manifest.Should().Contain($"audio_sample_count={expected.Length}\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Edge_cases_generate_and_are_deterministic()
    {
        string root = Path.Combine(Path.GetTempPath(), "refgen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = Ms110dInterleaverKind.Short;
            RefCase[] cases =
            [
                new(6, s, 7, 1, 0),
                new(6, s, 7, 1, 100, AppendEom: false),
                new(6, s, 7, 1, 100, AppendEot: false),
                new(6, s, 7, 1, 100, Amplitude: 0.25f),
                new(6, s, 7, 1, 100, RollOff: 0.10),
                new(6, s, 7, 1, 1536 - 32),
                new(0, Ms110dInterleaverKind.Medium, 7, 1, 300),
                new(7, Ms110dInterleaverKind.UltraShort, 9, 1, 100),
            ];
            foreach (RefCase c in cases)
            {
                RefWriter.WriteCase(c, Path.Combine(root, "a", c.DirectoryName), "test");
                RefWriter.WriteCase(c, Path.Combine(root, "b", c.DirectoryName), "test");
            }

            RefWriter.TreeHash(Path.Combine(root, "a")).Should().Be(RefWriter.TreeHash(Path.Combine(root, "b")));
            string m = File.ReadAllText(Path.Combine(root, "a", cases[0].DirectoryName, "manifest.txt"));
            m.Should().Contain("blocks=1\n"); // empty payload still sends one block
            File.ReadAllText(Path.Combine(root, "a", cases[6].DirectoryName, "manifest.txt"))
                .Should().Contain("frames_per_block=0\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_disagreement_between_tool_and_library_fails_generation()
    {
        string root = Path.Combine(Path.GetTempPath(), "refgen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var c = new RefCase(6, Ms110dInterleaverKind.Short, 7, 1, 100);
            Action act = () => RefWriter.WriteCase(
                c, Path.Combine(root, c.DirectoryName), "test",
                corruptReferenceForTest: symbols => symbols[10] = symbols[11] * -1f);
            act.Should().Throw<InvalidOperationException>().WithMessage("*differs from the library*");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void The_standard_set_covers_every_valid_waveform_and_interleaver_and_both_codes()
    {
        var cases = StandardSet.Cases();
        foreach (int wn in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 13 })
        {
            foreach (Ms110dInterleaverKind il in Enum.GetValues<Ms110dInterleaverKind>())
            {
                bool valid = Ms110dInterleaverParams.Has3k(wn, il);
                cases.Any(c => c.WaveformNumber == wn && c.Interleaver == il).Should().Be(valid, $"WN{wn} {il}");
            }

            cases.Where(c => c.WaveformNumber == wn).Select(c => c.ConstraintLength).Distinct().Should().BeEquivalentTo(new[] { 7, 9 });
        }

        cases.Should().Contain(c => !c.AppendEom).And.Contain(c => !c.AppendEot)
            .And.Contain(c => c.Amplitude != 0.5f).And.Contain(c => c.RollOff != 0.35)
            .And.Contain(c => c.PayloadBits == 0);
    }

    [Fact]
    public void Different_seeds_give_different_payloads()
    {
        RefWriter.Payload(1, 200).Should().NotEqual(RefWriter.Payload(2, 200));
    }

    [Fact]
    public void The_standard_set_names_are_unique()
    {
        StandardSet.Cases().Select(c => c.DirectoryName).Should().OnlyHaveUniqueItems();
    }
}
