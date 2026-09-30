using System.Net;
using System.Text;
using System.Text.Json;

namespace Radar;

public static class CheckCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        bool experimental = false;
        for (int i = 0; i < args.Length;)
        {
            if (args[i] == "--experimental" && !experimental)
            {
                experimental = true;
                i++;
                continue;
            }
            if (
                i + 1 >= args.Length
                || !new[]
                {
                    "--index",
                    "--repo",
                    "--snapshots",
                    "--target",
                    "--next",
                    "--sdk",
                    "--props",
                    "--out",
                    "--html",
                    "--changes",
                    "--gaql-results",
                    "--gaql-queries",
                }.Contains(args[i])
                || !options.TryAdd(args[i], args[i + 1])
            )
            {
                Console.Error.WriteLine($"Invalid option: {args[i]}");
                return 2;
            }
            i += 2;
        }
        if (!options.ContainsKey("--index") || !options.ContainsKey("--repo"))
        {
            Console.Error.WriteLine(
                "Usage: radar check --index <code-index.json> --repo <root> [--snapshots data/snapshots] [--target v23] [--next v24,v25] [--sdk 25.1.0] [--props code/backend/Directory.Packages.props] [--gaql-results <json> --gaql-queries <json>] [--out data/out/findings.json] [--html data/out/report.html] [--experimental]"
            );
            return 2;
        }
        try
        {
            if (options.ContainsKey("--gaql-results") != options.ContainsKey("--gaql-queries"))
                throw new ArgumentException("--gaql-results and --gaql-queries must be supplied together.");
            string repo = Path.GetFullPath(options["--repo"]);
            var index =
                JsonSerializer.Deserialize<CodeIndexResult>(
                    File.ReadAllText(options["--index"]),
                    JsonOptions
                ) ?? throw new JsonException("Empty index.");
            string target = options.GetValueOrDefault("--target") ?? DetectTarget(index);
            if (!SchemaModel.IsVersion(target))
                throw new ArgumentException("Invalid target version.");
            string root = options.GetValueOrDefault(
                "--snapshots",
                Path.Combine("data", "snapshots")
            );
            string google = Path.Combine(root, "google");
            var versions = Directory
                .GetFiles(google, "v*.pb")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(v => v is not null && SchemaModel.IsVersion(v))
                .Select(v => v!)
                .OrderBy(v => int.Parse(v[1..]))
                .ToArray();
            if (!versions.Contains(target))
                throw new FileNotFoundException($"Target snapshot {target} not found.");
            string[] following = options.TryGetValue("--next", out string? requested)
                ? requested.Split(
                    ',',
                    StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
                )
                : versions.Where(v => int.Parse(v[1..]) > int.Parse(target[1..])).ToArray();
            if (
                following.Any(v =>
                    !SchemaModel.IsVersion(v)
                    || !versions.Contains(v)
                    || int.Parse(v[1..]) <= int.Parse(target[1..])
                )
                || following.Distinct(StringComparer.Ordinal).Count() != following.Length
            )
                throw new ArgumentException(
                    "--next must list unique available versions newer than target."
                );
            following = following.OrderBy(v => int.Parse(v[1..])).ToArray();
            var models = versions
                .Select(v => SchemaModel.Load(Path.Combine(google, v + ".pb"), v))
                .ToDictionary(m => m.Version);
            var modelChain = versions.Select(v => models[v]).ToArray();
            IReadOnlyList<ChangeRecord> catalog;
            if (options.TryGetValue("--changes", out string? catalogPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(catalogPath));
                catalog =
                    document
                        .RootElement.GetProperty("changes")
                        .Deserialize<List<ChangeRecord>>(JsonOptions)
                    ?? [];
            }
            else
            {
                string notesPath = Path.Combine(google, "release-notes.html");
                IReadOnlyList<ReleaseNoteEntry> notes = File.Exists(notesPath)
                    ? ChangeCatalog.ParseReleaseNotes(File.ReadAllText(notesPath))
                    : [];
                var rows = Read<List<DeprecationRow>>(Path.Combine(google, "deprecations.json"));
                var computed = new List<ChangeRecord>();
                SchemaModel previous = models[target];
                foreach (string version in following)
                {
                    computed.AddRange(
                        ChangeCatalog.Compare(previous, models[version], modelChain, notes, rows)
                    );
                    previous = models[version];
                }
                catalog = computed;
            }
            string sdkVersion =
                options.GetValueOrDefault("--sdk")
                ?? target switch
                {
                    "v23" => "25.1.0",
                    "v25" => "26.1.0",
                    _ => "",
                };
            var report = CheckEngine.Run(
                index,
                repo,
                models[target],
                modelChain,
                following.Select(v => models[v]).ToArray(),
                Read<List<SunsetRow>>(Path.Combine(google, "sunset.json")),
                Read<List<SdkDependencies>>(Path.Combine(google, "sdk-deps.json")),
                Read<List<DeprecationRow>>(Path.Combine(google, "deprecations.json")),
                sdkVersion,
                DateOnly.FromDateTime(DateTime.Today),
                catalog,
                experimental,
                options.GetValueOrDefault("--props") ?? "code/backend/Directory.Packages.props"
            );
            if (options.TryGetValue("--gaql-results", out string? gaqlResults))
                GaqlValidation.Merge(report, gaqlResults, options["--gaql-queries"]);
            string output = options.GetValueOrDefault(
                "--out",
                Path.Combine("data", "out", "findings.json")
            );
            string html = options.GetValueOrDefault(
                "--html",
                Path.Combine("data", "out", "report.html")
            );
            AtomicFile.WriteProduced(
                output,
                () => JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions)
            );
            AtomicFile.WriteProduced(html, () => Encoding.UTF8.GetBytes(CheckHtml.Render(report)));
            Console.WriteLine(
                $"Target: {target}" + (options.ContainsKey("--target") ? "" : " (auto-detected)")
            );
            foreach (var (category, count) in report.Counts)
                Console.WriteLine($"{category}: {count}");
            Console.WriteLine(
                $"Non-GAQL queries skipped: {report.NonGaqlQueriesSkipped}; unknown-root paths skipped: {report.UnknownRootPathsSkipped}; behaviour rows without hits: {report.BehaviourRowsWithoutHits}"
            );
            Console.WriteLine($"JSON: {Path.GetFullPath(output)}");
            Console.WriteLine($"HTML: {Path.GetFullPath(html)}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"check: {error.Message}");
            return 1;
        }
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions)
        ?? throw new JsonException($"Empty snapshot: {path}");

    private static string DetectTarget(CodeIndexResult index) => TargetDetector.Detect(index);
}
