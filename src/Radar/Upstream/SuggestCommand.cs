using System.Globalization;
using Google.Protobuf;

namespace Radar;

public static class SuggestCommand
{
    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !new[] { "--path", "--version", "--snapshots" }
                .Contains(args[i], StringComparer.Ordinal) || !options.TryAdd(args[i], args[i + 1]))
            {
                Console.Error.WriteLine($"Invalid option: {args[i]}");
                return 2;
            }
        }
        string version = options.GetValueOrDefault("--version", "v25");
        if (!options.TryGetValue("--path", out string? path)
            || path.Length == 0 || !path.Contains('.') || !SchemaModel.IsVersion(version))
        {
            Console.Error.WriteLine("Usage: radar suggest --path <gaql path> [--version v25] [--snapshots data/snapshots]");
            return 2;
        }
        string snapshots = options.GetValueOrDefault("--snapshots", Path.Combine("data", "snapshots"));
        try
        {
            var schema = SchemaModel.Load(Path.Combine(snapshots, "google", version + ".pb"), version);
            var candidates = ChangeCatalog.FindCandidates(path, [schema]);
            Console.WriteLine($"{path} ({version}): {candidates.Count} candidates");
            foreach (var candidate in candidates)
            {
                string parent = candidate.Symbol[..candidate.Symbol.LastIndexOf('.')];
                string name = candidate.Symbol[(candidate.Symbol.LastIndexOf('.') + 1)..];
                string display = schema.Messages.TryGetValue(parent, out var message)
                    && message.GaqlName is not null
                    ? message.GaqlName + "." + name : candidate.Symbol;
                Console.WriteLine($"  {display}  {candidate.Score.ToString("0.###", CultureInfo.InvariantCulture)}  {candidate.Reason}");
            }
            return 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
            or InvalidProtocolBufferException or FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"suggest: {error.Message}");
            return 1;
        }
    }
}
