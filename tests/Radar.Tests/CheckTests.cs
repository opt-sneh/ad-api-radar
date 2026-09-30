using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class CheckTests
{
    [TestMethod]
    public void SchemaChecksRespectPlatformGaqlLineageDeprecationAndStableIds()
    {
        WithModels(
            (before, after, repo) =>
            {
                var index = new CodeIndexResult { Repo = repo };
                index.Definitions.Add(
                    new FieldDefinition(
                        "GOOGLE_ADS_REPORT",
                        "Gone",
                        "campaign.removed_field",
                        "Fields",
                        "Fields.cs",
                        10,
                        [],
                        "google-ads"
                    )
                );
                index.Definitions.Add(
                    new FieldDefinition(
                        "GOOGLE_ADS_REPORT",
                        "Deprecated",
                        "campaign.deprecated_field",
                        "Fields",
                        "Fields.cs",
                        11,
                        [],
                        "google-ads"
                    )
                );
                index.Definitions.Add(
                    new FieldDefinition(
                        "SA360_REPORT_FIELDS",
                        "Sa",
                        "campaign.no_such_field",
                        "Fields",
                        "Sa.cs",
                        12,
                        [],
                        "sa360"
                    )
                );
                index.Uses.Add(
                    new FieldUse(
                        "GOOGLE_ADS_REPORT",
                        "Gone",
                        "campaign.removed_field",
                        "Uses.cs",
                        20,
                        "Run",
                        [],
                        "google-ads"
                    )
                );
                index.Uses.Add(
                    new FieldUse(
                        "GOOGLE_ADS_REPORT",
                        "Deprecated",
                        "campaign.deprecated_field",
                        "Uses.cs",
                        21,
                        "Run",
                        [],
                        "google-ads"
                    )
                );
                index.Queries.Add(
                    new QueryLiteral(
                        "SELECT users.name FROM users",
                        "users",
                        ["users.name"],
                        false,
                        "Mysql.cs",
                        5,
                        [],
                        "google-ads-candidate"
                    )
                );
                var sunsets = new[]
                {
                    new SunsetRow("v99", "January 2026", "February 2027 (tentative)", null),
                };
                CheckReport Check() =>
                    CheckEngine.Run(
                        index,
                        repo,
                        after,
                        [before, after],
                        [],
                        sunsets,
                        [],
                        [],
                        "26.1.0",
                        new DateOnly(2026, 9, 29)
                    );
                var report = Check();
                var broken = report.Findings.Single(f => f.Category == "BREAKING_RUNTIME");
                Assert.AreEqual("campaign.removed_field", broken.Path);
                Assert.AreEqual(1, broken.UseCount);
                StringAssert.Contains(broken.Message, "last present in v98; first removed in v99");
                Assert.AreEqual(1, report.NonGaqlQueriesSkipped);
                Assert.IsFalse(report.Findings.Any(f => f.File == "Sa.cs"));
                Assert.IsTrue(
                    report.Findings.Any(f =>
                        f.Category == "DEPRECATED" && f.Path == "campaign.deprecated_field"
                    )
                );
                Assert.AreEqual(
                    "high",
                    report.Findings.Single(f => f.Category == "SUNSET").Severity
                );
                CollectionAssert.AreEqual(
                    report.Findings.Select(f => f.Id).ToArray(),
                    Check().Findings.Select(f => f.Id).ToArray()
                );

                var nextIndex = new CodeIndexResult { Repo = repo };
                nextIndex.Definitions.Add(index.Definitions[0]);
                nextIndex.Uses.Add(index.Uses[0]);
                var upcoming = CheckEngine.Run(
                    nextIndex,
                    repo,
                    before,
                    [before, after],
                    [after],
                    [],
                    [],
                    [],
                    "",
                    new DateOnly(2026, 9, 29)
                );
                StringAssert.Contains(
                    upcoming.Findings.Single(f => f.Category == "UPCOMING_BREAK").Message,
                    "v99"
                );
            }
        );
    }

    [TestMethod]
    public void CompatibilityChecksSatisfiedAndUnsatisfiedRanges()
    {
        string root = Path.Combine(
            RepoRoot(),
            "data",
            "test-fixtures",
            Guid.NewGuid().ToString("N")
        );
        string props = Path.Combine(root, "code", "backend", "Directory.Packages.props");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(props)!);
            File.WriteAllText(
                props,
                "<Project><ItemGroup><PackageVersion Include=\"Google.Protobuf\" Version=\"3.28.2\" /></ItemGroup></Project>"
            );
            var okay = new[]
            {
                new SdkDependencies(
                    "25.1.0",
                    [],
                    ["Google.Ads.GoogleAds 25.1.0", "Google.Protobuf [3.25.0,4.0.0)"],
                    "[3.25.0,4.0.0)"
                ),
            };
            var bad = new[]
            {
                new SdkDependencies(
                    "25.1.0",
                    [],
                    ["Google.Ads.GoogleAds 25.1.0", "Google.Protobuf [3.30.0,4.0.0)"],
                    "[3.30.0,4.0.0)"
                ),
            };
            Assert.AreEqual(
                "info",
                CheckEngine.CheckCompatibility(root, "v23", "25.1.0", okay).Severity
            );
            Assert.AreEqual(
                "high",
                CheckEngine.CheckCompatibility(root, "v23", "25.1.0", bad).Severity
            );
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void HtmlHasEveryHeadingAndEscapesFindingText()
    {
        var report = new CheckReport
        {
            Target = "v99",
            Next = [],
            Findings =
            [
                new Finding(
                    "id",
                    "BREAKING_RUNTIME",
                    "high",
                    "<script>",
                    "google-ads",
                    "<file>",
                    1,
                    0,
                    ["<evidence>"],
                    "<alert>"
                ),
            ],
            Counts = new Dictionary<string, int> { ["BREAKING_RUNTIME"] = 1 },
            UnknownDefinitionsByOwner = [],
            Limitations = [],
        };
        string html = CheckHtml.Render(report);
        foreach (
            string heading in new[]
            {
                "Summary counts",
                "Breaking at runtime",
                "Not checked / unknown",
            }
        )
            StringAssert.Contains(html, $"<h2>{heading}</h2>");
        StringAssert.Contains(html, "&lt;script&gt;");
        Assert.IsFalse(html.Contains("<h3><script>", StringComparison.Ordinal));
        StringAssert.Contains(html, "&lt;evidence&gt;");
    }

    [TestMethod]
    public void HtmlShowsFunnelFiltersAndOneChangeGroup()
    {
        var first = new Finding(
            "a",
            "UPCOMING_BREAK",
            "high",
            "campaign.old",
            "google-ads",
            "A.cs",
            4,
            2,
            [],
            "Change"
        )
        {
            ChangeId = "C1",
            Lines = [4],
        };
        var report = new CheckReport
        {
            Target = "v23",
            Next = ["v24"],
            Findings = [first, first with { Id = "b", File = "B.cs", Line = 8, Lines = [8] }],
            Counts = new Dictionary<string, int> { ["UPCOMING_BREAK"] = 2 },
            Funnel = new Dictionary<string, int>
            {
                ["changesInCatalog"] = 7,
                ["changesTouchingCode"] = 1,
                ["actionable"] = 2,
                ["files"] = 2,
            },
            UnknownDefinitionsByOwner = [],
            Limitations = [],
        };
        string html = CheckHtml.Render(report);
        StringAssert.Contains(
            html,
            "7 upstream changes → 1 touch Optmyzr code → 2 actionable findings in 2 files"
        );
        StringAssert.Contains(html, "data-category=\"UPCOMING_BREAK\"");
        Assert.AreEqual(1, html.Split("<details class=\"group\"").Length - 1);
        StringAssert.Contains(html, "2 locations");
    }

    [TestMethod]
    public void HtmlNamesPlatformAndLinksTheOtherReport()
    {
        var report = new CheckReport
        {
            Platform = "Microsoft Ads",
            Target = "13.0.28",
            Next = ["13.0.30"],
            Findings = [],
            Counts = [],
            UnknownDefinitionsByOwner = [],
            Limitations = [],
        };
        string html = CheckHtml.Render(report, "../report.html");
        StringAssert.Contains(html, "<title>Microsoft Advertising upgrade review");
        StringAssert.Contains(html, "<h1>Microsoft Advertising upgrade review</h1>");
        StringAssert.Contains(html, "href=\"../report.html\">Open Google Ads report</a>");
        StringAssert.Contains(html, "Coverage is partial");
    }

    private static void WithModels(Action<SchemaModel, SchemaModel, string> check)
    {
        string repo = RepoRoot();
        string fixture = Path.Combine(repo, "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            string api = Path.Combine(fixture, "google", "api", "resource.proto");
            Directory.CreateDirectory(Path.GetDirectoryName(api)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "resource.proto"), api);
            var models = new List<SchemaModel>();
            foreach (string version in new[] { "v98", "v99" })
            {
                foreach (string part in new[] { "", "-common", "-enums", "-services" })
                {
                    string package = part switch
                    {
                        "" => "resources",
                        "-common" => "common",
                        "-enums" => "enums",
                        _ => "services",
                    };
                    string destination = Path.Combine(
                        fixture,
                        "google",
                        "ads",
                        "googleads",
                        version,
                        package,
                        version + part + ".proto"
                    );
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(
                        Path.Combine(
                            AppContext.BaseDirectory,
                            "Fixtures",
                            version + part + ".proto"
                        ),
                        destination
                    );
                }
                string pb = Path.Combine(fixture, version + ".pb");
                File.WriteAllBytes(pb, new ProtoCompiler(repo).Compile(fixture, version, out _));
                models.Add(SchemaModel.Load(pb, version));
            }
            check(models[0], models[1], repo);
        }
        finally
        {
            if (Directory.Exists(fixture))
                Directory.Delete(fixture, true);
        }
    }

    private static string RepoRoot()
    {
        for (
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent
        )
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
