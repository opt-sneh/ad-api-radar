using System.Text.RegularExpressions;

namespace Radar;

public static partial class CheckEngine
{
    // Behaviour rules: code that compiles fine but may silently lose or mislabel data.
    private sealed partial class CheckRun
    {
        // catch blocks around Google Ads calls that swallow the failure (RESILIENCE).
        public void SwallowedCatches()
        {
            foreach (var swallowed in index.SwallowedCatches)
                Add(
                    "RESILIENCE",
                    swallowed.Kind == "log-only" ? "low" : "medium",
                    "swallowed Google Ads API error",
                    swallowed.File,
                    swallowed.Line,
                    $"{swallowed.Enclosing}: catch ({swallowed.CaughtType}) swallows a Google Ads API failure ({swallowed.Kind}); after an upgrade a rejected query here returns no rows silently. Rethrow, or record the failure so the run fails loudly.",
                    confidence: swallowed.CaughtType.Contains(
                        "GoogleAdsException",
                        StringComparison.Ordinal
                    ) || swallowed.CaughtType.Contains("RpcException", StringComparison.Ordinal)
                        ? "high"
                        : "medium",
                    evidence:
                    [
                        $"Catch: {swallowed.File}:{swallowed.Line} ({swallowed.Enclosing})",
                        swallowed.CallLine,
                    ]
                );
        }

        // switch on an SDK enum that doesn't list values added in the next version.
        public void EnumSwitches()
        {
            foreach (var statement in index.EnumSwitches)
            {
                var missing = nextAddedEnums
                    .Where(change =>
                        change.CsharpName is not null
                        && change.CsharpName.StartsWith(
                            statement.EnumType + ".",
                            StringComparison.Ordinal
                        )
                        && !statement.Values.Contains(
                            change.CsharpName.Split('.').Last(),
                            StringComparer.Ordinal
                        )
                    )
                    .ToArray();
                if (missing.Length == 0)
                    continue;
                string values = string.Join(
                    ", ",
                    missing
                        .Select(change => change.CsharpName!.Split('.').Last())
                        .Distinct(StringComparer.Ordinal)
                );
                Add(
                    "SILENT_RISK",
                    "medium",
                    statement.EnumType,
                    statement.File,
                    statement.Line,
                    $"switch on {statement.EnumType} in {statement.Enclosing} does not handle value(s) {values} added in {missing[0].VersionTo}; they fall to {(statement.HasDefault ? "default" : "no case")} and may be dropped or mislabelled.",
                    change: missing[0],
                    evidence: [$"Switch: {statement.File}:{statement.Line} ({statement.Enclosing})"]
                );
            }
        }

