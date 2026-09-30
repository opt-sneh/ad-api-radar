using System.Diagnostics;
using System.Text.Json;

namespace Radar;

public static class CodeIndexCommand
{
    public static int Run(string[] args)
    {
        string? repo = null;
        string? compileSet = null;
        string output = Path.Combine("data", "out", "code-index.json");
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for {args[i]}");
                return 2;
            }
            switch (args[i])
            {
                case "--repo":
                    repo = args[i + 1];
                    break;
                case "--compile-set":
                    compileSet = args[i + 1];
                    break;
                case "--out":
                    output = args[i + 1];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 2;
            }
        }
        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(compileSet))
        {
            Console.Error.WriteLine("Both --repo and --compile-set are required.");
            return 2;
        }

        var timer = Stopwatch.StartNew();
        try
        {
            string repoPath = Path.GetFullPath(repo);
            string compileSetPath = Path.GetFullPath(compileSet);
            if (!Directory.Exists(repoPath))
                throw new DirectoryNotFoundException($"Repository not found: {repoPath}");
            if (!File.Exists(compileSetPath))
                throw new FileNotFoundException("Compile set not found", compileSetPath);
            var set =
                JsonSerializer.Deserialize<CompileSetResult>(File.ReadAllText(compileSetPath))
                ?? throw new JsonException("Compile set is empty.");
            var result = CodeIndexer.Index(repoPath, set, (indexed, total) =>
                Console.Error.WriteLine($"Indexed {indexed}/{total} files"));
            timer.Stop();
            string outputPath = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(
                    result,
                    new JsonSerializerOptions
                    {
                        WriteIndented = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    }
                )
            );
            Console.WriteLine(
                $"Files: {result.FileCount}; definitions: {result.Counts.Definitions}; uses: {result.Counts.Uses}; queries: {result.Counts.Queries}; path literals: {result.Counts.PathLiterals}; SDK refs: {result.Counts.SdkVersionRefs}; enum members: {result.Counts.EnumMembers}; SDK types: {result.Counts.SdkTypes}; typed members: {result.Counts.TypedMembers}; GAQL enum filters: {result.Counts.GaqlEnumFilters}; parse-error files: {result.ParseErrorFiles.Count}"
            );
            Console.WriteLine(
                "Platforms: "
                    + string.Join(
                        ", ",
                        result
                            .Counts.Platforms.OrderBy(p => p.Key)
                            .Select(p => $"{p.Key}={p.Value}")
                    )
            );
            Console.WriteLine($"Elapsed: {timer.ElapsedMilliseconds} ms");
            Console.WriteLine($"JSON: {outputPath}");
            Console.Error.WriteLine($"JSON: {outputPath} ({timer.ElapsedMilliseconds} ms)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
