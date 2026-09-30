using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class SchemaTests
{
    [TestMethod]
    public void CompiledVersionsProduceFlattenedFieldsAndChanges()
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
            string api = Path.Combine(fixtureRoot, "google", "api", "resource.proto");
            Directory.CreateDirectory(Path.GetDirectoryName(api)!);
            File.Copy(Fixture("resource.proto"), api);
            var compiler = new ProtoCompiler(root);
            var models = new Dictionary<string, SchemaModel>();
            foreach (string version in new[] { "v98", "v99" })
            {
                string versionRoot = Path.Combine(
                    fixtureRoot,
                    "google",
                    "ads",
                    "googleads",
                    version
                );
                foreach (string part in new[] { "", "-common", "-enums", "-services" })
                {
                    string package = part switch
                    {
                        "" => "resources",
                        "-common" => "common",
                        "-enums" => "enums",
                        _ => "services",
                    };
                    string target = Path.Combine(versionRoot, package, $"{version}{part}.proto");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(Fixture($"{version}{part}.proto"), target);
                }
                string pb = Path.Combine(fixtureRoot, version + ".pb");
                File.WriteAllBytes(pb, compiler.Compile(fixtureRoot, version, out int fileCount));
                Assert.AreEqual(4, fileCount);
                models.Add(version, SchemaModel.Load(pb, version));
            }

            var before = models["v98"];
            var after = models["v99"];
            Assert.IsTrue(
                before.Fields.ContainsKey("campaign.network_settings.target_search_network")
            );
            Assert.IsTrue(before.Fields.ContainsKey("campaign.other_resource"));
            Assert.IsTrue(before.Fields.ContainsKey("campaign.other_resource.secret"));
            Assert.IsFalse(
                before.Fields.ContainsKey("campaign.network_settings.cycle.target_search_network")
            );
            Assert.IsTrue(before.Fields.ContainsKey("metrics.clicks"));
            Assert.IsTrue(before.Fields.ContainsKey("segments.device"));
            Assert.IsTrue(before.Fields["campaign.repeated_field"].Repeated);
            Assert.AreEqual("string", before.Fields["campaign.name"].ProtoType);
            Assert.AreEqual(12, before.Fields["campaign.removed_field"].Line);
            Assert.AreEqual("google.ads.googleads.enums.StatusEnum.Status", before.Fields["campaign.status"].ProtoType);
            Assert.AreEqual(before.Fields["campaign.status"].ProtoType, after.Fields["campaign.status"].ProtoType);
            var changes = SchemaDiff.Compare(before, after);
            AssertChange(changes, "FieldRemoved", "campaign.removed_field");
            AssertChange(changes, "FieldAdded", "campaign.added_field");
            var type = AssertChange(changes, "FieldTypeChanged", "campaign.changed_field");
            Assert.AreEqual("int64", type.FromType);
            Assert.AreEqual("string", type.ToType);
            var repeated = AssertChange(changes, "FieldTypeChanged", "campaign.repeated_field");
            Assert.AreEqual("string[]", repeated.FromType);
            Assert.AreEqual("string", repeated.ToType);
            Assert.IsFalse(changes.Any(change => change.Kind == "FieldTypeChanged" && change.Path == "campaign.status"));
            AssertChange(changes, "FieldNewlyDeprecated", "campaign.deprecated_field");
            AssertChange(changes, "EnumValueRemoved", "Status.REMOVED");
            AssertChange(changes, "EnumValueAdded", "Status.ADDED");
            AssertChange(changes, "ServiceMethodRemoved", "CampaignService.Removed");
            AssertChange(changes, "ServiceMethodAdded", "CampaignService.Added");
            Assert.IsTrue(
                AssertChange(changes, "FieldRemoved", "campaign.removed_field")
                    .FromLocation!.EndsWith("v98.proto:12", StringComparison.Ordinal)
            );
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }
    }

    [TestMethod]
    public void MissingSnapshotReturnsFailure()
    {
        Assert.AreEqual(
            1,
            SchemaCommand.RunDiff([
                "--from",
                "v98",
                "--to",
                "v99",
                "--snapshots",
                "data/missing-snapshots",
            ])
        );
    }

    private static SchemaChange AssertChange(
        IReadOnlyList<SchemaChange> changes,
        string kind,
        string path
    ) => changes.Single(change => change.Kind == kind && change.Path == path);

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

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
