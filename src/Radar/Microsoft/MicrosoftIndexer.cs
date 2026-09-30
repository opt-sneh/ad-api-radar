using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Radar;

// A reference to an SDK type. Qualifier: the namespace written/aliased at the use site, if any;
// otherwise the check resolves the name against the file's Microsoft.BingAds usings.
public sealed record MsTypeUse(
    string Type,
    string? Qualifier,
    string File,
    int Line,
    string Enclosing,
    string Via
);

// Type.Member where Type is an SDK type: enum values, statics, object-initializer properties,
// and properties read or written on locals/fields declared with that type.
public sealed record MsMemberUse(
    string Type,
    string? Qualifier,
    string Member,
    string File,
    int Line,
    string Enclosing,
    string Via,
    bool Assigned
);

// A string literal in Microsoft Ads code: report column names, bulk CSV headers, type names.
// Context: csv-map (CsvHelper .Name("..")), call:<method>, registry, resource, literal.
public sealed record MsStringUse(
    string Value,
    string File,
    int Line,
    string Enclosing,
    string Context
);

public sealed record MsSwitch(
    string File,
    int Line,
    string Enclosing,
    string? EnumType,
    string? Qualifier,
    List<string> Values,
    string DefaultKind
);

public sealed record MsPinnedComment(string Version, string Text, string File, int Line);

public sealed record MsUrl(string Url, string File, int Line);

public sealed class MicrosoftIndex
{
    public int FilesScanned { get; set; }
    public Dictionary<string, List<string>> NamespacesByFile { get; init; } = new();
    public List<MsTypeUse> Types { get; init; } = [];
    public List<MsMemberUse> Members { get; init; } = [];
    public List<MsStringUse> Strings { get; init; } = [];
    public List<MsSwitch> Switches { get; init; } = [];
    public List<SwallowedCatch> SwallowedCatches { get; init; } = [];
    public List<MsPinnedComment> PinnedComments { get; init; } = [];
    public List<MsUrl> Urls { get; init; } = [];
    public List<string> InternalNamespaceFiles { get; init; } = [];
}

