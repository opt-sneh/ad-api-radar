using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Radar;

public sealed record GaqlExport(
    string Commit,
    string Target,
    List<string> Versions,
    List<GaqlExportQuery> Queries
);

public sealed record GaqlExportQuery(string Id, string Query, List<string> Locations);

public sealed record GaqlResult(string Id, string Version, int Status, bool Ok, string? Error, string? ErrorCode = null);

public sealed record GaqlResults(List<GaqlResult> Results);

internal static class GaqlValidation
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static GaqlExport Export(
        CodeIndexResult index,
        SchemaModel schema,
        string commit,
        IReadOnlyList<string> versions
    )
    {
        var roots = schema
            .Fields.Keys.Select(path => path.Split('.')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queries = index
            .Queries.Where(query =>
                query.Platform == "google-ads-candidate"
                && !query.IsInterpolated
                && !query.Text.Contains('{')
                && query.Resource is not null
                && roots.Contains(query.Resource)
            )
            .Select(query =>
                (
                    Text: Regex.Replace(query.Text.Trim(), @"\s+", " "),
                    Location: $"{query.File}:{query.Line}"
                )
            )
            .GroupBy(query => query.Text, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new GaqlExportQuery(
                "q-"
                    + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(group.Key)))[
                        ..12
                    ],
                group.Key,
                group
                    .Select(query => query.Location)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList()
            ))
            .ToList();
        return new GaqlExport(commit, schema.Version, versions.ToList(), queries);
    }

    public static void Write(GaqlExport export, string path) =>
        AtomicFile.WriteProduced(
            path,
            () => JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions)
        );

    public static void Merge(CheckReport report, string resultsPath, string queriesPath)
    {
        var export =
            JsonSerializer.Deserialize<GaqlExport>(File.ReadAllText(queriesPath), JsonOptions)
            ?? throw new JsonException("Empty GAQL query export.");
        if (export.Target != report.Target)
            throw new JsonException("GAQL query export target does not match the check target.");
        var results =
            JsonSerializer.Deserialize<GaqlResults>(File.ReadAllText(resultsPath), JsonOptions)
            ?? throw new JsonException("Empty GAQL results.");
        var byId = export.Queries.ToDictionary(query => query.Id, StringComparer.Ordinal);
        int rejected = 0, inconclusive = 0, validated = 0;
        var seen = new HashSet<(string Id, string Version)>();
        foreach (var result in results.Results)
        {
            if (!export.Versions.Contains(result.Version, StringComparer.Ordinal))
                throw new JsonException($"Unexpected GAQL result version: {result.Version}");
            if (!byId.TryGetValue(result.Id, out var query))
                throw new JsonException($"Unknown GAQL query id: {result.Id}");
            if (!seen.Add((result.Id, result.Version)))
                throw new JsonException($"Duplicate GAQL result: {result.Id} in {result.Version}");
            if (result.Ok && result.Status is >= 200 and < 300)
            {
                validated++;
                continue;
            }
            // HTTP 400 also covers invalid tokens and malformed requests. Require a
            // query-specific Google error code before blaming the repository query.
            if (result.Status != 400 || !IsQueryError(result.ErrorCode))
            {
                inconclusive++;
                continue;
            }
            rejected++;
            string category =
                result.Version == report.Target ? "BREAKING_RUNTIME" : "UPCOMING_BREAK";
            foreach (string location in query.Locations)
            {
                int colon = location.LastIndexOf(':');
                if (colon <= 0 || !int.TryParse(location[(colon + 1)..], out int line))
                    throw new JsonException($"Invalid GAQL location: {location}");
                string file = location[..colon];
                string message =
                    result.Version == report.Target
                        ? $"Google rejected this query in {result.Version}: {result.Error}"
                        : $"Google rejects this query in {result.Version}: {result.Error}";
                string key = $"{category}\0{result.Id}\0{result.Version}\0{file}\0{line}";
                string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[
                    ..16
                ];
                report.Findings.Add(
                    new Finding(
                        id,
                        category,
                        "high",
                        "GAQL validation",
                        "google-ads",
                        file,
                        line,
                        1,
                        [
                            $"Live validation ({result.Version}, validateOnly): HTTP {result.Status}",
                            query.Query,
                        ],
                        message
                    )
                    {
                        Confidence = "high",
                        Lines = [line],
                    }
                );
                report.Counts[category] = report.Counts.GetValueOrDefault(category) + 1;
            }
        }
        report.Funnel["findings"] = report.Findings.Count;
        report.Funnel["actionable"] = report.Findings.Count(ReportFindings.IsActionable);
        report.Funnel["files"] = report.Findings.Where(finding => finding.Line > 0)
            .Select(finding => finding.File).Distinct(StringComparer.Ordinal).Count();
        report.Limitations.Add(
            $"Live GAQL validation: {results.Results.Count} results, {validated} valid, {rejected} query errors, {inconclusive} inconclusive (versions {string.Join(", ", results.Results.Select(result => result.Version).Distinct().Order())}). Only HTTP 400 with QUERY_ERROR or FIELD_ERROR is a confirmed query error; retry inconclusive results."
        );
    }

    private static bool IsQueryError(string? code) => code is not null &&
        (code.Equals("QUERY_ERROR", StringComparison.OrdinalIgnoreCase)
            || code.Equals("FIELD_ERROR", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("QUERY_ERROR.", StringComparison.OrdinalIgnoreCase)
            || code.StartsWith("FIELD_ERROR.", StringComparison.OrdinalIgnoreCase));
}
