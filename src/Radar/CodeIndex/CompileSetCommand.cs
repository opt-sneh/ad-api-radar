using System.Diagnostics;
using System.Text.Json;
using Microsoft.Build.Locator;

namespace Radar;

public static class CompileSetCommand
{
    private static readonly object RegistrationLock = new();

    public static int Run(string repo, string solution, string configuration, string output)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            lock (RegistrationLock)
            {
                if (!MSBuildLocator.IsRegistered)
                    MSBuildLocator.RegisterDefaults();
            }

            string repoPath = Path.GetFullPath(repo);
            string solutionPath = Path.GetFullPath(Path.Combine(repoPath, solution));
            string outputPath = Path.GetFullPath(output);

            if (!Directory.Exists(repoPath))
                throw new DirectoryNotFoundException($"Repository not found: {repoPath}");
            if (!File.Exists(solutionPath))
                throw new FileNotFoundException("Solution not found", solutionPath);
            if (IsWithin(outputPath, repoPath))
                throw new ArgumentException("Output must be outside --repo.");

            var result = CompileSetEvaluator.Evaluate(repoPath, solutionPath, configuration);
            timer.Stop();
            result.ElapsedMs = timer.ElapsedMilliseconds;

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllText(
                outputPath,
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })
            );

            int ok = result.Projects.Count(project => project.Success);
            int failed = result.Projects.Count - ok;
            int files = result.Projects.Sum(project => project.CompiledFileCount);
            int linked = result.Projects.Sum(project => project.Files.Count(file => file.Linked));
            Console.WriteLine(
                $"Projects: {ok} ok, {failed} failed; files: {files}; linked: {linked}; excluded generated: {result.Projects.Sum(project => project.ExcludedGeneratedCount)}; elapsed: {result.ElapsedMs} ms"
            );
            foreach (var project in result.Projects.Where(project => !project.Success))
                Console.Error.WriteLine($"FAILED {project.ProjectPath}: {project.Error}");
            Console.WriteLine($"JSON: {outputPath}");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static bool IsWithin(string path, string directory)
    {
        string prefix = Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar;
        return path.Equals(directory, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
