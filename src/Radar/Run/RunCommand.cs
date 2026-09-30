using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

namespace Radar;

public static class RunCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private const string Solution = "code/backend/OptmyzrProcessorService/OptmyzrProcessorService.sln";
    private const string DefaultProps = "code/backend/Directory.Packages.props";

    public static int Run(string[] args)
    {
        string repo = "C:/server/code/optmyzr", reference = "origin/release";
        string solution = Solution, props = DefaultProps, benchPath = "code/backend";
        string? sdkOverride = null, targetOverride = null;
        string platform = "all";
        int maxAgeDays = 7;
        bool experimental = false, force = false;
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--experimental" && !experimental) { experimental = true; continue; }
                if (args[i] == "--force" && !force) { force = true; continue; }
                if (i + 1 >= args.Length) throw new ArgumentException($"Missing value for {args[i]}");
                switch (args[i])
                {
                    case "--repo": repo = args[++i]; break;
                    case "--ref": reference = args[++i]; break;
                    case "--sln": solution = args[++i]; break;
                    case "--props": props = args[++i]; break;
                    case "--bench-path": benchPath = args[++i]; break;
                    case "--sdk": sdkOverride = args[++i]; break;
                    case "--target": targetOverride = args[++i]; break;
                    case "--platform":
                        platform = args[++i];
                        if (platform is not ("all" or "google" or "microsoft"))
                            throw new ArgumentException("--platform must be all, google or microsoft.");
                        break;
                    case "--max-age-days":
                        if (!int.TryParse(args[++i], out maxAgeDays) || maxAgeDays < 0)
                            throw new ArgumentException("--max-age-days must be a nonnegative integer.");
                        break;
                    default: throw new ArgumentException($"Unknown option: {args[i]}");
                }
            }
            string root = FindRoot();
            if (targetOverride is not null && !SchemaModel.IsVersion(targetOverride))
                throw new ArgumentException("--target must be vNN.");
            repo = Path.GetFullPath(repo);
            if (!Directory.Exists(repo)) throw new DirectoryNotFoundException(repo);
            string full = ProcessOutput("git", ["-C", repo, "rev-parse", "--verify", reference]).Trim();
            if (full.Length < 11 || !full.All(Uri.IsHexDigit))
                throw new InvalidOperationException("git did not return a full commit SHA.");
            string shortSha = full[..11];
            string bench = Path.Combine(root, "data", "bench", shortSha);
            if (!Directory.Exists(bench))
                ProcessOutput("pwsh", FreezeArguments(root, full, repo, benchPath));
            if (!Directory.Exists(bench)) throw new DirectoryNotFoundException($"Benchmark was not created: {bench}");
            string runDir = Path.Combine(root, "data", "runs", shortSha);
            Directory.CreateDirectory(runDir);
            string compileSet = Path.Combine(runDir, "compile-set.json");
            string indexPath = Path.Combine(runDir, "index.json");
            string stampPath = Path.Combine(runDir, "inputs.stamp");
            string stamp = $"{solution}|{props}|{benchPath}|{typeof(RunCommand).Assembly.ManifestModule.ModuleVersionId}";
            if (force || !File.Exists(stampPath) || File.ReadAllText(stampPath) != stamp)
            {
                File.Delete(compileSet);
                File.Delete(indexPath);
                File.WriteAllText(stampPath, stamp);
            }
            if (force || !File.Exists(compileSet))
                Require(CompileSetCommand.Run(bench, solution, "Debug", compileSet), "compile-set");
            MicrosoftCommand.Result? microsoft = platform == "google" ? null
                : MicrosoftCommand.Execute(root, bench, compileSet, props, Path.Combine(runDir, "microsoft"), null, null,
                    platform == "all" ? "../report.html" : null);
            if (platform == "microsoft")
            {
                Console.WriteLine($"Commit: {full}");
                MicrosoftCommand.Print(microsoft!);
                return 0;
            }
            string sdk = ResolveInstalledSdk(bench, props, sdkOverride);
            if (force || !File.Exists(indexPath))
                Require(CodeIndexCommand.Run(["--repo", bench, "--compile-set", compileSet, "--out", indexPath]), "index");
            var index = JsonSerializer.Deserialize<CodeIndexResult>(File.ReadAllText(indexPath), JsonOptions)
                ?? throw new JsonException("Empty index.");
            string target = targetOverride ?? TargetDetector.Detect(index);

            string snapshots = Path.Combine(root, "data", "snapshots");
            string manifestPath = Path.Combine(snapshots, "manifest.json");
            SnapshotManifest? previous = File.Exists(manifestPath)
                ? JsonSerializer.Deserialize<SnapshotManifest>(File.ReadAllText(manifestPath), JsonOptions) : null;
            string[] previousVersions = ManifestVersions(previous);
            string[] previousSdks = ReadSdkVersions(Path.Combine(snapshots, "google", "sdk-deps.json"));
            string[] available = previousVersions;
            string[] desiredSdks = previousSdks;
            string? staleWarning = null;
            try
            {
                available = new GoogleApisCache(root).FetchVersionFolders();
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var nuget = http.GetFromJsonAsync<NugetIndex>(
                    "https://api.nuget.org/v3-flatcontainer/google.ads.googleads/index.json", JsonOptions)
                    .GetAwaiter().GetResult();
                string newestSdk = nuget?.Versions.Where(version => !version.Contains('-') && Version.TryParse(version, out _))
                    .OrderBy(version => Version.Parse(version)).LastOrDefault()
                    ?? throw new JsonException("NuGet returned no stable SDK versions.");
                desiredSdks = RequestedSdks(previousSdks, sdk, newestSdk);
            }
            catch (Exception error)
            {
                available = previousVersions;
                desiredSdks = RequestedSdks(previousSdks, sdk);
                staleWarning = $"STALE SOURCE WARNING: upstream discovery unavailable ({error.Message}); using existing snapshots.";
                Console.Error.WriteLine(staleWarning);
            }
            int newest = available.Select(version => int.Parse(version[1..])).DefaultIfEmpty(0).Max();
            string[] versions = TargetDetector.RequestedVersions(previousVersions, target, newest);
            string[] newVersions = TargetDetector.NewVersions(versions, previousVersions);
            string[] newSdks = TargetDetector.NewVersions(desiredSdks, previousSdks);
            foreach (string version in newVersions) Console.WriteLine($"NEW GOOGLE ADS VERSION DETECTED: {version}");
            foreach (string version in newSdks) Console.WriteLine($"NEW GOOGLE ADS SDK VERSION DETECTED: {version}");
            bool refresh = staleWarning is null && NeedsSnapshotRefresh(previous, previousVersions, versions,
                previousSdks, desiredSdks, snapshots, maxAgeDays);
            if (refresh)
            {
                string stage = Path.Combine(runDir, ".snapshot-stage-" + Guid.NewGuid().ToString("N"));
                try
                {
                    int result = SnapshotCommand.Run(["--out", stage, "--versions", string.Join(',', versions),
                        "--sdk-versions", string.Join(',', desiredSdks)]);
                    if (result != 0) throw new InvalidOperationException("snapshot command failed");
                    var staged = JsonSerializer.Deserialize<SnapshotManifest>(
                        File.ReadAllText(Path.Combine(stage, "manifest.json")), JsonOptions)
                        ?? throw new JsonException("Empty staged snapshot manifest.");
                    foreach (var source in staged.Sources)
                    {
                        string relative = source.File.Replace('/', Path.DirectorySeparatorChar);
                        AtomicFile.WriteProduced(Path.Combine(snapshots, relative),
                            () => File.ReadAllBytes(Path.Combine(stage, relative)));
                    }
                    AtomicFile.WriteProduced(manifestPath,
                        () => File.ReadAllBytes(Path.Combine(stage, "manifest.json")));
                }
                catch (Exception error)
                {
                    staleWarning = $"STALE SOURCE WARNING: snapshot refresh failed ({error.Message}); using existing snapshots.";
                    Console.Error.WriteLine(staleWarning);
                    int previousNewest = previousVersions.Select(version => int.Parse(version[1..])).DefaultIfEmpty(0).Max();
                    versions = TargetDetector.RequestedVersions(previousVersions, target, previousNewest);
                }
                finally
                {
                    if (Directory.Exists(stage)) Directory.Delete(stage, true);
                }
            }
            string[] usable = versions.Where(version => File.Exists(Path.Combine(snapshots, "google", version + ".pb"))).ToArray();
            if (!usable.Contains(target)) throw new FileNotFoundException($"Target snapshot {target} is unavailable.");
            if (usable.Length != versions.Length)
                throw new FileNotFoundException("Some requested newer snapshots are unavailable; cannot claim an all-clear.");
            string[] next = usable.Where(version => int.Parse(version[1..]) > int.Parse(target[1..])).ToArray();
            var schema = SchemaModel.Load(Path.Combine(snapshots, "google", target + ".pb"), target);
            string queriesPath = Path.Combine(runDir, "gaql-queries.json");
            GaqlValidation.Write(GaqlValidation.Export(index, schema, shortSha, [target, .. next]), queriesPath);
            string findingsPath = Path.Combine(runDir, "findings.json");
            string reportPath = Path.Combine(runDir, "report.html");
            var checkArgs = new List<string> { "--index", indexPath, "--repo", bench, "--snapshots", snapshots,
                "--target", target, "--next", string.Join(',', next), "--sdk", sdk, "--props", props,
                "--out", findingsPath, "--html", reportPath };
            if (experimental) checkArgs.Add("--experimental");
            string resultsPath = Path.Combine(runDir, "gaql-results.json");
            if (File.Exists(resultsPath))
                checkArgs.AddRange(["--gaql-results", resultsPath, "--gaql-queries", queriesPath]);
            Require(CheckCommand.Run(checkArgs.ToArray()), "check");
            Require(FixCommand.Run(["--findings", findingsPath, "--repo", bench, "--out", Path.Combine(runDir, "fixes")]), "fix");
            var report = JsonSerializer.Deserialize<CheckReport>(File.ReadAllText(findingsPath), JsonOptions)
                ?? throw new JsonException("Empty findings.");
            if (staleWarning is not null)
            {
                report.Limitations.Add(staleWarning);
                AtomicFile.WriteProduced(findingsPath, () => JsonSerializer.SerializeToUtf8Bytes(report, JsonOptions));
            }
            if (microsoft is not null)
                AtomicFile.WriteProduced(reportPath,
                    () => System.Text.Encoding.UTF8.GetBytes(CheckHtml.Render(report, "microsoft/report.html")));
            else if (staleWarning is not null)
                AtomicFile.WriteProduced(reportPath,
                    () => System.Text.Encoding.UTF8.GetBytes(CheckHtml.Render(report)));
            var summary = new RunSummary(full, target, next, sdk, newVersions, newSdks, staleWarning, report.Counts, reportPath);
            AtomicFile.WriteProduced(Path.Combine(runDir, "summary.json"), () => JsonSerializer.SerializeToUtf8Bytes(summary, JsonOptions));
            Console.WriteLine($"Commit: {full}");
            Console.WriteLine($"Target: {target}; next: {(next.Length == 0 ? "none" : string.Join(", ", next))}; SDK: {sdk}");
            Console.WriteLine($"New versions: {(newVersions.Length == 0 ? "none" : string.Join(", ", newVersions))}; new SDK: {(newSdks.Length == 0 ? "none" : string.Join(", ", newSdks))}");
            Console.WriteLine($"Sources: {(staleWarning is null ? "current" : staleWarning)}");
            Console.WriteLine("Categories: " + string.Join(", ", report.Counts.Select(pair => $"{pair.Key}={pair.Value}")));
            Console.WriteLine($"Report: {reportPath}");
            if (microsoft is not null)
                MicrosoftCommand.Print(microsoft);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"run: {error.Message}");
            return 1;
        }
    }

    private static string[] ManifestVersions(SnapshotManifest? manifest) => manifest?.Sources
        .Where(source => source.Name.StartsWith("google-protos-v", StringComparison.Ordinal))
        .Select(source => source.Name["google-protos-".Length..])
        .OrderBy(version => int.Parse(version[1..])).ToArray() ?? [];

    private static string[] ReadSdkVersions(string path) => File.Exists(path)
        ? (JsonSerializer.Deserialize<List<SdkDependencies>>(File.ReadAllText(path), JsonOptions) ?? [])
            .Select(item => item.SdkVersion)
            .OrderBy(version => Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0))
            .ToArray() : [];

    internal static string[] RequestedSdks(IEnumerable<string> existing, params string[] requested) =>
        existing.Concat(requested).Distinct(StringComparer.Ordinal)
            .OrderBy(version => Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0))
            .ToArray();

    internal static string ResolveInstalledSdk(string bench, string props, string? sdkOverride) =>
        sdkOverride ?? CheckEngine.PackageVersion(bench, "Google.Ads.GoogleAds", props).Version
            ?? throw new InvalidOperationException("Benchmark has no Google.Ads.GoogleAds package version; supply --sdk.");

    internal static string[] FreezeArguments(string root, string sha, string repo, string benchPath) =>
        ["-NoProfile", "-File", Path.Combine(root, "scripts", "freeze-benchmark.ps1"),
            "-Sha", sha, "-Repo", repo, "-Path", benchPath];

    internal static bool NeedsSnapshotRefresh(SnapshotManifest? previous, string[] previousVersions,
        string[] versions, string[] previousSdks, string[] desiredSdks, string snapshots, int maxAgeDays) =>
        previous is null
        || previous.Sources.Any(source => source.Status != "ok" || DateTime.UtcNow - source.FetchedAtUtc > TimeSpan.FromDays(maxAgeDays))
        || !versions.SequenceEqual(previousVersions)
        || !desiredSdks.SequenceEqual(previousSdks)
        || versions.Any(version => !File.Exists(Path.Combine(snapshots, "google", version + ".pb")));

    private static void Require(int code, string step)
    {
        if (code != 0) throw new InvalidOperationException($"{step} failed (exit {code}).");
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AdApiRadar.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("AdApiRadar.slnx not found above the current directory.");
    }

    private static string ProcessOutput(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        string output = stdout.GetAwaiter().GetResult();
        string error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} failed: {error.Trim()}");
        return output;
    }

    private sealed record NugetIndex(List<string> Versions);
    private sealed record RunSummary(string Commit, string Target, string[] Next, string Sdk,
        string[] NewVersions, string[] NewSdkVersions, string? StaleSourceWarning,
        Dictionary<string, int> Counts, string Report);
}
