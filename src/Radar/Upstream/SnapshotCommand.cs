using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Radar;

public sealed class SnapshotManifest
{
    public List<SnapshotSource> Sources { get; init; } = [];
}

public sealed class SnapshotSource
{
    public required string Name { get; init; }
    public string? Url { get; init; }
    public string? Repo { get; init; }
    public string? Commit { get; set; }
    public required DateTime FetchedAtUtc { get; init; }
    public required string File { get; init; }
    public string? Sha256 { get; set; }
    public long? SizeBytes { get; set; }
    public required string Status { get; set; }
    public int? RowCount { get; set; }
    public int? FileCount { get; set; }
    public string? Error { get; set; }
    public long ElapsedMs { get; set; }
}

internal sealed record SnapshotData(byte[] Bytes, int? RowCount = null, int? FileCount = null);

public static class SnapshotCommand
{
    private const string SunsetUrl =
        "https://developers.google.com/google-ads/api/docs/sunset-dates";
    private const string DeprecationsUrl =
        "https://developers.google.com/google-ads/api/docs/deprecations";
    private const string ReleaseNotesUrl =
        "https://developers.google.com/google-ads/api/docs/release-notes";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static int Run(string[] args)
    {
        try
        {
            string output = Path.Combine("data", "snapshots");
            string versionsArg = "v23,v24,v25";
            string sdkVersionsArg = "25.1.0,26.1.0";
            string reference = "master";
            for (int i = 0; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for {args[i]}");
                switch (args[i])
                {
                    case "--out":
                        output = args[i + 1];
                        break;
                    case "--versions":
                        versionsArg = args[i + 1];
                        break;
                    case "--sdk-versions":
                        sdkVersionsArg = args[i + 1];
                        break;
                    case "--googleapis-ref":
                        reference = args[i + 1];
                        break;
                    default:
                        throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
            string[] versions = ParseList(versionsArg, @"v\d+");
            string[] sdkVersions = ParseList(sdkVersionsArg, @"\d+\.\d+\.\d+");
            if (
                string.IsNullOrWhiteSpace(reference)
                || reference.StartsWith('-')
                || reference.Contains(' ')
            )
                throw new ArgumentException("Invalid --googleapis-ref.");

            string root = FindRepoRoot();
            string outputPath = Path.GetFullPath(output);
            if (
                !IsWithin(outputPath, root)
                || outputPath.Equals(root, StringComparison.OrdinalIgnoreCase)
            )
                throw new ArgumentException("Snapshot output must be inside ad-api-radar.");
            string cachePath = Path.Combine(root, "data", "cache", "googleapis");
            if (IsWithin(outputPath, cachePath) || IsWithin(cachePath, outputPath))
                throw new ArgumentException(
                    "Snapshot output must not overlap data/cache/googleapis."
                );

            Directory.CreateDirectory(outputPath);
            var manifest = new SnapshotManifest();
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("AdApiRadar/1.0");
            var cache = new GoogleApisCache(root);
            string? commit = null;
            string? checkoutError = null;
            ProtoCompiler? compiler = null;
            foreach (string version in versions)
            {
                string file = $"google/{version}.pb";
                Capture(
                    manifest,
                    outputPath,
                    $"google-protos-{version}",
                    file,
                    null,
                    GoogleApisCache.RepositoryUrl,
                    () =>
                    {
                        if (commit is null && checkoutError is null)
                        {
                            try
                            {
                                commit = cache.Checkout(reference, versions);
                                compiler = new ProtoCompiler(root);
                            }
                            catch (Exception ex)
                            {
                                checkoutError = ex.Message;
                            }
                        }
                        if (checkoutError is not null)
                            throw new InvalidOperationException(checkoutError);
                        int count;
                        byte[] bytes = compiler!.Compile(cache.DirectoryPath, version, out count);
                        return new SnapshotData(bytes, FileCount: count);
                    },
                    () => commit
                );
            }

            Capture(
                manifest,
                outputPath,
                "sunset",
                "google/sunset.json",
                SunsetUrl,
                null,
                () =>
                {
                    var rows = SnapshotParsers.ParseSunset(Fetch(http, SunsetUrl));
                    return new SnapshotData(JsonBytes(rows), RowCount: rows.Count);
                }
            );
            Capture(
                manifest,
                outputPath,
                "deprecations",
                "google/deprecations.json",
                DeprecationsUrl,
                null,
                () =>
                {
                    var rows = SnapshotParsers.ParseDeprecations(Fetch(http, DeprecationsUrl));
                    return new SnapshotData(JsonBytes(rows), RowCount: rows.Count);
                }
            );
            Capture(
                manifest,
                outputPath,
                "release-notes",
                "google/release-notes.html",
                ReleaseNotesUrl,
                null,
                () =>
                {
                    string html = Fetch(http, ReleaseNotesUrl);
                    var document = new HtmlDocument();
                    document.LoadHtml(html);
                    if (document.DocumentNode.SelectSingleNode("//html") is null)
                        throw new FormatException(
                            "Release notes response is not an HTML document."
                        );
                    return new SnapshotData(Encoding.UTF8.GetBytes(html), FileCount: 1);
                }
            );
            Capture(
                manifest,
                outputPath,
                "sdk-deps",
                "google/sdk-deps.json",
                "https://api.nuget.org/v3-flatcontainer/google.ads.googleads/",
                null,
                () =>
                {
                    var sdks = sdkVersions
                        .Select(version =>
                            SnapshotParsers.ResolveProtobuf(version, url => Fetch(http, url))
                        )
                        .ToList();
                    return new SnapshotData(JsonBytes(sdks), RowCount: sdks.Count);
                }
            );

            AtomicFile.WriteProduced(
                Path.Combine(outputPath, "manifest.json"),
                () => JsonBytes(manifest)
            );
            foreach (var source in manifest.Sources)
            {
                string count =
                    source.RowCount is not null ? $"rows: {source.RowCount}"
                    : source.FileCount is not null ? $"files: {source.FileCount}"
                    : "count: n/a";
                string size =
                    source.SizeBytes is not null
                    && source.File.EndsWith(".pb", StringComparison.Ordinal)
                        ? $"; bytes: {source.SizeBytes}"
                        : "";
                Console.WriteLine(
                    $"{source.Name}: {source.Status}; {count}{size}; {source.ElapsedMs} ms"
                );
                if (source.Error is not null)
                    Console.Error.WriteLine($"FAILED {source.Name}: {source.Error}");
            }
            Console.WriteLine($"Manifest: {Path.Combine(outputPath, "manifest.json")}");
            return manifest.Sources.All(source => source.Status == "ok") ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void Capture(
        SnapshotManifest manifest,
        string root,
        string name,
        string file,
        string? url,
        string? repo,
        Func<SnapshotData> produce,
        Func<string?>? commit = null
    )
    {
        var timer = Stopwatch.StartNew();
        var source = new SnapshotSource
        {
            Name = name,
            Url = url,
            Repo = repo,
            FetchedAtUtc = DateTime.UtcNow,
            File = file,
            Status = "error",
            FileCount =
                file.EndsWith(".pb", StringComparison.Ordinal)
                || file.EndsWith(".html", StringComparison.Ordinal)
                    ? 0
                    : null,
            RowCount = file.EndsWith(".json", StringComparison.Ordinal) ? 0 : null,
        };
        manifest.Sources.Add(source);
        try
        {
            SnapshotData data = produce();
            string sha256 = Convert.ToHexStringLower(SHA256.HashData(data.Bytes));
            AtomicFile.WriteProduced(
                Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)),
                () => data.Bytes
            );
            source.Sha256 = sha256;
            source.SizeBytes = data.Bytes.LongLength;
            source.RowCount = data.RowCount;
            source.FileCount = data.FileCount;
            source.Status = "ok";
        }
        catch (Exception ex)
        {
            source.Error = ex.Message;
        }
        finally
        {
            source.Commit = commit?.Invoke();
            source.ElapsedMs = timer.ElapsedMilliseconds;
        }
    }

    private static string[] ParseList(string value, string itemPattern)
    {
        string[] items = value.Split(',', StringSplitOptions.TrimEntries);
        if (
            items.Length == 0
            || items.Any(item => !Regex.IsMatch(item, "^(?:" + itemPattern + ")$"))
            || items.Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length
        )
            throw new ArgumentException($"Invalid or duplicate list: {value}");
        return items;
    }

    private static string Fetch(HttpClient http, string url)
    {
        using var response = http.GetAsync(url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static byte[] JsonBytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);

    private static string FindRepoRoot()
    {
        for (
            var directory = new DirectoryInfo(Environment.CurrentDirectory);
            directory is not null;
            directory = directory.Parent
        )
        {
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            "AdApiRadar.slnx not found above the current directory."
        );
    }

    private static bool IsWithin(string path, string directory)
    {
        string prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.Equals(directory, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
