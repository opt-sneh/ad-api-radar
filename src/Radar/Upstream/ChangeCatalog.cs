using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Radar;

public sealed record ReplacementCandidate(string Symbol, double Score, string Reason);

public sealed record ChangeRecord(
    string Id,
    string VersionFrom,
    string VersionTo,
    string Kind,
    string Symbol,
    string? GaqlPath,
    string? CsharpName,
    string? FromType,
    string? ToType,
    string? FromLocation,
    string? ToLocation,
    string Severity,
    IReadOnlyList<ReplacementCandidate> ReplacementCandidates,
    IReadOnlyList<string> ReleaseNoteSnippets,
    IReadOnlyList<DeprecationRow> DeprecationRows
)
{
    public IReadOnlyList<string> RemovedFields { get; init; } = [];
}

public sealed record ReleaseNoteEntry(
    string Version,
    string Text,
    IReadOnlyList<string> CodeTokens
);

public static class ChangeCatalog
{
    private static readonly (string Old, string New)[] TokenSwaps =
    [
        ("extension", "asset"),
        ("location", "profile_location"),
        ("rule_based", "flexible_rule"),
        ("combined_rule", "flexible_rule"),
    ];

    public static IReadOnlyList<ChangeRecord> Compare(
        SchemaModel from,
        SchemaModel to,
        IReadOnlyList<SchemaModel>? chain = null,
        IReadOnlyList<ReleaseNoteEntry>? notes = null,
        IReadOnlyList<DeprecationRow>? deprecations = null
    )
    {
        chain ??= [from, to];
        notes ??= [];
        deprecations ??= [];
        var changes = new List<ChangeRecord>();
        void Add(
            string kind,
            string symbol,
            string? gaql,
            string? csharp,
            string? oldType,
            string? newType,
            string? oldLocation,
            string? newLocation,
            IReadOnlyList<ReplacementCandidate>? candidates = null,
            IReadOnlyList<string>? removedFields = null
        )
        {
            string severity = kind switch
            {
                "FIELD_ADDED" => "info",
                "ENUM_VALUE_ADDED" => "silent-risk",
                "FIELD_NEWLY_DEPRECATED" => "deprecated",
                _ => "breaking",
            };
            var probes = Tokens(symbol, gaql, csharp).ToArray();
            var snippets = notes
                .Where(n => VersionBase(n.Version) == to.Version)
                .Select(n => new { Entry = n, Score = MatchScore(n.Text, n.CodeTokens, probes) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => Snippet(x.Entry.Text, probes))
                .Distinct(StringComparer.Ordinal)
                .Take(3)
                .ToArray();
            var rows = deprecations
                .Where(row =>
                    MatchScore(
                        row.ChangeArea + " " + row.Description + " " + string.Join(" ", row.Links),
                        [],
                        probes
                    ) > 0
                )
                .ToArray();
            string removedName = symbol.Split('.').Last();
            var rankedCandidates = (candidates ?? [])
                .Select(candidate =>
                {
                    string candidateName = candidate.Symbol.Split('.').Last();
                    return snippets.Any(snippet =>
                        Mentions(snippet, removedName) && Mentions(snippet, candidateName)
                    )
                        ? candidate with
                        {
                            Reason = "release-note",
                        }
                        : candidate;
                })
                .OrderByDescending(candidate => candidate.Reason == "release-note")
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Symbol, StringComparer.Ordinal)
                .Take(3)
                .ToArray();
            changes.Add(
                new ChangeRecord(
                    Id(kind, symbol),
                    from.Version,
                    to.Version,
                    kind,
                    symbol,
                    gaql,
                    csharp,
                    oldType,
                    newType,
                    oldLocation,
                    newLocation,
                    severity,
                    rankedCandidates,
                    snippets,
                    rows
                )
                {
                    RemovedFields = removedFields ?? [],
                }
            );
        }

        foreach (var (path, message) in from.Messages)
        {
            if (to.Messages.ContainsKey(path))
                continue;
            if (message.IsResource)
            {
                string symbol = message.GaqlName!;
                Add(
                    "RESOURCE_REMOVED",
                    symbol,
                    symbol,
                    path.Split('.').Last(),
                    null,
                    null,
                    message.Location,
                    null,
                    removedFields: from.ProtoFields.Keys.Where(k =>
                            k.StartsWith(path + ".", StringComparison.Ordinal)
                        )
                        .ToArray()
                );
            }
            else if (!InsideRemovedMessage(path, from, to))
                Add(
                    "MESSAGE_REMOVED",
                    path,
                    null,
                    path.Split('.').Last(),
                    null,
                    null,
                    message.Location,
                    null
                );
        }

        foreach (var (path, oldField) in from.ProtoFields)
        {
            string messagePath = path[..path.LastIndexOf('.')];
            if (!to.Messages.ContainsKey(messagePath))
                continue;
            string? gaql = GaqlPath(path, from);
            string symbol = gaql ?? path;
            if (!to.ProtoFields.TryGetValue(path, out var next))
            {
                var candidates = FindCandidates(path, chain, to, int.MaxValue);
                Add(
                    candidates.Count > 0 ? "FIELD_RENAMED_CANDIDATE" : "FIELD_REMOVED",
                    symbol,
                    gaql,
                    oldField.CsharpName,
                    Type(oldField),
                    null,
                    oldField.Location,
                    null,
                    candidates
                );
                continue;
            }
            if (oldField.ProtoType != next.ProtoType)
                Add(
                    "FIELD_TYPE_CHANGED",
                    symbol,
                    gaql,
                    next.CsharpName,
                    Type(oldField),
                    Type(next),
                    oldField.Location,
                    next.Location
                );
            if (oldField.Repeated != next.Repeated)
                Add(
                    "FIELD_REPEATEDNESS_CHANGED",
                    symbol,
                    gaql,
                    next.CsharpName,
                    Type(oldField),
                    Type(next),
                    oldField.Location,
                    next.Location
                );
            if (!oldField.Required && next.Required)
                Add(
                    "FIELD_BECAME_REQUIRED",
                    symbol,
                    gaql,
                    next.CsharpName,
                    Type(oldField),
                    Type(next),
                    oldField.Location,
                    next.Location
                );
            if (!oldField.Deprecated && next.Deprecated)
                Add(
                    "FIELD_NEWLY_DEPRECATED",
                    symbol,
                    gaql,
                    next.CsharpName,
                    Type(oldField),
                    Type(next),
                    oldField.Location,
                    next.Location
                );
        }
        foreach (var (path, field) in to.ProtoFields)
            if (
                !from.ProtoFields.ContainsKey(path)
                && from.Messages.ContainsKey(path[..path.LastIndexOf('.')])
            )
            {
                string? gaql = GaqlPath(path, to);
                Add(
                    "FIELD_ADDED",
                    gaql ?? path,
                    gaql,
                    field.CsharpName,
                    null,
                    Type(field),
                    null,
                    field.Location
                );
            }

        foreach (var (path, entry) in from.Enums)
            if (!to.Enums.ContainsKey(path))
                Add(
                    "ENUM_VALUE_REMOVED",
                    path,
                    null,
                    entry.CsharpName,
                    null,
                    null,
                    entry.Location,
                    null
                );
        foreach (var (path, entry) in to.Enums)
            if (!from.Enums.ContainsKey(path))
                Add(
                    "ENUM_VALUE_ADDED",
                    path,
                    null,
                    entry.CsharpName,
                    null,
                    null,
                    null,
                    entry.Location
                );
        foreach (var (path, entry) in from.ServiceNames)
            if (!to.ServiceNames.ContainsKey(path))
                Add(
                    "SERVICE_REMOVED",
                    path,
                    null,
                    entry.CsharpName,
                    null,
                    null,
                    entry.Location,
                    null
                );
        foreach (var (path, entry) in from.Services)
            if (!to.Services.ContainsKey(path) && to.ServiceNames.ContainsKey(path.Split('.')[0]))
                Add(
                    "SERVICE_METHOD_REMOVED",
                    path,
                    null,
                    entry.CsharpName,
                    null,
                    null,
                    entry.Location,
                    null
                );
        return changes
            .OrderBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Symbol, StringComparer.Ordinal)
            .ToArray();
    }

    public static IReadOnlyList<ReplacementCandidate> FindCandidates(
        string removedPath,
        IReadOnlyList<SchemaModel> chain
    ) => chain.Count == 0 ? [] : FindCandidates(removedPath, chain, chain[^1], 3);

    private static IReadOnlyList<ReplacementCandidate> FindCandidates(
        string removedPath,
        IReadOnlyList<SchemaModel> chain,
        SchemaModel target,
        int limit
    )
    {
        if (chain.Count == 0 || !removedPath.Contains('.'))
            return [];
        if (
            !removedPath.StartsWith("resources.", StringComparison.Ordinal)
            && !removedPath.StartsWith("common.", StringComparison.Ordinal)
            && !removedPath.StartsWith("services.", StringComparison.Ordinal)
            && !removedPath.StartsWith("enums.", StringComparison.Ordinal)
            && !removedPath.StartsWith("errors.", StringComparison.Ordinal)
        )
        {
            string root = removedPath[..removedPath.IndexOf('.')];
            var message = chain
                .SelectMany(model => model.Messages.Values)
                .FirstOrDefault(item => item.GaqlName == root);
            if (message is not null)
                removedPath = message.Path + removedPath[root.Length..];
            else if (root is "metrics" or "segments")
                removedPath =
                    "common."
                    + (root == "metrics" ? "Metrics" : "Segments")
                    + removedPath[root.Length..];
        }
        string parent = removedPath[..removedPath.LastIndexOf('.')];
        string name = removedPath[(removedPath.LastIndexOf('.') + 1)..];
        bool stale = !chain.Any(model => model.ProtoFields.ContainsKey(removedPath));
        var introduced = chain
            .Skip(1)
            .SelectMany(
                (model, index) =>
                    model.ProtoFields.Keys.Where(path =>
                        !chain[index].ProtoFields.ContainsKey(path)
                    )
            )
            .ToHashSet(StringComparer.Ordinal);
        var added = target.ProtoFields.Keys.Where(path =>
            path.StartsWith(parent + ".", StringComparison.Ordinal)
            && (stale || introduced.Contains(path))
        );
        return added
            .Distinct(StringComparer.Ordinal)
            .Where(path => path != removedPath)
            .Select(path =>
            {
                string candidate = path[(path.LastIndexOf('.') + 1)..];
                double overlap = TokenOverlap(name, candidate);
                double containment = TokenContainment(name, candidate);
                double edit =
                    1d
                    - (double)Levenshtein(name, candidate)
                        / Math.Max(name.Length, candidate.Length);
                double score = Math.Max(Math.Max(overlap, containment), edit);
                string reason =
                    containment == score && containment > 0 ? "token containment"
                    : overlap == score ? "token overlap"
                    : "name similarity";
                if (
                    TokenSwaps.Any(swap =>
                        name.Replace(swap.Old, swap.New, StringComparison.Ordinal) == candidate
                    )
                )
                {
                    score = Math.Max(score, 0.75);
                    reason = "known token swap";
                }
                return new ReplacementCandidate(path, score, reason);
            })
            .Where(candidate => candidate.Score >= 0.5)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Symbol, StringComparer.Ordinal)
            .Take(limit)
            .Select(candidate => candidate with { Score = Math.Round(candidate.Score, 3) })
            .ToArray();
    }

    public static IReadOnlyList<ReleaseNoteEntry> ParseReleaseNotes(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var entries = new List<ReleaseNoteEntry>();
        string? version = null;
        foreach (
            var node in document.DocumentNode.SelectNodes("//*") ?? Enumerable.Empty<HtmlNode>()
        )
        {
            if (Regex.IsMatch(node.Name, "^h[1-6]$"))
            {
                var match = Regex.Match(
                    HtmlEntity.DeEntitize(node.InnerText),
                    @"\bv(?<number>\d+)(?:\.\d+)?\b",
                    RegexOptions.IgnoreCase
                );
                if (match.Success)
                    version = "v" + match.Groups["number"].Value;
                continue;
            }
            if (version is null || node.Name != "li" && node.Name != "tr")
                continue;
            if (node.Ancestors().Any(parent => parent.Name == node.Name))
                continue;
            string text = Regex.Replace(HtmlEntity.DeEntitize(node.InnerText), @"\s+", " ").Trim();
            if (text.Length == 0)
                continue;
            var code = (node.SelectNodes(".//code") ?? Enumerable.Empty<HtmlNode>())
                .Select(codeNode => HtmlEntity.DeEntitize(codeNode.InnerText).Trim())
                .Where(token => token.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            entries.Add(new ReleaseNoteEntry(version, text, code));
        }
        return entries;
    }

    private static bool InsideRemovedMessage(string path, SchemaModel from, SchemaModel to) =>
        from.Messages.Keys.Any(parent =>
            path.StartsWith(parent + ".", StringComparison.Ordinal)
            && !to.Messages.ContainsKey(parent)
        );

    private static string? GaqlPath(string path, SchemaModel model)
    {
        string parent = path[..path.LastIndexOf('.')];
        if (!model.Messages.TryGetValue(parent, out var message))
            return null;
        string? root = message.GaqlName;
        if (root is null && parent is "common.Metrics" or "common.Segments")
            root = parent == "common.Metrics" ? "metrics" : "segments";
        return root is null ? null : root + "." + path[(path.LastIndexOf('.') + 1)..];
    }

    private static string Type(ProtoField field) => field.ProtoType + (field.Repeated ? "[]" : "");

    private static string Id(string kind, string symbol) =>
        Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(kind + ":" + symbol)))
            .ToLowerInvariant();

    private static string VersionBase(string version) => Regex.Match(version, @"^v\d+").Value;

    private static IEnumerable<string> Tokens(string symbol, string? gaql, string? csharp)
    {
        yield return symbol;
        if (
            symbol.StartsWith("services.", StringComparison.Ordinal)
            || symbol.StartsWith("resources.", StringComparison.Ordinal)
            || symbol.StartsWith("common.", StringComparison.Ordinal)
        )
            yield return symbol[(symbol.IndexOf('.') + 1)..];
        if (gaql is not null)
            yield return gaql;
        if (csharp is { Length: >= 8 })
            yield return csharp;
        string field = symbol.Split('.').Last();
        if (field.Length >= 8)
            yield return field;
    }

    private static string Snippet(string text, IReadOnlyList<string> probes)
    {
        if (text.Length <= 300)
            return text;
        int match = probes
            .Select(probe => text.IndexOf(probe, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        int start = Math.Max(0, Math.Min(match - 60, text.Length - 300));
        return text.Substring(start, 300);
    }

    private static int MatchScore(
        string text,
        IReadOnlyList<string> code,
        IReadOnlyList<string> probes
    )
    {
        int score = 0;
        foreach (
            string probe in probes
                .Where(p => p.Length >= 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            string alternate = probe.Replace('_', ' ');
            if (
                text.Contains(probe, StringComparison.OrdinalIgnoreCase)
                || text.Contains(alternate, StringComparison.OrdinalIgnoreCase)
            )
                score = Math.Max(score, probe.Contains('.') ? 3 : 1);
            if (code.Any(token => token.Contains(probe, StringComparison.OrdinalIgnoreCase)))
                score = Math.Max(score, probe.Contains('.') ? 4 : 2);
        }
        return score;
    }

    private static bool Mentions(string text, string name) =>
        Regex.IsMatch(
            text,
            @"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])",
            RegexOptions.IgnoreCase
        );

    private static double TokenOverlap(string left, string right)
    {
        var a = NameTokens(left);
        var b = NameTokens(right);
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }

    private static double TokenContainment(string left, string right)
    {
        var a = NameTokens(left);
        var b = NameTokens(right);
        return a.IsSubsetOf(b) || b.IsSubsetOf(a) ? 1d : 0d;
    }

    private static HashSet<string> NameTokens(string name) =>
        name.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.EndsWith('s') && token.Length > 1 ? token[..^1] : token)
            .ToHashSet(StringComparer.Ordinal);

    private static int Levenshtein(string left, string right)
    {
        var distance = new int[left.Length + 1, right.Length + 1];
        for (int i = 0; i <= left.Length; i++)
            distance[i, 0] = i;
        for (int j = 0; j <= right.Length; j++)
            distance[0, j] = j;
        for (int i = 1; i <= left.Length; i++)
        for (int j = 1; j <= right.Length; j++)
            distance[i, j] = Math.Min(
                Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                distance[i - 1, j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1)
            );
        return distance[left.Length, right.Length];
    }
}
