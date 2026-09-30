using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class RunTests
{
    [TestMethod]
    public void TargetUsesMostFilesAndHighestVersionOnTie()
    {
        var index = new CodeIndexResult { Repo = "fixture" };
        index.SdkVersionsByFile["A.cs"] = [25, 26, 26];
        index.SdkVersionsByFile["B.cs"] = [25];
        index.SdkVersionsByFile["C.cs"] = [26];
        Assert.AreEqual("v26", TargetDetector.Detect(index));
        index.SdkVersionsByFile["D.cs"] = [25];
        Assert.AreEqual("v25", TargetDetector.Detect(index));
    }

    [TestMethod]
    public void VersionChainAndNewVersionDetection()
    {
        CollectionAssert.AreEqual(new[] { "v25", "v26", "v27" }, TargetDetector.Versions("v25", 27));
        CollectionAssert.AreEqual(new[] { "v26", "v27" },
            TargetDetector.NewVersions(TargetDetector.Versions("v25", 27), ["v23", "v24", "v25"]));
        CollectionAssert.AreEqual(Array.Empty<string>(),
            TargetDetector.NewVersions(["26.1.0"], ["25.1.0", "26.1.0"]));
    }

    [TestMethod]
    public void ExistingSnapshotVersionsRemainRequestedWithoutRefresh()
    {
        string directory = Path.Combine(RepoRoot(), "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            string google = Path.Combine(directory, "google");
            Directory.CreateDirectory(google);
            string[] existing = ["v23", "v24", "v25"];
            foreach (string version in existing) File.WriteAllBytes(Path.Combine(google, version + ".pb"), []);
            string[] requested = TargetDetector.RequestedVersions(existing, "v25", 25);
            CollectionAssert.AreEqual(existing, requested);
            var manifest = new SnapshotManifest
            {
                Sources = existing.Select(version => new SnapshotSource
                {
                    Name = "google-protos-" + version,
                    File = "google/" + version + ".pb",
                    Status = "ok",
                    FetchedAtUtc = DateTime.UtcNow,
                }).ToList(),
            };
            Assert.IsFalse(RunCommand.NeedsSnapshotRefresh(manifest, existing, requested,
                ["26.1.0"], ["26.1.0"], directory, 7));
            CollectionAssert.AreEqual(new[] { "25.1.0", "26.1.0", "27.0.0" },
                RunCommand.RequestedSdks(["25.1.0", "26.1.0"], "26.1.0", "27.0.0"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void PropsOverrideAndSdkOverride()
    {
        string directory = Path.Combine(RepoRoot(), "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, "custom"));
            File.WriteAllText(Path.Combine(directory, "custom", "Packages.props"),
                "<Project><ItemGroup><PackageVersion Include=\"Google.Ads.GoogleAds\" Version=\"28.2.0\" />" +
                "<PackageVersion Include=\"Google.Protobuf\" Version=\"3.30.0\" /></ItemGroup></Project>");
            Assert.AreEqual("28.2.0", RunCommand.ResolveInstalledSdk(directory, "custom/Packages.props", null));
            Assert.AreEqual("29.0.0", RunCommand.ResolveInstalledSdk(directory, "missing.props", "29.0.0"));
            var finding = CheckEngine.CheckCompatibility(directory, "v25", "28.2.0", [], "custom/Packages.props");
            Assert.AreEqual("custom/Packages.props", finding.File);
            Assert.AreEqual(1, finding.Line);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void FreezeArgumentsKeepDefaultPathAndAcceptWholeRepo()
    {
        string[] defaults = RunCommand.FreezeArguments("root", "sha", "repo", "code/backend");
        CollectionAssert.AreEqual(new[] { "-Sha", "sha", "-Repo", "repo", "-Path", "code/backend" }, defaults[^6..]);
        string[] wholeRepo = RunCommand.FreezeArguments("root", "sha", "repo", ".");
        Assert.AreEqual(".", wholeRepo[^1]);
    }

    [TestMethod]
    public void GaqlExportDeduplicatesSkipsAndKeepsStableIds()
    {
        WithSchema(schema =>
        {
            var index = new CodeIndexResult { Repo = "fixture" };
            index.Queries.Add(new QueryLiteral("SELECT campaign.name\n FROM campaign", "campaign", [], false,
                "A.cs", 3, [], "google-ads-candidate"));
            index.Queries.Add(new QueryLiteral(" SELECT   campaign.name FROM campaign ", "campaign", [], false,
                "B.cs", 9, [], "google-ads-candidate"));
            index.Queries.Add(new QueryLiteral("SELECT x FROM unknown", "unknown", [], false,
                "C.cs", 1, [], "google-ads-candidate"));
            index.Queries.Add(new QueryLiteral("SELECT {field} FROM campaign", "campaign", [], false,
                "D.cs", 1, [], "google-ads-candidate"));
            index.Queries.Add(new QueryLiteral("SELECT campaign.name FROM campaign", "campaign", [], false,
                "E.cs", 1, [], "sa360"));
            var export = GaqlValidation.Export(index, schema, "abc", ["v98", "v99"]);
            Assert.HasCount(1, export.Queries);
            Assert.AreEqual("SELECT campaign.name FROM campaign", export.Queries[0].Query);
            CollectionAssert.AreEqual(new[] { "A.cs:3", "B.cs:9" }, export.Queries[0].Locations);
            Assert.AreEqual(export.Queries[0].Id, GaqlValidation.Export(index, schema, "def", ["v98"]).Queries[0].Id);
        });
    }

    [TestMethod]
    public void GaqlResultsMergeTargetAndFutureButIgnore404AndSuccess()
    {
        string directory = Path.Combine(RepoRoot(), "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string queries = Path.Combine(directory, "queries.json");
            string results = Path.Combine(directory, "results.json");
            GaqlValidation.Write(new GaqlExport("abc", "v98", ["v98", "v99"],
                [new GaqlExportQuery("q-123", "SELECT campaign.name FROM campaign", ["A.cs:3", "B.cs:9"])]), queries);
            File.WriteAllText(results, """
                {"results":[
                  {"id":"q-123","version":"v98","status":400,"ok":false,"error":"bad field","errorCode":"QUERY_ERROR"},
                  {"id":"q-123","version":"v99","status":400,"ok":false,"error":"removed","errorCode":"FIELD_ERROR"}
                ]}
                """);
            var report = new CheckReport { Target = "v98", Next = ["v99"], Findings = [], Counts = [],
                UnknownDefinitionsByOwner = [], Limitations = [] };
            GaqlValidation.Merge(report, results, queries);
            Assert.AreEqual(2, report.Counts["BREAKING_RUNTIME"]);
            Assert.AreEqual(2, report.Counts["UPCOMING_BREAK"]);
            Assert.HasCount(4, report.Findings);
            Assert.IsTrue(report.Findings.All(f => f.Severity == "high" && f.Confidence == "high"
                && f.Path == "GAQL validation"));
            StringAssert.Contains(report.Limitations.Single(), "2 results, 0 valid, 2 query errors, 0 inconclusive");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void GaqlHttp400WithoutQueryErrorCodeIsInconclusive()
    {
        string directory = Path.Combine(RepoRoot(), "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string queries = Path.Combine(directory, "queries.json");
            string results = Path.Combine(directory, "results.json");
            GaqlValidation.Write(new GaqlExport("abc", "v98", ["v98"],
                [new GaqlExportQuery("q-123", "SELECT campaign.name FROM campaign", ["A.cs:3"])]), queries);
            File.WriteAllText(results, """
                {"results":[{"id":"q-123","version":"v98","status":400,"ok":false,
                "error":"invalid developer token","errorCode":"AUTHENTICATION_ERROR"}]}
                """);
            var report = new CheckReport { Target = "v98", Next = [], Findings = [], Counts = [],
                UnknownDefinitionsByOwner = [], Limitations = [] };
            GaqlValidation.Merge(report, results, queries);
            Assert.HasCount(0, report.Findings);
            StringAssert.Contains(report.Limitations.Single(), "1 inconclusive");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static void WithSchema(Action<SchemaModel> action)
    {
        string root = RepoRoot();
        string fixture = Path.Combine(root, "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            string api = Path.Combine(fixture, "google", "api", "resource.proto");
            Directory.CreateDirectory(Path.GetDirectoryName(api)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "resource.proto"), api);
            foreach (string part in new[] { "", "-common", "-enums", "-services" })
            {
                string package = part switch { "" => "resources", "-common" => "common",
                    "-enums" => "enums", _ => "services" };
                string destination = Path.Combine(fixture, "google", "ads", "googleads", "v98",
                    package, "v98" + part + ".proto");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v98" + part + ".proto"), destination);
            }
            string pb = Path.Combine(fixture, "v98.pb");
            File.WriteAllBytes(pb, new ProtoCompiler(root).Compile(fixture, "v98", out _));
            action(SchemaModel.Load(pb, "v98"));
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
