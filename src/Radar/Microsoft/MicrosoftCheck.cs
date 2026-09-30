namespace Radar;

// Matches Microsoft.BingAds.SDK changes (installed version → newest) and dated Microsoft
// announcements to Optmyzr's Microsoft Ads code. Same categories and report as Google.
public static class MicrosoftCheck
{
    public const string Platform = "microsoft-ads";
    private const string Package = MicrosoftSnapshot.PackageId;
    private const string MaxCpcSource =
        "https://techcommunity.microsoft.com/t5/bing-ads-api-blog/upcoming-change-max-cpc-removal-for-new-non-portfolio-campaigns/ba-p/4559366";
    private const string RestSource = "https://learn.microsoft.com/en-us/advertising/guides/migrate-to-rest";
    private static readonly DateOnly MaxCpcDate = new(2027, 1, 12);
    private static readonly DateOnly SoapRetirement = new(2027, 1, 31);

    // Bidding schemes whose Max CPC is rejected on new/updated non-portfolio campaigns from 2027-01-12.
    private static readonly HashSet<string> MaxCpcSchemes = new(StringComparer.Ordinal)
    {
        "TargetCpaBiddingScheme", "TargetRoasBiddingScheme", "MaxConversionsBiddingScheme",
        "MaxConversionValueBiddingScheme", "MaxClicksBiddingScheme",
    };

