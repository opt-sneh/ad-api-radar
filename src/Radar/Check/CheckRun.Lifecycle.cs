namespace Radar;

public static partial class CheckEngine
{
    // Lifecycle rules: version sunset, SDK/protobuf compatibility, unknowns, and the upgrade itself.
    private sealed partial class CheckRun
    {
        public void Sunset(IReadOnlyList<SunsetRow> sunsets, DateOnly now)
        {
            var sunset = sunsets.FirstOrDefault(s => s.Version == target.Version);
            if (sunset is not null)
            {
                bool soon =
                    TrySunsetMonth(sunset.SunsetDate, out var month) && month <= now.AddMonths(6);
                Add(
                    "SUNSET",
                    soon ? "high" : "info",
                    target.Version,
                    "google/sunset.json",
                    0,
                    $"{target.Version} sunset: {sunset.SunsetDate}.",
                    relevance: "used",
                    count: 0,
                    evidence: [$"Sunset: {sunset.SunsetDate}", $"Release: {sunset.ReleaseDate}"]
                );
            }
            else
                Add(
                    "SUNSET",
                    "unknown",
                    target.Version,
                    "google/sunset.json",
                    0,
                    $"No sunset row for {target.Version}.",
                    count: 0
                );
        }

        public void Compatibility(IReadOnlyList<SdkDependencies> sdkDependencies, string sdkVersion)
        {
            var compatibility = CheckCompatibility(repo, target.Version, sdkVersion, sdkDependencies, propsPath);
            findings.Add(
                compatibility with
                {
                    Category = "COMPAT",
                    Lines = compatibility.Line > 0 ? [compatibility.Line] : [],
                }
            );
        }

        public void Unknowns()
        {
            if (index.Queries.Any(q => q.IsInterpolated))
                Add(
                    "UNKNOWN",
                    "unknown",
                    "dynamic GAQL",
                    "",
                    0,
                    $"{index.Queries.Count(q => q.IsInterpolated)} interpolated queries need review.",
                    confidence: "low",
                    count: index.Queries.Count(q => q.IsInterpolated)
                );
            if (index.Definitions.Any(d => d.Platform.StartsWith("unknown", StringComparison.Ordinal)))
                Add(
                    "UNKNOWN",
                    "unknown",
                    "unknown platforms",
                    "",
                    0,
                    "Some path definitions have an unknown platform.",
                    confidence: "low",
                    count: 0
                );
        }

        // Only when target is behind the newest snapshot: SDK bump, namespace swap, generated protos.
        public void Migration(IReadOnlyList<SdkDependencies> sdkDependencies, string sdkVersion)
        {
            var newest = allModels.LastOrDefault();
            if (newest is null || VersionNumber(newest.Version) <= targetNumber)
                return;
            var (installedSdk, sdkLine, propsFile) = PackageVersion(repo, "Google.Ads.GoogleAds", propsPath);
            var (installedProtobuf, _, _) = PackageVersion(repo, "Google.Protobuf", propsPath);
            var newestSdk = sdkDependencies
                .OrderBy(s =>
                    Version.TryParse(s.SdkVersion, out var version) ? version : new Version(0, 0)
                )
                .LastOrDefault();
            string requiredRange = newestSdk?.ProtobufRange ?? "unknown";
            string satisfaction =
                installedProtobuf is null || newestSdk?.ProtobufRange is null ? "unknown"
                : SatisfiesRange(installedProtobuf, newestSdk.ProtobufRange) ? "satisfies"
                : "does not satisfy";
            Add(
                "MIGRATION",
                "medium",
                "Google.Ads.GoogleAds",
                propsFile,
                sdkLine,
                $"Upgrade Google.Ads.GoogleAds {installedSdk ?? sdkVersion} → {newestSdk?.SdkVersion ?? "unknown"} for API {newest.Version}.",
                evidence:
                [
                    $"Google.Protobuf {installedProtobuf ?? "unknown"} {satisfaction} required {requiredRange}.",
                    $"Chain: {string.Join(" → ", newestSdk?.ProtobufChain ?? [])}",
                ],
                count: 0
            );

            string oldNamespace = "Google.Ads.GoogleAds.V" + targetNumber;
            var namespaceFiles = index
                .SdkVersionsByFile.Where(pair => pair.Value.Contains(targetNumber))
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Add(
                "MIGRATION",
                "medium",
                oldNamespace,
                namespaceFiles.FirstOrDefault() ?? "",
                0,
                $"{namespaceFiles.Length} files reference {oldNamespace}; move them to V{VersionNumber(newest.Version)}.",
                evidence:
                [
                    .. namespaceFiles.Take(10),
                    .. namespaceFiles.Length > 10
                        ? new[] { $"... and {namespaceFiles.Length - 10} more" }
                        : Array.Empty<string>(),
                ],
                count: 0
            );

            string fullRoot =
                Path.GetFullPath(repo).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            foreach (string file in index.SdkVersionsByFile.Keys.Order(StringComparer.Ordinal))
            {
                string full = Path.GetFullPath(
                    Path.Combine(repo, file.Replace('/', Path.DirectorySeparatorChar))
                );
                if (
                    !full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(full)
                )
                    continue;
                string source = File.ReadAllText(full);
                if (
                    !source.Contains(
                        "Generated by the protocol buffer compiler",
                        StringComparison.Ordinal
                    ) || !source.Contains(oldNamespace, StringComparison.Ordinal)
                )
                    continue;
                int line =
                    Array.FindIndex(
                        source.Split('\n'),
                        text => text.Contains(oldNamespace, StringComparison.Ordinal)
                    ) + 1;
                Add(
                    "MIGRATION",
                    "medium",
                    Path.GetFileName(file),
                    file,
                    line,
                    $"Generated protobuf code references {oldNamespace}; regenerate its .proto against {newest.Version}.",
                    evidence: ["Generated by the protocol buffer compiler"],
                    count: 0
                );
            }
        }
    }
}
