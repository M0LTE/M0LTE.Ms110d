using M0LTE.Ms110d;
using M0LTE.Ms110d.RefGen;

return Cli.Run(args);

namespace M0LTE.Ms110d.RefGen
{
    internal static class Cli
    {
        private const string Usage = """
            M0LTE.Ms110d.RefGen: per-stage transmit reference files (format v1, see docs/reference-files.md)

              standard --out DIR [--verify] [--git-commit SHA]
                  Write the standard set. --verify writes it twice and compares tree hashes.
              case --out DIR --wn N --interleaver short|long|ultrashort|medium --k 7|9 --seed S --bits N
                   [--superframes M] [--tlc N] [--git-commit SHA]
                  Write one case directory under DIR.
              hash DIR
                  Print the tree hash of a reference directory.
            """;

        public static int Run(string[] args)
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine(Usage);
                return 2;
            }

            var opts = new Dictionary<string, string>();
            var flags = new HashSet<string>();
            var positional = new List<string>();
            for (int i = 1; i < args.Length; i++)
            {
                if (args[i] == "--verify")
                {
                    flags.Add("verify");
                }
                else if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                {
                    opts[args[i][2..]] = args[++i];
                }
                else
                {
                    positional.Add(args[i]);
                }
            }

            try
            {
                switch (args[0])
                {
                    case "hash" when positional.Count == 1:
                        Console.WriteLine(RefWriter.TreeHash(positional[0]));
                        return 0;
                    case "standard":
                        return Standard(opts, flags);
                    case "case":
                        return Single(opts);
                    default:
                        Console.Error.WriteLine(Usage);
                        return 2;
                }
            }
            catch (Exception ex) when (ex is KeyNotFoundException or FormatException or ArgumentException)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine(Usage);
                return 2;
            }
        }

        private static string Commit(Dictionary<string, string> opts) =>
            opts.TryGetValue("git-commit", out string? c) ? c : RefWriter.GitCommit();

        private static int Standard(Dictionary<string, string> opts, HashSet<string> flags)
        {
            string outDir = opts["out"];
            string commit = Commit(opts);
            var cases = StandardSet.Cases();
            RefWriter.WriteSet(cases, outDir, commit);
            long bytes = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            string hash = RefWriter.TreeHash(outDir);
            Console.WriteLine($"{cases.Count} cases, {bytes / 1048576.0:F1} MiB, tree hash {hash}");
            if (flags.Contains("verify"))
            {
                string second = outDir.TrimEnd('/', '\\') + ".verify";
                if (Directory.Exists(second))
                {
                    Directory.Delete(second, recursive: true);
                }

                RefWriter.WriteSet(cases, second, commit);
                string hash2 = RefWriter.TreeHash(second);
                Directory.Delete(second, recursive: true);
                Console.WriteLine($"second run tree hash {hash2}: {(hash == hash2 ? "IDENTICAL" : "DIFFERENT")}");
                return hash == hash2 ? 0 : 1;
            }

            return 0;
        }

        private static int Single(Dictionary<string, string> opts)
        {
            var il = Enum.Parse<Ms110dInterleaverKind>(opts["interleaver"], ignoreCase: true);
            var c = new RefCase(
                int.Parse(opts["wn"]), il, int.Parse(opts["k"]), ulong.Parse(opts["seed"]), int.Parse(opts["bits"]),
                opts.TryGetValue("superframes", out string? m) ? int.Parse(m) : 3,
                opts.TryGetValue("tlc", out string? t) ? int.Parse(t) : 0);
            RefWriter.WriteCase(c, Path.Combine(opts["out"], c.DirectoryName), Commit(opts));
            Console.WriteLine(c.DirectoryName);
            return 0;
        }
    }
}
