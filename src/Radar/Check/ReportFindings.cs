namespace Radar;

public static class ReportFindings
{
    public static readonly string[] Categories =
    [
        "BREAKING_RUNTIME", "BREAKING_COMPILE", "UPCOMING_BREAK", "MIGRATION", "SILENT_RISK", "RESILIENCE",
        "DEPRECATED", "SUNSET", "COMPAT", "CLEANUP", "UNKNOWN",
    ];

    // Low severity with low confidence is radar saying "likely unaffected": listed, but not queued for action.
    public static bool IsActionable(Finding finding) => !finding.PossiblyHandled
        && !(finding.Severity == "low" && finding.Confidence == "low") &&
        (finding.Category is "BREAKING_COMPILE" or "BREAKING_RUNTIME" or "UPCOMING_BREAK" or "SILENT_RISK" or "DEPRECATED"
            || finding.Severity == "high" && finding.Category is "SUNSET" or "COMPAT" or "MIGRATION" or "RESILIENCE");

    public static int CategoryOrder(string category)
    {
        int index = Array.IndexOf(Categories, category);
        return index < 0 ? Categories.Length : index;
    }

    public static int SeverityOrder(string severity) => severity.ToLowerInvariant() switch
    {
        "high" => 0, "medium" => 1, "low" => 2, "info" => 3, _ => 4,
    };

    public static string GroupKey(Finding finding) => finding.ChangeId is null
        ? "local\0" + finding.Category + "\0" + finding.Path
        : "change\0" + finding.ChangeId;
}
