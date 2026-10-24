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
            byte[] audio = File.ReadAllBytes(Path.Combine(root, "a", c.DirectoryName, "10_audio.f32"));
            audio.Length.Should().Be(expected.Length * 4);
            for (int i = 0; i < expected.Length; i++)
            {
                BitConverter.ToSingle(audio, i * 4).Should().Be(expected[i]);
            }

            string manifest = File.ReadAllText(Path.Combine(root, "a", c.DirectoryName, "manifest.txt"));
            manifest.Should().Contain("format_version=1\n");
            manifest.Should().Contain($"audio_sample_count={expected.Length}\n");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
