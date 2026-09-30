namespace Radar;

public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "run")
            return RunCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "snapshot")
            return SnapshotCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "schema-diff")
            return SchemaCommand.RunDiff(args[1..]);
        if (args.Length > 0 && args[0] == "changes")
            return ChangesCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "suggest")
            return SuggestCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "schema-info")
            return SchemaCommand.RunInfo(args[1..]);
        if (args.Length > 0 && args[0] == "index")
            return CodeIndexCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "check")
            return CheckCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "score")
            return ScoreCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "fix")
            return FixCommand.Run(args[1..]);
        if (args.Length > 0 && args[0] == "microsoft")
            return MicrosoftCommand.Run(args[1..]);

        if (args.Length == 0 || args[0] != "compile-set")
        {
            Console.Error.WriteLine(
                "Usage: radar compile-set --repo <root> --sln <path relative to root> [--configuration Debug] [--out <file>]"
            );
            Console.Error.WriteLine(
                "       radar run [--repo C:/server/code/optmyzr] [--ref origin/release] [--sln <path>] [--props <path>] [--sdk <version>] [--bench-path code/backend|.] [--target vNN] [--max-age-days 7] [--platform all|google|microsoft] [--experimental] [--force]"
            );
            Console.Error.WriteLine(
                "       radar microsoft --repo <root> --compile-set <json> [--props <path>] [--from <sdk>] [--to <sdk>] [--out <dir>]"
            );
            Console.Error.WriteLine(
                "       radar snapshot [--out data/snapshots] [--versions v23,v24,v25] [--googleapis-ref master] [--sdk-versions 25.1.0,26.1.0]"
            );
            Console.Error.WriteLine(
                "       radar schema-diff --from v23 --to v24 [--snapshots data/snapshots] [--out data/out/diff-v23-v24.json]"
            );
            Console.Error.WriteLine(
                "       radar changes --from v23 --to v25 [--snapshots data/snapshots] [--out data/out/changes-v23-v25.json]"
            );
            Console.Error.WriteLine(
                "       radar suggest --path smart_campaign_setting.business_location [--version v25] [--snapshots data/snapshots]"
            );
            Console.Error.WriteLine(
                "       radar schema-info --version v25 [--path campaign.name] [--snapshots data/snapshots]"
            );
            Console.Error.WriteLine(
                "       radar index --repo <root> --compile-set <compile-set.json> [--out data/out/code-index.json]"
            );
            Console.Error.WriteLine(
                "       radar check --index <code-index.json> --repo <root> [--snapshots data/snapshots] [--target v23] [--next v24,v25] [--sdk 25.1.0] [--props code/backend/Directory.Packages.props] [--gaql-results <json> --gaql-queries <json>] [--out data/out/findings.json] [--html data/out/report.html] [--experimental]"
            );
            Console.Error.WriteLine(
                "       radar score --findings <json> --key <json> [--out <md>]"
            );
            Console.Error.WriteLine("       radar fix --findings <json> --repo <root> --out <dir>");
            return 2;
        }

        string? repo = null;
        string? solution = null;
        string configuration = "Debug";
        string output = Path.Combine("data", "out", "compile-set.json");

        for (int i = 1; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"Missing value for {args[i]}");
                return 2;
            }

            switch (args[i])
            {
                case "--repo":
                    repo = args[i + 1];
                    break;
                case "--sln":
                    solution = args[i + 1];
                    break;
                case "--configuration":
                    configuration = args[i + 1];
                    break;
                case "--out":
                    output = args[i + 1];
                    break;
                default:
                    Console.Error.WriteLine($"Unknown option: {args[i]}");
                    return 2;
            }
        }

        if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(solution))
        {
            Console.Error.WriteLine("Both --repo and --sln are required.");
            return 2;
        }

        return CompileSetCommand.Run(repo, solution, configuration, output);
    }
}
