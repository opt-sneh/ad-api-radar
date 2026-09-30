using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Radar;

public sealed record Finding(
    string Id,
    string Category,
    string Severity,
    string Path,
    string Platform,
    string File,
    int Line,
    int UseCount,
    List<string> Evidence,
    string Message
)
{
    public string Confidence { get; init; } = "high";
    public string Relevance { get; init; } = "used";
    public string? ChangeId { get; init; }
    public string? SuggestedReplacement { get; init; }
    public bool PossiblyHandled { get; init; }
    public List<int> Lines { get; init; } = [];
    // Written to findings.json so tools outside radar (issue sync) use the same rule as the report.
    public bool Actionable => ReportFindings.IsActionable(this);
}

public sealed class CheckReport
{
    public required string Target { get; init; }
    public required List<string> Next { get; init; }
    public required List<Finding> Findings { get; init; }
    public required Dictionary<string, int> Counts { get; init; }
    public Dictionary<string, int> Funnel { get; init; } = new();
    public int NonGaqlQueriesSkipped { get; init; }
    public int UnknownRootPathsSkipped { get; init; }
    public int BehaviourRowsWithoutHits { get; init; }
    public required Dictionary<string, int> UnknownDefinitionsByOwner { get; init; }
    public int InterpolatedQueries { get; init; }
    public int ParseErrorFiles { get; init; }
    public required List<string> Limitations { get; init; }
    public string Platform { get; init; } = "Google Ads";
    public List<string> Announcements { get; init; } = [];
}

public static partial class CheckEngine
{
    [GeneratedRegex(@"\b[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+)+\b", RegexOptions.CultureInvariant)]
    private static partial Regex PascalIdentifiers();

    public static Finding CheckCompatibility(
        string repo,
        string target,
        string sdkVersion,
        IReadOnlyList<SdkDependencies> all,
        string propsPath = "code/backend/Directory.Packages.props"
    )
    {
        var sdk = all.FirstOrDefault(s => s.SdkVersion == sdkVersion);
        string? range = sdk?.ProtobufRange;
        var (installed, line, relative) = PackageVersion(repo, "Google.Protobuf", propsPath);
        if (sdk is not null && !string.IsNullOrWhiteSpace(range) && installed is null)
            return NewFinding(
                "Compatibility",
                "info",
                "Google.Protobuf",
                "google-ads",
                relative,
                0,
                0,
                [$"Target {target}; SDK {sdkVersion}; required {range}", $"Chain: {string.Join(" → ", sdk.ProtobufChain ?? [])}"],
                $"Google.Protobuf is not pinned directly, so NuGet resolves it from SDK {sdkVersion} ({range})."
            );
        if (sdk is null || string.IsNullOrWhiteSpace(range) || installed is null)
        {
            string reason =
                sdk is null ? $"SDK {sdkVersion} is absent from sdk-deps.json"
                : string.IsNullOrWhiteSpace(range)
                    ? $"SDK {sdkVersion} has no resolved Google.Protobuf range in sdk-deps.json"
                : "Google.Protobuf is absent from Directory.Packages.props";
            return NewFinding(
                "Compatibility",
                "unknown",
                "Google.Protobuf",
                "google-ads",
                relative,
                line,
                0,
                [
                    $"Target {target}; SDK {sdkVersion}; repo version {installed ?? "unknown"}",
                    reason,
                    $"Chain: {string.Join(" → ", sdk?.ProtobufChain ?? [])}",
                ],
                $"Google.Protobuf compatibility cannot be determined: {reason}."
            );
        }
        bool satisfied = SatisfiesRange(installed, range);
        return NewFinding(
            "Compatibility",
            satisfied ? "info" : "high",
            "Google.Protobuf",
            "google-ads",
            relative,
            line,
            0,
            [
                $"Target {target}; SDK {sdkVersion}",
                $"Google.Protobuf {installed}; required {range}",
                $"Chain: {string.Join(" → ", sdk.ProtobufChain ?? [])}",
            ],
            satisfied
                ? $"Google.Protobuf {installed} satisfies SDK {sdkVersion} range {range}."
                : $"Google.Protobuf {installed} does not satisfy SDK {sdkVersion} range {range}."
        );
    }

