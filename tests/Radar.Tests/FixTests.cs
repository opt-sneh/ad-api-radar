using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class FixTests
{
    [TestMethod]
    public void DraftsMergedCrlfPatchAndBriefAndRecordsSkippedLine()
    {
        string root = RepoRoot();
        string fixture = Path.Combine(root, "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(fixture, "repo"), output = Path.Combine(fixture, "out");
        try
        {
            Directory.CreateDirectory(repo);
            string source = "one\r\n  \r\npublic static Field Old = new Field(\"campaign.old\");\r\n \r\npublic static Field Gone = new Field(\"campaign.gone\");\r\n \r\nseven\r\neight\r\n";
            File.WriteAllText(Path.Combine(repo, "Fields.cs"), source);
            Finding Cleanup(string path, int line) => new("id", "CLEANUP", "info", path, "google-ads", "Fields.cs", line, 0, [], "stale");
            var actionable = new Finding("a", "BREAKING_RUNTIME", "high", "campaign.bad", "google-ads", "Fields.cs", 4, 1, ["Evidence"], "Breaks");
            FixCommand.Draft([Cleanup("campaign.old", 3), Cleanup("campaign.gone", 5), Cleanup("wrong", 2), actionable,
                actionable with { Id = "b", PossiblyHandled = true, Path = "campaign.handled" }], repo, output);
            string patch = File.ReadAllText(Path.Combine(output, "cleanup.patch"));
            string expected = "diff --git a/Fields.cs b/Fields.cs\n--- a/Fields.cs\n+++ b/Fields.cs\n@@ -1,8 +1,4 @@\n one\r\n   \r\n-public static Field Old = new Field(\"campaign.old\");\r\n- \r\n-public static Field Gone = new Field(\"campaign.gone\");\r\n- \r\n seven\r\n eight\r\n";
            Assert.AreEqual(expected, patch);
            StringAssert.Contains(File.ReadAllText(Path.Combine(output, "README.md")), "Deleted lines: 4\nSkipped: 1\nBriefs: 1");
            StringAssert.Contains(File.ReadAllText(Path.Combine(output, "README.md")), "definition does not match the path");
            string brief = File.ReadAllText(Path.Combine(output, "briefs", "01-campaign-bad.md"));
            StringAssert.Contains(brief, "3: public static Field Old");
            StringAssert.Contains(brief, "Draft the minimal fix as a unified diff");
            Assert.AreEqual(source, File.ReadAllText(Path.Combine(repo, "Fields.cs")));
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public void PatchMarksMissingFinalNewline()
    {
        string fixture = Path.Combine(RepoRoot(), "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(fixture, "repo"), output = Path.Combine(fixture, "out");
        try
        {
            Directory.CreateDirectory(repo);
            File.WriteAllText(Path.Combine(repo, "Only.cs"), "public static Field Old = new Field(\"campaign.old\");");
            FixCommand.Draft([new Finding("id", "CLEANUP", "info", "campaign.old", "google-ads", "Only.cs", 1, 0, [], "stale")], repo, output);
            string patch = File.ReadAllText(Path.Combine(output, "cleanup.patch"));
            StringAssert.Contains(patch, "@@ -1,1 +0,0 @@\n");
            StringAssert.Contains(patch, "-public static Field Old = new Field(\"campaign.old\");\n\\ No newline at end of file\n");
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }

    [TestMethod]
    public void BriefListsEnumUseLocationsAfterOtherUses()
    {
        string fixture = Path.Combine(RepoRoot(), "data", "test-fixtures", Guid.NewGuid().ToString("N"));
        string repo = Path.Combine(fixture, "repo"), output = Path.Combine(fixture, "out");
        try
        {
            Directory.CreateDirectory(repo);
            File.WriteAllText(Path.Combine(repo, "Regular.cs"), "regular\n");
            File.WriteAllText(Path.Combine(repo, "Enum.cs"), "enum\n");
            var enumUse = new Finding("e", "UPCOMING_BREAK", "high", "campaign.old", "google-ads", "Enum.cs", 1, 9,
                ["Enum use: Campaign.Old"], "change") { ChangeId = "C1" };
            var regular = enumUse with { Id = "r", File = "Regular.cs", UseCount = 1, Evidence = ["Use: Campaign.Old"] };
            FixCommand.Draft([enumUse, regular], repo, output);
            string brief = File.ReadAllText(Path.Combine(output, "briefs", "01-campaign-old.md"));
            Assert.IsLessThan(brief.IndexOf("## Enum.cs:1", StringComparison.Ordinal),
                brief.IndexOf("## Regular.cs:1", StringComparison.Ordinal));
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
