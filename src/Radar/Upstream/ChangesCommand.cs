using System.Text.Json;
using Google.Protobuf;

namespace Radar;

public static class ChangesCommand
{
    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (
                i + 1 >= args.Length
                || !new[] { "--from", "--to", "--snapshots", "--out" }.Contains(
                    args[i],
                    StringComparer.Ordinal
                )
                || !options.TryAdd(args[i], args[i + 1])
            )
            {
                Console.Error.WriteLine($"Invalid option: {args[i]}");
                return 2;
            }
        }
        if (
            !options.TryGetValue("--from", out string? from)
            || !options.TryGetValue("--to", out string? to)
            || !SchemaModel.IsVersion(from)
            || !SchemaModel.IsVersion(to)
            || int.Parse(from[1..]) >= int.Parse(to[1..])
        )
        {
            Console.Error.WriteLine(
                "Usage: radar changes --from v23 --to v25 [--snapshots data/snapshots] [--out data/out/changes-v23-v25.json]"
            );
            return 2;
        }
        string snapshots = options.GetValueOrDefault(
            "--snapshots",
            Path.Combine("data", "snapshots")
        );
        string output = options.GetValueOrDefault(
            "--out",
            Path.Combine("data", "out", $"changes-{from}-{to}.json")
        );
        try
        {
            var versions = Enumerable
                .Range(int.Parse(from[1..]), int.Parse(to[1..]) - int.Parse(from[1..]) + 1)
                .Select(number => "v" + number)
                .ToArray();
            var models = versions
                .Select(version =>
                    SchemaModel.Load(Path.Combine(snapshots, "google", version + ".pb"), version)
                )
                .ToArray();
            string notesPath = Path.Combine(snapshots, "google", "release-notes.html");
            string deprecationsPath = Path.Combine(snapshots, "google", "deprecations.json");
            IReadOnlyList<ReleaseNoteEntry> notes = File.Exists(notesPath)
                ? ChangeCatalog.ParseReleaseNotes(File.ReadAllText(notesPath))
                : [];
            IReadOnlyList<DeprecationRow> deprecations = File.Exists(deprecationsPath)
                ? JsonSerializer.Deserialize<List<DeprecationRow>>(
                    File.ReadAllText(deprecationsPath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                ) ?? []
                : [];
            var changes = new List<ChangeRecord>();
            for (int i = 0; i < models.Length - 1; i++)
                changes.AddRange(
                    ChangeCatalog.Compare(models[i], models[i + 1], models, notes, deprecations)
                );
            var countsByKind = changes
                .GroupBy(change => change.Kind)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            var countsBySeverity = changes
                .GroupBy(change => change.Severity)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            AtomicFile.WriteProduced(
                output,
                () =>
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            versionFrom = from,
                            versionTo = to,
                            countsByKind,
                            countsBySeverity,
                            changes,
                        },
                        new JsonSerializerOptions
                        {
                            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                            WriteIndented = true,
                        }
                    )
            );
            Console.WriteLine($"{from} -> {to}: {changes.Count} changes; {output}");
            foreach (
                var (kind, count) in countsByKind.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            )
                Console.WriteLine($"  {kind}: {count}");
            foreach (
                var (severity, count) in countsBySeverity.OrderBy(
                    pair => pair.Key,
                    StringComparer.Ordinal
                )
            )
                Console.WriteLine($"  {severity}: {count}");
            Console.WriteLine(
                $"  with release notes: {changes.Count(change => change.ReleaseNoteSnippets.Count > 0)}"
            );
            Console.WriteLine(
                $"  with rename candidates: {changes.Count(change => change.ReplacementCandidates.Count > 0)}"
            );
            return 0;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidProtocolBufferException
                        or FormatException
                        or ArgumentException
                        or JsonException
            )
        {
            Console.Error.WriteLine($"changes: {error.Message}");
            return 1;
        }
    }
}
