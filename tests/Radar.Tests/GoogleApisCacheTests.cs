using System.Diagnostics;
using Radar;

namespace Radar.Tests;

[TestClass]
public sealed class GoogleApisCacheTests
{
    [TestMethod]
    public void FreshNoCheckoutCloneHasNoLocalChangesButAnEditedCheckoutDoes()
    {
        // Fresh CI machine: `radar run` discovers versions first, which leaves a --no-checkout clone.
        // Its `git status` lists every file as deleted; that must not block the snapshot step.
        string root = Path.Combine(Path.GetTempPath(), "radar-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source");
        string clone = Path.Combine(root, "clone");
        try
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.proto"), "syntax = \"proto3\";\n");
            Git(source, "init", "-q");
            Git(source, "-c", "user.name=t", "-c", "user.email=t@t", "add", ".");
            Git(source, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "one");
            Git(root, "clone", "-q", "--no-checkout", source, clone);

            Assert.IsFalse(GoogleApisCache.HasLocalChanges(clone), "a never-checked-out clone has nothing to protect");

            Git(clone, "checkout", "-q", "--detach", "HEAD");
            Assert.IsFalse(GoogleApisCache.HasLocalChanges(clone));
            File.AppendAllText(Path.Combine(clone, "a.proto"), "// edited\n");
            Assert.IsTrue(GoogleApisCache.HasLocalChanges(clone), "a hand-edited checkout is still protected");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal); // git marks pack files read-only
                Directory.Delete(root, true);
            }
        }
    }

    private static void Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"git {string.Join(' ', arguments)}: {error}");
    }
}
