using System.Text.Json;
using Google.Protobuf;

namespace Radar;

public static class SchemaCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int RunDiff(string[] args)
    {
        if (!ReadOptions(args, ["--from", "--to", "--snapshots", "--out"], out var options))
            return 2;
        if (
            !options.TryGetValue("--from", out string? from)
            || !options.TryGetValue("--to", out string? to)
            || !SchemaModel.IsVersion(from)
            || !SchemaModel.IsVersion(to)
        )
        {
            Console.Error.WriteLine(
                "Usage: radar schema-diff --from v23 --to v24 [--snapshots data/snapshots] [--out data/out/diff-v23-v24.json]"
            );
            return 2;
        }
        string snapshots = options.GetValueOrDefault(
            "--snapshots",
            Path.Combine("data", "snapshots")
        );
        string output = options.GetValueOrDefault(
            "--out",
            Path.Combine("data", "out", $"diff-{from}-{to}.json")
        );
        try
        {
            var before = SchemaModel.Load(Path.Combine(snapshots, "google", from + ".pb"), from);
            var after = SchemaModel.Load(Path.Combine(snapshots, "google", to + ".pb"), to);
            var changes = SchemaDiff.Compare(before, after);
            var counts = changes
                .GroupBy(change => change.Kind)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            AtomicFile.WriteProduced(
                output,
                () =>
                    JsonSerializer.SerializeToUtf8Bytes(
                        new
                        {
                            from,
                            to,
                            counts,
                            changes,
                        },
                        JsonOptions
                    )
            );
            Console.WriteLine($"{from} -> {to}: {changes.Count} changes; {output}");
            foreach (var (kind, count) in counts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                Console.WriteLine($"  {kind}: {count}");
            return 0;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or UnauthorizedAccessException
                        or InvalidProtocolBufferException
                        or FormatException
                        or ArgumentException
            )
        {
            Console.Error.WriteLine($"schema-diff: {error.Message}");
            return 1;
        }
    }

    public static int RunInfo(string[] args)
    {
        if (!ReadOptions(args, ["--version", "--path", "--snapshots"], out var options))
            return 2;
        if (
            !options.TryGetValue("--version", out string? version)
            || !SchemaModel.IsVersion(version)
        )
        {
            Console.Error.WriteLine(
                "Usage: radar schema-info --version v25 [--path campaign.name] [--snapshots data/snapshots]"
            );
            return 2;
        }
        string snapshots = options.GetValueOrDefault(
            "--snapshots",
            Path.Combine("data", "snapshots")
        );
        try
        {
            var model = SchemaModel.Load(
                Path.Combine(snapshots, "google", version + ".pb"),
                version
            );
            if (options.TryGetValue("--path", out string? path))
            {
                if (!model.Fields.TryGetValue(path, out var field))
                {
                    Console.Error.WriteLine($"Unknown field: {path}");
                    return 1;
                }
                Console.WriteLine(JsonSerializer.Serialize(field, JsonOptions));
            }
            else
                Console.WriteLine(
                    $"{version}: {model.Fields.Count} fields, {model.Enums.Count} enum values, {model.Services.Count} service methods"
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
            )
        {
            Console.Error.WriteLine($"schema-info: {error.Message}");
            return 1;
        }
    }

    private static bool ReadOptions(
        string[] args,
        string[] allowed,
        out Dictionary<string, string> options
    )
    {
        options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (
                i + 1 >= args.Length
                || !allowed.Contains(args[i], StringComparer.Ordinal)
                || !options.TryAdd(args[i], args[i + 1])
            )
            {
                Console.Error.WriteLine($"Invalid option: {args[i]}");
                return false;
            }
        }
        return true;
    }
}