    public static CheckReport Run(
        MicrosoftIndex index,
        string repo,
        string propsPath,
        MicrosoftSchema from,
        MicrosoftSchema to,
        IReadOnlyList<MicrosoftSchema> between,
        DateOnly now,
        IReadOnlyList<string>? announcements = null
    )
    {
        var findings = new List<Finding>();
        bool upgrade = MicrosoftSnapshot.Compare(to.Version, from.Version) > 0;
        int changes = 0;

        void Add(string category, string severity, string path, string file, int line, string message,
            List<string> evidence, string? changeId = null, string confidence = "high", string? suggestion = null,
            int count = 1) =>
            findings.Add(CheckEngine.NewFinding(category, severity, path, Platform, file, line, count, evidence, message) with
            {
                Confidence = confidence,
                ChangeId = changeId,
                SuggestedReplacement = suggestion,
                Lines = line > 0 ? [line] : [],
            });

        MsType? Resolve(MicrosoftSchema schema, string name, string? qualifier, string file)
        {
            if (qualifier is not null)
                return schema.Find(qualifier + "." + name);
            var imported = index.NamespacesByFile.GetValueOrDefault(file) ?? [];
            return schema.Named(name).FirstOrDefault(type => imported.Contains(type.Namespace, StringComparer.Ordinal));
        }

        // 1. SDK types we use that the newer SDK removes: the build breaks on upgrade.
        var removedTypes = from.Types.Where(type => to.Find(type.FullName) is null)
            .ToDictionary(type => type.FullName, StringComparer.Ordinal);
        var addedTypes = to.Types.Where(type => from.Find(type.FullName) is null).ToList();
        changes += removedTypes.Count + addedTypes.Count;
        var typeSites = index.Types.Select(use => (use.Type, use.Qualifier, use.File, use.Line, Evidence: $"{use.Via} in {use.Enclosing}"))
            .Concat(index.Members.Select(use => (use.Type, use.Qualifier, use.File, use.Line, Evidence: $"{use.Type}.{use.Member} in {use.Enclosing}")));
        foreach (var site in typeSites)
        {
            var type = Resolve(from, site.Type, site.Qualifier, site.File);
            if (type is null || !removedTypes.ContainsKey(type.FullName))
                continue;
            var replacement = to.Types.Where(candidate => candidate.Kind == type.Kind)
                .Select(candidate => (candidate, Score: Similarity(candidate.Name, type.Name)))
                .Where(pair => pair.Score >= 0.5).OrderByDescending(pair => pair.Score).FirstOrDefault().candidate;
            Add("UPCOMING_BREAK", "high", type.FullName, site.File, site.Line,
                $"{type.Name} is removed in {Package} {to.Version}; this won't compile after the upgrade.",
                [$"Use: {site.File}:{site.Line} ({site.Evidence})", $"Present in {from.Version}, absent in {to.Version}"],
                "ms-type-removed:" + type.FullName, suggestion: replacement?.FullName);
        }

        // 2. Members (properties, enum values, methods) we use that the newer SDK removes.
        int membersRemoved = 0;
        foreach (var use in index.Members)
        {
            var type = Resolve(from, use.Type, use.Qualifier, use.File);
            if (type is null || removedTypes.ContainsKey(type.FullName) || !from.HasMember(type, use.Member))
                continue;
            var target = to.Find(type.FullName);
            if (target is null || to.HasMember(target, use.Member))
                continue;
            membersRemoved++;
            string what = type.Kind == "enum" ? "enum value" : "member";
            string? replacement = AllMembers(to, target)
                .Select(member => (member, Score: Similarity(member, use.Member)))
                .Where(pair => pair.Score >= 0.5).OrderByDescending(pair => pair.Score).FirstOrDefault().member;
            Add("UPCOMING_BREAK", "high", $"{type.Name}.{use.Member}", use.File, use.Line,
                $"{what} {type.Name}.{use.Member} is removed in {Package} {to.Version}; this won't compile after the upgrade.",
                [$"Use: {use.File}:{use.Line} ({use.Via} in {use.Enclosing})"],
                $"ms-member-removed:{type.FullName}.{use.Member}",
                suggestion: replacement is null ? null : $"{type.Name}.{replacement}");
        }

        // 3. Report columns referenced as strings (field registries, bing_metadata, Enum.Parse inputs).
        //    The compiler can't see these; a removed column fails at runtime.
        //    A column dropped from one report but still in others is only flagged where the same file uses that report.
        static bool IsColumnEnum(MsType type) => type.Kind == "enum" && type.Name.EndsWith("ReportColumn", StringComparison.Ordinal);
        var stillColumns = to.Types.Where(IsColumnEnum).SelectMany(type => type.Members).ToHashSet(StringComparer.Ordinal);
        var removedColumns = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var report in from.Types.Where(IsColumnEnum))
        {
            var next = to.Find(report.FullName);
            foreach (string value in report.Members.Where(value => next is null || !next.Members.Contains(value, StringComparer.Ordinal)))
                (removedColumns.TryGetValue(value, out var list) ? list : removedColumns[value] = []).Add(report.Name);
        }
        changes += removedColumns.Count;
        var reportsByFile = index.Types.Select(use => (use.File, use.Type)).Concat(index.Members.Select(use => (use.File, use.Type)))
            .ToHashSet();
        foreach (var use in index.Strings.Where(use => use.Context != "switch" && removedColumns.ContainsKey(use.Value)))
        {
            bool everywhere = !stillColumns.Contains(use.Value);
            if (!everywhere && !removedColumns[use.Value].Any(report => reportsByFile.Contains((use.File, report))))
                continue;
            bool strong = use.Context is "registry" or "resource" || use.Context.Contains("Parse", StringComparison.Ordinal);
            Add("UPCOMING_BREAK", strong ? "high" : "medium", use.Value, use.File, use.Line,
                $"Report column \"{use.Value}\" is removed from {string.Join(", ", removedColumns[use.Value])} in {Package} {to.Version}. "
                + "It is referenced as a string, so the build still passes and the report request fails (or Enum.TryParse silently drops it) at runtime.",
                [$"String: {use.File}:{use.Line} ({use.Context}{(use.Enclosing.Length > 0 ? " in " + use.Enclosing : "")})"],
                "ms-column-removed:" + use.Value, strong ? "high" : "low");
        }

