using System.Text.RegularExpressions;

namespace Radar;

// Entry point for `radar check`. The rules live in CheckRun, split across:
//   CheckRun.Schema.cs     - field paths, GAQL queries/filters, enum members, SDK types, typed members
//   CheckRun.Behaviour.cs  - swallowed errors, enum switches, deprecation-page behaviour rows
//   CheckRun.Lifecycle.cs  - sunset, SDK/protobuf compatibility, unknowns, migration
public static partial class CheckEngine
{
    public static CheckReport Run(
        CodeIndexResult index,
        string repo,
        SchemaModel target,
        IReadOnlyList<SchemaModel> snapshots,
        IReadOnlyList<SchemaModel> next,
        IReadOnlyList<SunsetRow> sunsets,
        IReadOnlyList<SdkDependencies> sdkDependencies,
        IReadOnlyList<DeprecationRow> deprecations,
        string sdkVersion,
        DateOnly now,
        IReadOnlyList<ChangeRecord>? catalog = null,
        bool experimental = false,
        string propsPath = "code/backend/Directory.Packages.props"
    )
    {
        var changes = catalog?.ToList() ?? [];
        if (catalog is null)
        {
            SchemaModel previous = target;
            foreach (var following in next)
            {
                changes.AddRange(ChangeCatalog.Compare(previous, following, snapshots));
                previous = following;
            }
        }
        var run = new CheckRun(index, repo, target, snapshots, next, changes, experimental, propsPath);
        run.FieldPaths();
        run.Queries();
        run.SwallowedCatches();
        run.EnumSwitches();
        run.GaqlEnumFilters();
        run.EnumMembers();
        run.SdkTypes();
        run.TypedMembers();
        run.BehaviourRows(deprecations);
        run.Sunset(sunsets, now);
        run.Compatibility(sdkDependencies, sdkVersion);
        run.Unknowns();
        run.Migration(sdkDependencies, sdkVersion);
        return run.Report();
    }

    private sealed partial class CheckRun
    {
        private readonly CodeIndexResult index;
        private readonly string repo;
        private readonly string propsPath;
        private readonly SchemaModel target;
        private readonly IReadOnlyList<SchemaModel> next;
        private readonly List<ChangeRecord> changes;
        private readonly bool experimental;
        private readonly List<Finding> findings = [];
        private readonly SchemaModel[] allModels;
        private readonly HashSet<string> roots;
        private readonly HashSet<string> targetTypes;
        private readonly ChangeRecord[] nextAddedEnums;
        private readonly int targetNumber;
        private int nonGaql,
            unknownRoots,
            behaviourMisses;

        public CheckRun(
            CodeIndexResult index,
            string repo,
            SchemaModel target,
            IReadOnlyList<SchemaModel> snapshots,
            IReadOnlyList<SchemaModel> next,
            List<ChangeRecord> changes,
            bool experimental,
            string propsPath
        )
        {
            this.index = index;
            this.repo = repo;
            this.propsPath = propsPath;
            this.target = target;
            this.next = next;
            this.changes = changes;
            this.experimental = experimental;
            allModels = snapshots.OrderBy(s => VersionNumber(s.Version)).ToArray();
            roots = target
                .Fields.Keys.Select(path => path.Split('.')[0])
                .ToHashSet(StringComparer.Ordinal);
            targetTypes = TypeNames(target);
            nextAddedEnums = changes
                .Where(c =>
                    c.Kind == "ENUM_VALUE_ADDED" && next.Any(model => model.Version == c.VersionTo)
                )
                .ToArray();
            targetNumber = VersionNumber(target.Version);
        }

        // Code that already references the target SDK namespace compiles against it, so a typed
        // "absent from target" hit there is a matcher false positive (generated Has*/Clear*/Clone
        // members, same-named Bing/Optmyzr classes), not a real compile break.
        private bool CompiledAgainstTarget(string file) =>
            index.SdkVersionsByFile.TryGetValue(file, out var versions)
            && versions.Contains(targetNumber);

        private ChangeRecord? ChangeFor(string kind, string symbol) =>
            changes.FirstOrDefault(change =>
                change.Kind == kind
                && (
                    change.Symbol == symbol
                    || change.GaqlPath == symbol
                    || change.CsharpName == symbol
                )
            );

        private static string? Suggest(ChangeRecord? change) =>
            change?.ReplacementCandidates.FirstOrDefault()?.Symbol;

        private static List<string> ChangeEvidence(ChangeRecord? change, string site) =>
            change is null
                ? [site]
                :
                [
                    site,
                    $"Proto: {change.FromLocation ?? change.ToLocation ?? "unknown"}",
                    .. change.ReleaseNoteSnippets.Select(note => "Release note: " + note),
                    .. change.DeprecationRows.Select(row => "Deprecation: " + row.Description),
                ];

