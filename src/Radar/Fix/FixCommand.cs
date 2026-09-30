using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Radar;

public static class FixCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
    private static readonly Regex Definition = new(
        @"^\s*public static \w+ \w+ = new \w+\(""(?<path>(?:[^""\\]|\\.)*)""\);\s*$",
        RegexOptions.Compiled
    );
    private const string Instruction =
        "Draft the minimal fix as a unified diff against the repo root. Do not change unrelated code. If Google's documentation says the call is rejected, prefer skipping/guarding the operation the same way existing code already does; cite the existing guard if you find one. State how to test it.";

    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (
                i + 1 >= args.Length
                || args[i] is not ("--findings" or "--repo" or "--out")
                || !options.TryAdd(args[i], args[i + 1])
            )
                return 2;
        if (
            !options.ContainsKey("--findings")
            || !options.ContainsKey("--repo")
            || !options.ContainsKey("--out")
        )
            return 2;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(options["--findings"]));
            var property = document
                .RootElement.EnumerateObject()
                .First(p => p.Name.Equals("findings", StringComparison.OrdinalIgnoreCase));
            var findings =
                property.Value.Deserialize<List<Finding>>(JsonOptions)
                ?? throw new JsonException("Missing findings.");
            Draft(findings, options["--repo"], options["--out"]);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"fix: {error.Message}");
            return 1;
        }
    }

    public static void Draft(IReadOnlyList<Finding> findings, string repo, string output)
    {
        repo = Path.GetFullPath(repo);
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        var skipped = new List<string>();
        var patch = new StringBuilder();
        int deleted = 0;
        foreach (
            var fileGroup in findings
                .Where(f => f.Category == "CLEANUP" && f.UseCount == 0 && f.Line > 0)
                .GroupBy(f => f.File)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
        )
        {
            string file = fileGroup.Key.Replace('\\', '/');
            string path = SourcePath(repo, file);
            if (!File.Exists(path))
            {
                skipped.Add($"{file}: source file missing");
                continue;
            }
            var lines = ReadLines(File.ReadAllText(path));
            var remove = new SortedSet<int>();
            foreach (var finding in fileGroup.OrderBy(f => f.Line))
            {
                if (finding.Line > lines.Count)
                {
                    skipped.Add($"{file}:{finding.Line}: line is outside the file");
                    continue;
                }
                var match = Definition.Match(lines[finding.Line - 1].Text);
                string escaped = finding.Path.Replace("\\", "\\\\").Replace("\"", "\\\"");
                if (!match.Success || match.Groups["path"].Value != escaped)
                    skipped.Add($"{file}:{finding.Line}: definition does not match the path");
                else
                {
                    remove.Add(finding.Line);
                    if (
                        finding.Line > 1
                        && finding.Line < lines.Count
                        && string.IsNullOrWhiteSpace(lines[finding.Line - 2].Text)
                        && string.IsNullOrWhiteSpace(lines[finding.Line].Text)
                    )
                        remove.Add(finding.Line + 1);
                }
            }
            if (remove.Count == 0)
                continue;
            deleted += remove.Count;
            patch
                .Append("diff --git a/")
                .Append(file)
                .Append(" b/")
                .Append(file)
                .Append('\n')
                .Append("--- a/")
                .Append(file)
                .Append('\n')
                .Append("+++ b/")
                .Append(file)
                .Append('\n');
            var ranges = new List<(int Start, int End)>();
            foreach (int line in remove)
            {
                int start = Math.Max(1, line - 3),
                    end = Math.Min(lines.Count, line + 3);
                if (ranges.Count > 0 && start <= ranges[^1].End + 1)
                    ranges[^1] = (ranges[^1].Start, Math.Max(end, ranges[^1].End));
                else
                    ranges.Add((start, end));
            }
            foreach (var (start, end) in ranges)
            {
                int oldCount = end - start + 1;
                int newCount = oldCount - remove.Count(line => line >= start && line <= end);
                int newStart = start - remove.Count(line => line < start);
                if (newCount == 0)
                    newStart--;
                patch
                    .Append("@@ -")
                    .Append(start)
                    .Append(',')
                    .Append(oldCount)
                    .Append(" +")
                    .Append(newStart)
                    .Append(',')
                    .Append(newCount)
                    .Append(" @@\n");
                for (int line = start; line <= end; line++)
                {
                    patch.Append(remove.Contains(line) ? '-' : ' ').Append(lines[line - 1].Text);
                    if (lines[line - 1].Ending.Length > 0)
                        patch.Append(lines[line - 1].Ending);
                    else
                        patch.Append("\n\\ No newline at end of file\n");
                }
            }
        }
        AtomicFile.WriteProduced(
            Path.Combine(output, "cleanup.patch"),
            () => Encoding.UTF8.GetBytes(patch.ToString())
        );

        var briefs = new List<string>();
        var groups = findings
            .Where(ReportFindings.IsActionable)
            .GroupBy(ReportFindings.GroupKey)
            .OrderBy(g => ReportFindings.CategoryOrder(g.First().Category))
            .ThenBy(g => ReportFindings.SeverityOrder(g.First().Severity))
            .ThenByDescending(g => g.First().UseCount)
            .ToArray();
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            var first = group.First();
            string slug = Regex.Replace(first.Path.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slug.Length == 0)
                slug = "finding";
            if (slug.Length > 60)
                slug = slug[..60].TrimEnd('-');
            string relative = $"briefs/{i + 1:00}-{slug}.md";
            var brief = new StringBuilder()
                .Append("# ")
                .Append(first.Path)
                .Append("\n\n")
                .Append("Category: ")
                .Append(first.Category)
                .Append("; severity: ")
                .Append(first.Severity)
                .Append("; confidence: ")
                .Append(first.Confidence)
                .Append("\n\n")
                .Append("Change: ")
                .Append(first.ChangeId ?? "unknown")
                .Append("\n\n")
                .Append(first.Message)
                .Append("\n\n");
            foreach (
                var evidence in group.SelectMany(f => f.Evidence).Distinct(StringComparer.Ordinal)
            )
                brief.Append("- ").Append(evidence).Append('\n');
            if (first.SuggestedReplacement is not null)
                brief
                    .Append("\nSuggested replacement: ")
                    .Append(first.SuggestedReplacement)
                    .Append('\n');
            int locations = 0;
            foreach (
                var finding in group
                    .OrderBy(f =>
                        f.Evidence.FirstOrDefault()
                            ?.StartsWith("Enum use:", StringComparison.Ordinal) == true
                    )
                    .ThenBy(f => ReportFindings.SeverityOrder(f.Severity))
                    .ThenByDescending(f => f.UseCount)
            )
            foreach (int line in finding.Lines.Count > 0 ? finding.Lines : [finding.Line])
            {
                if (locations >= 5)
                    break;
                if (line <= 0)
                    continue;
                string path = SourcePath(repo, finding.File);
                if (!File.Exists(path))
                    continue;
                var source = ReadLines(File.ReadAllText(path));
                if (line > source.Count)
                    continue;
                locations++;
                brief
                    .Append("\n## ")
                    .Append(finding.File)
                    .Append(':')
                    .Append(line)
                    .Append("\n\n```text\n");
                for (
                    int number = Math.Max(1, line - 12);
                    number <= Math.Min(source.Count, line + 12);
                    number++
                )
                    brief.Append(number).Append(": ").Append(source[number - 1].Text).Append('\n');
                brief.Append("```\n");
            }
            brief.Append("\n").Append(Instruction).Append('\n');
            AtomicFile.WriteProduced(
                Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar)),
                () => Encoding.UTF8.GetBytes(brief.ToString())
            );
            briefs.Add(relative);
        }
        var readme = new StringBuilder("# Fix drafts\n\n")
            .Append("Deleted lines: ")
            .Append(deleted)
            .Append("\nSkipped: ")
            .Append(skipped.Count)
            .Append("\nBriefs: ")
            .Append(briefs.Count)
            .Append("\n\n");
        foreach (string item in skipped)
            readme.Append("- Skipped ").Append(item).Append('\n');
        foreach (string item in briefs)
            readme.Append("- ").Append(item).Append('\n');
        AtomicFile.WriteProduced(
            Path.Combine(output, "README.md"),
            () => Encoding.UTF8.GetBytes(readme.ToString())
        );
    }

    private static string SourcePath(string repo, string file)
    {
        string full = Path.GetFullPath(
            Path.Combine(repo, file.Replace('/', Path.DirectorySeparatorChar))
        );
        if (
            !full.StartsWith(
                repo.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            )
        )
            throw new ArgumentException($"Finding file escapes repo: {file}");
        return full;
    }

    private sealed record SourceLine(string Text, string Ending);

    private static List<SourceLine> ReadLines(string text)
    {
        var lines = new List<SourceLine>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n')
            {
                bool cr = i > start && text[i - 1] == '\r';
                lines.Add(new SourceLine(text[start..(cr ? i - 1 : i)], cr ? "\r\n" : "\n"));
                start = i + 1;
            }
        if (start < text.Length)
            lines.Add(new SourceLine(text[start..], ""));
        return lines;
    }
}
