using M0LTE.Ms110d;
using M0LTE.Ms110d.RefGen;

return Cli.Run(args);

namespace M0LTE.Ms110d.RefGen
{
    internal static class Cli
    {
        private const string Usage = """
            M0LTE.Ms110d.RefGen: per-stage transmit reference files (format v2, see docs/reference-files.md)

              standard --out DIR [--verify] [--git-commit SHA] [--allow-dirty]
                  Write the standard set. --verify writes it twice and compares tree hashes.
              case --out DIR --wn N --interleaver short|long|ultrashort|medium --k 7|9 --seed S --bits N
                   [--superframes M] [--tlc N] [--no-eom] [--no-eot] [--amplitude A] [--roll-off R]
                   [--git-commit SHA] [--allow-dirty]
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
                if (args[i] is "--verify" or "--allow-dirty" or "--no-eom" or "--no-eot")
                {
                    flags.Add(args[i][2..]);
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
                        return Single(opts, flags);
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

        // An explicit --git-commit pins the manifest and skips the git check. Otherwise a dirty
        // tree is refused unless --allow-dirty is given (the manifest then says dirty=true).
        private static (string Commit, bool Dirty) Commit(Dictionary<string, string> opts, HashSet<string> flags)
        {
            if (opts.TryGetValue("git-commit", out string? c))
            {
                return (c, false);
            }

            (string commit, bool dirty) = RefWriter.GitState();
            if (dirty && !flags.Contains("allow-dirty"))
            {
                throw new ArgumentException(
                    "the working tree has uncommitted changes; commit them, pass --git-commit SHA, or pass --allow-dirty");
            }

            return (commit, dirty);
        }

        private static int Standard(Dictionary<string, string> opts, HashSet<string> flags)
        {
            string outDir = opts["out"];
            (string commit, bool dirty) = Commit(opts, flags);
            var cases = StandardSet.Cases();
            RefWriter.WriteSet(cases, outDir, commit, dirty);
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

                RefWriter.WriteSet(cases, second, commit, dirty);
                string hash2 = RefWriter.TreeHash(second);
                Directory.Delete(second, recursive: true);
                Console.WriteLine($"second run tree hash {hash2}: {(hash == hash2 ? "IDENTICAL" : "DIFFERENT")}");
                return hash == hash2 ? 0 : 1;
            }

            return 0;
        }

        private static int Single(Dictionary<string, string> opts, HashSet<string> flags)
        {
            var il = Enum.Parse<Ms110dInterleaverKind>(opts["interleaver"], ignoreCase: true);
            var c = new RefCase(
                int.Parse(opts["wn"]), il, int.Parse(opts["k"]), ulong.Parse(opts["seed"]), int.Parse(opts["bits"]),
                opts.TryGetValue("superframes", out string? m) ? int.Parse(m) : 3,
                opts.TryGetValue("tlc", out string? t) ? int.Parse(t) : 0,
                !flags.Contains("no-eom"),
                !flags.Contains("no-eot"),
                opts.TryGetValue("amplitude", out string? a) ? float.Parse(a, System.Globalization.CultureInfo.InvariantCulture) : 0.5f,
                opts.TryGetValue("roll-off", out string? r) ? double.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : 0.35);
            (string commit, bool dirty) = Commit(opts, flags);
            RefWriter.WriteCase(c, Path.Combine(opts["out"], c.DirectoryName), commit, dirty);
            Console.WriteLine(c.DirectoryName);
            return 0;
        }
    }
}
