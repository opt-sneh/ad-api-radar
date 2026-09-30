using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace Radar;

public sealed class CompileSetResult
{
    public required string Repo { get; init; }
    public required string Solution { get; init; }
    public required string Configuration { get; init; }
    public long ElapsedMs { get; set; }
    public List<ProjectResult> Projects { get; init; } = [];
}

public sealed class ProjectResult
{
    public required string ProjectPath { get; init; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int CompiledFileCount { get; set; }
    public int DuplicateCount { get; set; }
    public int ExcludedGeneratedCount { get; set; }
    public List<CompileFile> Files { get; init; } = [];
}

public sealed class CompileFile
{
    public required string FullPath { get; init; }
    public required string RelativePath { get; init; }
    public required bool Linked { get; init; }
    public string? Link { get; init; }
}

internal static class CompileSetEvaluator
{
    public static CompileSetResult Evaluate(string repo, string solution, string configuration)
    {
        var result = new CompileSetResult
        {
            Repo = repo,
            Solution = Path.GetRelativePath(repo, solution),
            Configuration = configuration,
        };

        foreach (var entry in SolutionFile.Parse(solution).ProjectsInOrder)
        {
            if (!entry.AbsolutePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;

            string projectPath = Path.GetFullPath(entry.AbsolutePath);
            string projectDirectory = Path.GetDirectoryName(projectPath)!;
            var projectResult = new ProjectResult
            {
                ProjectPath = Path.GetRelativePath(repo, projectPath),
            };
            result.Projects.Add(projectResult);

            try
            {
                using var collection = new ProjectCollection(
                    new Dictionary<string, string> { ["Configuration"] = configuration }
                );
                var project = collection.LoadProject(projectPath);
                // Multi-targeting projects list no Compile items until a TargetFramework is chosen: take the union.
                string[] frameworks = project.GetPropertyValue("TargetFramework").Length == 0
                    ? project.GetPropertyValue("TargetFrameworks").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    : [];
                var items = frameworks.Length == 0
                    ? project.GetItems("Compile").ToList()
                    : frameworks.SelectMany(framework => collection.LoadProject(projectPath,
                        new Dictionary<string, string> { ["Configuration"] = configuration, ["TargetFramework"] = framework },
                        null).GetItems("Compile")).DistinctBy(item => item.EvaluatedInclude, StringComparer.OrdinalIgnoreCase).ToList();
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    string fullPath = Path.GetFullPath(
                        Path.Combine(projectDirectory, item.EvaluatedInclude)
                    );
                    if (!seenPaths.Add(fullPath))
                    {
                        projectResult.DuplicateCount++;
                        continue;
                    }

                    string relativeToProject = Path.GetRelativePath(projectDirectory, fullPath);
                    if (HasGeneratedSegment(relativeToProject))
                    {
                        projectResult.ExcludedGeneratedCount++;
                        continue;
                    }

                    bool linked =
                        relativeToProject == ".."
                        || relativeToProject.StartsWith(
                            ".." + Path.DirectorySeparatorChar,
                            StringComparison.Ordinal
                        );
                    string link = item.GetMetadataValue("Link");
                    projectResult.Files.Add(
                        new CompileFile
                        {
                            FullPath = fullPath,
                            RelativePath = Path.GetRelativePath(repo, fullPath),
                            Linked = linked,
                            Link = string.IsNullOrEmpty(link) ? null : link,
                        }
                    );
                }
                projectResult.CompiledFileCount = projectResult.Files.Count;
                projectResult.Success = true;
            }
            catch (Exception ex)
            {
                projectResult.Files.Clear();
                projectResult.CompiledFileCount = 0;
                projectResult.DuplicateCount = 0;
                projectResult.ExcludedGeneratedCount = 0;
                projectResult.Error = ex.Message;
            }
        }

        return result;
    }

    private static bool HasGeneratedSegment(string relativePath) =>
        relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment =>
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            );
}