        // 4. Bulk CSV headers referenced as strings that the newer SDK renames or drops.
        var removedHeaders = from.BulkHeadersKnown && to.BulkHeadersKnown
            ? from.BulkHeaders.Except(to.BulkHeaders, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal)
            : [];
        changes += removedHeaders.Count;
        foreach (var use in index.Strings.Where(use => removedHeaders.Contains(use.Value)))
        {
            string? replacement = to.BulkHeaders.Select(header => (header, Score: Similarity(header, use.Value)))
                .Where(pair => pair.Score >= 0.5).OrderByDescending(pair => pair.Score).FirstOrDefault().header;
            Add("SILENT_RISK", "high", use.Value, use.File, use.Line,
                $"Bulk column \"{use.Value}\" is no longer in the {to.Version} bulk file. Code that reads it by name gets empty values (or CsvHelper throws a header error).",
                [$"String: {use.File}:{use.Line} ({use.Context})"],
                "ms-bulk-header-removed:" + use.Value, use.Context == "csv-map" ? "high" : "medium", replacement);
        }

        // 5/6. switch statements over SDK values (enum members, or bidding-scheme type-name strings) that don't
        //      handle values the newer SDK adds, or values that already exist (long-standing gaps, only when the
        //      switch covers at least half the values, i.e. it means to be complete). What happens to an unhandled
        //      value decides severity: a throwing default crashes the operation; a pass-through default is fine.
        static string Fate(string kind) => kind switch
        {
            "throw" => "hit a default that throws, so the whole operation fails",
            "passthrough" => "pass through unchanged",
            "none" => "match no case and are ignored",
            _ => "fall to default and may be dropped or mislabelled",
        };
        void Coverage(MsSwitch statement, string subject, string changeKey, IReadOnlyList<string> gaps, IReadOnlyList<string> added)
        {
            var evidence = new List<string>
            {
                $"Switch: {statement.File}:{statement.Line} ({statement.Enclosing})",
                $"Handles: {string.Join(", ", statement.Values)}",
            };
            bool throws = statement.DefaultKind == "throw";
            if (gaps.Count > 0 && statement.DefaultKind != "passthrough")
                Add("SILENT_RISK", throws ? "medium" : "low", subject, statement.File, statement.Line,
                    $"switch on {subject} in {statement.Enclosing} does not handle {string.Join(", ", gaps)}, which already exist in SDK {from.Version}; they {Fate(statement.DefaultKind)}.",
                    evidence, $"ms-{changeKey}-gap:{subject}", "medium");
            if (added.Count > 0)
                Add("SILENT_RISK", throws ? "high" : statement.DefaultKind == "passthrough" ? "low" : "medium", subject, statement.File, statement.Line,
                    $"switch on {subject} in {statement.Enclosing} does not handle {string.Join(", ", added)} (new in {to.Version}); they {Fate(statement.DefaultKind)}.",
                    evidence, $"ms-{changeKey}-added:{subject}");
        }

        foreach (var statement in index.Switches.Where(s => s.EnumType is not null))
        {
            var type = Resolve(from, statement.EnumType!, statement.Qualifier, statement.File);
            var target = type is null ? null : to.Find(type.FullName);
            if (type?.Kind != "enum" || target is null)
                continue;
            var added = target.Members.Except(type.Members, StringComparer.Ordinal).Except(statement.Values, StringComparer.Ordinal).ToList();
            var gaps = type.Members.Where(value => value is not ("Unknown" or "None"))
                .Except(statement.Values, StringComparer.Ordinal).ToList();
            Coverage(statement, type.Name, "enum", statement.Values.Count * 2 >= type.Members.Count ? gaps : [], added);
        }
        changes += to.Types.Where(t => t.Kind == "enum")
            .Sum(t => from.Find(t.FullName) is { } old ? t.Members.Except(old.Members, StringComparer.Ordinal).Count() : 0);

