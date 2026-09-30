using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Radar;

public sealed class CodeIndexResult
{
    public required string Repo { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
    public int FileCount { get; set; }
    public List<ParseErrorFile> ParseErrorFiles { get; init; } = [];
    public List<FieldDefinition> Definitions { get; init; } = [];
    public List<FieldUse> Uses { get; init; } = [];
    public List<QueryLiteral> Queries { get; init; } = [];
    public List<PathLiteral> PathLiterals { get; init; } = [];
    public List<EnumMemberRef> EnumMembers { get; init; } = [];
    public List<SdkTypeRef> SdkTypes { get; init; } = [];
    public List<TypedMemberRef> TypedMembers { get; init; } = [];
    public List<GaqlEnumFilter> GaqlEnumFilters { get; init; } = [];
    public List<SwallowedCatch> SwallowedCatches { get; init; } = [];
    public List<EnumSwitch> EnumSwitches { get; init; } = [];
    public List<DisabledFlag> DisabledFlags { get; init; } = [];
    public List<DisabledGuardUse> DisabledGuardUses { get; init; } = [];
    public Dictionary<string, List<int>> SdkVersionsByFile { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public IndexCounts Counts { get; init; } = new();
}

public sealed record ParseErrorFile(string File, int DiagnosticsCount);

public sealed record FieldDefinition(
    string OwnerType,
    string Member,
    string Path,
    string DeclaringClass,
    string File,
    int Line,
    List<string> Projects,
    string Platform
);

public sealed record FieldUse(
    string OwnerType,
    string Member,
    string Path,
    string File,
    int Line,
    string Enclosing,
    List<string> Projects,
    string Platform
);

public sealed record QueryLiteral(
    string Text,
    string? Resource,
    List<string> Paths,
    bool IsInterpolated,
    string File,
    int Line,
    List<string> Projects,
    string Platform
);

public sealed record PathLiteral(
    string Path,
    string File,
    int Line,
    List<string> Projects,
    string Platform
);

public sealed record EnumMemberRef(string EnumType, string Value, string File, int Line,
    string Enclosing, bool PossiblyHandled = false);
public sealed record SdkTypeRef(string TypeName, string File, int Line, string Enclosing,
    bool ExplicitSdkNamespace = false);
public sealed record TypedMemberRef(string ReceiverHint, string Member, string File, int Line,
    string Confidence, string Enclosing = "", bool PossiblyHandled = false);
public sealed record GaqlEnumFilter(string Path, string Operator, List<string> Values,
    string File, int Line);

public sealed record SwallowedCatch(string File, int Line, string Enclosing,
    string CaughtType, string Kind, string CallLine);
public sealed record EnumSwitch(string File, int Line, string Enclosing,
    string EnumType, List<string> Values, bool HasDefault);
public sealed record DisabledFlag(string Name, string File, int Line);
public sealed record DisabledGuardUse(string File, int Line, string Evidence);

public sealed class IndexCounts
{
    public int Definitions { get; set; }
    public int Uses { get; set; }
    public int Queries { get; set; }
    public int PathLiterals { get; set; }
    public int SdkVersionRefs { get; set; }
    public int EnumMembers { get; set; }
    public int SdkTypes { get; set; }
    public int TypedMembers { get; set; }
    public int GaqlEnumFilters { get; set; }
    public int SwallowedCatches { get; set; }
    public int EnumSwitches { get; set; }
    public int DisabledFlags { get; set; }
    public Dictionary<string, int> Platforms { get; init; } = [];
}

public static partial class CodeIndexer
{
    [GeneratedRegex("^[a-z_][a-z0-9_]*(?:\\.[a-z0-9_]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex ExactPath();

    [GeneratedRegex(
        "(?<![a-z0-9_.])[a-z_][a-z0-9_]*(?:\\.[a-z0-9_]+)+(?![a-z0-9_.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex Paths();

    [GeneratedRegex(
        "\\bSELECT\\b[\\s\\S]*?\\bFROM\\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex SelectFrom();

    [GeneratedRegex(
        "\\bFROM\\s+([a-z_][a-z0-9_]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex FromWord();

    [GeneratedRegex(
        "^Google\\.Ads\\.GoogleAds\\.V([0-9]+)(?:\\.|$)",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex SdkName();

    private sealed record ParsedFile(
        string File,
        List<string> Projects,
        CompilationUnitSyntax Root,
        SyntaxTree Tree
    );

    public static CodeIndexResult Index(string repo, CompileSetResult set,
        Action<int, int>? progress = null)
    {
        string repoPath = Path.GetFullPath(repo);
        var result = new CodeIndexResult { Repo = repoPath };
        var entries = set
            .Projects.SelectMany(project =>
                project.Files.Select(file => (project.ProjectPath, file))
            )
            .GroupBy(
                entry => Path.GetFullPath(entry.file.FullPath),
                StringComparer.OrdinalIgnoreCase
            )
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var parsed = new List<ParsedFile>(entries.Count);

        foreach (var entry in entries)
        {
            if (!File.Exists(entry.Key))
                throw new FileNotFoundException("Listed source file not found", entry.Key);
            string relative = Path.GetRelativePath(repoPath, entry.Key).Replace('\\', '/');
            var projects = entry
                .Select(item => item.ProjectPath.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var tree = CSharpSyntaxTree.ParseText(
                File.ReadAllText(entry.Key),
                new CSharpParseOptions(LanguageVersion.Latest),
                path: entry.Key
            );
            var root = (CompilationUnitSyntax)tree.GetRoot();
            int errors = tree.GetDiagnostics()
                .Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            if (errors > 0)
                result.ParseErrorFiles.Add(new ParseErrorFile(relative, errors));
            parsed.Add(new ParsedFile(relative, projects, root, tree));
        }
        result.FileCount = parsed.Count;

        foreach (var file in parsed)
        {
            AddDefinitions(file, result);
            AddDisabledFlags(file, result);
        }

        var definitions = result
            .Definitions.GroupBy(d => (d.OwnerType, d.Member))
            .ToDictionary(group => group.Key, group => group.First());
        int indexed = 0;
        foreach (var file in parsed)
        {
            ScanFile(file, definitions, result);
            indexed++;
            if (indexed % 250 == 0 || indexed == parsed.Count)
                progress?.Invoke(indexed, parsed.Count);
        }
        if (parsed.Count == 0)
            progress?.Invoke(0, 0);

        result.Counts.Definitions = result.Definitions.Count;
        result.Counts.Uses = result.Uses.Count;
        result.Counts.Queries = result.Queries.Count;
        result.Counts.PathLiterals = result.PathLiterals.Count;
        result.Counts.EnumMembers = result.EnumMembers.Count;
        result.Counts.SdkTypes = result.SdkTypes.Count;
        result.Counts.TypedMembers = result.TypedMembers.Count;
        result.Counts.GaqlEnumFilters = result.GaqlEnumFilters.Count;
        result.Counts.SwallowedCatches = result.SwallowedCatches.Count;
        result.Counts.EnumSwitches = result.EnumSwitches.Count;
        result.Counts.DisabledFlags = result.DisabledFlags.Count;
        foreach (
            string platform in result
                .Definitions.Select(d => d.Platform)
                .Concat(result.Uses.Select(u => u.Platform))
                .Concat(result.Queries.Select(q => q.Platform))
                .Concat(result.PathLiterals.Select(p => p.Platform))
        )
            result.Counts.Platforms[platform] =
                result.Counts.Platforms.GetValueOrDefault(platform) + 1;
        return result;
    }

    private static void AddDefinitions(ParsedFile file, CodeIndexResult result)
    {
        foreach (var field in file.Root.DescendantNodes().OfType<FieldDeclarationSyntax>())
        {
            if (!field.Modifiers.Any(SyntaxKind.StaticKeyword))
                continue;
            foreach (var variable in field.Declaration.Variables)
                AddDefinition(
                    field.Declaration.Type,
                    variable.Initializer?.Value,
                    variable.Identifier.ValueText,
                    variable,
                    file,
                    result
                );
        }
        foreach (var property in file.Root.DescendantNodes().OfType<PropertyDeclarationSyntax>())
        {
            if (property.Modifiers.Any(SyntaxKind.StaticKeyword))
                AddDefinition(
                    property.Type,
                    property.Initializer?.Value,
                    property.Identifier.ValueText,
                    property,
                    file,
                    result
                );
        }
    }

    private static void AddDefinition(
        TypeSyntax declaredType,
        ExpressionSyntax? initializer,
        string member,
        SyntaxNode node,
        ParsedFile file,
        CodeIndexResult result
    )
    {
        TypeSyntax ownerSyntax;
        ArgumentListSyntax arguments;
        if (initializer is ObjectCreationExpressionSyntax creation)
        {
            ownerSyntax = creation.Type;
            arguments = creation.ArgumentList!;
        }
        else if (initializer is ImplicitObjectCreationExpressionSyntax implicitCreation)
        {
            ownerSyntax = declaredType;
            arguments = implicitCreation.ArgumentList;
        }
        else
            return;
        if (
            arguments is null
            || arguments.Arguments.Count != 1
            || arguments.Arguments[0].Expression is not LiteralExpressionSyntax literal
            || !literal.IsKind(SyntaxKind.StringLiteralExpression)
        )
            return;
        string path = literal.Token.ValueText;
        if (!ExactPath().IsMatch(path))
            return;
        string owner = TypeName(ownerSyntax);
        string declaringClass =
            node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
            ?? "";
        result.Definitions.Add(
            new FieldDefinition(
                owner,
                member,
                path,
                declaringClass,
                file.File,
                Line(file.Tree, node),
                file.Projects,
                Platform(owner)
            )
        );
    }

    private static void ScanFile(
        ParsedFile file,
        Dictionary<(string OwnerType, string Member), FieldDefinition> definitions,
        CodeIndexResult result
    )
    {
        var versions = new HashSet<int>();
        foreach (var directive in file.Root.Usings)
            AddSdkVersion(directive.Name?.ToString(), versions, result.Counts);
        foreach (var name in file.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (
                name.Expression is IdentifierNameSyntax owner
                && definitions.TryGetValue(
                    (owner.Identifier.ValueText, name.Name.Identifier.ValueText),
                    out var definition
                )
            )
            {
                string enclosing = name.Ancestors()
                    .OfType<BaseMethodDeclarationSyntax>()
                    .FirstOrDefault() switch
                {
                    MethodDeclarationSyntax method => method.Identifier.ValueText,
                    ConstructorDeclarationSyntax constructor => constructor.Identifier.ValueText,
                    _ => name.Ancestors()
                        .OfType<TypeDeclarationSyntax>()
                        .FirstOrDefault()
                        ?.Identifier.ValueText
                        ?? "",
                };
                result.Uses.Add(
                    new FieldUse(
                        definition.OwnerType,
                        definition.Member,
                        definition.Path,
                        file.File,
                        Line(file.Tree, name),
                        enclosing,
                        file.Projects,
                        definition.Platform
                    )
                );
            }
            // Only the outermost member access in a qualified chain represents this reference.
            if (name.Parent is not MemberAccessExpressionSyntax parent || parent.Expression != name)
                AddSdkVersion(name.ToString(), versions, result.Counts);
        }
        foreach (var name in file.Root.DescendantNodes().OfType<NameSyntax>())
        {
            if (name.Ancestors().OfType<UsingDirectiveSyntax>().Any())
                continue;
            if (name.Parent is QualifiedNameSyntax qualified && qualified.Left == name)
                continue;
            if (name.Parent is AliasQualifiedNameSyntax alias && alias.Name == name)
                continue;
            AddSdkVersion(name.ToString(), versions, result.Counts);
        }
        result.SdkVersionsByFile[file.File] = versions.Order().ToList();
        string platform = versions.Count > 0 ? "google-ads-candidate" : "unknown";
        if (versions.Count > 0)
            ScanTyped(file, result);

        foreach (var literal in file.Root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression))
                continue;
            string value = literal.Token.ValueText;
            if (IsPath(value))
                result.PathLiterals.Add(
                    new PathLiteral(
                        value,
                        file.File,
                        Line(file.Tree, literal),
                        file.Projects,
                        platform
                    )
                );
            if (
                literal
                    .Ancestors()
                    .OfType<BinaryExpressionSyntax>()
                    .Any(binary => binary.IsKind(SyntaxKind.AddExpression))
            )
                continue;
            AddQuery(value, false, literal, file, platform, result);
        }
        foreach (
            var interpolation in file
                .Root.DescendantNodes()
                .OfType<InterpolatedStringExpressionSyntax>()
        )
            AddQuery(
                RenderInterpolated(interpolation),
                true,
                interpolation,
                file,
                platform,
                result
            );
        foreach (var binary in file.Root.DescendantNodes().OfType<BinaryExpressionSyntax>())
        {
            if (
                !binary.IsKind(SyntaxKind.AddExpression)
                || binary.Parent is BinaryExpressionSyntax parent
                    && parent.IsKind(SyntaxKind.AddExpression)
            )
                continue;
            if (
                !binary
                    .DescendantNodes()
                    .OfType<LiteralExpressionSyntax>()
                    .Any(l => l.IsKind(SyntaxKind.StringLiteralExpression))
            )
                continue;
            AddQuery(
                RenderExpression(binary),
                binary.DescendantNodes().OfType<InterpolatedStringExpressionSyntax>().Any(),
                binary,
                file,
                platform,
                result
            );
        }
        foreach (var invocation in file.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression.ToString() != "string.Join"
                && invocation.Expression.ToString() != "String.Join") continue;
            AddQuery(RenderExpression(invocation), false, invocation, file, platform, result);
        }
    }

    private static void AddQuery(
        string value,
        bool interpolated,
        SyntaxNode node,
        ParsedFile file,
        string platform,
        CodeIndexResult result
    )
    {
        if (!SelectFrom().IsMatch(value))
            return;
        // GAQL never contains braces, so any {…} is an unresolved expression from RenderExpression/RenderJoin.
        interpolated |= value.Contains('{');
        var from = FromWord().Match(value);
        var paths = Paths()
            .Matches(value)
            .Select(match => match.Value.ToLowerInvariant())
            .Distinct()
            .ToList();
        result.Queries.Add(
            new QueryLiteral(
                value,
                from.Success ? from.Groups[1].Value : null,
                paths,
                interpolated,
                file.File,
                Line(file.Tree, node),
                file.Projects,
                platform
            )
        );
        if (platform == "google-ads-candidate")
            AddGaqlFilters(value, file.File, Line(file.Tree, node), result);
    }

    private static string RenderExpression(ExpressionSyntax expression) =>
        expression switch
        {
            BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
                RenderExpression(binary.Left) + RenderExpression(binary.Right),
            LiteralExpressionSyntax literal
                when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            InterpolatedStringExpressionSyntax interpolated => RenderInterpolated(interpolated),
            InvocationExpressionSyntax invocation when (invocation.Expression.ToString() is "string.Join" or "String.Join")
                && invocation.ArgumentList.Arguments.Count >= 2 => RenderJoin(invocation),
            _ => "{" + expression + "}",
        };

    private static string RenderJoin(InvocationExpressionSyntax invocation)
    {
        string separator = RenderExpression(invocation.ArgumentList.Arguments[0].Expression);
        var items = invocation.ArgumentList.Arguments[1].Expression switch
        {
            ArrayCreationExpressionSyntax array => array.Initializer?.Expressions,
            ImplicitArrayCreationExpressionSyntax array => array.Initializer.Expressions,
            InitializerExpressionSyntax initializer => initializer.Expressions,
            _ => null,
        };
        return items is null ? "{" + invocation + "}" : string.Join(separator, items.Value.Select(RenderExpression));
    }

    private static string RenderInterpolated(InterpolatedStringExpressionSyntax expression) =>
        string.Concat(
            expression.Contents.Select(content =>
                content switch
                {
                    InterpolatedStringTextSyntax text => text.TextToken.ValueText,
                    InterpolationSyntax interpolation => "{" + interpolation.Expression + "}",
                    _ => "",
                }
            )
        );

    private static bool IsPath(string value) =>
        ExactPath().IsMatch(value)
        && !value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        && !value.Contains('/')
        && !new[] { ".cs", ".json", ".dll" }.Any(extension =>
            value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
        );

    private static void AddSdkVersion(string? name, HashSet<int> versions, IndexCounts counts)
    {
        var match = SdkName().Match(name ?? "");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int version))
        {
            versions.Add(version);
            counts.SdkVersionRefs++;
        }
    }

    private static string TypeName(TypeSyntax type) =>
        type switch
        {
            QualifiedNameSyntax qualified => TypeName(qualified.Right),
            AliasQualifiedNameSyntax alias => TypeName(alias.Name),
            SimpleNameSyntax simple => simple.Identifier.ValueText,
            _ => type.ToString(),
        };

    private static string Platform(string owner) =>
        owner switch
        {
            "GOOGLE_ADS_REPORT" => "google-ads",
            "SA360_REPORT_FIELDS" => "sa360",
            _ => "unknown:" + owner,
        };

    private static int Line(SyntaxTree tree, SyntaxNode node) =>
        tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
}