        // "Behavioral" / "Deprecation" rows from Google's deprecations page, matched to code by name.
        public void BehaviourRows(IReadOnlyList<DeprecationRow> deprecations)
        {
            foreach (
                var row in deprecations.Where(r =>
                    r.ChangeType.Contains("Behavioral", StringComparison.OrdinalIgnoreCase)
                    || r.ChangeType.Contains("Deprecation", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                var symbols = Regex
                    .Matches(
                        row.ChangeArea + " " + row.Description,
                        @"\b(?<type>[A-Z][A-Za-z0-9]*)\.(?<member>[a-zA-Z_][A-Za-z0-9_]*)\b"
                    )
                    .Select(match =>
                        (
                            Type: match.Groups["type"].Value,
                            Member: char.IsUpper(match.Groups["member"].Value[0])
                                ? match.Groups["member"].Value
                                : SchemaModel.PascalCase(match.Groups["member"].Value)
                        )
                    )
                    .Distinct()
                    .ToArray();
                var bareTypes = PascalIdentifiers()
                    .Matches(row.ChangeArea + " " + row.Description)
                    .Select(match => match.Value)
                    .Where(type => !symbols.Any(s => s.Type == type))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                // Enum values named in the row (e.g. BUSINESS_NAME, LOGO) narrow bare-type matches to
                // methods that actually use those values; otherwise every CampaignAsset use would match.
                var rowEnumValues = Regex
                    .Matches(row.Description, @"\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)*\b")
                    .Select(match => match.Value)
                    .Where(value => value.Length >= 3 && value != "API")
                    .Select(SchemaModel.PascalCase)
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var symbol in symbols)
                {
                    // CampaignCriterion.language -> GAQL paths under campaign_criterion.language: catches
                    // code that fetches the field and copies whole objects without touching .Language.
                    string gaqlPrefix = Snake(symbol.Type) + "." + Snake(symbol.Member);
                    foreach (
                        var definition in index.Definitions.Where(d =>
                            d.Platform == "google-ads"
                            && (
                                d.Path == gaqlPrefix
                                || d.Path.StartsWith(gaqlPrefix + ".", StringComparison.Ordinal)
                            )
                        )
                    )
                    foreach (
                        var use in index.Uses.Where(u =>
                            u.OwnerType == definition.OwnerType && u.Member == definition.Member
                        )
                    )
                        Add(
                            "SILENT_RISK",
                            "medium",
                            symbol.Type + "." + symbol.Member,
                            use.File,
                            use.Line,
                            $"Behavior check: {row.Description}",
                            "medium",
                            evidence:
                            [
                                $"GAQL field use: {definition.Path} in {use.Enclosing}",
                                $"Deprecation: {row.EffectiveDateText}; {row.Description}",
                            ]
                        );
                    foreach (
                        var query in index.Queries.Where(q =>
                            q.Paths.Any(p =>
                                p == gaqlPrefix
                                || p.StartsWith(gaqlPrefix + ".", StringComparison.Ordinal)
                            )
                        )
                    )
                        Add(
                            "SILENT_RISK",
                            "medium",
                            symbol.Type + "." + symbol.Member,
                            query.File,
                            query.Line,
                            $"Behavior check: {row.Description}",
                            "medium",
                            evidence:
                            [
                                $"GAQL query selects {gaqlPrefix}",
                                $"Deprecation: {row.EffectiveDateText}; {row.Description}",
                            ]
                        );
                    foreach (
                        var member in index.TypedMembers.Where(m =>
                            m.Member == symbol.Member && ReceiverMatches(m.ReceiverHint, symbol.Type)
                        )
                    )
                    {
                        string? other = UnnamedChannels(member.File, row.Description);
                        Add(
                            "SILENT_RISK",
                            other is null ? "medium" : "low",
                            symbol.Type + "." + symbol.Member,
                            member.File,
                            member.Line,
                            $"Behavior check: {row.Description}",
                            other is null ? member.Confidence : "low",
                            handled: member.PossiblyHandled,
                            evidence:
                            [
                                $"Use: {member.File}:{member.Line} ({member.Enclosing})",
                                $"Deprecation: {row.EffectiveDateText}; {row.Description}",
                                .. other is null ? Array.Empty<string>()
                                    : [$"File only sets AdvertisingChannelType {other}, which this change does not name; likely unaffected."],
                            ]
                        );
                    }
                    foreach (
                        var value in index.EnumMembers.Where(value =>
                            value.Value == symbol.Member
                            && value.EnumType.EndsWith(
                                "CriterionTypeEnum.Types.CriterionType",
                                StringComparison.Ordinal
                            )
                            && symbol.Type == "CampaignCriterion"
                        )
                    )
                        Add(
                            "SILENT_RISK",
                            "medium",
                            symbol.Type + "." + symbol.Member,
                            value.File,
                            value.Line,
                            $"Behavior check: {row.Description}",
                            handled: value.PossiblyHandled,
                            evidence:
                            [
                                $"Enum use: {value.EnumType}.{value.Value}",
                                $"Deprecation: {row.EffectiveDateText}; {row.Description}",
                            ]
                        );
                }
                foreach (string type in bareTypes)
                foreach (var use in index.SdkTypes.Where(use => use.TypeName == type))
                {
                    bool narrowed =
                        rowEnumValues.Count == 0
                        || index.EnumMembers.Any(value =>
                            value.File == use.File
                            && value.Enclosing == use.Enclosing
                            && rowEnumValues.Contains(value.Value)
                        );
                    if (!narrowed)
                        continue;
                    Add(
                        "SILENT_RISK",
                        "medium",
                        type,
                        use.File,
                        use.Line,
                        $"Behavior check: {row.Description}",
                        rowEnumValues.Count == 0 ? "low" : "medium",
                        evidence:
                        [
                            $"Type use: {use.File}:{use.Line} ({use.Enclosing})",
                            $"Deprecation: {row.EffectiveDateText}; {row.Description}",
                        ]
                    );
                }
                if (
                    !findings.Any(f =>
                        f.Category == "SILENT_RISK" && f.Message == $"Behavior check: {row.Description}"
                    )
                )
                    behaviourMisses++;
            }
        }

        // Campaign types a deprecation row names, as AdvertisingChannelType members.
        private static readonly (string Words, string Channel)[] ChannelWords =
        [
            ("Performance Max", "PerformanceMax"), ("Search", "Search"), ("Shopping", "Shopping"),
            ("Display", "Display"), ("Video", "Video"), ("Demand Gen", "DemandGen"), ("App", "MultiChannel"),
            ("Hotel", "Hotel"), ("Local", "Local"), ("Smart", "Smart"), ("Travel", "Travel"),
        ];
        private readonly Dictionary<string, string[]> channelsByFile = new(StringComparer.Ordinal);

        // ponytail: file-level heuristic; a file that builds several campaign types is judged by all of them.
        // Returns the file's channel types when none of them is named by the row, else null.
        private string? UnnamedChannels(string file, string description)
        {
            if (!channelsByFile.TryGetValue(file, out var channels))
            {
                string full = Path.Combine(repo, file);
                channels = File.Exists(full)
                    ? Regex.Matches(File.ReadAllText(full), @"AdvertisingChannelType\.(\w+)")
                        .Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray()
                    : [];
                channelsByFile[file] = channels;
            }
            var named = ChannelWords.Where(c => Regex.IsMatch(description, $@"\b{c.Words}\b"))
                .Select(c => c.Channel).ToHashSet(StringComparer.Ordinal);
            return channels.Length > 0 && named.Count > 0 && !channels.Any(named.Contains)
                ? string.Join(", ", channels) : null;
        }
    }
}