        private void Add(
            string category,
            string severity,
            string path,
            string file,
            int line,
            string message,
            string confidence = "high",
            string relevance = "used",
            ChangeRecord? change = null,
            string? suggestion = null,
            bool handled = false,
            List<string>? evidence = null,
            int count = 1
        )
        {
            var siteEvidence = evidence ?? ChangeEvidence(change, $"Use: {file}:{line}");
            siteEvidence =
            [
                .. siteEvidence,
                .. index
                    .DisabledGuardUses.Where(use => use.File == file && use.Line == line)
                    .Select(use => use.Evidence),
            ];
            findings.Add(
                NewFinding(
                    category,
                    severity,
                    path,
                    "google-ads",
                    file,
                    line,
                    count,
                    siteEvidence,
                    message
                ) with
                {
                    Confidence = confidence,
                    Relevance = relevance,
                    ChangeId = change?.Id,
                    SuggestedReplacement = suggestion ?? Suggest(change),
                    PossiblyHandled = handled,
                    Lines = line > 0 ? [line] : [],
                }
            );
        }

        // Merges findings for the same change/file/message into one row, then builds the report.
        public CheckReport Report()
        {
            var categories = ReportFindings.Categories;
            var grouped = findings
                .GroupBy(f =>
                    (
                        f.Category,
                        f.ChangeId,
                        f.File,
                        f.Path,
                        f.Message,
                        Site: f.Category == "RESILIENCE"
                        || f.Category == "SILENT_RISK"
                            && (
                                f.Message.StartsWith("switch on ", StringComparison.Ordinal)
                                || f.Message.StartsWith("Query on ", StringComparison.Ordinal)
                            )
                            ? f.Line
                            : 0
                    )
                )
                .Select(group =>
                    // The merged row speaks for its most severe location.
                    group.OrderBy(f => ReportFindings.SeverityOrder(f.Severity)).First() with
                    {
                        Lines = group.SelectMany(f => f.Lines).Distinct().Order().ToList(),
                        UseCount = group.Sum(f => f.UseCount),
                        PossiblyHandled = group.All(f => f.PossiblyHandled),
                        Evidence = group
                            .Where(f => f.PossiblyHandled && !group.All(g => g.PossiblyHandled))
                            .Select(f => $"Possibly handled only at line {f.Line}; other locations are unguarded.")
                            .Concat(group.SelectMany(f => f.Evidence))
                            .Distinct(StringComparer.Ordinal)
                            .Take(30)
                            .ToList(),
                    }
                )
                .OrderBy(f => Array.IndexOf(categories, f.Category))
                .ThenBy(f => f.Path, StringComparer.Ordinal)
                .ThenBy(f => f.File, StringComparer.Ordinal)
                .ToList();
            var limitations = new List<string>
            {
                "Conformance checks schema presence, not GAQL selectability or field compatibility.",
                "Typed references use syntax and local declarations; cross-method and inferred receiver types are unknown.",
            };
            if (experimental)
                limitations.Add("EXPERIMENTAL unfiltered-enum rule (low confidence).");
            return new CheckReport
            {
                Target = target.Version,
                Next = next.Select(model => model.Version).ToList(),
                Findings = grouped,
                Counts = categories.ToDictionary(
                    category => category,
                    category => grouped.Count(f => f.Category == category)
                ),
                Funnel = new Dictionary<string, int>
                {
                    ["changesInCatalog"] = changes.Count,
                    ["changesTouchingCode"] = grouped
                        .Where(f => f.ChangeId is not null)
                        .Select(f => f.ChangeId)
                        .Distinct()
                        .Count(),
                    ["findings"] = grouped.Count,
                    ["actionable"] = grouped.Count(ReportFindings.IsActionable),
                    ["possiblyHandled"] = grouped.Count(f => f.PossiblyHandled),
                    ["files"] = grouped
                        .Where(f => f.Line > 0)
                        .Select(f => f.File)
                        .Distinct(StringComparer.Ordinal)
                        .Count(),
                },
                NonGaqlQueriesSkipped = nonGaql,
                UnknownRootPathsSkipped = unknownRoots,
                BehaviourRowsWithoutHits = behaviourMisses,
                UnknownDefinitionsByOwner = index
                    .Definitions.Where(d => d.Platform.StartsWith("unknown", StringComparison.Ordinal))
                    .GroupBy(d => d.OwnerType)
                    .ToDictionary(group => group.Key, group => group.Count()),
                InterpolatedQueries = index.Queries.Count(q => q.IsInterpolated),
                ParseErrorFiles = index.ParseErrorFiles.Count,
                Limitations = limitations,
            };
        }
    }

    private static HashSet<string> TypeNames(SchemaModel model) =>
        model
            .Messages.Keys.Select(path => path.Split('.').Last())
            .Concat(model.ServiceNames.Values.Select(service => service.CsharpName ?? ""))
            .ToHashSet(StringComparer.Ordinal);

    private static string Snake(string value) =>
        Regex.Replace(value, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();

    private static bool ReceiverMatches(string receiver, string path)
    {
        string last = receiver.Split('.').Last();
        return path.Split('.').Any(part => part == last || SchemaModel.PascalCase(part) == last);
    }
}
