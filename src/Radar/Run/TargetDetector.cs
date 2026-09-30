namespace Radar;

internal static class TargetDetector
{
    public static string Detect(CodeIndexResult index)
    {
        var selected = index.SdkVersionsByFile
            .SelectMany(pair => pair.Value.Distinct().Select(version => (pair.Key, version)))
            .GroupBy(item => item.version)
            .OrderByDescending(group => group.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count())
            .ThenByDescending(group => group.Key)
            .FirstOrDefault();
        return selected is null
            ? throw new InvalidOperationException("Index has no Google Ads SDK version references.")
            : $"v{selected.Key}";
    }

    public static string[] Versions(string target, int newest)
    {
        if (!SchemaModel.IsVersion(target) || newest < int.Parse(target[1..]))
            throw new ArgumentException("No upstream versions include the detected target.");
        return Enumerable.Range(int.Parse(target[1..]), newest - int.Parse(target[1..]) + 1)
            .Select(number => $"v{number}").ToArray();
    }

    public static string[] NewVersions(IEnumerable<string> current, IEnumerable<string> previous) =>
        current.Except(previous, StringComparer.Ordinal).ToArray();

    public static string[] RequestedVersions(IEnumerable<string> existing, string target, int newest) =>
        existing.Concat(Versions(target, newest))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(version => int.Parse(version[1..]))
            .ToArray();
}
