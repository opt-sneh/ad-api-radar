using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using HtmlAgilityPack;

namespace Radar;

public sealed record SunsetRow(
    string Version,
    string ReleaseDate,
    string SunsetDate,
    string? UpgradeGuideUrl
);

public sealed record DeprecationRow(
    string? EffectiveDate,
    string EffectiveDateText,
    string ChangeArea,
    string ChangeType,
    string Description,
    List<string> Links
);

public sealed record SdkDependencies(
    string SdkVersion,
    List<SdkDependency> Dependencies,
    List<string>? ProtobufChain = null,
    string? ProtobufRange = null
);

public sealed record SdkDependency(string Id, string VersionRange, string? TargetFramework);

public static class SnapshotParsers
{
    private static readonly Uri SunsetUrl = new(
        "https://developers.google.com/google-ads/api/docs/sunset-dates"
    );
    private static readonly Uri DeprecationsUrl = new(
        "https://developers.google.com/google-ads/api/docs/deprecations"
    );

    public static List<SunsetRow> ParseSunset(string html)
    {
        var table = FindTable(
            html,
            ["API version", "Release date", "Sunset date", "Upgrade guide"]
        );
        var rows = new List<SunsetRow>();
        foreach (var cells in DataRows(table))
        {
            if (cells.Count == 1)
                continue; // Section headings span the entire table.
            if (cells.Count != 4)
                throw new FormatException(
                    "Sunset table has a row with an unexpected column count."
                );
            string version = PlainText(cells[0]);
            if (!Regex.IsMatch(version, @"^v\d+(?:\.\d+)?\*?$", RegexOptions.IgnoreCase))
                throw new FormatException($"Sunset table has an unexpected version: {version}.");
            string releaseDate = PlainText(cells[1]);
            string sunsetDate = PlainText(cells[2]);
            if (releaseDate.Length == 0 || sunsetDate.Length == 0)
                throw new FormatException($"Sunset row {version} has an empty date.");
            var guide = cells[3].SelectSingleNode(".//a[@href]")?.GetAttributeValue("href", "");
            rows.Add(new SunsetRow(version, releaseDate, sunsetDate, ResolveUrl(SunsetUrl, guide)));
        }
        if (rows.Count == 0)
            throw new FormatException("Sunset table has no version rows.");
        return rows;
    }

    public static List<DeprecationRow> ParseDeprecations(string html)
    {
        var table = FindTable(
            html,
            ["Effective date", "Change area", "Change type", "Guidance and description"]
        );
        var rows = new List<DeprecationRow>();
        foreach (var cells in DataRows(table))
        {
            if (cells.Count != 4)
                throw new FormatException(
                    "Deprecations table has a row with an unexpected column count."
                );
            var (date, dateText) = ParseEffectiveDate(cells[0]);
            string area = PlainText(cells[1]);
            string type = PlainText(cells[2]);
            string description = PlainText(cells[3]);
            if (
                dateText.Length == 0
                || area.Length == 0
                || type.Length == 0
                || description.Length == 0
            )
                throw new FormatException("Deprecations table has an empty required cell.");
            var links =
                cells[3]
                    .SelectNodes(".//a[@href]")
                    ?.Select(anchor =>
                        ResolveUrl(DeprecationsUrl, anchor.GetAttributeValue("href", ""))
                    )
                    .Where(url => url is not null)
                    .Select(url => url!)
                    .Distinct()
                    .ToList()
                ?? [];
            rows.Add(new DeprecationRow(date, dateText, area, type, description, links));
        }
        if (rows.Count == 0)
            throw new FormatException("Deprecations table has no rows.");
        return rows;
    }

    public static SdkDependencies ParseNuspec(string xml, string sdkVersion)
        => ParseNuspec(xml, sdkVersion, allowEmpty: false);

    private static SdkDependencies ParseNuspec(string xml, string sdkVersion, bool allowEmpty)
    {
        var document = XDocument.Parse(xml);
        var dependencies = document
            .Descendants()
            .Where(node => node.Name.LocalName == "dependency")
            .Select(node => new SdkDependency(
                (string?)node.Attribute("id") ?? "",
                (string?)node.Attribute("version") ?? "",
                (string?)node.Parent?.Attribute("targetFramework")
            ))
            .ToList();
        if (
            !allowEmpty && dependencies.Count == 0
            || dependencies.Any(item => item.Id.Length == 0 || item.VersionRange.Length == 0)
        )
            throw new FormatException($"Nuspec {sdkVersion} has no valid dependencies.");
        return new SdkDependencies(sdkVersion, dependencies);
    }

