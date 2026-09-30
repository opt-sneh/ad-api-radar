using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class TypedScanTests
{
    [TestMethod]
    public void TypedIndexResolvesAliasesAssignmentsInitializersAndIgnoresComments()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Typed.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            using static Google.Ads.GoogleAds.V98.Enums.CriterionTypeEnum.Types;
            using CriterionAlias = Google.Ads.GoogleAds.V98.Enums.CriterionTypeEnum.Types.CriterionType;
            class Example {
              void Copy(CampaignCriterion criterion) {
                var cpnCriterion = new CampaignCriterion();
                cpnCriterion.Language = criterion.Language;
                var other = new CampaignCriterion { Language = 1 };
                var a = CriterionType.Language;
                var b = CriterionAlias.Language;
                // CriterionType.Removed and cpnCriterion.Removed must not be indexed.
              }
            }
            """);
        var index = fixture.Index();
        Assert.AreEqual(2, index.EnumMembers.Count(e => e.EnumType == "CriterionTypeEnum.Types.CriterionType"
            && e.Value == "Language"));
        Assert.IsTrue(index.TypedMembers.Any(m => m.ReceiverHint == "CampaignCriterion"
            && m.Member == "Language" && m.Confidence == "high" && m.Enclosing == "Copy"));
        // Local `new`, method parameter, and object initializer.
        Assert.AreEqual(3, index.TypedMembers.Count(m => m.ReceiverHint == "CampaignCriterion"
            && m.Member == "Language"));
        Assert.IsFalse(index.EnumMembers.Any(m => m.Value == "Removed"));
        Assert.IsFalse(index.TypedMembers.Any(m => m.Member == "Removed"));
    }

    [TestMethod]
    public void GaqlEnumFiltersProduceRuntimeAndSilentRiskFindings()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Filters.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Filters {
              string Query() => "SELECT campaign.status FROM campaign WHERE campaign.status IN ('REMOVED','BOGUS')";
            }
            """);
        var index = fixture.Index();
        var filter = index.GaqlEnumFilters.Single();
        Assert.AreEqual("IN", filter.Operator);
        CollectionAssert.AreEqual(new[] { "REMOVED", "BOGUS" }, filter.Values);
        fixture.WithModels((before, after) =>
        {
            var added = Change("ENUM_VALUE_ADDED", "Status.ADDED", "StatusEnum.Types.Status.Added");
            var report = Report(index, fixture.Root, before, [before, after], [after], [added]);
            Assert.IsTrue(report.Findings.Any(f => f.Category == "BREAKING_RUNTIME"
                && f.Message.Contains("'BOGUS'", StringComparison.Ordinal)));
            Assert.IsTrue(report.Findings.Any(f => f.Category == "SILENT_RISK"
                && f.Message.Contains("new value ADDED", StringComparison.Ordinal)));
        });
    }

    [TestMethod]
    public void NewEnumValueMattersMoreForExclusionFilters()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Only.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Only { string Q() => "SELECT campaign.status FROM campaign WHERE campaign.status = 'REMOVED'"; }
            """);
        fixture.Add("Except.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Except { string Q() => "SELECT campaign.status FROM campaign WHERE campaign.status NOT IN ('REMOVED')"; }
            """);
        fixture.Add("Both.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Both {
              string A() => "SELECT campaign.status FROM campaign WHERE campaign.status = 'REMOVED'";
              string B() => "SELECT campaign.status FROM campaign WHERE campaign.status NOT IN ('REMOVED')";
            }
            """);
        var index = fixture.Index();
        CollectionAssert.AreEquivalent(new[] { "=", "NOT IN", "=", "NOT IN" }, index.GaqlEnumFilters.Select(f => f.Operator).ToArray());
        fixture.WithModels((before, after) =>
        {
            var added = Change("ENUM_VALUE_ADDED", "Status.ADDED", "StatusEnum.Types.Status.Added");
            var report = Report(index, fixture.Root, before, [before, after], [after], [added]);
            var risks = report.Findings.Where(f => f.Category == "SILENT_RISK"
                && f.Message.Contains("new value ADDED", StringComparison.Ordinal)).ToList();
            Assert.AreEqual("low", risks.Single(f => f.File == "Only.cs").Severity);
            Assert.AreEqual("medium", risks.Single(f => f.File == "Except.cs").Severity);
            Assert.AreEqual("medium", risks.Single(f => f.File == "Both.cs").Severity, "merged row keeps the worst severity");
        });
    }

    [TestMethod]
    public void GaqlFiltersInJoinAndConcatenationAreIndexed()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Assembled.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Assembled {
              string Joined() => string.Join("", new[] { "SELECT campaign.status FROM campaign",
                " WHERE campaign.status IN ('REMOVED','ADDED')" });
              string Added() => "SELECT campaign.status FROM campaign"
                + " WHERE campaign.status = 'REMOVED'";
            }
            """);
        var filters = fixture.Index().GaqlEnumFilters;
        Assert.IsTrue(filters.Any(filter => filter.Operator == "IN"
            && filter.Values.SequenceEqual(new[] { "REMOVED", "ADDED" })));
        Assert.IsTrue(filters.Any(filter => filter.Operator == "="
            && filter.Values.SequenceEqual(new[] { "REMOVED" })));
    }

    [TestMethod]
    public void RemovedTypedFieldUsesSuggestionAndGroupsLines()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Copy.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Copy {
              void Apply() {
                var campaign = new Campaign();
                campaign.RemovedField = "one";
                campaign.RemovedField = "two";
              }
            }
            """);
        var index = fixture.Index();
        fixture.WithModels((before, after) =>
        {
            var change = Change("FIELD_RENAMED_CANDIDATE", "campaign.removed_field", "RemovedField",
                "campaign.removed_field", "resources.Campaign.added_field");
            var report = Report(index, fixture.Root, before, [before, after], [after], [change]);
            var finding = report.Findings.Single(f => f.Category == "UPCOMING_BREAK"
                && f.ChangeId == change.Id && f.Path == "Campaign.RemovedField");
            Assert.AreEqual("resources.Campaign.added_field", finding.SuggestedReplacement);
            Assert.HasCount(2, finding.Lines);
            Assert.AreEqual(2, finding.UseCount);
        });
    }

    [TestMethod]
    public void ConditionalAccessAndUntypedChainsFindRemovedMembersOnlyWithSdkUsing()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Sdk.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Example {
              void Copy() {
                System.Func<dynamic, string> video = x => x.Campaign?.VideoBrandSafetySuitability.ToString();
                System.Func<dynamic, object> removed = x => x.Campaign?.RemovedField;
                System.Func<dynamic, object> direct = x => x.Campaign.RemovedField;
                System.Func<dynamic, object> nested = row => row.AdGroupAd?.Ad?.RemovedField;
              }
            }
            """);
        fixture.Add("Other.cs", """
            class Other { void Copy() { System.Func<dynamic, object> removed = x => x.Campaign?.RemovedField; } }
            """);
        var index = fixture.Index();
        Assert.IsTrue(index.TypedMembers.Any(m => m.File == "Sdk.cs"
            && m.Member == "VideoBrandSafetySuitability" && m.ReceiverHint.EndsWith("Campaign", StringComparison.Ordinal)
            && m.Confidence == "medium"));
        Assert.IsTrue(index.TypedMembers.Any(m => m.File == "Sdk.cs" && m.Member == "RemovedField"
            && m.ReceiverHint.EndsWith("Ad", StringComparison.Ordinal)));
        Assert.AreEqual(2, index.TypedMembers.Count(m => m.File == "Sdk.cs" && m.Member == "RemovedField"
            && m.ReceiverHint == "x.Campaign"));
        Assert.IsFalse(index.TypedMembers.Any(m => m.File == "Other.cs" && m.Member == "RemovedField"));
        fixture.WithModels((before, after) =>
        {
            var change = Change("FIELD_REMOVED", "campaign.removed_field", "RemovedField", "campaign.removed_field");
            var report = Report(index, fixture.Root, before, [before, after], [after], [change]);
            Assert.IsTrue(report.Findings.Any(f => f.Category == "UPCOMING_BREAK" && f.ChangeId == change.Id
                && f.File == "Sdk.cs" && f.Path == "x.Campaign.RemovedField"));
        });
    }

    [TestMethod]
    public void OlderTargetReportsSdkNamespaceAndGeneratedCodeMigration()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Using.cs", "using Google.Ads.GoogleAds.V98.Resources; class Using { }");
        fixture.Add("Generated.cs", """
            // Generated by the protocol buffer compiler
            using Google.Ads.GoogleAds.V98.Resources;
            class Generated { }
            """);
        string props = Path.Combine(fixture.Root, "code", "backend", "Directory.Packages.props");
        Directory.CreateDirectory(Path.GetDirectoryName(props)!);
        File.WriteAllText(props, """
            <Project><ItemGroup>
              <PackageVersion Include="Google.Ads.GoogleAds" Version="98.1.0" />
              <PackageVersion Include="Google.Protobuf" Version="3.28.0" />
            </ItemGroup></Project>
            """);
        var index = fixture.Index();
        fixture.WithModels((before, after) =>
        {
            var deps = new[]
            {
                new SdkDependencies("98.1.0", [], ["Google.Protobuf [3.25.0,4.0.0)"], "[3.25.0,4.0.0)"),
                new SdkDependencies("99.1.0", [], ["Google.Ads.GoogleAds 99.1.0", "Google.Protobuf [3.30.0,4.0.0)"], "[3.30.0,4.0.0)"),
            };
            var report = CheckEngine.Run(index, fixture.Root, before, [before, after], [after], [], deps,
                [], "98.1.0", new DateOnly(2026, 9, 29), []);
            var migration = report.Findings.Where(f => f.Category == "MIGRATION").ToArray();
            Assert.HasCount(3, migration);
            var sdk = migration.Single(f => f.Path == "Google.Ads.GoogleAds");
            Assert.AreEqual("code/backend/Directory.Packages.props", sdk.File);
            Assert.AreEqual(2, sdk.Line);
            StringAssert.Contains(sdk.Message, "98.1.0 → 99.1.0 for API v99");
            Assert.IsTrue(sdk.Evidence.Any(e => e.Contains("does not satisfy required [3.30.0,4.0.0)", StringComparison.Ordinal)));
            var namespaces = migration.Single(f => f.Path == "Google.Ads.GoogleAds.V98");
            Assert.AreEqual("Generated.cs", namespaces.File);
            StringAssert.Contains(namespaces.Message, "2 files reference Google.Ads.GoogleAds.V98; move them to V99.");
            var generated = migration.Single(f => f.Path == "Generated.cs");
            StringAssert.Contains(generated.Message, "regenerate its .proto against v99");
            Assert.AreEqual(3, report.Counts["MIGRATION"]);
            Assert.IsFalse(ReportFindings.IsActionable(sdk));
            StringAssert.Contains(CheckHtml.Render(report), "<code>MIGRATION</code>");
        });
    }

    [TestMethod]
    public void BehaviourMemberRequiresActualUseAndReportsGuardHint()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Copy.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Copy {
              void Apply(CampaignCriterion criterion) {
                var copy = new CampaignCriterion();
                copy.Language = criterion.Language;
              }
            }
            """);
        fixture.Add("Guarded.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Guarded {
              bool SupportsLanguageTargeting() => false;
              void Apply() {
                var copy = new CampaignCriterion();
                if (SupportsLanguageTargeting()) copy.Language = 1;
              }
            }
            """);
        fixture.Add("TypeOnly.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class TypeOnly { void Apply() { var copy = new CampaignCriterion(); } }
            """);
        var index = fixture.Index();
        var row = new DeprecationRow(null, "September 2026", "CampaignCriterion.language",
            "Behavioral shift", "CampaignCriterion.language is rejected", []);
        fixture.WithModels((before, after) =>
        {
            var report = CheckEngine.Run(index, fixture.Root, before, [before, after], [], [], [],
                [row], "", new DateOnly(2026, 9, 29), []);
            var behaviour = report.Findings.Where(f => f.Category == "SILENT_RISK"
                && f.Message.StartsWith("Behavior check:", StringComparison.Ordinal)).ToArray();
            Assert.IsTrue(behaviour.Any(f => f.File == "Copy.cs"));
            Assert.IsFalse(behaviour.Any(f => f.File == "TypeOnly.cs"));
            Assert.IsTrue(behaviour.Any(f => f.File == "Guarded.cs" && f.PossiblyHandled));
        });
    }

    [TestMethod]
    public void SwallowedGoogleAdsCatchesReportResilienceOnlyWhenFailureIsHidden()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Catches.cs", """
            using Google.Ads.GoogleAds.V98.Services;
            class Catches {
              void Empty(dynamic client) {
                try { client.Search("query"); }
                catch (GoogleAdsException) { }
              }
              void Rethrows(dynamic client) {
                try { client.Search("query"); }
                catch (GoogleAdsException) { throw; }
              }
              System.Collections.Generic.List<string> ReturnsEmpty(dynamic client) {
                try { client.Search("query"); }
                catch (RpcException) { Log("failure"); return new System.Collections.Generic.List<string>(); }
                return new System.Collections.Generic.List<string>();
              }
              void LogsOnly(dynamic client) {
                try { client.Search("query"); }
                catch (RpcException) { Log("failure"); }
              }
              void ServiceMethod(SearchServiceClient client) {
                try { client.Execute(); }
                catch (GoogleAdsException) { }
              }
              void Bare(dynamic client) {
                try { client.Search("query"); }
                catch { }
              }
              void Cancelled(dynamic client) {
                try { client.Search("query"); }
                catch (OperationCanceledException) { }
              }
              void Other() {
                try { DoWork(); }
                catch (GoogleAdsException) { }
              }
              void Log(string text) { }
              void DoWork() { }
            }
            """);
        var index = fixture.Index();
        Assert.HasCount(5, index.SwallowedCatches);
        CollectionAssert.AreEquivalent(new[] { "empty", "empty", "empty", "return-empty", "log-only" },
            index.SwallowedCatches.Select(catchSite => catchSite.Kind).ToArray());
        fixture.WithModels((before, after) =>
        {
            var report = Report(index, fixture.Root, before, [before, after], [after], []);
            var resilience = report.Findings.Where(finding => finding.Category == "RESILIENCE").ToArray();
            Assert.HasCount(5, resilience);
            Assert.IsTrue(resilience.All(finding => finding.Path == "swallowed Google Ads API error"));
            Assert.AreEqual("medium", resilience.Single(finding => finding.Message.Contains("Bare: catch ()", StringComparison.Ordinal)).Confidence);
            Assert.IsTrue(resilience.Where(finding => !finding.Message.Contains("Bare: catch ()", StringComparison.Ordinal))
                .All(finding => finding.Confidence == "high"));
            Assert.AreEqual("low", resilience.Single(finding => finding.Message.Contains("(log-only)", StringComparison.Ordinal)).Severity);
            Assert.IsTrue(resilience.Where(finding => !finding.Message.Contains("(log-only)", StringComparison.Ordinal))
                .All(finding => finding.Severity == "medium"));
            Assert.IsTrue(resilience.All(finding => finding.Evidence.Any(e => e.StartsWith("Catch:", StringComparison.Ordinal))));
            Assert.IsTrue(resilience.Any(finding => finding.Evidence.Any(e => e.Contains("client.Execute", StringComparison.Ordinal))));
            Assert.IsTrue(resilience.All(finding => !ReportFindings.IsActionable(finding)));
            StringAssert.Contains(CheckHtml.Render(report), "<code>RESILIENCE</code>");
        });
    }

    [TestMethod]
    public void EnumSwitchReportsNewValueUnlessItIsCovered()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Switches.cs", """
            using Google.Ads.GoogleAds.V98.Enums;
            using static Google.Ads.GoogleAds.V98.Enums.CriterionTypeEnum.Types;
            using CriterionAlias = Google.Ads.GoogleAds.V98.Enums.CriterionTypeEnum.Types.CriterionType;
            class Switches {
              string Missing(CriterionTypeEnum.Types.CriterionType value) {
                switch (value) {
                  case CriterionType.Language: return "language";
                  default: return "other";
                }
              }
              string Covered(CriterionTypeEnum.Types.CriterionType value) => value switch {
                CriterionAlias.Language => "language",
                CriterionAlias.Search => "search",
                _ => "other"
              };
            }
            """);
        var index = fixture.Index();
        Assert.HasCount(2, index.EnumSwitches);
        fixture.WithModels((before, after) =>
        {
            var changes = ChangeCatalog.Compare(before, after);
            var added = changes.Single(change => change.Kind == "ENUM_VALUE_ADDED"
                && change.Symbol == "CriterionType.SEARCH");
            var report = Report(index, fixture.Root, before, [before, after], [after], changes);
            var risk = report.Findings.Single(finding => finding.Category == "SILENT_RISK"
                && finding.Path == "CriterionTypeEnum.Types.CriterionType");
            Assert.AreEqual(added.Id, risk.ChangeId);
            StringAssert.Contains(risk.Message, "Missing");
            StringAssert.Contains(risk.Message, "Search");
            StringAssert.Contains(risk.Message, "default");
            Assert.IsFalse(report.Findings.Any(finding => finding.Category == "SILENT_RISK"
                && finding.Message.Contains("Covered", StringComparison.Ordinal)));
            var noNext = Report(index, fixture.Root, before, [before, after], [], changes);
            Assert.IsFalse(noNext.Findings.Any(finding => finding.Category == "SILENT_RISK"
                && finding.Path == "CriterionTypeEnum.Types.CriterionType"));
        });
    }

    [TestMethod]
    public void UnfilteredEnumRowsAreExperimentalAndRequireNeitherSelectNorFilter()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Queries.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Queries {
              string Unfiltered() => "SELECT campaign.id FROM campaign";
              string Selected() => "SELECT campaign.status FROM campaign";
              string Filtered() => "SELECT campaign.id FROM campaign WHERE campaign.status = 'REMOVED'";
            }
            """);
        var index = fixture.Index();
        fixture.WithModels((before, after) =>
        {
            var changes = ChangeCatalog.Compare(before, after);
            var defaultReport = Report(index, fixture.Root, before, [before, after], [after], changes);
            Assert.IsFalse(defaultReport.Findings.Any(finding => finding.Message.StartsWith("Query on", StringComparison.Ordinal)));
            Assert.AreEqual(0, defaultReport.Limitations.Count(limitation =>
                limitation == "EXPERIMENTAL unfiltered-enum rule (low confidence)."));
            Assert.IsTrue(before.Fields["campaign.statuses"].Repeated);
            var report = Report(index, fixture.Root, before, [before, after], [after], changes,
                experimental: true);
            var findings = report.Findings.Where(finding => finding.Category == "SILENT_RISK"
                && finding.Path == "campaign.status" && finding.Message.StartsWith("Query on", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, findings);
            Assert.IsFalse(report.Findings.Any(finding => finding.Path == "campaign.statuses"
                && finding.Message.StartsWith("Query on", StringComparison.Ordinal)));
            Assert.AreEqual("low", findings[0].Severity);
            Assert.AreEqual("low", findings[0].Confidence);
            StringAssert.Contains(findings[0].Message, "ADDED");
            CollectionAssert.Contains(report.Limitations, "EXPERIMENTAL unfiltered-enum rule (low confidence).");

            string google = Path.Combine(fixture.Root, "google");
            foreach (string version in new[] { "v98", "v99" })
                File.Copy(Path.Combine(fixture.Root, version + ".pb"),
                    Path.Combine(google, version + ".pb"));
            foreach (string name in new[] { "sunset", "sdk-deps", "deprecations" })
                File.WriteAllText(Path.Combine(google, name + ".json"), "[]");
            string indexPath = Path.Combine(fixture.Root, "index.json");
            string changesPath = Path.Combine(fixture.Root, "changes.json");
            string outputPath = Path.Combine(fixture.Root, "findings.json");
            File.WriteAllText(indexPath, System.Text.Json.JsonSerializer.Serialize(index));
            File.WriteAllText(changesPath, System.Text.Json.JsonSerializer.Serialize(new { changes }));
            var args = new List<string> { "--index", indexPath, "--repo", fixture.Root,
                "--snapshots", fixture.Root, "--target", "v98", "--next", "v99",
                "--changes", changesPath, "--out", outputPath,
                "--html", Path.Combine(fixture.Root, "report.html") };
            Assert.AreEqual(0, CheckCommand.Run(args.ToArray()));
            Assert.IsFalse(File.ReadAllText(outputPath).Contains("neither selects nor filters", StringComparison.Ordinal));
            args.Add("--experimental");
            Assert.AreEqual(0, CheckCommand.Run(args.ToArray()));
            string enabledJson = File.ReadAllText(outputPath);
            StringAssert.Contains(enabledJson, "neither selects nor filters campaign.status");
            Assert.IsFalse(enabledJson.Contains("neither selects nor filters campaign.statuses", StringComparison.Ordinal));
        });
    }

    [TestMethod]
    public void BehaviourRuleDowngradesCampaignTypesTheRowDoesNotName()
    {
        using var fixture = new SourceFixture();
        fixture.Add("App.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class App {
              void Apply() {
                var type = AdvertisingChannelType.MultiChannel;
                var copy = new CampaignCriterion();
                copy.Language = 1;
              }
            }
            """);
        fixture.Add("Pmax.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Pmax {
              void Apply() {
                var type = AdvertisingChannelType.PerformanceMax;
                var copy = new CampaignCriterion();
                copy.Language = 1;
              }
            }
            """);
        var index = fixture.Index();
        var row = new DeprecationRow(null, "September 2026", "CampaignCriterion.language",
            "Behavioral shift", "CampaignCriterion.language is no longer supported for Search and Performance Max", []);
        fixture.WithModels((before, after) =>
        {
            var report = CheckEngine.Run(index, fixture.Root, before, [before, after], [], [], [],
                [row], "", new DateOnly(2026, 9, 29), []);
            Finding Behaviour(string file) => report.Findings.Single(f => f.Category == "SILENT_RISK" && f.File == file
                && f.Message.StartsWith("Behavior check:", StringComparison.Ordinal));
            Assert.AreEqual("low", Behaviour("App.cs").Severity);
            Assert.IsTrue(Behaviour("App.cs").Evidence.Any(e => e.Contains("MultiChannel", StringComparison.Ordinal)));
            Assert.AreEqual("medium", Behaviour("Pmax.cs").Severity);
            Assert.IsFalse(ReportFindings.IsActionable(Behaviour("App.cs")), "likely-unaffected findings are not queued");
            Assert.IsTrue(ReportFindings.IsActionable(Behaviour("Pmax.cs")));
        });
    }

    [TestMethod]
    public void PackageVersionFallsBackToCsprojWithoutCentralProps()
    {
        // Outside the repo: other tests use the repo root as "repo" and would see this project.
        string root = Path.Combine(Path.GetTempPath(), "radar-" + Guid.NewGuid().ToString("N"));
        try
        {
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Google.Ads.GoogleAds.Extensions" Version="2.0.6" />
                <PackageReference Include="Google.Ads.GoogleAds" Version="25.1.0" />
              </ItemGroup>
            </Project>
            """);
        Directory.CreateDirectory(Path.Combine(root, "a", "sample"));
        File.WriteAllText(Path.Combine(root, "a", "sample", "Sample.csproj"),
            "<Project><ItemGroup><PackageReference Include=\"Google.Ads.GoogleAds\" Version=\"1.0.0\" /></ItemGroup></Project>");
        var (version, line, file) = CheckEngine.PackageVersion(root, "Google.Ads.GoogleAds");
        Assert.AreEqual("25.1.0", version);
        Assert.AreEqual(4, line);
        Assert.AreEqual("app/App.csproj", file, "shallowest project wins over a deeper sample");
        Assert.AreEqual("", CheckEngine.PackageVersion(root, "No.Such.Package").File);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void DisabledGuardDoesNotMarkBehaviourAsHandled()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Disabled.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Disabled {
              public const bool GoogleEnforcesSearchLanguageRemoval = false;
              static readonly bool Secondary = false;
              bool SupportsLanguageTargeting() => !GoogleEnforcesSearchLanguageRemoval;
              void Apply() {
                const bool LocalOff = false;
                var copy = new CampaignCriterion();
                if (SupportsLanguageTargeting()) copy.Language = 1;
              }
            }
            """);
        fixture.Add("Enabled.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Enabled {
              public const bool GoogleEnforcesSearchLanguageRemoval = true;
              bool SupportsLanguageTargeting() => !GoogleEnforcesSearchLanguageRemoval;
              void Apply() {
                var copy = new CampaignCriterion();
                if (SupportsLanguageTargeting()) copy.Language = 1;
              }
            }
            """);
        var index = fixture.Index();
        Assert.IsTrue(index.DisabledFlags.Any(flag => flag.Name == "GoogleEnforcesSearchLanguageRemoval"
            && flag.File == "Disabled.cs"));
        Assert.IsTrue(index.DisabledFlags.Any(flag => flag.Name == "Secondary" && flag.File == "Disabled.cs"));
        Assert.IsTrue(index.DisabledFlags.Any(flag => flag.Name == "LocalOff" && flag.File == "Disabled.cs"));
        var row = new DeprecationRow(null, "September 2026", "CampaignCriterion.language",
            "Behavioral shift", "CampaignCriterion.language is rejected", []);
        fixture.WithModels((before, after) =>
        {
            var report = CheckEngine.Run(index, fixture.Root, before, [before, after], [], [], [],
                [row], "", new DateOnly(2026, 9, 29), []);
            var disabled = report.Findings.Single(finding => finding.Category == "SILENT_RISK"
                && finding.File == "Disabled.cs" && finding.Message.StartsWith("Behavior check:", StringComparison.Ordinal));
            Assert.IsFalse(disabled.PossiblyHandled);
            Assert.IsTrue(disabled.Evidence.Any(e => e.StartsWith("Guard disabled: GoogleEnforcesSearchLanguageRemoval = false", StringComparison.Ordinal)));
            Assert.IsTrue(report.Findings.Any(finding => finding.Category == "SILENT_RISK"
                && finding.File == "Enabled.cs" && finding.PossiblyHandled));
        });
    }

    [TestMethod]
    public void OneGuardedLocationDoesNotHideAnUnguardedOne()
    {
        using var fixture = new SourceFixture();
        fixture.Add("Mixed.cs", """
            using Google.Ads.GoogleAds.V98.Resources;
            class Mixed {
              public const bool GoogleEnforcesSearchLanguageRemoval = true;
              bool SupportsLanguageTargeting() => !GoogleEnforcesSearchLanguageRemoval;
              void Guarded() {
                var copy = new CampaignCriterion();
                if (SupportsLanguageTargeting()) copy.Language = 1;
              }
              void Unguarded() {
                var copy = new CampaignCriterion();
                copy.Language = 1;
              }
            }
            """);
        var index = fixture.Index();
        var row = new DeprecationRow(null, "September 2026", "CampaignCriterion.language",
            "Behavioral shift", "CampaignCriterion.language is rejected", []);
        fixture.WithModels((before, after) =>
        {
            var report = CheckEngine.Run(index, fixture.Root, before, [before, after], [], [], [],
                [row], "", new DateOnly(2026, 9, 29), []);
            var mixed = report.Findings.Where(finding => finding.Category == "SILENT_RISK"
                && finding.File == "Mixed.cs" && finding.Message.StartsWith("Behavior check:", StringComparison.Ordinal)).ToList();
            Assert.IsNotEmpty(mixed);
            Assert.IsTrue(mixed.Any(finding => !finding.PossiblyHandled && ReportFindings.IsActionable(finding)));
        });
    }

    [TestMethod]
    public void ThousandsOfTypedUsesIndexWithinFiveSeconds()
    {
        using var fixture = new SourceFixture();
        var source = new System.Text.StringBuilder("""
            using Google.Ads.GoogleAds.V98.Resources;
            class Large {
              const bool GuardOff = false;
              bool SupportsLanguageTargeting() => !GuardOff;
            """);
        for (int method = 0; method < 100; method++)
        {
            source.Append("void M").Append(method)
                .Append("(CampaignCriterion criterion) { if (SupportsLanguageTargeting()) {");
            for (int use = 0; use < 30; use++)
                source.Append("criterion.Language = ").Append(use).Append(';');
            source.AppendLine("}}");
        }
        source.Append('}');
        fixture.Add("Large.cs", source.ToString());
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var index = fixture.Index();
        timer.Stop();
        Assert.AreEqual(3000, index.TypedMembers.Count(member => member.Member == "Language"));
        Assert.IsTrue(index.TypedMembers.Where(member => member.Member == "Language")
            .All(member => !member.PossiblyHandled));
        Assert.IsTrue(timer.Elapsed < TimeSpan.FromSeconds(5),
            $"Indexing 3,000 typed uses took {timer.Elapsed.TotalSeconds:F2} s.");
    }

    private static CheckReport Report(CodeIndexResult index, string root, SchemaModel target,
        IReadOnlyList<SchemaModel> snapshots, IReadOnlyList<SchemaModel> next,
        IReadOnlyList<ChangeRecord> changes, bool experimental = false) => CheckEngine.Run(index, root, target,
            snapshots, next, [], [], [], "", new DateOnly(2026, 9, 29), changes, experimental);

    private static ChangeRecord Change(string kind, string symbol, string csharp,
        string? gaql = null, string? suggestion = null) => new(
            kind + ":" + symbol, "v98", "v99", kind, symbol, gaql, csharp,
            null, null, "fixture.proto:5", null, "breaking",
            suggestion is null ? [] : [new ReplacementCandidate(suggestion, 1, "fixture")], [], []);

    private sealed class SourceFixture : IDisposable
    {
        public string Root { get; }
        private readonly List<string> files = [];
        public SourceFixture()
        {
            Root = Path.Combine(RepoRoot(), "data", "test-fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }
        public void Add(string name, string source)
        {
            File.WriteAllText(Path.Combine(Root, name), source);
            files.Add(name);
        }
        public CodeIndexResult Index()
        {
            var project = new ProjectResult { ProjectPath = "Fixture.csproj", Success = true };
            foreach (string file in files)
                project.Files.Add(new CompileFile { FullPath = Path.Combine(Root, file),
                    RelativePath = file, Linked = false });
            var set = new CompileSetResult { Repo = Root, Solution = "Fixture.slnx",
                Configuration = "Debug", Projects = [project] };
            return CodeIndexer.Index(Root, set);
        }
        public void WithModels(Action<SchemaModel, SchemaModel> check)
        {
            string api = Path.Combine(Root, "google", "api", "resource.proto");
            Directory.CreateDirectory(Path.GetDirectoryName(api)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "resource.proto"), api);
            var models = new List<SchemaModel>();
            foreach (string version in new[] { "v98", "v99" })
            {
                foreach (string part in new[] { "", "-common", "-enums", "-services" })
                {
                    string package = part switch { "" => "resources", "-common" => "common",
                        "-enums" => "enums", _ => "services" };
                    string destination = Path.Combine(Root, "google", "ads", "googleads", version,
                        package, version + part + ".proto");
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", version + part + ".proto"), destination);
                }
                string pb = Path.Combine(Root, version + ".pb");
                File.WriteAllBytes(pb, new ProtoCompiler(RepoRoot()).Compile(Root, version, out _));
                models.Add(SchemaModel.Load(pb, version));
            }
            check(models[0], models[1]);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
            directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
