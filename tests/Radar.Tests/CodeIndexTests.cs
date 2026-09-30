using System.Text.Json;
using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class CodeIndexTests
{
    [TestMethod]
    public void GoogleDefinitionResolvesUseAcrossFiles()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        var definition = index.Definitions.Single(d => d.OwnerType == "GOOGLE_ADS_REPORT" && d.Member == "campaign_name");
        Assert.AreEqual("campaign.name", definition.Path);
        Assert.AreEqual("google-ads", definition.Platform);
        var use = index.Uses.Single(u => u.OwnerType == "GOOGLE_ADS_REPORT" && u.Member == "campaign_name");
        Assert.AreEqual("Uses.cs", use.File);
        Assert.AreEqual("campaign.name", use.Path);
        Assert.AreEqual("Build", use.Enclosing);
        Assert.AreEqual("google-ads", use.Platform);
    }

    [TestMethod]
    public void Sa360KeepsSamePathSeparateFromGoogle()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        var matching = index.Definitions.Where(d => d.Path == "campaign.name").ToList();
        Assert.HasCount(2, matching);
        Assert.IsTrue(matching.Any(d => d.OwnerType == "SA360_REPORT_FIELDS" && d.Platform == "sa360"));
        Assert.IsTrue(index.Uses.Any(u => u.OwnerType == "SA360_REPORT_FIELDS" && u.Platform == "sa360"));
    }

    [TestMethod]
    public void CommentsDoNotProduceUses()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        Assert.HasCount(2, index.Uses);
        Assert.IsFalse(index.Uses.Any(u => u.Member == "ad_group_id"));
    }

    [TestMethod]
    public void UnlistedFileIsExcluded()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        Assert.AreEqual(4, index.FileCount);
        Assert.IsFalse(index.Uses.Any(u => u.File == "Unlisted.cs"));
    }

    [TestMethod]
    public void GaqlLiteralHasCandidatePlatformResourceAndPaths()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        var query = index.Queries.Single(q => q.Text == "SELECT campaign.id, ad_group.id FROM campaign");
        Assert.AreEqual("google-ads-candidate", query.Platform);
        Assert.AreEqual("campaign", query.Resource);
        CollectionAssert.AreEquivalent(new[] { "campaign.id", "ad_group.id" }, query.Paths);
    }

    [TestMethod]
    public void MysqlConcatenationStaysUnknown()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        var query = index.Queries.Single(q => q.File == "Mysql.cs");
        Assert.AreEqual("unknown", query.Platform);
        Assert.AreEqual("users", query.Resource);
        StringAssert.Contains(query.Text, "SELECT");
    }

    [TestMethod]
    public void InterpolatedQueryIsMarked()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        var query = index.Queries.Single(q => q.IsInterpolated && q.File == "Uses.cs");
        Assert.AreEqual("google-ads-candidate", query.Platform);
        Assert.IsTrue(index.Queries.Single(q => q.File == "Mysql.cs").IsInterpolated, "concatenated {x} placeholder is dynamic");
        CollectionAssert.Contains(query.Paths, "campaign.id");
    }

    [TestMethod]
    public void MultipleSdkVersionsAreSummarizedPerFile()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        CollectionAssert.AreEquivalent(new[] { 23, 25 }, index.SdkVersionsByFile["Uses.cs"]);
        Assert.IsGreaterThanOrEqualTo(3, index.Counts.SdkVersionRefs);
    }

    [TestMethod]
    public void SharedFileIsIndexedOnceWithBothProjects()
    {
        using var fixture = new Fixture();
        var index = fixture.Run();
        Assert.AreEqual(4, index.FileCount);
        var use = index.Uses.Single(u => u.OwnerType == "GOOGLE_ADS_REPORT");
        CollectionAssert.AreEquivalent(new[] { "ProjectA/ProjectA.csproj", "ProjectB/ProjectB.csproj" }, use.Projects);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root;
        private readonly string output;

        public Fixture()
        {
            string repo = FindRepoRoot();
            root = Path.Combine(repo, "data", "test-fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string source = Path.Combine(repo, "tests", "Radar.Tests", "Fixtures", "CodeIndex");
            foreach (string path in Directory.GetFiles(source))
                File.Copy(path, Path.Combine(root, Path.GetFileName(path)));
            string setPath = Path.Combine(root, "compile-set.json");
            File.WriteAllText(setPath, File.ReadAllText(setPath).Replace("{{ROOT}}", root.Replace('\\', '/')));
            output = Path.Combine(root, "index.json");
        }

        public CodeIndexResult Run()
        {
            int exit = Program.Main(["index", "--repo", root, "--compile-set", Path.Combine(root, "compile-set.json"), "--out", output]);
            Assert.AreEqual(0, exit);
            var index = JsonSerializer.Deserialize<CodeIndexResult>(File.ReadAllText(output), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.IsNotNull(index);
            return index;
        }

        public void Dispose() => Directory.Delete(root, recursive: true);

        private static string FindRepoRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx"))) return directory.FullName;
            throw new DirectoryNotFoundException("Repository root not found");
        }
    }
}
