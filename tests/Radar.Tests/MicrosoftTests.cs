using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class MicrosoftTests
{
    private const string Cm = "Microsoft.BingAds.V13.CampaignManagement";
    private const string Rp = "Microsoft.BingAds.V13.Reporting";

    private static MsType Enum(string ns, string name, params string[] values) => new(name, ns, "enum", "System.Enum", [.. values]);
    private static MsType Class(string name, string? baseType, params string[] members) => new(name, Cm, "class", baseType, [.. members]);

    private static MicrosoftSchema From(string version = "13.0.20") => new()
    {
        Version = version,
        Types =
        [
            Enum(Cm, "CampaignType", "Search", "Shopping", "Audience", "Hotel"),
            Enum(Cm, "OldEnum", "A"),
            Enum(Cm, "MatchType", "Broad", "Phrase", "Exact", "Legacy"),
            Enum(Cm, "ProfileType", "CompanyName", "Industry", "JobFunction"),
            Class("BiddingScheme", "System.Object", "Type"),
            Class("MaxConversionsBiddingScheme", Cm + ".BiddingScheme", "MaxCpc", "TargetCpa"),
            Class("ManualCpcBiddingScheme", Cm + ".BiddingScheme"),
            Class("TargetRoasBiddingScheme", Cm + ".BiddingScheme", "MaxCpc", "TargetRoas"),
            Class("Campaign", "System.Object", "Name", "BiddingScheme", "OldProp"),
            Enum(Rp, "CampaignPerformanceReportColumn", "Impressions", "Clicks", "OldColumn"),
            Enum(Rp, "AdPerformanceReportColumn", "Impressions", "AverageCpc"),
            Enum(Rp, "MSClickIdPerformanceReportColumn", "AverageCpc"),
        ],
        BulkHeaders = ["Parent Id", "Bid Strategy MaxCpc", "Old Header Name"],
        BulkHeadersKnown = true,
        Dependencies = new() { ["Newtonsoft.Json"] = "13.0.4" },
    };

    private static MicrosoftSchema To() => new()
    {
        Version = "13.0.30",
        Types =
        [
            Enum(Cm, "CampaignType", "Search", "Shopping", "Audience", "Hotel", "ObjectiveBased"),
            Enum(Cm, "NewEnum", "B"),
            Enum(Cm, "MatchType", "Broad", "Phrase", "Exact"),
            Enum(Cm, "ProfileType", "CompanyName", "Industry", "JobFunction", "JobTitle"),
            Class("BiddingScheme", "System.Object", "Type"),
            Class("MaxConversionsBiddingScheme", Cm + ".BiddingScheme", "MaxCpc", "TargetCpa"),
            Class("ManualCpcBiddingScheme", Cm + ".BiddingScheme"),
            Class("TargetRoasBiddingScheme", Cm + ".BiddingScheme", "MaxCpc", "TargetRoas"),
            Class("MaxReachBiddingScheme", Cm + ".BiddingScheme"),
            Class("Campaign", "System.Object", "Name", "BiddingScheme"),
            Enum(Rp, "CampaignPerformanceReportColumn", "Impressions", "Clicks"),
            Enum(Rp, "AdPerformanceReportColumn", "Impressions", "AverageCpc"),
            Enum(Rp, "MSClickIdPerformanceReportColumn"),
        ],
        BulkHeaders = ["Parent Id", "Bid Strategy MaxCpc", "New Header Name"],
        BulkHeadersKnown = true,
        ReleaseNotes = "API Updates:\n* New campaign type ObjectiveBased.",
        Dependencies = new() { ["Newtonsoft.Json"] = "13.0.4" },
    };

    private const string Worker = """
        using System;
        using Microsoft.BingAds.V13.CampaignManagement;
        using Rep = Microsoft.BingAds.V13.Reporting;
        namespace App
        {
            class Worker
            {
                // absent in SDK 13.0.20, re-check later
                string Label(CampaignType type)
                {
                    switch (type)
                    {
                        case CampaignType.Search: return "s";
                        case CampaignType.Shopping: return "p";
                        case CampaignType.Audience: return "a";
                        case CampaignType.Hotel: return "h";
                        default: return "";
                    }
                }
                string Echo(CampaignType type)
                {
                    switch (type)
                    {
                        case CampaignType.Search: return "s";
                        case CampaignType.Shopping: return "p";
                        default: return type.ToString();
                    }
                }
                int Profile(ProfileType type) => type switch
                {
                    ProfileType.CompanyName => 1,
                    ProfileType.Industry => 2,
                    _ => throw new InvalidOperationException(type.ToString()),
                };
                int Bid(string t)
                {
                    switch (t)
                    {
                        case "MaxConversions": return 1;
                        case "ManualCpc": return 2;
                        case "TargetRoas": return 3;
                        default: throw new Exception(t);
                    }
                }
                void Build(Campaign campaign)
                {
                    var old = OldEnum.A;
                    var match = MatchType.Legacy;
                    campaign.OldProp = "x";
                    campaign.BiddingScheme = new MaxConversionsBiddingScheme { MaxCpc = new Bid { Amount = 1 } };
                    var roas = new TargetRoasBiddingScheme { TargetRoas = 2 };
                    var column = Rep.CampaignPerformanceReportColumn.Impressions;
                    var headers = new[] { "Old Header Name", "Parent Id" };
                    var soap = "https://campaign.api.bingads.microsoft.com/Api/Advertiser/CampaignManagement/v13/CampaignManagementService.svc";
                    var scope = "https://ads.microsoft.com/msads.manage";
                    var content = "https://content.api.ads.microsoft.com/v9.1/bmc/stores/{0}";
                }
                async void Call(dynamic client, dynamic result)
                {
                    try { await client.CallAsync(1); } catch (Exception) { }
                    try { await client.CallAsync(2); } catch (Exception e) { result.AddError(e.Message); }
                    try { await client.CallAsync(3); } catch (Exception) { throw; }
                }
            }
        }
        """;

    // Imports only Reporting, so its own CampaignType is not the SDK's.
    private const string Unrelated = """
        using Microsoft.BingAds.V13.Reporting;
        namespace App
        {
            enum CampaignType { Search }
            class Other { object Get() => CampaignType.Search; void Set(dynamic c) { c.OldProp = 1; } }
        }
        """;

    private const string Registry = """
        namespace Reg
        {
            class MicrosoftCampaignFieldRegistry
            {
                System.Collections.Generic.List<string> Fields = new() { "OldColumn", "AverageCpc", "Clicks" };
            }
        }
        """;

    private static (CheckReport Report, MicrosoftIndex Index) Check(string sdkPin = "13.0.20", string newtonsoftPin = "13.0.4", DateOnly? now = null)
    {
        string fixture = Path.Combine(RepoRoot(), "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "code", "backend"));
            File.WriteAllText(Path.Combine(fixture, "code", "backend", "Directory.Packages.props"),
                $"<Project><ItemGroup>\n<PackageVersion Include=\"Microsoft.BingAds.SDK\" Version=\"{sdkPin}\" />\n"
                + $"<PackageVersion Include=\"Newtonsoft.Json\" Version=\"{newtonsoftPin}\" />\n</ItemGroup></Project>");
            var from = From(sdkPin);
            var to = To();
            var known = from.Types.Concat(to.Types).Select(t => t.Name).ToHashSet();
            var index = new MicrosoftIndex();
            foreach (var (file, text) in new[]
            {
                ("code/backend/App/Worker.cs", Worker),
                ("code/backend/App/Other.cs", Unrelated),
                ("code/backend/CQS.Shared/PlatformFieldsRegistry/Microsoft/MicrosoftCampaignFieldRegistry.cs", Registry),
            })
            {
                var tree = CSharpSyntaxTree.ParseText(text);
                MicrosoftIndexer.ScanFile(file, (CompilationUnitSyntax)tree.GetRoot(), tree,
                    MicrosoftIndexer.IsRegistryPath(file), known, index);
            }
            var report = MicrosoftCheck.Run(index, fixture, "code/backend/Directory.Packages.props", from, to, [to],
                now ?? new DateOnly(2026, 9, 30), ["2026-09-23 Upcoming change — https://example"]);
            return (report, index);
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }

    private static List<Finding> Find(CheckReport report, string category, string path) =>
        report.Findings.Where(f => f.Category == category && f.Path == path).ToList();

    [TestMethod]
    public void RemovedTypesAndMembersBreakTheUpgradeOnlyWhenResolvedToTheSdk()
    {
        var (report, _) = Check();
        var type = Find(report, "UPCOMING_BREAK", Cm + ".OldEnum").Single();
        StringAssert.Contains(type.Message, "won't compile after the upgrade");
        StringAssert.Contains(Find(report, "UPCOMING_BREAK", "MatchType.Legacy").Single().Message, "enum value");
        var member = Find(report, "UPCOMING_BREAK", "Campaign.OldProp").Single();
        Assert.AreEqual("code/backend/App/Worker.cs", member.File);
        Assert.IsFalse(report.Findings.Any(f => f.File == "code/backend/App/Other.cs" && f.Category == "UPCOMING_BREAK"));
    }

    [TestMethod]
    public void StringColumnsFlagOnlyWhenGoneFromEveryReportOrFromOneTheFileUses()
    {
        var (report, _) = Check();
        var gone = Find(report, "UPCOMING_BREAK", "OldColumn").Single();
        Assert.AreEqual("high", gone.Severity);
        StringAssert.Contains(gone.Message, "build still passes");
        Assert.AreEqual(0, Find(report, "UPCOMING_BREAK", "AverageCpc").Count);
        Assert.AreEqual(0, Find(report, "UPCOMING_BREAK", "Clicks").Count);
    }

    [TestMethod]
    public void BulkHeaderRemovalIsSilentRiskWithReplacement()
    {
        var (report, _) = Check();
        var header = Find(report, "SILENT_RISK", "Old Header Name").Single();
        Assert.AreEqual("New Header Name", header.SuggestedReplacement);
        Assert.AreEqual(0, Find(report, "SILENT_RISK", "Parent Id").Count);
    }

    [TestMethod]
    public void SwitchCoverageUsesWhatTheDefaultBranchDoes()
    {
        var (report, index) = Check();
        var campaign = Find(report, "SILENT_RISK", "CampaignType");
        Assert.AreEqual(2, campaign.Count, string.Join("\n", campaign.Select(f => f.Message)));
        Assert.IsTrue(campaign.Any(f => f.Severity == "medium" && f.Message.Contains("ObjectiveBased (new in 13.0.30)") && f.Message.Contains("fall to default")));
        Assert.IsTrue(campaign.Any(f => f.Severity == "low" && f.Message.Contains("pass through unchanged")));
        var profile = Find(report, "SILENT_RISK", "ProfileType");
        Assert.IsTrue(profile.Any(f => f.Severity == "medium" && f.Message.Contains("JobFunction, which already exist") && f.Message.Contains("throws")));
        Assert.IsTrue(profile.Any(f => f.Severity == "high" && f.Message.Contains("JobTitle (new in 13.0.30)")));
        var bidding = Find(report, "SILENT_RISK", "BiddingScheme").Single();
        StringAssert.Contains(bidding.Message, "MaxReachBiddingScheme (new in 13.0.30)");
        Assert.AreEqual("high", bidding.Severity);
        Assert.AreEqual("passthrough", index.Switches.Single(s => s.Enclosing == "Echo").DefaultKind);
    }

    [TestMethod]
    public void SwallowedFaultsSkipRecordedAndRethrownFailures()
    {
        var (report, index) = Check();
        Assert.AreEqual(1, index.SwallowedCatches.Count);
        Assert.AreEqual("empty", index.SwallowedCatches[0].Kind);
        Assert.AreEqual(1, report.Findings.Count(f => f.Category == "RESILIENCE"));
    }

    [TestMethod]
    public void DatedRulesMaxCpcAndSoap()
    {
        var (report, _) = Check();
        var maxCpc = Find(report, "UPCOMING_BREAK", "MaxConversionsBiddingScheme.MaxCpc").Single();
        StringAssert.Contains(maxCpc.Message, "From 2027-01-12");
        Assert.AreEqual(0, Find(report, "UPCOMING_BREAK", "TargetRoasBiddingScheme.MaxCpc").Count);
        var soap = Find(report, "SUNSET", "SOAP API").Single();
        Assert.AreEqual("high", soap.Severity);
        Assert.AreEqual(1, Find(report, "SUNSET", "SOAP endpoint").Count);
        var direct = Find(report, "UNKNOWN", "direct Microsoft Ads HTTP call");
        Assert.AreEqual(1, direct.Count, "OAuth scope strings must not count as calls");
        StringAssert.Contains(direct[0].Message, "content.api.ads.microsoft.com");
        var late = Check(now: new DateOnly(2028, 1, 1)).Report;
        StringAssert.Contains(Find(late, "BREAKING_RUNTIME", "MaxConversionsBiddingScheme.MaxCpc").Single().Message, "Since 2027-01-12");
        var modern = Check("13.0.28").Report;
        Assert.AreEqual("info", Find(modern, "SUNSET", "SOAP API").Single().Severity);
    }

    [TestMethod]
    public void UpgradeDependenciesAndPinnedComments()
    {
        var (report, _) = Check(newtonsoftPin: "13.0.1");
        var migration = Find(report, "MIGRATION", "Microsoft.BingAds.SDK").Single();
        StringAssert.Contains(migration.Message, "13.0.20 → 13.0.30");
        StringAssert.Contains(string.Join("\n", migration.Evidence), "13.0.30: New campaign type ObjectiveBased.");
        StringAssert.Contains(Find(report, "COMPAT", "Newtonsoft.Json").Single().Message, "NU1605");
        Assert.AreEqual(0, Find(Check().Report, "COMPAT", "Newtonsoft.Json").Count);
        Assert.AreEqual(1, Find(report, "CLEANUP", "SDK-version comment").Count);
        Assert.AreEqual("Microsoft Ads", report.Platform);
        Assert.AreEqual(1, report.Announcements.Count);
        StringAssert.Contains(CheckHtml.Render(report), "Recent announcements to review");
    }

    [TestMethod]
    public void BulkHeadersComeFromCsvHeadersListedStringTableConstants()
    {
        string table = "class StringTable { public const string ParentId = \"Parent Id\"; public const string Unused = \"Unused\"; public const string Type = \"Type\"; }";
        string csv = "class CsvHeaders { public static readonly string[] Headers = { StringTable.Type, StringTable.ParentId, }; }";
        CollectionAssert.AreEqual(new[] { "Type", "Parent Id" }, MicrosoftSnapshot.BulkHeaders(table, csv));
        Assert.AreEqual(0, MicrosoftSnapshot.BulkHeaders(table, "no headers here").Count);
    }

    [TestMethod]
    public void SimilarityMatchesCamelAndSpacedWords()
    {
        Assert.IsTrue(MicrosoftCheck.Similarity("Bid Strategy MaxCpc", "Bid Strategy Max Cpc") >= 0.5);
        Assert.AreEqual(0, MicrosoftCheck.Similarity("Same", "Same"));
        Assert.IsTrue(MicrosoftCheck.Similarity("OldEnum", "NewEnum") >= 0.3);
    }

    [TestMethod]
    public void ReadsPublicSurfaceFromTheRealPackageWhenCached()
    {
        string nupkg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages",
            "microsoft.bingads.sdk", "13.0.28", "microsoft.bingads.sdk.13.0.28.nupkg");
        if (!File.Exists(nupkg))
            Assert.Inconclusive("Microsoft.BingAds.SDK 13.0.28 is not in the NuGet cache.");
        var schema = MicrosoftSnapshot.Build(nupkg, "13.0.28", null);
        var campaignType = schema.Find(Cm + ".CampaignType");
        Assert.IsNotNull(campaignType);
        Assert.AreEqual("enum", campaignType.Kind);
        CollectionAssert.Contains(campaignType.Members, "PerformanceMax");
        var scheme = schema.Find(Cm + ".MaxConversionsBiddingScheme");
        Assert.IsNotNull(scheme);
        Assert.IsTrue(schema.HasMember(scheme, "MaxCpc"));
        Assert.IsTrue(schema.DerivesFrom(scheme, "BiddingScheme"));
        Assert.AreEqual("13.0.4", schema.Dependencies["Newtonsoft.Json"]);
        StringAssert.Contains(schema.ReleaseNotes, "MSClickIdPerformanceReport");
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
