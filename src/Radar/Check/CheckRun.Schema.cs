using System.Text.RegularExpressions;

namespace Radar;

public static partial class CheckEngine
{
    // Schema rules: does what the code references still exist, and is it removed/retyped/deprecated next?
    private sealed partial class CheckRun
    {
        // Field path definitions (e.g. `new AdwordsField("campaign.name")`) and their uses.
        public void FieldPaths()
        {
            var uses = index
                .Uses.GroupBy(use => (use.OwnerType, use.Member, use.Path))
                .ToDictionary(group => group.Key, group => group.ToArray());
            foreach (var definition in index.Definitions.Where(d => d.Platform == "google-ads"))
            {
                uses.TryGetValue(
                    (definition.OwnerType, definition.Member, definition.Path),
                    out var sites
                );
                sites ??= [];
                if (sites.Length == 0)
                    Field(
                        definition.Path,
                        definition.File,
                        definition.Line,
                        $"Definition: {definition.File}:{definition.Line}",
                        false,
                        0
                    );
                else
                    foreach (var site in sites)
                        Field(
                            definition.Path,
                            site.File,
                            site.Line,
                            $"Definition: {definition.File}:{definition.Line}; use: {site.Enclosing}",
                            true,
                            1
                        );
            }
        }

        // GAQL query literals: every selected/filtered path, plus the experimental unfiltered-enum rule.
        public void Queries()
        {
            var enumFieldsWithAdds = target
                .Fields.Where(pair => experimental && !pair.Value.Repeated
                    && pair.Key.Count(character => character == '.') == 1)
                .Select(pair =>
                    (
                        pair.Key,
                        Changes: nextAddedEnums
                            .Where(change =>
                                change.CsharpName is not null
                                && change.CsharpName.Contains('.')
                                && pair.Value.ProtoType.EndsWith(
                                    "."
                                        + change
                                            .CsharpName[..change.CsharpName.LastIndexOf('.')]
                                            .Replace(".Types.", ".", StringComparison.Ordinal),
                                    StringComparison.Ordinal
                                )
                            )
                            .ToArray()
                    )
                )
                .Where(pair => pair.Changes.Length > 0)
                .ToArray();
            foreach (var query in index.Queries.Where(q => q.Platform == "google-ads-candidate"))
            {
                if (query.Resource is null || !roots.Contains(query.Resource.ToLowerInvariant()))
                {
                    nonGaql++;
                    continue;
                }
                foreach (string path in query.Paths.Distinct(StringComparer.Ordinal))
                    Field(path, query.File, query.Line, $"GAQL: {query.Text}", true, 1);
                var selection = Regex
                    .Match(
                        query.Text,
                        @"\bSELECT\b(?<fields>[\s\S]*?)\bFROM\b",
                        RegexOptions.IgnoreCase
                    )
                    .Groups["fields"]
                    .Value;
                foreach (
                    var group in enumFieldsWithAdds.Where(pair =>
                        pair.Key.StartsWith(
                            query.Resource.ToLowerInvariant() + ".",
                            StringComparison.Ordinal
                        )
                    )
                )
                {
                    string path = group.Key;
                    if (
                        Regex.IsMatch(
                            selection,
                            @"(?<![A-Za-z0-9_.])" + Regex.Escape(path) + @"(?![A-Za-z0-9_.])",
                            RegexOptions.IgnoreCase
                        )
                        || index.GaqlEnumFilters.Any(filter =>
                            filter.File == query.File
                            && filter.Line == query.Line
                            && filter.Path == path
                        )
                    )
                        continue;
                    string values = string.Join(
                        ", ",
                        group
                            .Changes.Select(change => change.Symbol.Split('.').Last())
                            .Distinct(StringComparer.Ordinal)
                    );
                    Add(
                        "SILENT_RISK",
                        "low",
                        path,
                        query.File,
                        query.Line,
                        $"Query on {query.Resource} neither selects nor filters {path}; {group.Changes[0].VersionTo} adds {values}, so new kinds of rows will be returned and cannot be told apart.",
                        confidence: "low",
                        change: group.Changes[0],
                        evidence: [$"GAQL: {query.Text}"]
                    );
                }
            }
        }

