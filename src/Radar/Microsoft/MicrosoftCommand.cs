using System.Text.Json;
using System.Xml.Linq;

namespace Radar;

// radar microsoft --repo <root> --compile-set <json> [--props <path>] [--from 13.0.28] [--to 13.0.30] [--out <dir>]
public static class MicrosoftCommand
{
    private const string Feed =
        "https://techcommunity.microsoft.com/t5/s/gxcuf89792/rss/board?board.id=AdsAPIBlog";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public sealed record Result(
        CheckReport Report,
        string ReportPath,
        string From,
        string To,
        string? Warning
    );

    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < args.Length; i += 2)
            options[args[i]] = args[i + 1];
        if (
            args.Length % 2 != 0
            || !options.ContainsKey("--repo")
            || !options.ContainsKey("--compile-set")
        )
        {
            Console.Error.WriteLine(
                "Usage: radar microsoft --repo <root> --compile-set <json> [--props code/backend/Directory.Packages.props] [--from <sdk>] [--to <sdk>] [--out data/out/microsoft]"
            );
            return 2;
        }
        try
        {
            var result = Execute(
                Environment.CurrentDirectory,
                options["--repo"],
                options["--compile-set"],
                options.GetValueOrDefault("--props", "code/backend/Directory.Packages.props"),
                options.GetValueOrDefault("--out", Path.Combine("data", "out", "microsoft")),
                options.GetValueOrDefault("--from"),
                options.GetValueOrDefault("--to")
            );
            Print(result);
            return 0;
        }
        catch (Exception error)
            when (error
                    is IOException
                        or JsonException
                        or InvalidOperationException
                        or HttpRequestException
                        or InvalidDataException
                        or ArgumentException
            )
        {
            Console.Error.WriteLine($"microsoft: {error.Message}");
            return 1;
        }
    }

    internal static void Print(Result result)
    {
        Console.WriteLine(
            $"Microsoft Ads: SDK {result.From} -> {(result.To == result.From ? "latest (no newer SDK)" : result.To)}"
        );
        if (result.Warning is not null)
            Console.WriteLine(result.Warning);
        Console.WriteLine(
            "Microsoft categories: "
                + string.Join(
                    ", ",
                    result.Report.Counts.Where(p => p.Value > 0).Select(p => $"{p.Key}={p.Value}")
                )
        );
        Console.WriteLine($"Microsoft report: {result.ReportPath}");
    }

    internal static Result Execute(
        string root,
        string repo,
        string compileSetPath,
        string props,
        string outDir,
        string? fromOverride,
        string? toOverride,
        string? otherReportHref = null
    )
    {
        repo = Path.GetFullPath(repo);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ad-api-radar/1.0");
        string installed =
            fromOverride
            ?? CheckEngine.PackageVersion(repo, MicrosoftSnapshot.PackageId, props).Version
            ?? throw new InvalidOperationException(
                $"No {MicrosoftSnapshot.PackageId} version in {props}."
            );

        string? warning = null;
        List<string> versions;
        try
        {
            versions = MicrosoftSnapshot.StableVersions(http);
        }
        catch (Exception error)
            when (error is HttpRequestException or TaskCanceledException or JsonException)
        {
            versions = [];
            warning =
                $"STALE SOURCE WARNING: NuGet unavailable ({error.Message}); newer SDK versions not checked.";
        }
        string to =
            toOverride
            ?? versions.LastOrDefault(v => MicrosoftSnapshot.Compare(v, installed) >= 0)
            ?? installed;
        var from = MicrosoftSnapshot.Load(root, installed, http);
        var target = to == installed ? from : MicrosoftSnapshot.Load(root, to, http);
        var between = versions
            .Where(v =>
                MicrosoftSnapshot.Compare(v, installed) > 0 && MicrosoftSnapshot.Compare(v, to) <= 0
            )
            .Select(v => v == to ? target : MicrosoftSnapshot.Load(root, v, http))
            .ToList();

        var set =
            JsonSerializer.Deserialize<CompileSetResult>(File.ReadAllText(compileSetPath))
            ?? throw new JsonException("Empty compile set.");
        var known = from
            .Types.Concat(target.Types)
            .Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);
        var index = MicrosoftIndexer.Index(repo, set, known);
        Directory.CreateDirectory(outDir);
        AtomicFile.WriteProduced(
            Path.Combine(outDir, "index.json"),
            () => JsonSerializer.SerializeToUtf8Bytes(index, JsonOptions)
        );

        var report = MicrosoftCheck.Run(
            index,
            repo,
            props,
            from,
            target,
            between,
            DateOnly.FromDateTime(DateTime.UtcNow),
            Announcements(http)
        );
        if (warning is not null)
            report.Limitations.Add(warning);
        string findingsPath = Path.Combine(outDir, "findings.json");
        string reportPath = Path.Combine(outDir, "report.html");
        AtomicFile.WriteProduced(
            findingsPath,
            () => JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions)
        );
        AtomicFile.WriteProduced(
            reportPath,
            () => System.Text.Encoding.UTF8.GetBytes(CheckHtml.Render(report, otherReportHref))
        );
        FixCommand.Run([
            "--findings",
            findingsPath,
            "--repo",
            repo,
            "--out",
            Path.Combine(outDir, "fixes"),
        ]);
        return new Result(report, reportPath, installed, to, warning);
    }

    // Latest Microsoft Ads API blog posts (deprecations and sunsets are announced there as prose).
    private static List<string> Announcements(HttpClient http)
    {
        try
        {
            var feed = XDocument.Parse(http.GetStringAsync(Feed).GetAwaiter().GetResult());
            return feed.Descendants("item")
                .Take(8)
                .Select(item =>
                {
                    string date = DateTime.TryParse(
                        (string?)item.Element("pubDate"),
                        out var published
                    )
                        ? published.ToString("yyyy-MM-dd")
                        : "";
                    return $"{date} {(string?)item.Element("title")} — {(string?)item.Element("link")}".Trim();
                })
                .ToList();
        }
        catch (Exception error)
            when (error is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return
            [
                $"Microsoft Ads API blog unavailable ({error.Message}); check {Feed} manually.",
            ];
        }
    }
}
