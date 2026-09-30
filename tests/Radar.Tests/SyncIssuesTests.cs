using System.Diagnostics;

namespace Radar.Tests;

// scripts/sync-issues.ps1 in -DryRun mode: prints the plan, never calls GitHub.
[TestClass]
public sealed class SyncIssuesTests
{
    [TestMethod]
    public void OneIssueForTheWholeScanWithActionableFindingsOnly()
    {
        string root = RepoRoot();
        string findings = Path.Combine(Path.GetTempPath(), "radar-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(findings, """
                {"target":"v23","next":["v24","v25"],"findings":[
                  {"category":"UPCOMING_BREAK","severity":"high","confidence":"high","path":"Campaign.Gone","file":"src/A.cs","line":10,"lines":[10],"message":"Removed in v25.","evidence":["e1"],"actionable":true},
                  {"category":"UPCOMING_BREAK","severity":"high","confidence":"high","path":"Campaign.Also","file":"src/A.cs","line":20,"lines":[20],"message":"Removed in v25.","evidence":["e2"],"actionable":true},
                  {"category":"UPCOMING_BREAK","severity":"high","confidence":"high","path":"Campaign.Gone","file":"src/A.cs","line":30,"lines":[30],"message":"Removed in v25.","evidence":["e3"],"actionable":true},
                  {"category":"SILENT_RISK","severity":"medium","confidence":"high","path":"Criterion.Language","file":"src/B.cs","line":5,"lines":[5],"message":"Ignored on Search.","evidence":[],"actionable":true},
                  {"category":"SILENT_RISK","severity":"low","confidence":"low","path":"Criterion.Language","file":"src/C.cs","line":7,"lines":[7],"message":"Likely unaffected.","evidence":[],"actionable":false}
                ]}
                """);
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(root, "scripts", "sync-issues.ps1"), "-Findings", findings, "-DryRun" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            Assert.AreEqual(0, process.ExitCode, output + stderr.Result);
            var creates = output.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("create", StringComparison.Ordinal)).ToArray();
            // One Issue for everything; the repeated Campaign.Gone in A.cs counts once, C.cs is not actionable.
            CollectionAssert.AreEqual(new[] { "create  [radar] API upgrade v23 -> v25: 3 findings in 2 files" }, creates);
            StringAssert.Contains(output, "Scan Issue: 3 actionable findings in 2 files.");
            Assert.IsFalse(output.Contains("C.cs", StringComparison.Ordinal), "non-actionable findings get no Issue");
        }
        finally { if (File.Exists(findings)) File.Delete(findings); }
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