        // One GAQL path used at file:line: missing from target, removed next, or deprecated.
        private void Field(string path, string file, int line, string site, bool used, int count)
        {
            string root = path.Split('.')[0];
            if (!roots.Contains(root))
            {
                if (!used && !allModels.Any(model => model.Fields.ContainsKey(path)))
                    Add(
                        "CLEANUP",
                        "info",
                        path,
                        file,
                        line,
                        $"{path} is defined but absent from every snapshot and has no uses.",
                        relevance: "unused",
                        suggestion: ChangeCatalog
                            .FindCandidates(path, allModels)
                            .FirstOrDefault()
                            ?.Symbol,
                        evidence: [site],
                        count: 0
                    );
                else
                    unknownRoots++;
                return;
            }
            if (!target.Fields.TryGetValue(path, out var field))
            {
                var last = allModels.LastOrDefault(model => model.Fields.ContainsKey(path));
                string lineage = last is null
                    ? "not present in any snapshot"
                    : $"last present in {last.Version}; first removed in "
                        + (
                            allModels
                                .FirstOrDefault(model =>
                                    VersionNumber(model.Version) > VersionNumber(last.Version)
                                    && !model.Fields.ContainsKey(path)
                                )
                                ?.Version
                            ?? "unknown version"
                        );
                if (used || last is not null)
                    Add(
                        "BREAKING_RUNTIME",
                        "high",
                        path,
                        file,
                        line,
                        $"{path} does not exist in {target.Version}; {lineage}.",
                        relevance: used ? "used" : "defined-only",
                        evidence:
                        [
                            site,
                            lineage,
                            $"Proto: {(last is null ? "unknown" : last.Fields[path].Location ?? "unknown")}",
                        ],
                        count: count
                    );
                else
                    Add(
                        "CLEANUP",
                        "info",
                        path,
                        file,
                        line,
                        $"{path} is defined but absent from every snapshot and has no uses.",
                        relevance: "unused",
                        suggestion: ChangeCatalog
                            .FindCandidates(path, allModels)
                            .FirstOrDefault()
                            ?.Symbol,
                        evidence: [site, lineage],
                        count: 0
                    );
                return;
            }
            if (!used)
                return;
            foreach (
                var change in changes.Where(c =>
                    c.GaqlPath == path
                    || c.Kind == "RESOURCE_REMOVED"
                        && c.GaqlPath is not null
                        && path.StartsWith(c.GaqlPath + ".", StringComparison.Ordinal)
                )
            )
            {
                if (
                    change.Kind
                    is "FIELD_REMOVED"
                        or "FIELD_RENAMED_CANDIDATE"
                        or "RESOURCE_REMOVED"
                )
                    Add(
                        "UPCOMING_BREAK",
                        "high",
                        path,
                        file,
                        line,
                        $"{path} is removed or renamed in {change.VersionTo}.",
                        change: change,
                        count: count
                    );
                else if (change.Kind == "FIELD_TYPE_CHANGED")
                {
                    Add(
                        "UPCOMING_BREAK",
                        "medium",
                        path,
                        file,
                        line,
                        $"{path} is retyped in {change.VersionTo}.",
                        change: change,
                        count: count
                    );
                    Add(
                        "SILENT_RISK",
                        "medium",
                        path,
                        file,
                        line,
                        $"{path} changes type in {change.VersionTo}.",
                        change: change,
                        count: count
                    );
                }
                else if (change.Kind == "FIELD_NEWLY_DEPRECATED")
                    Add(
                        "DEPRECATED",
                        "medium",
                        path,
                        file,
                        line,
                        $"{path} is deprecated in {change.VersionTo}.",
                        change: change,
                        count: count
                    );
            }
            if (field.Deprecated)
                Add(
                    "DEPRECATED",
                    "medium",
                    path,
                    file,
                    line,
                    $"{path} is deprecated.",
                    evidence: [site, $"Proto: {field.Location ?? "unknown"}"],
                    count: count
                );
        }

