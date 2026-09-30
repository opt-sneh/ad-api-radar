using System.Text.Json;
using Radar;

namespace Radar.Tests;

// Contracts other tools depend on: the findings.json shape, the report page, COMPAT wording, and root lookup.
[TestClass]
public sealed class ReportContractTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static Finding NewFinding(string category, string severity, string path, string message,
        string confidence = "high", string file = "App/Worker.cs", int line = 12) =>
        new("id-" + path, category, severity, path, "google-ads", file, line, 1, ["evidence"], message)
        {
            Confidence = confidence,
            Lines = line > 0 ? [line] : [],
        };

    [TestMethod]
    public void FindingsJsonCarriesActionableAndStillReadsBack()
    {
        // scripts/sync-issues.ps1 opens Issues only for findings whose "actionable" is true.
        var queued = NewFinding("UPCOMING_BREAK", "high", "Campaign.Gone", "Removed in v25.");
        var unaffected = NewFinding("SILENT_RISK", "low", "Campaign.Language", "Likely unaffected.", confidence: "low");
        string json = JsonSerializer.Serialize(new[] { queued, unaffected }, JsonOptions);

        using var document = JsonDocument.Parse(json);
        Assert.IsTrue(document.RootElement[0].GetProperty("actionable").GetBoolean());
        Assert.IsFalse(document.RootElement[1].GetProperty("actionable").GetBoolean());
        var back = JsonSerializer.Deserialize<Finding[]>(json, JsonOptions)!;
        Assert.AreEqual("Campaign.Gone", back[0].Path);
        Assert.IsTrue(back[0].Actionable);
    }

    [TestMethod]
    public void ReportForLatestVersionShowsStatusSunsetGapsAndEscapesScannedText()
    {
        var report = new CheckReport
        {
            Target = "v23",
            Next = [],
            Findings =
            [
                NewFinding("SILENT_RISK", "medium", "<b>campaign.status</b>", "Filter <i>may</i> miss values."),
                NewFinding("SUNSET", "high", "v23", "v23 sunset: February 2027.", file: "google/sunset.json", line: 0),
            ],
            Counts = new Dictionary<string, int> { ["SILENT_RISK"] = 1, ["SUNSET"] = 1 },
            Funnel = new Dictionary<string, int> { ["changesInCatalog"] = 0, ["findings"] = 2, ["actionable"] = 2, ["files"] = 1 },
            UnknownDefinitionsByOwner = [],
            InterpolatedQueries = 3,
            Limitations = ["STALE SOURCE WARNING: <googleapis unreachable>"],
        };

        string html = CheckHtml.Render(report);

        StringAssert.Contains(html, "No newer version in this scan");
        StringAssert.Contains(html, "newer-version changes");
        StringAssert.Contains(html, "v23 sunset: February 2027.");
        StringAssert.Contains(html, "3 dynamic queries not statically checked");
        StringAssert.Contains(html, "upstream sources are stale");
        StringAssert.Contains(html, "live GAQL validation not run");
        StringAssert.Contains(html, "pill sev-medium");
        // Paths and messages come from scanned code, so they must never reach the page as markup.
        Assert.IsFalse(html.Contains("<b>campaign.status</b>", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<i>may</i>", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("<googleapis unreachable>", StringComparison.Ordinal));
        StringAssert.Contains(html, "&lt;b&gt;campaign.status&lt;/b&gt;");
    }

    [TestMethod]
    public void UnpinnedProtobufIsInformationNotUnknown()
    {
        string root = Path.Combine(Path.GetTempPath(), "radar-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "app"));
            File.WriteAllText(Path.Combine(root, "app", "App.csproj"),
                "<Project><ItemGroup><PackageReference Include=\"Google.Ads.GoogleAds\" Version=\"25.1.0\" /></ItemGroup></Project>");
            var sdk = new SdkDependencies("25.1.0", [], ["Google.Ads.GoogleAds 25.1.0", "Google.Protobuf [3.28.2,4.0.0)"], "[3.28.2,4.0.0)");

            var finding = CheckEngine.CheckCompatibility(root, "v23", "25.1.0", [sdk]);

            Assert.AreEqual("info", finding.Severity);
            StringAssert.Contains(finding.Message, "not pinned directly");
            Assert.AreEqual("", finding.File, "no props file exists, so none is named");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public void RootIsFoundFromTheBuildFolderWhenStartedElsewhere()
    {
        // CI starts radar from the workspace root with the tool in a radar/ subfolder.
        string outside = Path.Combine(Path.GetTempPath(), "radar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            string root = RunCommand.FindRoot(outside, AppContext.BaseDirectory);
            Assert.IsTrue(File.Exists(Path.Combine(root, "AdApiRadar.slnx")));
            Assert.ThrowsExactly<DirectoryNotFoundException>(() => RunCommand.FindRoot(outside));
        }
        finally { Directory.Delete(outside, true); }
    }
}
