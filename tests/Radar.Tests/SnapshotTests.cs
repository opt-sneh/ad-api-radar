using Google.Protobuf.Reflection;
using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class SnapshotTests
{
    [TestMethod]
    public void SunsetParserReadsVersionRowsAndPreservesTentativeText()
    {
        var rows = SnapshotParsers.ParseSunset(Fixture("sunset.html"));
        Assert.HasCount(2, rows);
        Assert.AreEqual("v23", rows[0].Version);
        Assert.AreEqual("January 28, 2026", rows[0].ReleaseDate);
        Assert.AreEqual("February 2027", rows[0].SunsetDate);
        Assert.AreEqual(
            "https://developers.google.com/google-ads/api/docs/migration/v23",
            rows[0].UpgradeGuideUrl
        );
        Assert.AreEqual("v24", rows[1].Version);
        Assert.AreEqual("May 2027 (tentative)", rows[1].SunsetDate);
    }

    [TestMethod]
    public void DeprecationsParserReadsFieldsAndLinks()
    {
        var rows = SnapshotParsers.ParseDeprecations(Fixture("deprecations.html"));
        Assert.HasCount(2, rows);
        Assert.AreEqual("2026-09-09", rows[0].EffectiveDate);
        Assert.AreEqual("September 9, 2026", rows[0].EffectiveDateText);
        Assert.AreEqual("API policy", rows[0].ChangeArea);
        Assert.AreEqual("Deprecation", rows[0].ChangeType);
        Assert.AreEqual(
            "Developer tokens are deprecated. See the API access guide.",
            rows[0].Description
        );
        CollectionAssert.AreEqual(
            new[]
            {
                "https://developers.google.com/google-ads/api/docs/api-policy/developer-token",
            },
            rows[0].Links
        );
        Assert.AreEqual("Behavioral shift", rows[1].ChangeType);
        Assert.IsNull(rows[1].EffectiveDate);
        Assert.AreEqual("July 2025", rows[1].EffectiveDateText);
        CollectionAssert.AreEqual(
            new[] { "https://ads-developers.googleblog.com/example" },
            rows[1].Links
        );
    }

    [TestMethod]
    public void TablesWithoutExpectedHeadersOrRowsFailClosed()
    {
        Assert.ThrowsExactly<FormatException>(() =>
            SnapshotParsers.ParseSunset("<html><body>No table</body></html>")
        );
        Assert.ThrowsExactly<FormatException>(() =>
            SnapshotParsers.ParseSunset(
                "<table><tr><th>Wrong</th></tr><tr><td>v23</td></tr></table>"
            )
        );
        Assert.ThrowsExactly<FormatException>(() =>
            SnapshotParsers.ParseDeprecations("<table><tr><th>Wrong</th></tr></table>")
        );
        Assert.ThrowsExactly<FormatException>(() =>
            SnapshotParsers.ParseDeprecations(
                "<table><tr><th>Effective date</th><th>Change area</th><th>Change type</th><th>Guidance and description</th></tr></table>"
            )
        );
    }

    [TestMethod]
    public void NuspecParserReadsDependencyGroups()
    {
        var sdk = SnapshotParsers.ParseNuspec(Fixture("sdk.nuspec"), "25.1.0");
        Assert.AreEqual("25.1.0", sdk.SdkVersion);
        Assert.HasCount(3, sdk.Dependencies);
        CollectionAssert.AreEquivalent(
            new[] { "net6.0", "netstandard2.0" },
            sdk.Dependencies.Where(item => item.Id == "Google.Protobuf")
                .Select(item => item.TargetFramework)
                .ToArray()
        );
        Assert.IsTrue(
            sdk.Dependencies.Where(item => item.Id == "Google.Protobuf")
                .All(item => item.VersionRange == "[3.31.1, 4.0.0)")
        );
    }

    [TestMethod]
    public void ProtobufResolverFollowsCompatibleGoogleDependencyGroups()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SnapshotParsers.NuspecUrl("Google.Ads.GoogleAds", "26.1.0")] = "sdk-chain-root.nuspec",
            [SnapshotParsers.NuspecUrl("Google.Ads.GoogleAds.Core", "4.0.8")] = "sdk-chain-core.nuspec",
            [SnapshotParsers.NuspecUrl("Google.Api.Gax.Grpc", "4.2.0")] = "sdk-chain-gax.nuspec",
        };
        var fetched = new List<string>();
        var resolved = SnapshotParsers.ResolveProtobuf("26.1.0", url =>
        {
            fetched.Add(url);
            return Fixture(files[url]);
        });
        Assert.AreEqual("[3.31.1, 4.0.0)", resolved.ProtobufRange);
        CollectionAssert.AreEqual(new[]
        {
            "Google.Ads.GoogleAds 26.1.0",
            "Google.Ads.GoogleAds.Core [4.0.8, 5.0.0)",
            "Google.Api.Gax.Grpc [4.2.0, 5.0.0)",
            "Google.Protobuf [3.31.1, 4.0.0)",
        }, resolved.ProtobufChain);
        Assert.HasCount(3, fetched);
    }

    [TestMethod]
    public void RealProtocPreservesFieldsAndDeprecationOption()
    {
        string root = RepoRoot();
        string fixtureRoot = Path.Combine(
            root,
            "data",
            "test-fixtures",
            Guid.NewGuid().ToString("N")
        );
        try
        {
            string proto = Path.Combine(
                fixtureRoot,
                "google",
                "ads",
                "googleads",
                "v99",
                "campaign.proto"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(proto)!);
            File.WriteAllText(proto, Fixture("campaign.proto"));
            byte[] bytes = new ProtoCompiler(root).Compile(fixtureRoot, "v99", out int count);
            Assert.AreEqual(1, count);
            var descriptor = FileDescriptorSet.Parser.ParseFrom(bytes);
            var campaign = descriptor
                .File.Single(file => file.Package == "google.ads.googleads.v99.resources")
                .MessageType.Single(message => message.Name == "Campaign");
            CollectionAssert.AreEquivalent(
                new[] { "name", "old_id" },
                campaign.Field.Select(field => field.Name).ToArray()
            );
            Assert.IsTrue(
                campaign.Field.Single(field => field.Name == "old_id").Options.Deprecated
            );
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [TestMethod]
    public void FailedProductionLeavesPreviousFileUntouched()
    {
        string root = Path.Combine(
            RepoRoot(),
            "data",
            "test-fixtures",
            Guid.NewGuid().ToString("N")
        );
        string path = Path.Combine(root, "sunset.json");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(path, "previous good snapshot");
            Assert.ThrowsExactly<FormatException>(() =>
                AtomicFile.WriteProduced(
                    path,
                    () =>
                    {
                        SnapshotParsers.ParseSunset("<html>broken source</html>");
                        return [];
                    }
                )
            );
            Assert.AreEqual("previous good snapshot", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

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