    internal static (string? Version, int Line, string File) PackageVersion(
        string repo, string package, string propsPath = "code/backend/Directory.Packages.props")
    {
        string props = Path.Combine(repo, propsPath);
        if (!File.Exists(props))
            return ProjectPackageVersion(repo, package) ?? (null, 0, "");
        var doc = XDocument.Load(props);
        string? version = doc.Descendants()
            .FirstOrDefault(e =>
                e.Name.LocalName == "PackageVersion"
                && ((string?)e.Attribute("Include"))?.Equals(
                    package,
                    StringComparison.OrdinalIgnoreCase
                ) == true
            )
            ?.Attribute("Version")
            ?.Value;
        int line =
            Array.FindIndex(
                File.ReadAllLines(props),
                l => l.Contains(package, StringComparison.OrdinalIgnoreCase)
            ) + 1;
        return (version, line, propsPath.Replace('\\', '/'));
    }

    // No central package management: the shallowest .csproj that references the package with a literal version.
    // ponytail: shallowest-first guesses the main project; per-project versions if repos pin different ones.
    private static (string? Version, int Line, string File)? ProjectPackageVersion(string repo, string package)
    {
        if (!Directory.Exists(repo))
            return null;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (string project in Directory.EnumerateFiles(repo, "*.csproj", options)
            .OrderBy(path => path.Count(c => c is '/' or '\\')).ThenBy(path => path, StringComparer.Ordinal))
        {
            try
            {
                var reference = XDocument.Load(project).Descendants().FirstOrDefault(e =>
                    e.Name.LocalName == "PackageReference"
                    && ((string?)e.Attribute("Include"))?.Equals(package, StringComparison.OrdinalIgnoreCase) == true
                    && e.Attribute("Version") is not null);
                if (reference is null)
                    continue;
                int line = Array.FindIndex(File.ReadAllLines(project),
                    l => l.Contains($"\"{package}\"", StringComparison.OrdinalIgnoreCase)) + 1;
                return (reference.Attribute("Version")!.Value, line, Path.GetRelativePath(repo, project).Replace('\\', '/'));
            }
            // An unreadable or vanished project says nothing about the version; try the next one.
            catch (Exception error) when (error is System.Xml.XmlException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static bool SatisfiesRange(string version, string range)
    {
        if (!Version.TryParse(version, out var actual))
            return false;
        range = range.Trim();
        if (!(range.StartsWith('[') || range.StartsWith('(')))
            return Version.TryParse(range, out var minimum) && actual >= minimum;
        if (!(range.EndsWith(']') || range.EndsWith(')')))
            return false;
        string[] ends = range[1..^1].Split(',');
        if (ends.Length == 1)
            return Version.TryParse(ends[0].Trim(), out var exact) && actual == exact;
        if (ends.Length != 2)
            return false;
        if (ends[0].Trim().Length > 0)
        {
            if (!Version.TryParse(ends[0].Trim(), out var lower))
                return false;
            if (range[0] == '[' ? actual < lower : actual <= lower)
                return false;
        }
        if (ends[1].Trim().Length > 0)
        {
            if (!Version.TryParse(ends[1].Trim(), out var upper))
                return false;
            if (range[^1] == ']' ? actual > upper : actual >= upper)
                return false;
        }
        return true;
    }

    private static bool TrySunsetMonth(string text, out DateOnly month)
    {
        string clean = Regex.Replace(text, @"\s*\(.*\)", "").Trim();
        return DateOnly.TryParseExact(
            clean,
            "MMMM yyyy",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out month
        );
    }

    internal static Finding NewFinding(
        string category,
        string severity,
        string path,
        string platform,
        string file,
        int line,
        int useCount,
        List<string> evidence,
        string message
    )
    {
        string key = $"{category}\0{path}\0{file.Replace('\\', '/')}\0{line}";
        string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return new(id, category, severity, path, platform, file, line, useCount, evidence, message);
    }

    private static int VersionNumber(string version) =>
        int.Parse(version.AsSpan(1), CultureInfo.InvariantCulture);

    private static bool IsWithin(string path, string root)
    {
        string fullRoot =
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}
