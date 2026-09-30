using System.Diagnostics;

namespace Radar.Tests;

// Runs scripts/verify-fix.ps1 for real (pwsh + two dotnet builds per case), so these are slow.
// They pin the P0 rule: a build that failed, or failed in a way we cannot read, is never a pass.
[TestClass]
[TestCategory("Slow")]
public sealed class VerifyFixTests
{
    private const string Program = "System.Console.WriteLine(\"hi\");\n";

    // Fails the build with an error that has no compiler code, i.e. nothing the script can parse.
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
          <Target Name="RadarUnreadableFailure" BeforeTargets="Build"
                  Condition="$([System.IO.File]::ReadAllText('$(MSBuildProjectDirectory)/Program.cs').Contains('RADAR_FAIL'))">
            <Error Text="radar test failure" />
          </Target>
        </Project>
        """;

    private static string Patch(string replacement) =>
        "--- a/app/Program.cs\n+++ b/app/Program.cs\n@@ -1 +1 @@\n-System.Console.WriteLine(\"hi\");\n+" + replacement + "\n";

    [TestMethod]
    public void CleanPatchIsVerifiedBuild()
    {
        var (exit, output) = Verify(Patch("System.Console.WriteLine(\"hello\");"));
        Assert.AreEqual(0, exit, output);
        StringAssert.Contains(output, "VERIFY PASS VERIFIED_BUILD");
    }

    [TestMethod]
    public void PatchThatBreaksAWorkingBuildFails()
    {
        var (exit, output) = Verify(Patch("NoSuchMethod();"));
        Assert.AreNotEqual(0, exit, output);
        StringAssert.Contains(output, "BUILD_FAILED");
        Assert.IsFalse(output.Contains("VERIFY PASS", StringComparison.Ordinal), output);
    }

    [TestMethod]
    public void FailureWithoutReadableErrorsIsInconclusiveNotPass()
    {
        var (exit, output) = Verify(Patch("System.Console.WriteLine(\"hi\"); // RADAR_FAIL"));
        Assert.AreNotEqual(0, exit, output);
        StringAssert.Contains(output, "INCONCLUSIVE");
        Assert.IsFalse(output.Contains("VERIFY PASS", StringComparison.Ordinal), output);
    }

    [TestMethod]
    public void StillFailingBuildWithOnlyOldErrorsIsInconclusive()
    {
        // No new errors, but the patched build still exits non-zero: not proof the fix works.
        var (exit, output) = Verify(Patch("System.Console.WriteLine(\"hello\");"), brokenFile: "class Broken { void M() { Missing(); } }\n");
        Assert.AreNotEqual(0, exit, output);
        StringAssert.Contains(output, "INCONCLUSIVE NO_NEW_ERRORS");
        Assert.IsFalse(output.Contains("VERIFY PASS", StringComparison.Ordinal), output);
    }

    [TestMethod]
    public void NewErrorOnTopOfOldErrorsFails()
    {
        var (exit, output) = Verify(Patch("NoSuchMethod();"), brokenFile: "class Broken { void M() { Missing(); } }\n");
        Assert.AreNotEqual(0, exit, output);
        StringAssert.Contains(output, "VERIFY FAIL: 1 new errors");
        StringAssert.Contains(output, "CS0103");
    }

    private static (int Exit, string Output) Verify(string patch, string? brokenFile = null)
    {
        string root = RepoRoot();
        string id = "test-" + Guid.NewGuid().ToString("N")[..12];
        string bench = Path.Combine(root, "data", "bench", id);
        string patchFile = Path.Combine(root, "data", "test-fixtures", id + ".patch");
        string verifyCopy = Path.Combine(root, "data", "verify", id);
        try
        {
            Directory.CreateDirectory(Path.Combine(bench, "app"));
            File.WriteAllText(Path.Combine(bench, "app", "App.csproj"), Project);
            File.WriteAllText(Path.Combine(bench, "app", "Program.cs"), Program);
            if (brokenFile is not null)
                File.WriteAllText(Path.Combine(bench, "app", "Broken.cs"), brokenFile);
            Directory.CreateDirectory(Path.GetDirectoryName(patchFile)!);
            File.WriteAllText(patchFile, patch);

            var start = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(root, "scripts", "verify-fix.ps1"),
                "-Bench", bench, "-Patch", patchFile, "-Project", "app/App.csproj", "-Name", id })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            string stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            {
                process.Kill(true);
                Assert.Fail("verify-fix.ps1 did not finish within 5 minutes.");
            }
            return (process.ExitCode, stdout + stderr.Result);
        }
        finally
        {
            foreach (string directory in new[] { bench, verifyCopy })
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            if (File.Exists(patchFile)) File.Delete(patchFile);
        }
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }
}
