using System.Text;
using System.Text.Json;

namespace Radar;

public static class ScoreCommand
{
    public static int Run(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i += 2)
            if (i + 1 >= args.Length || args[i] is not ("--findings" or "--key" or "--out")
                || !options.TryAdd(args[i], args[i + 1])) return 2;
        if (!options.ContainsKey("--findings") || !options.ContainsKey("--key")) return 2;
        try
        {
            using var findingsJson = JsonDocument.Parse(File.ReadAllText(options["--findings"]));
            using var keyJson = JsonDocument.Parse(File.ReadAllText(options["--key"]));
            string markdown = Evaluate(findingsJson.RootElement, keyJson.RootElement);
            Console.Write(markdown);
            if (options.TryGetValue("--out", out string? output))
                AtomicFile.WriteProduced(output, () => Encoding.UTF8.GetBytes(markdown));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"score: {error.Message}");
            return 1;
        }
    }

    public static string Evaluate(JsonElement findingsJson, JsonElement keyJson)
    {
        var findingsArray = Required(findingsJson, "findings", JsonValueKind.Array);
        var items = Required(keyJson, "items", JsonValueKind.Array);
        var findings = findingsArray.EnumerateArray().Select(ReadFinding).ToArray();
        var matched = new HashSet<int>();
        var result = new StringBuilder("| id | counted | status | matched at | description |\n| --- | --- | --- | --- | --- |\n");
        int counted = 0, found = 0, partial = 0;
        var ambiguous = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            string id = Text(Required(item, "id", JsonValueKind.String));
            string description = Text(Required(item, "description", JsonValueKind.String));
            bool included = Required(item, "counted", JsonValueKind.True, JsonValueKind.False).GetBoolean();
            if (included) counted++;
            var foundRule = Required(item, "found", JsonValueKind.Object);
            if (Matches(findings, foundRule).Take(2).Count() > 1) ambiguous.Add(id);
            int index = Find(findings, foundRule, matched);
            string status = "found";
            if (index < 0)
            {
                status = "partial";
                index = Try(item, "partial", out var rule) ? Find(findings, rule, matched) : -1;
            }
            if (index < 0) status = "missed";
            else matched.Add(index);
            if (included && status == "found") found++;
            if (included && status == "partial") partial++;
            string at = index < 0 ? "" : $"{findings[index].File}:{findings[index].Line}";
            result.Append("| ").Append(Cell(id)).Append(" | ").Append(included ? "true" : "false")
                .Append(" | ").Append(status).Append(" | ").Append(Cell(at)).Append(" | ")
                .Append(Cell(description)).Append(" |\n");
        }
        double recall = counted == 0 ? 0 : 100.0 * (found + 0.5 * partial) / counted;
        result.Append("\nRecall (counted items): found ").Append(found).Append(" + partial ").Append(partial)
            .Append(" of ").Append(counted).Append(" = ").Append(recall.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)).Append("%\n\n");
        result.Append("Findings: total ").Append(findings.Length).Append("; matched a key item: ")
            .Append(matched.Count).Append("; unmatched: ").Append(findings.Length - matched.Count).Append("\n");
        if (ambiguous.Count > 0)
            result.Append("Ambiguous key items (multiple findings satisfy the found rule; review matching): ")
                .Append(string.Join(", ", ambiguous.Select(Cell))).Append("\n");
        foreach (var group in findings.Where((_, index) => !matched.Contains(index)).GroupBy(f => f.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
            result.Append("- ").Append(group.Key).Append(": ").Append(group.Count()).Append('\n');
        return result.ToString();
    }

    private sealed record InputFinding(string Category, string Path, string File, string Message, string[] Evidence, int Line);

    private static InputFinding ReadFinding(JsonElement element)
    {
        string[] evidence = Try(element, "evidence", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(Text).ToArray() : [];
        int line = Try(element, "line", out value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
        return new(Text(Required(element, "category", JsonValueKind.String)), Text(Required(element, "path", JsonValueKind.String)),
            Text(Required(element, "file", JsonValueKind.String)), Text(Required(element, "message", JsonValueKind.String)), evidence, line);
    }

    private static int Find(InputFinding[] findings, JsonElement rule, HashSet<int> taken)
        => Matches(findings, rule).FirstOrDefault(i => !taken.Contains(i), -1);

    private static IEnumerable<int> Matches(InputFinding[] findings, JsonElement rule)
    {
        string[] any = List(rule, "any"), files = List(rule, "fileAny"), categories = List(rule, "categoryIn");
        if (any.Length + files.Length + categories.Length == 0) yield break;
        for (int i = 0; i < findings.Length; i++)
        {
            var finding = findings[i];
            string searchable = finding.Path + "\n" + finding.Message + "\n" + string.Join("\n", finding.Evidence);
            if ((any.Length == 0 || any.Any(s => searchable.Contains(s, StringComparison.OrdinalIgnoreCase)))
                && (files.Length == 0 || files.Any(s => finding.File.Contains(s, StringComparison.OrdinalIgnoreCase)))
                && (categories.Length == 0 || categories.Contains(finding.Category, StringComparer.Ordinal))) yield return i;
        }
    }

    private static string[] List(JsonElement element, string name) => Try(element, name, out var value)
        ? value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Select(Text).ToArray()
            : throw new JsonException($"{name} must be an array.") : [];
    private static string Text(JsonElement value) => value.GetString() ?? throw new JsonException("Null string.");
    private static string Cell(string value) => value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
    private static bool Try(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }
    private static JsonElement Required(JsonElement element, string name, params JsonValueKind[] kinds)
    {
        if (!Try(element, name, out var value) || !kinds.Contains(value.ValueKind))
            throw new JsonException($"Missing or invalid {name}.");
        return value;
    }
}