        foreach (var statement in index.Switches.Where(s => s.EnumType is null))
        {
            var family = statement.Values
                .SelectMany(value => from.Types.Where(type => type.Kind == "class"
                    && (type.Name == value || type.Name == value + "BiddingScheme")))
                .Select(type => from.Base(type)).Where(parent => parent is not null && parent.Namespace.StartsWith("Microsoft.BingAds", StringComparison.Ordinal))
                .GroupBy(parent => parent!.FullName).Where(group => group.Count() >= 2)
                .Select(group => group.First()!).FirstOrDefault();
            if (family is null)
                continue;
            bool Handled(MsType type) => statement.Values.Contains(type.Name, StringComparer.Ordinal)
                || statement.Values.Contains(type.Name.Replace(family.Name, "", StringComparison.Ordinal), StringComparer.Ordinal);
            var members = from.Types.Where(type => type.Kind == "class" && from.DerivesFrom(type, family.Name)).ToList();
            var gaps = members.Where(type => !Handled(type)).Select(type => type.Name).ToList();
            var added = addedTypes.Where(type => type.Kind == "class" && to.DerivesFrom(type, family.Name) && !Handled(type))
                .Select(type => type.Name).ToList();
            Coverage(statement, family.Name, "subtype", (members.Count - gaps.Count) * 2 >= members.Count ? gaps : [], added);
        }

        // 7. Microsoft Ads faults swallowed around SDK calls.
        foreach (var swallowed in index.SwallowedCatches)
            Add("RESILIENCE", swallowed.Kind == "log-only" ? "low" : "medium", "swallowed Microsoft Ads API error",
                swallowed.File, swallowed.Line,
                $"{swallowed.Enclosing}: catch ({swallowed.CaughtType}) swallows a Microsoft Ads API failure ({swallowed.Kind}); "
                + "after an SDK or API change the call fails and this code carries on with no data. Rethrow, or record the failure so the run fails loudly.",
                [$"Catch: {swallowed.File}:{swallowed.Line} ({swallowed.Enclosing})", swallowed.CallLine],
                confidence: swallowed.CaughtType.Contains("Fault", StringComparison.Ordinal) ? "high" : "medium");

        // 8. Dated rule: Max CPC on non-portfolio campaigns (verified announcement, 2026-09-23).
        // Stays visible after the date: a passed deadline with the code still present is a live break, not a resolved one.
        {
            string when = now < MaxCpcDate ? $"From {MaxCpcDate:yyyy-MM-dd}" : $"Since {MaxCpcDate:yyyy-MM-dd}";
            string maxCpcCategory = now < MaxCpcDate ? "UPCOMING_BREAK" : "BREAKING_RUNTIME";
            foreach (var use in index.Members.Where(use => use.Member == "MaxCpc" && use.Assigned && MaxCpcSchemes.Contains(use.Type)))
                Add(maxCpcCategory, "high", $"{use.Type}.MaxCpc", use.File, use.Line,
                    $"{when}, creating or updating a non-portfolio campaign whose {use.Type} sets Max CPC fails with an explicit API error. "
                    + "Portfolio bid strategies and existing campaigns keep it. Stop sending MaxCpc for non-portfolio campaigns or use a portfolio strategy.",
                    [$"Sets MaxCpc: {use.File}:{use.Line} ({use.Via} in {use.Enclosing})", "Source: " + MaxCpcSource],
                    "ms-rule:max-cpc-2027-01-12", "medium");
            foreach (var use in index.Strings.Where(use => use.Value == "Bid Strategy MaxCpc"))
                Add(maxCpcCategory, "medium", "Bid Strategy MaxCpc", use.File, use.Line,
                    $"{when}, bulk uploads that set \"Bid Strategy MaxCpc\" on non-portfolio Target CPA/ROAS or Max Conversions/Conversion Value/Clicks campaigns fail. Check whether this column is written.",
                    [$"Bulk column: {use.File}:{use.Line} ({use.Context})", "Source: " + MaxCpcSource],
                    "ms-rule:max-cpc-2027-01-12", "medium");
        }