    public static SdkDependencies ResolveProtobuf(string sdkVersion, Func<string, string> fetch)
    {
        const string sdkId = "Google.Ads.GoogleAds";
        var queue = new Queue<(string Id, string Version, List<string> Chain, int Depth)>();
        queue.Enqueue((sdkId, sdkVersion, [$"{sdkId} {sdkVersion}"], 0));
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SdkDependencies? sdk = null;
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current.Id + "/" + current.Version))
                continue;
            var package = ParseNuspec(fetch(NuspecUrl(current.Id, current.Version)), current.Version, current.Depth > 0);
            if (current.Depth == 0)
                sdk = package;
            foreach (var dependency in BestCompatibleGroup(package.Dependencies)
                .Where(d => d.Id.StartsWith("Google.", StringComparison.OrdinalIgnoreCase)))
            {
                var chain = new List<string>(current.Chain) { $"{dependency.Id} {dependency.VersionRange}" };
                if (dependency.Id.Equals("Google.Protobuf", StringComparison.OrdinalIgnoreCase))
                    return sdk! with { ProtobufChain = chain, ProtobufRange = dependency.VersionRange };
                if (current.Depth < 5)
                    queue.Enqueue((dependency.Id, LowerBound(dependency.VersionRange), chain, current.Depth + 1));
            }
        }
        throw new FormatException($"No Google.Protobuf dependency found for {sdkId} {sdkVersion} within depth 5.");
    }

    public static string NuspecUrl(string id, string version)
    {
        string lower = id.ToLowerInvariant();
        return $"https://api.nuget.org/v3-flatcontainer/{lower}/{version.ToLowerInvariant()}/{lower}.nuspec";
    }

    private static IReadOnlyList<SdkDependency> BestCompatibleGroup(List<SdkDependency> dependencies)
    {
        var group = dependencies.GroupBy(d => d.TargetFramework ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Score = FrameworkScore(g.Key), Dependencies = g.ToArray() })
            .Where(g => g.Score >= 0)
            .OrderByDescending(g => g.Score).FirstOrDefault();
        return group?.Dependencies ?? [];
    }

    private static int FrameworkScore(string framework)
    {
        if (framework.Length == 0)
            return 1;
        var net = Regex.Match(framework, @"^net(\d+)\.(\d+)$", RegexOptions.IgnoreCase);
        if (net.Success)
        {
            int major = int.Parse(net.Groups[1].Value, CultureInfo.InvariantCulture);
            int minor = int.Parse(net.Groups[2].Value, CultureInfo.InvariantCulture);
            return major < 10 || major == 10 && minor == 0
                ? 1000 + major * 100 + minor
                : -1;
        }
        return framework.ToLowerInvariant() switch
        {
            "netstandard2.1" or ".netstandard2.1" => 200,
            "netstandard2.0" or ".netstandard2.0" => 100,
            _ => -1,
        };
    }

    private static string LowerBound(string range)
    {
        string value = range.Trim();
        if (value.StartsWith('[') || value.StartsWith('('))
            value = value[1..].Split(',')[0].TrimEnd(']').Trim();
        if (!Version.TryParse(value, out _))
            throw new FormatException($"Dependency range has no usable lower-bound version: {range}");
        return value;
    }

    private static HtmlNode FindTable(string html, string[] headers)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var tables = document.DocumentNode.SelectNodes("//table");
        if (tables is null)
            throw new FormatException("Expected HTML table was not found.");
        foreach (var table in tables)
        {
            var firstRow = table.SelectSingleNode(".//tr[th]");
            var actual = firstRow?.SelectNodes("./th")?.Select(PlainText).ToArray();
            if (
                actual is not null
                && actual.SequenceEqual(headers, StringComparer.OrdinalIgnoreCase)
            )
                return table;
        }
        throw new FormatException(
            $"Expected table headers not found: {string.Join(", ", headers)}."
        );
    }

    private static IEnumerable<List<HtmlNode>> DataRows(HtmlNode table)
    {
        foreach (var row in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var cells = row.SelectNodes("./td");
            if (cells is not null)
                yield return cells.ToList();
        }
    }

    private static string PlainText(HtmlNode node) =>
        Regex.Replace(HtmlEntity.DeEntitize(node.InnerText), @"\s+", " ").Trim();

    private static (string? IsoDate, string Text) ParseEffectiveDate(HtmlNode cell)
    {
        var copy = cell.CloneNode(true);
        var elements = copy.SelectNodes(".//*")?.ToList() ?? [];
        string? isoDate = elements
            .SelectMany(node =>
                new[]
                {
                    node.GetAttributeValue("datetime", ""),
                    node.GetAttributeValue("data-date", ""),
                }
            )
            .FirstOrDefault(IsIsoDate);

        // Google's date cell includes a separate machine-readable ISO element.
        // Removing that node before reading the text avoids joining two dates together.
        foreach (var node in elements.Where(node => node.ParentNode is not null))
        {
            string value = PlainText(node);
            if (!IsIsoDate(value))
                continue;
            isoDate ??= value;
            node.Remove();
        }
        return (isoDate, PlainText(copy));
    }

    private static bool IsIsoDate(string value) =>
        DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _
        );

    private static string? ResolveUrl(Uri page, string? href) =>
        string.IsNullOrWhiteSpace(href) ? null : new Uri(page, href).AbsoluteUri;
}
