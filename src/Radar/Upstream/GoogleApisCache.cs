using System.Diagnostics;

namespace Radar;

internal sealed class GoogleApisCache(string repoRoot)
{
    public const string RepositoryUrl = "https://github.com/googleapis/googleapis.git";
    public string DirectoryPath { get; } = Path.Combine(repoRoot, "data", "cache", "googleapis");

    public string[] FetchVersionFolders()
    {
        if (!System.IO.Directory.Exists(DirectoryPath))
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(DirectoryPath)!);
            Git(repoRoot, "clone", "--filter=blob:none", "--sparse", "--no-checkout",
                RepositoryUrl, DirectoryPath);
        }
        if (!System.IO.Directory.Exists(Path.Combine(DirectoryPath, ".git")))
            throw new InvalidOperationException($"Googleapis cache is not a git clone: {DirectoryPath}");
        string remote = Git(DirectoryPath, "remote", "get-url", "origin").Trim();
        if (remote != RepositoryUrl && remote != RepositoryUrl[..^4])
            throw new InvalidOperationException($"Googleapis cache has an unexpected origin: {remote}");
        Git(DirectoryPath, "fetch", "--filter=blob:none", "origin", "master");
        return Git(DirectoryPath, "ls-tree", "--name-only", "FETCH_HEAD:google/ads/googleads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(SchemaModel.IsVersion)
            .OrderBy(version => int.Parse(version[1..]))
            .ToArray();
    }

    public string Checkout(string reference, IReadOnlyList<string> versions)
    {
        bool existing = System.IO.Directory.Exists(DirectoryPath);
        if (!existing)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(DirectoryPath)!);
            Git(
                repoRoot,
                "clone",
                "--filter=blob:none",
                "--sparse",
                "--no-checkout",
                RepositoryUrl,
                DirectoryPath
            );
        }
        if (!System.IO.Directory.Exists(Path.Combine(DirectoryPath, ".git")))
            throw new InvalidOperationException(
                $"Googleapis cache is not a git clone: {DirectoryPath}"
            );
        string remote = Git(DirectoryPath, "remote", "get-url", "origin").Trim();
        if (remote != RepositoryUrl && remote != RepositoryUrl[..^4])
            throw new InvalidOperationException(
                $"Googleapis cache has an unexpected origin: {remote}"
            );
        if (existing && Git(DirectoryPath, "status", "--porcelain").Length != 0)
            throw new InvalidOperationException(
                "Googleapis cache has local changes; refusing to overwrite them."
            );

        var sparsePaths = versions
            .Select(version => $"google/ads/googleads/{version}")
            .Concat(["google/api", "google/rpc", "google/longrunning", "google/type"]);
        Git(DirectoryPath, ["sparse-checkout", "set", .. sparsePaths]);
        Git(DirectoryPath, "fetch", "--filter=blob:none", "origin", reference);
        Git(DirectoryPath, "checkout", "--detach", "FETCH_HEAD");
        return Git(DirectoryPath, "rev-parse", "HEAD").Trim();
    }

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        using var process =
            Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string stdout = stdoutTask.GetAwaiter().GetResult();
        string stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"git {string.Join(" ", arguments)} failed (exit {process.ExitCode}): {stderr.Trim()}"
            );
        return stdout;
    }
}