public static partial class MicrosoftIndexer
{
    [GeneratedRegex(@"(?i)\bSDK\s*v?(13\.0\.\d+(?:\.\d+)?)")]
    private static partial Regex PinnedVersion();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9 ()/%\-]{1,60}$")]
    private static partial Regex Columnish();

    [GeneratedRegex(@"""([A-Za-z][A-Za-z0-9]{2,60})""")]
    private static partial Regex ResourceWord();

    private static readonly HashSet<string> BingCalls = new(StringComparer.Ordinal)
    {
        "CallAsync",
        "DownloadFileAsync",
        "DownloadEntitiesAsync",
        "SubmitDownloadAsync",
        "UploadFileAsync",
        "SubmitUploadAsync",
        "UploadEntitiesAsync",
        "DownloadReportAsync",
        "SubmitDownloadAsync",
        "GetDownloadStatusAsync",
        "GetUploadStatusAsync",
        "RequestAccessAndRefreshTokensAsync",
    };

    // knownTypes: short type names across the SDK versions being compared (keeps the index small).
    public static MicrosoftIndex Index(
        string repo,
        CompileSetResult set,
        IReadOnlySet<string> knownTypes
    )
    {
        string repoPath = Path.GetFullPath(repo);
        var index = new MicrosoftIndex();
        var files = set
            .Projects.SelectMany(project => project.Files)
            .Select(file => Path.GetFullPath(file.FullPath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        foreach (string full in files)
        {
            if (!File.Exists(full))
                continue;
            string relative = Path.GetRelativePath(repoPath, full).Replace('\\', '/');
            string text = File.ReadAllText(full);
            bool registry = IsRegistryPath(relative);
            if (!registry && !text.Contains("Microsoft.BingAds", StringComparison.Ordinal))
                continue;
            index.FilesScanned++;
            var tree = CSharpSyntaxTree.ParseText(
                text,
                new CSharpParseOptions(LanguageVersion.Latest),
                path: full
            );
            ScanFile(
                relative,
                (CompilationUnitSyntax)tree.GetRoot(),
                tree,
                registry,
                knownTypes,
                index
            );
        }
        // Report/field metadata kept outside C# (e.g. ReportingFramework/Resources/bing_metadata.txt).
        string backend = Path.Combine(repoPath, "code", "backend");
        if (Directory.Exists(backend))
            foreach (
                string resource in Directory
                    .EnumerateFiles(backend, "*bing*metadata*", SearchOption.AllDirectories)
                    .Where(path =>
                        !path.Contains(
                            $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"
                        )
                        && !path.Contains(
                            $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"
                        )
                    )
                    .Order(StringComparer.OrdinalIgnoreCase)
            )
            {
                string relative = Path.GetRelativePath(repoPath, resource).Replace('\\', '/');
                string[] lines = File.ReadAllLines(resource);
                for (int i = 0; i < lines.Length; i++)
                    foreach (Match match in ResourceWord().Matches(lines[i]))
                        index.Strings.Add(
                            new MsStringUse(match.Groups[1].Value, relative, i + 1, "", "resource")
                        );
            }
        return index;
    }

    internal static bool IsRegistryPath(string relative) =>
        relative.Contains("PlatformFieldsRegistry/Microsoft/", StringComparison.OrdinalIgnoreCase);

    internal static void ScanFile(
        string file,
        CompilationUnitSyntax root,
        SyntaxTree tree,
        bool registry,
        IReadOnlySet<string> knownTypes,
        MicrosoftIndex index
    )
    {
        // using Microsoft.BingAds.X;  and  using A = Microsoft.BingAds.X[.Type];
        var namespaces = new List<string>();
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            string target = directive
                .NamespaceOrType.ToString()
                .Replace("global::", "", StringComparison.Ordinal);
            if (!target.StartsWith("Microsoft.BingAds", StringComparison.Ordinal))
                continue;
            if (directive.Alias is not null)
                aliases[directive.Alias.Name.Identifier.ValueText] = target;
            else if (directive.StaticKeyword.IsKind(SyntaxKind.None))
                namespaces.Add(target);
            if (target.StartsWith("Microsoft.BingAds.Internal", StringComparison.Ordinal))
                index.InternalNamespaceFiles.Add(file);
        }
        if (namespaces.Count > 0)
            index.NamespacesByFile[file] = namespaces.Distinct(StringComparer.Ordinal).ToList();

        int Line(SyntaxNode node) => tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;

        // Resolve a type expression to (short name, qualifier) if it can be an SDK type.
        (string Name, string? Qualifier)? Resolve(SyntaxNode? node)
        {
            switch (node)
            {
                case IdentifierNameSyntax id:
                {
                    string name = id.Identifier.ValueText;
                    if (aliases.TryGetValue(name, out string? target))
                    {
                        int dot = target.LastIndexOf('.');
                        string last = target[(dot + 1)..];
                        return knownTypes.Contains(last) ? (last, target[..dot]) : null;
                    }
                    return knownTypes.Contains(name) && namespaces.Count > 0 ? (name, null) : null;
                }
                case GenericNameSyntax generic:
                    return knownTypes.Contains(generic.Identifier.ValueText) && namespaces.Count > 0
                        ? (generic.Identifier.ValueText, null)
                        : null;
                case QualifiedNameSyntax
                or MemberAccessExpressionSyntax
                or AliasQualifiedNameSyntax:
                {
                    string text = node.ToString().Replace("global::", "", StringComparison.Ordinal);
                    int dot = text.LastIndexOf('.');
                    if (dot < 0)
                        return null;
                    string last = text[(dot + 1)..];
                    string qualifier = text[..dot];
                    string head = qualifier.Split('.')[0];
                    if (aliases.TryGetValue(head, out string? expanded))
                        qualifier = expanded + qualifier[head.Length..];
                    return
                        knownTypes.Contains(last)
                        && qualifier.StartsWith("Microsoft.BingAds", StringComparison.Ordinal)
                        ? (last, qualifier)
                        : null;
                }
                case NullableTypeSyntax nullable:
                    return Resolve(nullable.ElementType);
                default:
                    return null;
            }
        }

        // Locals, parameters, fields and properties declared with an SDK type, per scope.
        var typed =
            new Dictionary<(SyntaxNode Scope, string Name), (string Type, string? Qualifier)>();
        void Declare(SyntaxNode at, string name, TypeSyntax? type, ExpressionSyntax? initializer)
        {
            var resolved =
                type is null || type.IsVar
                    ? initializer is ObjectCreationExpressionSyntax creation
                        ? Resolve(creation.Type)
                        : null
                    : Resolve(type);
            if (resolved is { } value)
                typed[(Scope(at), name)] = value;
        }
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator
                    when declarator.Parent is VariableDeclarationSyntax declaration:
                    Declare(
                        declarator,
                        declarator.Identifier.ValueText,
                        declaration.Type,
                        declarator.Initializer?.Value
                    );
                    break;
                case ParameterSyntax parameter:
                    Declare(parameter, parameter.Identifier.ValueText, parameter.Type, null);
                    break;
                case PropertyDeclarationSyntax property:
                    Declare(property, property.Identifier.ValueText, property.Type, null);
                    break;
                case ForEachStatementSyntax loop:
                    Declare(loop.Statement, loop.Identifier.ValueText, loop.Type, null);
                    break;
                case DeclarationPatternSyntax
                {
                    Designation: SingleVariableDesignationSyntax designation
                } pattern:
                    Declare(pattern, designation.Identifier.ValueText, pattern.Type, null);
                    break;
            }
        }
        (string Type, string? Qualifier)? Local(SyntaxNode at, string name) =>
            typed.TryGetValue((Scope(at), name), out var hit) ? hit
            : typed.TryGetValue((TypeScope(at), name), out hit) ? hit
            : null;

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ObjectCreationExpressionSyntax creation
                    when Resolve(creation.Type) is { } created:
                    index.Types.Add(
                        new MsTypeUse(
                            created.Name,
                            created.Qualifier,
                            file,
                            Line(creation),
                            Enclosing(creation),
                            "new"
                        )
                    );
                    foreach (
                        var assignment in creation.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>()
                            ?? []
                    )
                        if (assignment.Left is IdentifierNameSyntax property)
                            index.Members.Add(
                                new MsMemberUse(
                                    created.Name,
                                    created.Qualifier,
                                    property.Identifier.ValueText,
                                    file,
                                    Line(assignment),
                                    Enclosing(assignment),
                                    "initializer",
                                    true
                                )
                            );
                    break;
                case MemberAccessExpressionSyntax access
                    when access.Parent is not QualifiedNameSyntax:
                {
                    string member = access.Name.Identifier.ValueText;
                    bool assigned =
                        access.Parent is AssignmentExpressionSyntax parentAssignment
                        && parentAssignment.Left == access;
                    if (Resolve(access.Expression) is { } owner)
                        index.Members.Add(
                            new MsMemberUse(
                                owner.Name,
                                owner.Qualifier,
                                member,
                                file,
                                Line(access),
                                Enclosing(access),
                                "static",
                                assigned
                            )
                        );
                    else if (
                        access.Expression is IdentifierNameSyntax receiver
                        && Local(access, receiver.Identifier.ValueText) is { } local
                    )
                        index.Members.Add(
                            new MsMemberUse(
                                local.Type,
                                local.Qualifier,
                                member,
                                file,
                                Line(access),
                                Enclosing(access),
                                "instance",
                                assigned
                            )
                        );
                    break;
                }
                case VariableDeclarationSyntax declaration
                    when Resolve(declaration.Type) is { } declared:
                    index.Types.Add(
                        new MsTypeUse(
                            declared.Name,
                            declared.Qualifier,
                            file,
                            Line(declaration),
                            Enclosing(declaration),
                            "declaration"
                        )
                    );
                    break;
                case ParameterSyntax { Type: not null } parameter
                    when Resolve(parameter.Type) is { } parameterType:
                    index.Types.Add(
                        new MsTypeUse(
                            parameterType.Name,
                            parameterType.Qualifier,
                            file,
                            Line(parameter),
                            Enclosing(parameter),
                            "parameter"
                        )
                    );
                    break;
                case TypeArgumentListSyntax arguments:
                    foreach (var argument in arguments.Arguments)
                        if (Resolve(argument) is { } generic)
                            index.Types.Add(
                                new MsTypeUse(
                                    generic.Name,
                                    generic.Qualifier,
                                    file,
                                    Line(argument),
                                    Enclosing(argument),
                                    "generic"
                                )
                            );
                    break;
                case CatchDeclarationSyntax catchDeclaration
                    when Resolve(catchDeclaration.Type) is { } caught:
                    index.Types.Add(
                        new MsTypeUse(
                            caught.Name,
                            caught.Qualifier,
                            file,
                            Line(catchDeclaration),
                            Enclosing(catchDeclaration),
                            "catch"
                        )
                    );
                    break;
                case CastExpressionSyntax cast when Resolve(cast.Type) is { } castType:
                    index.Types.Add(
                        new MsTypeUse(
                            castType.Name,
                            castType.Qualifier,
                            file,
                            Line(cast),
                            Enclosing(cast),
                            "cast"
                        )
                    );
                    break;
                case TypeOfExpressionSyntax typeOf when Resolve(typeOf.Type) is { } typeOfType:
                    index.Types.Add(
                        new MsTypeUse(
                            typeOfType.Name,
                            typeOfType.Qualifier,
                            file,
                            Line(typeOf),
                            Enclosing(typeOf),
                            "typeof"
                        )
                    );
                    break;
                case LiteralExpressionSyntax literal
                    when literal.IsKind(SyntaxKind.StringLiteralExpression):
                    AddString(literal);
                    break;
            }
        }

        void AddString(LiteralExpressionSyntax literal)
        {
            string value = literal.Token.ValueText;
            if (
                value.Contains("bingads.microsoft.com", StringComparison.OrdinalIgnoreCase)
                || value.Contains("ads.microsoft.com", StringComparison.OrdinalIgnoreCase)
            )
                index.Urls.Add(new MsUrl(value, file, Line(literal)));
            if (!Columnish().IsMatch(value))
                return;
            string context = registry ? "registry" : "literal";
            if (
                literal.Parent is ArgumentSyntax
                {
                    Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax invocation }
                }
            )
            {
                string callee = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => "",
                };
                context =
                    callee == "Name" ? "csv-map"
                    : registry ? "registry"
                    : "call:" + callee;
            }
            else if (literal.Parent is CaseSwitchLabelSyntax or ConstantPatternSyntax)
                context = "switch";
            index.Strings.Add(
                new MsStringUse(value, file, Line(literal), Enclosing(literal), context)
            );
        }

        // switch on SDK enums (case Enum.Value) and on strings (case "TypeName").
        foreach (var statement in root.DescendantNodes().OfType<SwitchStatementSyntax>())
        {
            var fallback = statement.Sections.FirstOrDefault(section =>
                section.Labels.Any(label => label is DefaultSwitchLabelSyntax)
            );
            AddSwitch(
                statement,
                statement
                    .Sections.SelectMany(section => section.Labels)
                    .Select(label =>
                        label switch
                        {
                            CaseSwitchLabelSyntax value => (SyntaxNode)value.Value,
                            CasePatternSwitchLabelSyntax pattern => pattern.Pattern,
                            _ => null,
                        }
                    ),
                DefaultKind(
                    fallback,
                    fallback?.Statements.Any(s =>
                        s is ThrowStatementSyntax
                        || s.DescendantNodes()
                            .Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax)
                    ) == true,
                    statement.Expression
                )
            );
        }
        foreach (var expression in root.DescendantNodes().OfType<SwitchExpressionSyntax>())
        {
            var fallback = expression.Arms.FirstOrDefault(arm =>
                arm.Pattern is DiscardPatternSyntax
            );
            AddSwitch(
                expression,
                expression.Arms.Select(arm => (SyntaxNode?)arm.Pattern),
                DefaultKind(
                    fallback,
                    fallback?.Expression is ThrowExpressionSyntax,
                    expression.GoverningExpression
                )
            );
        }

        // What an unhandled value does: none (no default), throw, passthrough (returns the value itself), other.
        static string DefaultKind(SyntaxNode? fallback, bool throws, ExpressionSyntax governing) =>
            fallback is null ? "none"
            : throws ? "throw"
            : fallback
                .DescendantNodes()
                .OfType<ReturnStatementSyntax>()
                .Any(r =>
                    r.Expression?.ToString()
                        .Contains(governing.ToString(), StringComparison.Ordinal) == true
                )
            || fallback is SwitchExpressionArmSyntax arm
                && arm.Expression.ToString()
                    .Contains(governing.ToString(), StringComparison.Ordinal)
                ? "passthrough"
            : "other";

        void AddSwitch(SyntaxNode node, IEnumerable<SyntaxNode?> labels, string defaultKind)
        {
            string? enumType = null,
                qualifier = null;
            var values = new List<string>();
            foreach (var label in labels)
            {
                var expression = label switch
                {
                    ConstantPatternSyntax constant => constant.Expression,
                    ExpressionSyntax plain => plain,
                    _ => null,
                };
                if (
                    expression is MemberAccessExpressionSyntax access
                    && Resolve(access.Expression) is { } owner
                )
                {
                    enumType ??= owner.Name;
                    qualifier ??= owner.Qualifier;
                    if (owner.Name == enumType)
                        values.Add(access.Name.Identifier.ValueText);
                }
                else if (
                    expression is LiteralExpressionSyntax literal
                    && literal.IsKind(SyntaxKind.StringLiteralExpression)
                )
                    values.Add(literal.Token.ValueText);
            }
            if (values.Count > 0)
                index.Switches.Add(
                    new MsSwitch(
                        file,
                        Line(node),
                        Enclosing(node),
                        enumType,
                        qualifier,
                        values.Distinct(StringComparer.Ordinal).ToList(),
                        defaultKind
                    )
                );
        }

        // try { ...Bing call... } catch { swallowed }
        var lines = tree.GetText().Lines;
        foreach (var clause in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            string caught = clause.Declaration?.Type.ToString() ?? "";
            if (
                caught.EndsWith("OperationCanceledException", StringComparison.Ordinal)
                || caught.EndsWith("TaskCanceledException", StringComparison.Ordinal)
                || clause.Parent is not TryStatementSyntax statement
            )
                continue;
            var call = statement
                .Block.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(invocation =>
                    invocation.Expression is MemberAccessExpressionSyntax access
                    && (
                        BingCalls.Contains(access.Name.Identifier.ValueText)
                        || access
                            .Expression.ToString()
                            .EndsWith("ServiceManager", StringComparison.Ordinal)
                    )
                );
            if (
                call is null
                || clause
                    .Block.DescendantNodes()
                    .Any(node => node is ThrowStatementSyntax or ThrowExpressionSyntax)
                || RecordsFailure(clause.Block)
            )
                continue;
            string? kind = CodeIndexer.CatchKind(clause.Block);
            if (kind is null)
                continue;
            int callLine = Line(call);
            index.SwallowedCatches.Add(
                new SwallowedCatch(
                    file,
                    Line(clause),
                    Enclosing(clause),
                    caught,
                    kind,
                    lines[callLine - 1].ToString().Trim()
                )
            );
        }

        // Comments pinned to an SDK version ("absent in SDK 13.0.28") need re-checking on upgrade.
        foreach (
            var trivia in root.DescendantTrivia()
                .Where(t =>
                    t.IsKind(SyntaxKind.SingleLineCommentTrivia)
                    || t.IsKind(SyntaxKind.MultiLineCommentTrivia)
                )
        )
        {
            var match = PinnedVersion().Match(trivia.ToString());
            if (match.Success)
                index.PinnedComments.Add(
                    new MsPinnedComment(
                        match.Groups[1].Value,
                        trivia.ToString().Trim(),
                        file,
                        tree.GetLineSpan(trivia.Span).StartLinePosition.Line + 1
                    )
                );
        }
    }

    // result.AddError(..), Errors.Add(..), Failed++, Success = false: the failure is recorded for the caller.
    internal static bool RecordsFailure(BlockSyntax block)
    {
        string text = block.ToString();
        return Regex.IsMatch(
                text,
                @"\b(AddError|RecordError|SetError|ReportError|MarkFailed|Errors\.Add)\s*\("
            )
            || Regex.IsMatch(
                text,
                @"\bFailed\s*(\+\+|\+=)|\bSuccess\s*=\s*false\b|\bIsSuccess\s*=\s*false\b"
            );
    }

    private static SyntaxNode Scope(SyntaxNode node) =>
        (SyntaxNode?)node.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()
        ?? (SyntaxNode?)
            node.AncestorsAndSelf().OfType<LocalFunctionStatementSyntax>().FirstOrDefault()
        ?? (SyntaxNode?)node.AncestorsAndSelf().OfType<AccessorDeclarationSyntax>().FirstOrDefault()
        ?? TypeScope(node);

    private static SyntaxNode TypeScope(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault()
        ?? node.SyntaxTree.GetRoot();

    private static string Enclosing(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? node.Ancestors()
            .OfType<ConstructorDeclarationSyntax>()
            .FirstOrDefault()
            ?.Identifier.ValueText
        ?? node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? "";
}