        // 9. SOAP retirement (2027-01-31): SDK < 13.0.22 still calls SOAP; raw .svc endpoints always do.
        bool soapSdk = MicrosoftSnapshot.Compare(from.Version, "13.0.22") < 0;
        var (installed, sdkLine, sdkFile) = CheckEngine.PackageVersion(repo, Package, propsPath);
        Add("SUNSET", soapSdk ? "high" : "info", "SOAP API", sdkFile, sdkLine,
            soapSdk
                ? $"{Package} {from.Version} calls the SOAP API, which is fully retired on {SoapRetirement:yyyy-MM-dd}. Upgrade to 13.0.22 or later (REST)."
                : $"{Package} {from.Version} already calls the REST API (13.0.22+). SOAP is fully retired on {SoapRetirement:yyyy-MM-dd}; only direct SOAP calls are affected.",
            ["Source: " + RestSource], "ms-rule:soap-2027-01-31", count: 0);
        foreach (var url in index.Urls.Where(url => url.Url.Contains(".svc", StringComparison.OrdinalIgnoreCase)))
            Add("SUNSET", "high", "SOAP endpoint", url.File, url.Line,
                $"Direct SOAP endpoint {url.Url} stops working when SOAP is retired on {SoapRetirement:yyyy-MM-dd}. Use the SDK or the REST URL.",
                [$"URL: {url.File}:{url.Line}", "Source: " + RestSource], "ms-rule:soap-2027-01-31");
        // Only API hosts: OAuth scope strings such as https://ads.microsoft.com/msads.manage are not calls.
        foreach (var url in index.Urls.Where(url => !url.Url.Contains(".svc", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(url.Url.Replace("{", "").Replace("}", ""), UriKind.Absolute, out var parsed)
            && parsed.Host.Contains("api.", StringComparison.OrdinalIgnoreCase)))
            Add("UNKNOWN", "low", "direct Microsoft Ads HTTP call", url.File, url.Line,
                $"{url.Url} is called directly, outside the SDK, so SDK upgrades and SDK diffs don't cover it. Track its API version separately.",
                [$"URL: {url.File}:{url.Line}"], "ms-direct-http", "medium");

        // 10. SDK internals: not a public contract.
        foreach (string file in index.InternalNamespaceFiles.Distinct(StringComparer.Ordinal))
            Add("COMPAT", "low", "Microsoft.BingAds.Internal", file, 0,
                "Imports Microsoft.BingAds.Internal, the SDK's internal namespace; it can change in any release without notice.",
                [$"File: {file}"], "ms-internal-namespace", "medium", count: 0);

        // 11. Comments pinned to an SDK version: re-verify on upgrade.
        foreach (var pinned in index.PinnedComments.Where(p => upgrade && MicrosoftSnapshot.Compare(p.Version, to.Version) < 0))
            Add("CLEANUP", "info", "SDK-version comment", pinned.File, pinned.Line,
                $"Comment is pinned to SDK {pinned.Version}; re-check it against {to.Version} when upgrading.",
                [pinned.Text], "ms-pinned-comment");

        // 12. The upgrade itself and its dependency requirements.
        if (upgrade)
        {
            var notes = between.Where(schema => MicrosoftSnapshot.Compare(schema.Version, from.Version) > 0)
                .OrderBy(schema => System.Version.Parse(schema.Version))
                .SelectMany(schema => schema.ReleaseNotes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(line => line.StartsWith('*'))
                    .Select(line => $"{schema.Version}: {line.TrimStart('*', ' ')}"))
                .ToList();
            Add("MIGRATION", "medium", Package, sdkFile, sdkLine,
                $"Upgrade {Package} {installed ?? from.Version} → {to.Version}. {index.FilesScanned} files use the SDK; "
                + $"{removedTypes.Count} types and {membersRemoved} used members are removed, {addedTypes.Count} types are new.",
                notes.Take(30).ToList(), "ms-upgrade", count: 0);
        }
        foreach (var schema in new[] { to }) // only the target: the installed version already restores
            foreach (var (id, required) in schema.Dependencies)
            {
                var (pinned, line, pinFile) = CheckEngine.PackageVersion(repo, id, propsPath);
                if (pinned is null || !System.Version.TryParse(pinned, out var have)
                    || !System.Version.TryParse(required, out var need) || have >= need)
                    continue;
                Add("COMPAT", "high", id, pinFile, line,
                    $"{Package} {schema.Version} needs {id} >= {required}, but {pinFile} pins {pinned}; restore fails with a package downgrade (NU1605).",
                    [$"Dependency of {Package} {schema.Version}: {id} {required}"], "ms-dependency:" + id);
            }

        var grouped = findings
            .GroupBy(f => (f.Category, f.ChangeId, f.File, f.Path, f.Message))
            .Select(group => group.OrderBy(f => ReportFindings.SeverityOrder(f.Severity)).First() with
            {
                Lines = group.SelectMany(f => f.Lines).Distinct().Order().ToList(),
                UseCount = group.Sum(f => f.UseCount),
                Evidence = group.SelectMany(f => f.Evidence).Distinct(StringComparer.Ordinal).Take(30).ToList(),
            })
            .OrderBy(f => ReportFindings.CategoryOrder(f.Category))
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ToList();

        var limitations = new List<string>
        {
            "Types are resolved from Microsoft.BingAds usings and aliases; global usings and chained member access (a.B.C) are not followed.",
            "Report-column and bulk-header strings are matched by exact value; strings built at runtime are not seen.",
            "Microsoft has no validate-only mode; findings are static. Use the sandbox (api.sandbox.bingads.microsoft.com) to confirm.",
            "SDK surface is compared by type and member name; a member whose signature changed under the same name is not detected.",
        };
        if (!from.BulkHeadersKnown || !to.BulkHeadersKnown)
            limitations.Add("Bulk headers unavailable for one SDK version (source tag not found); bulk column changes not checked.");
        if (upgrade)
            limitations.Add($"New in {to.Version}: {addedTypes.Count} types, e.g. {string.Join(", ", addedTypes.Take(8).Select(t => t.Name))}.");
        else
            limitations.Add($"{Package} {from.Version} is the newest release; only dated rules and resilience checks apply.");

        return new CheckReport
        {
            Platform = "Microsoft Ads",
            Target = $"SDK {from.Version}",
            Next = upgrade ? [to.Version] : [],
            Findings = grouped,
            Counts = ReportFindings.Categories.ToDictionary(c => c, c => grouped.Count(f => f.Category == c)),
            Funnel = new Dictionary<string, int>
            {
                ["changesInCatalog"] = changes,
                ["changesTouchingCode"] = grouped.Where(f => f.ChangeId is not null).Select(f => f.ChangeId).Distinct().Count(),
                ["findings"] = grouped.Count,
                ["actionable"] = grouped.Count(ReportFindings.IsActionable),
                ["possiblyHandled"] = 0,
                ["files"] = grouped.Where(f => f.Line > 0).Select(f => f.File).Distinct(StringComparer.Ordinal).Count(),
            },
            UnknownDefinitionsByOwner = new(),
            Limitations = limitations,
            Announcements = announcements?.ToList() ?? [],
        };
    }

    private static IEnumerable<string> AllMembers(MicrosoftSchema schema, MsType type)
    {
        for (MsType? current = type; current is not null; current = schema.Base(current))
            foreach (string member in current.Members)
                yield return member;
    }

    // Token overlap of CamelCase / space-separated words (e.g. "Bid Strategy MaxCpc" vs "Bid Strategy Max Cpc").
    internal static double Similarity(string left, string right)
    {
        static HashSet<string> Words(string value) => System.Text.RegularExpressions.Regex
            .Matches(value, "[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])")
            .Select(match => match.Value.ToLowerInvariant()).ToHashSet();
        var a = Words(left);
        var b = Words(right);
        if (a.Count == 0 || b.Count == 0 || left == right)
            return 0;
        return (double)a.Intersect(b).Count() / a.Union(b).Count();
    }
}