        // GAQL `WHERE field = 'VALUE'` / `IN (...)` enum literals.
        public void GaqlEnumFilters()
        {
            var enumTypeNames = allModels
                .SelectMany(model => model.Enums.Keys)
                .Where(key => key.Contains('.'))
                .Select(key => key[..key.LastIndexOf('.')])
                .ToHashSet(StringComparer.Ordinal);
            var addedEnumByType = changes
                .Where(c => c.Kind == "ENUM_VALUE_ADDED" && c.Symbol.Contains('.'))
                .GroupBy(c => c.Symbol[..c.Symbol.LastIndexOf('.')], StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            foreach (var filter in index.GaqlEnumFilters)
            {
                if (!target.Fields.TryGetValue(filter.Path, out var field))
                    continue;
                string enumName = field.ProtoType.Split('.').Last();
                if (!enumTypeNames.Contains(enumName))
                    continue;
                foreach (string value in filter.Values.Distinct(StringComparer.Ordinal))
                {
                    string symbol = enumName + "." + value;
                    bool present = target.Enums.ContainsKey(symbol);
                    var removed = ChangeFor("ENUM_VALUE_REMOVED", symbol);
                    if (!present)
                        Add(
                            "BREAKING_RUNTIME",
                            "high",
                            filter.Path,
                            filter.File,
                            filter.Line,
                            $"GAQL enum literal '{value}' is not valid for {filter.Path} in {target.Version}.",
                            evidence:
                            [
                                $"GAQL {filter.Path} {filter.Operator} '{value}'",
                                $"Enum: {symbol}",
                            ]
                        );
                    else if (removed is not null)
                        Add(
                            "UPCOMING_BREAK",
                            "high",
                            filter.Path,
                            filter.File,
                            filter.Line,
                            $"GAQL enum literal '{value}' is removed in {removed.VersionTo}.",
                            change: removed
                        );
                    if (present && target.Enums[symbol].Deprecated)
                        Add(
                            "DEPRECATED",
                            "medium",
                            filter.Path,
                            filter.File,
                            filter.Line,
                            $"GAQL enum literal '{value}' is deprecated."
                        );
                }
                bool excludes = filter.Operator is "!=" or "NOT IN";
                foreach (
                    var added in addedEnumByType
                        .GetValueOrDefault(enumName, [])
                        .Where(c =>
                            !filter.Values.Contains(c.Symbol.Split('.').Last(), StringComparer.Ordinal)
                        )
                )
                    Add(
                        "SILENT_RISK",
                        // = / IN only drop the new value from results; != / NOT IN let it in unnoticed.
                        excludes ? "medium" : "low",
                        filter.Path,
                        filter.File,
                        filter.Line,
                        $"filter on {filter.Path} may need to handle new value {added.Symbol.Split('.').Last()} (added in {added.VersionTo})",
                        confidence: excludes ? "high" : "low",
                        change: added,
                        evidence: ChangeEvidence(
                            added,
                            $"GAQL {filter.Path} {filter.Operator} ({string.Join(", ", filter.Values)})"
                        )
                    );
            }
        }

        // Typed C# enum members, e.g. CriterionTypeEnum.Types.CriterionType.Language.
        public void EnumMembers()
        {
            var targetEnumsByCsharp = target
                .Enums.Where(pair => pair.Value.CsharpName is not null)
                .GroupBy(pair => pair.Value.CsharpName!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var historicalEnumNames = allModels
                .SelectMany(model => model.Enums.Values)
                .Select(entry => entry.CsharpName)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal);
            var removedEnumByCsharp = changes
                .Where(c => c.Kind == "ENUM_VALUE_REMOVED" && c.CsharpName is not null)
                .GroupBy(c => c.CsharpName!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            foreach (var reference in index.EnumMembers)
            {
                string csharp = reference.EnumType + "." + reference.Value;
                bool exists = targetEnumsByCsharp.TryGetValue(csharp, out var entry);
                var change = removedEnumByCsharp.GetValueOrDefault(csharp);
                if (
                    !exists
                    && historicalEnumNames.Contains(csharp)
                    && !CompiledAgainstTarget(reference.File)
                )
                    Add(
                        "BREAKING_COMPILE",
                        "high",
                        reference.EnumType + "." + reference.Value,
                        reference.File,
                        reference.Line,
                        $"Enum member is absent from {target.Version}.",
                        evidence:
                        [
                            $"Use: {reference.File}:{reference.Line}",
                            "Proto: "
                                + (
                                    allModels
                                        .SelectMany(model => model.Enums.Values)
                                        .FirstOrDefault(value => value.CsharpName == csharp)
                                        ?.Location
                                    ?? "unknown"
                                ),
                        ]
                    );
                else if (change is not null)
                    Add(
                        "UPCOMING_BREAK",
                        "high",
                        change.Symbol,
                        reference.File,
                        reference.Line,
                        $"Enum value is removed in {change.VersionTo}.",
                        change: change
                    );
                if (exists && entry.Value.Deprecated)
                    Add(
                        "DEPRECATED",
                        "medium",
                        entry.Key,
                        reference.File,
                        reference.Line,
                        $"{entry.Key} is deprecated.",
                        handled: reference.PossiblyHandled
                    );
            }
        }

        // SDK type names (resources, messages, services).
        public void SdkTypes()
        {
            var historicalTypes = allModels.SelectMany(TypeNames).ToHashSet(StringComparer.Ordinal);
            foreach (var type in index.SdkTypes.DistinctBy(t => (t.TypeName, t.File, t.Line)))
            {
                bool exists = targetTypes.Contains(type.TypeName);
                bool known = historicalTypes.Contains(type.TypeName);
                var change = changes.FirstOrDefault(c =>
                    (c.Kind is "RESOURCE_REMOVED" or "MESSAGE_REMOVED" or "SERVICE_REMOVED")
                    && c.CsharpName == type.TypeName
                );
                if (
                    !exists
                    && (known || type.ExplicitSdkNamespace)
                    && !CompiledAgainstTarget(type.File)
                )
                    Add(
                        "BREAKING_COMPILE",
                        "high",
                        type.TypeName,
                        type.File,
                        type.Line,
                        $"SDK type {type.TypeName} is absent from {target.Version}."
                    );
                else if (change is not null)
                    Add(
                        "UPCOMING_BREAK",
                        "high",
                        type.TypeName,
                        type.File,
                        type.Line,
                        $"SDK type {type.TypeName} is removed in {change.VersionTo}.",
                        change: change
                    );
            }
        }

        // Typed property access (row.Campaign.Name) and service-client method calls.
        public void TypedMembers()
        {
            var targetFieldsByName = target
                .ProtoFields.Values.GroupBy(field => field.CsharpName)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var historicalFieldsByName = allModels
                .SelectMany(model => model.ProtoFields.Values)
                .GroupBy(field => field.CsharpName)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var changesByCsharp = changes
                .Where(change => change.CsharpName is not null)
                .GroupBy(change => change.CsharpName!, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var targetServiceMethods = target
                .Services.Values.Select(service => service.CsharpName)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal);
            var historicalServiceMethods = allModels
                .SelectMany(model => model.Services.Values)
                .Select(service => service.CsharpName)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var member in index.TypedMembers)
            {
                if (member.ReceiverHint.EndsWith("ServiceClient", StringComparison.Ordinal))
                {
                    string method = member.ReceiverHint + "." + member.Member;
                    bool methodExists = targetServiceMethods.Contains(method);
                    bool methodKnown = historicalServiceMethods.Contains(method);
                    var serviceChange = changes.FirstOrDefault(c =>
                        c.Kind == "SERVICE_METHOD_REMOVED" && c.CsharpName == method
                    );
                    if (!methodExists && methodKnown && !CompiledAgainstTarget(member.File))
                        Add(
                            "BREAKING_COMPILE",
                            "high",
                            method,
                            member.File,
                            member.Line,
                            $"Service method {method} is absent from {target.Version}."
                        );
                    else if (serviceChange is not null)
                        Add(
                            "UPCOMING_BREAK",
                            "high",
                            method,
                            member.File,
                            member.Line,
                            $"Service method {method} is removed in {serviceChange.VersionTo}.",
                            change: serviceChange
                        );
                    continue;
                }
                var candidates = targetFieldsByName
                    .GetValueOrDefault(member.Member, [])
                    .Where(field => ReceiverMatches(member.ReceiverHint, field.Path))
                    .ToArray();
                bool present = candidates.Length > 0;
                bool known =
                    historicalFieldsByName
                        .GetValueOrDefault(member.Member, [])
                        .Any(field => ReceiverMatches(member.ReceiverHint, field.Path))
                    || member.Confidence == "high" && targetTypes.Contains(member.ReceiverHint);
                var change = changesByCsharp
                    .GetValueOrDefault(member.Member, [])
                    .FirstOrDefault(c =>
                        (
                            c.Kind
                            is "FIELD_REMOVED"
                                or "FIELD_RENAMED_CANDIDATE"
                                or "FIELD_TYPE_CHANGED"
                                or "FIELD_NEWLY_DEPRECATED"
                        )
                        && c.CsharpName == member.Member
                        && ReceiverMatches(member.ReceiverHint, c.Symbol)
                    );
                string symbol = member.ReceiverHint + "." + member.Member;
                if (!present && known && !CompiledAgainstTarget(member.File))
                    Add(
                        "BREAKING_COMPILE",
                        "high",
                        symbol,
                        member.File,
                        member.Line,
                        $"Property {symbol} is absent from {target.Version}.",
                        member.Confidence,
                        handled: member.PossiblyHandled,
                        evidence:
                        [
                            $"Use: {member.File}:{member.Line}",
                            "Proto: "
                                + (
                                    historicalFieldsByName
                                        .GetValueOrDefault(member.Member, [])
                                        .FirstOrDefault(field =>
                                            ReceiverMatches(member.ReceiverHint, field.Path)
                                        )
                                        ?.Location
                                    ?? "unknown"
                                ),
                        ]
                    );
                else if (
                    change is not null
                    && change.Kind is ("FIELD_REMOVED" or "FIELD_RENAMED_CANDIDATE")
                )
                    Add(
                        "UPCOMING_BREAK",
                        "high",
                        symbol,
                        member.File,
                        member.Line,
                        $"Property {symbol} is removed or renamed in {change.VersionTo}.",
                        member.Confidence,
                        change: change,
                        handled: member.PossiblyHandled
                    );
                else if (change?.Kind == "FIELD_TYPE_CHANGED")
                {
                    Add(
                        "UPCOMING_BREAK",
                        "medium",
                        symbol,
                        member.File,
                        member.Line,
                        $"Property {symbol} is retyped in {change.VersionTo}.",
                        member.Confidence,
                        change: change,
                        handled: member.PossiblyHandled
                    );
                    Add(
                        "SILENT_RISK",
                        "medium",
                        symbol,
                        member.File,
                        member.Line,
                        $"Property {symbol} changes type in {change.VersionTo}.",
                        member.Confidence,
                        change: change,
                        handled: member.PossiblyHandled
                    );
                }
                if (change?.Kind == "FIELD_NEWLY_DEPRECATED")
                    Add(
                        "DEPRECATED",
                        "medium",
                        symbol,
                        member.File,
                        member.Line,
                        $"Property {symbol} is deprecated in {change.VersionTo}.",
                        member.Confidence,
                        change: change,
                        handled: member.PossiblyHandled
                    );
                if (candidates.Any(candidate => candidate.Deprecated))
                    Add(
                        "DEPRECATED",
                        "medium",
                        symbol,
                        member.File,
                        member.Line,
                        $"Property {symbol} is deprecated.",
                        member.Confidence,
                        handled: member.PossiblyHandled,
                        evidence:
                        [
                            $"Use: {member.File}:{member.Line}",
                            .. candidates.Where(c => c.Deprecated).Select(c => "Proto: " + c.Location),
                        ]
                    );
            }
        }
    }
}
