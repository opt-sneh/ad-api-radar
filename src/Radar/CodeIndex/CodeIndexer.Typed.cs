using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Radar;

public static partial class CodeIndexer
{
    private static void AddDisabledFlags(ParsedFile file, CodeIndexResult result)
    {
        foreach (var field in file.Root.DescendantNodes().OfType<FieldDeclarationSyntax>()
            .Where(field => field.Declaration.Type.ToString() == "bool"
                && (field.Modifiers.Any(SyntaxKind.ConstKeyword)
                    || field.Modifiers.Any(SyntaxKind.StaticKeyword)
                        && field.Modifiers.Any(SyntaxKind.ReadOnlyKeyword))))
            foreach (var variable in field.Declaration.Variables.Where(variable =>
                variable.Initializer?.Value.IsKind(SyntaxKind.FalseLiteralExpression) == true))
                result.DisabledFlags.Add(new DisabledFlag(variable.Identifier.ValueText,
                    file.File, Line(file.Tree, variable)));
        foreach (var local in file.Root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>()
            .Where(local => local.Modifiers.Any(SyntaxKind.ConstKeyword)
                && local.Declaration.Type.ToString() == "bool"))
            foreach (var variable in local.Declaration.Variables.Where(variable =>
                variable.Initializer?.Value.IsKind(SyntaxKind.FalseLiteralExpression) == true))
                result.DisabledFlags.Add(new DisabledFlag(variable.Identifier.ValueText,
                    file.File, Line(file.Tree, variable)));
    }

    [GeneratedRegex(@"(?<path>[a-z_][a-z_0-9]*(?:\.[a-z_][a-z_0-9]*)+)\s*(?<op>!=|=|\bNOT\s+IN\b|\bIN\b)\s*(?:(?<one>'[A-Z][A-Z_0-9]*')|\((?<many>\s*'[A-Z][A-Z_0-9]*'\s*(?:,\s*'[A-Z][A-Z_0-9]*'\s*)*)\))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GaqlFilterPattern();

    private static void AddGaqlFilters(string text, string file, int line, CodeIndexResult result)
    {
        foreach (Match match in GaqlFilterPattern().Matches(text))
        {
            var values = Regex.Matches(match.Groups["one"].Value + match.Groups["many"].Value,
                "'[A-Z][A-Z_0-9]*'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(value => value.Value.Trim('\'')).ToList();
            if (values.Count == 0) continue;
            var filter = new GaqlEnumFilter(match.Groups["path"].Value.ToLowerInvariant(),
                Regex.Replace(match.Groups["op"].Value.ToUpperInvariant(), @"\s+", " "), values, file, line);
            if (!result.GaqlEnumFilters.Any(existing => existing.File == file && existing.Line == line
                && existing.Path == filter.Path && existing.Operator == filter.Operator
                && existing.Values.SequenceEqual(filter.Values))) result.GaqlEnumFilters.Add(filter);
        }
    }

    private static void ScanTyped(ParsedFile file, CodeIndexResult result)
    {
        var aliases = file.Root.Usings.Where(u => u.Alias is not null && u.Name is not null)
            .ToDictionary(u => u.Alias!.Name.Identifier.ValueText, u => u.Name!.ToString(),
                StringComparer.Ordinal);
        var staticEnums = file.Root.Usings.Where(u => u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
            .Select(u => u.Name?.ToString() ?? "").Where(u => u.EndsWith("Enum.Types", StringComparison.Ordinal))
            .Select(u => u.Split('.').Last() == "Types" ? u.Split('.')[^2] + ".Types" : u).ToArray();
        bool sdkUsing = file.Root.Usings.Any(u => u.Name?.ToString().Contains("Google.Ads.GoogleAds.V", StringComparison.Ordinal) == true);
        var locals = new Dictionary<(SyntaxNode Scope, string Name), string>();
        var methods = file.Root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>().ToArray();
        var methodsByName = methods.OfType<MethodDeclarationSyntax>()
            .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(method => (
                Node: method,
                Identifiers: method.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Select(identifier => identifier.Identifier.ValueText).ToHashSet(StringComparer.Ordinal)))
                .ToArray(), StringComparer.Ordinal);
        var declaredNames = file.Root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(variable => variable.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var eligibleFlags = result.DisabledFlags.Where(flag => flag.File == file.File
            || !declaredNames.Contains(flag.Name)).ToArray();
        var conditionsByMethod = methods.ToDictionary(method => method, method =>
            method.DescendantNodes().OfType<IfStatementSyntax>().Select(statement => (
                Text: statement.Condition.ToString(),
                Invoked: statement.Condition.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                    .Select(invocation => invocation.Expression switch
                    {
                        IdentifierNameSyntax id => id.Identifier.ValueText,
                        MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                        _ => "",
                    }).ToHashSet(StringComparer.Ordinal))).ToArray());
        var commentsByMethod = methods.ToDictionary(method => method, method =>
            method.DescendantTrivia().Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
                || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia))
                .Select(trivia => trivia.ToString()).ToArray());
        var handledCache = new Dictionary<(BaseMethodDeclarationSyntax Method, string Member),
            (bool Handled, string? DisabledEvidence)>();

        bool Handled(SyntaxNode node, string member)
        {
            var method = node.Ancestors().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault();
            if (method is null) return false;
            if (!handledCache.TryGetValue((method, member), out var evaluation))
            {
                var conditions = conditionsByMethod[method].Where(condition =>
                    condition.Text.Contains(member, StringComparison.OrdinalIgnoreCase)).ToArray();
                string? disabledEvidence = null;
                foreach (var condition in conditions)
                {
                    var guardNames = condition.Invoked.Where(methodsByName.ContainsKey)
                        .SelectMany(name => methodsByName[name])
                        .Where(candidate => candidate.Node != method)
                        .SelectMany(candidate => candidate.Identifiers)
                        .ToHashSet(StringComparer.Ordinal);
                    var flag = eligibleFlags.FirstOrDefault(disabled => guardNames.Contains(disabled.Name));
                    if (flag is null) continue;
                    disabledEvidence = $"Guard disabled: {flag.Name} = false at {flag.File}:{flag.Line}";
                    break;
                }
                bool handled = disabledEvidence is null && (conditions.Length > 0
                    || commentsByMethod[method].Any(comment =>
                        comment.Contains(member, StringComparison.OrdinalIgnoreCase)
                        && Regex.IsMatch(comment, @"\b(removed|no longer|rejected)\b", RegexOptions.IgnoreCase)));
                evaluation = (handled, disabledEvidence);
                handledCache[(method, member)] = evaluation;
            }
            if (evaluation.DisabledEvidence is not null)
                result.DisabledGuardUses.Add(new DisabledGuardUse(file.File, Line(file.Tree, node),
                    evaluation.DisabledEvidence));
            return evaluation.Handled;
        }

        string Resolve(string name)
        {
            string head = name.Split('.')[0];
            return aliases.TryGetValue(head, out string? target) ? target + name[head.Length..] : name;
        }
        string? SdkType(string name)
        {
            name = Resolve(name).Replace("global::", "", StringComparison.Ordinal);
            string simple = name.Split('.').Last();
            return name.Contains("Google.Ads.GoogleAds.V", StringComparison.Ordinal)
                || aliases.Values.Any(a => name.StartsWith(a, StringComparison.Ordinal))
                || simple.EndsWith("ServiceClient", StringComparison.Ordinal)
                || (sdkUsing && simple.Length > 0 && char.IsUpper(simple[0])
                    && simple is not "String" and not "Task" and not "List" and not "Dictionary")
                ? simple : null;
        }
        foreach (var declaration in file.Root.DescendantNodes().OfType<VariableDeclarationSyntax>())
        {
            string? type = declaration.Type is IdentifierNameSyntax varName
                && varName.Identifier.ValueText == "var"
                ? declaration.Variables.FirstOrDefault()?.Initializer?.Value switch
                {
                    ObjectCreationExpressionSyntax creation => SdkType(creation.Type.ToString()),
                    _ => null,
                }
                : SdkType(declaration.Type.ToString());
            if (type is null) continue;
            SyntaxNode scope = Scope(declaration);
            foreach (var variable in declaration.Variables)
                locals[(scope, variable.Identifier.ValueText)] = type;
        }
        foreach (var parameter in file.Root.DescendantNodes().OfType<ParameterSyntax>())
        {
            string? type = parameter.Type is null ? null : SdkType(parameter.Type.ToString());
            if (type is not null) locals[(Scope(parameter), parameter.Identifier.ValueText)] = type;
        }

        foreach (var creation in file.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            string? type = SdkType(creation.Type.ToString());
            if (type is null) continue;
            result.SdkTypes.Add(new SdkTypeRef(type, file.File, Line(file.Tree, creation), Enclosing(creation),
                Resolve(creation.Type.ToString()).Contains("Google.Ads.GoogleAds.V", StringComparison.Ordinal)));
            if (creation.Initializer is null) continue;
            foreach (var assignment in creation.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                if (assignment.Left is IdentifierNameSyntax member)
                    result.TypedMembers.Add(new TypedMemberRef(type, member.Identifier.ValueText,
                        file.File, Line(file.Tree, member), "high", Enclosing(member),
                        Handled(member, member.Identifier.ValueText)));
        }
        foreach (var type in file.Root.DescendantNodes().OfType<TypeSyntax>())
        {
            if (type.Ancestors().OfType<UsingDirectiveSyntax>().Any()) continue;
            if (type.Parent is ObjectCreationExpressionSyntax) continue;
            if (type is not IdentifierNameSyntax and not QualifiedNameSyntax and not AliasQualifiedNameSyntax) continue;
            if (type.Parent is QualifiedNameSyntax) continue;
            string? sdk = SdkType(type.ToString());
            if (sdk is not null)
                result.SdkTypes.Add(new SdkTypeRef(sdk, file.File, Line(file.Tree, type), Enclosing(type),
                    Resolve(type.ToString()).Contains("Google.Ads.GoogleAds.V", StringComparison.Ordinal)));
        }
        string? EnumTypeOf(MemberAccessExpressionSyntax access)
        {
            string expression = Resolve(access.Expression.ToString());
            string? enumType = IsEnumType(expression) ? ShortEnumType(expression) : null;
            if (enumType is null && access.Expression is IdentifierNameSyntax identifier
                && staticEnums.Length == 1 && staticEnums[0] == identifier.Identifier.ValueText + "Enum.Types")
                enumType = staticEnums[0] + "." + identifier.Identifier.ValueText;
            return enumType is not null && enumType.EndsWith("Enum.Types." + enumType.Split('.').Last(), StringComparison.Ordinal)
                ? enumType : null;
        }
        foreach (var access in file.Root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            string value = access.Name.Identifier.ValueText;
            string? enumType = EnumTypeOf(access);
            if (enumType is not null)
            {
                result.EnumMembers.Add(new EnumMemberRef(enumType, value, file.File,
                    Line(file.Tree, access), Enclosing(access), Handled(access, value)));
                continue;
            }
            Record(access, access.Expression, value);
        }
        foreach (var binding in file.Root.DescendantNodes().OfType<MemberBindingExpressionSyntax>())
        {
            var conditional = binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>().FirstOrDefault();
            if (conditional is not null)
                Record(binding, conditional.Expression, binding.Name.Identifier.ValueText);
        }

        void AddSwitch(SyntaxNode node, IEnumerable<SyntaxNode> labels, bool hasDefault)
        {
            var values = labels.SelectMany(label => label.DescendantNodesAndSelf()
                .OfType<MemberAccessExpressionSyntax>())
                .Select(access => (Type: EnumTypeOf(access), Value: access.Name.Identifier.ValueText))
                .Where(value => value.Type is not null)
                .GroupBy(value => value.Type!, StringComparer.Ordinal);
            foreach (var group in values)
                result.EnumSwitches.Add(new EnumSwitch(file.File, Line(file.Tree, node),
                    Enclosing(node), group.Key, group.Select(value => value.Value)
                        .Distinct(StringComparer.Ordinal).ToList(), hasDefault));
        }
        foreach (var statement in file.Root.DescendantNodes().OfType<SwitchStatementSyntax>())
            AddSwitch(statement, statement.Sections.SelectMany(section => section.Labels),
                statement.Sections.SelectMany(section => section.Labels)
                    .Any(label => label is DefaultSwitchLabelSyntax));
        foreach (var expression in file.Root.DescendantNodes().OfType<SwitchExpressionSyntax>())
            AddSwitch(expression, expression.Arms.Select(arm => (SyntaxNode)arm.Pattern),
                expression.Arms.Any(arm => arm.Pattern is DiscardPatternSyntax));

        var googleCallsByTry = new Dictionary<TryStatementSyntax, InvocationExpressionSyntax?>();
        var sourceLines = file.Tree.GetText().Lines;
        IEnumerable<CatchClauseSyntax> catches = sdkUsing
            ? file.Root.DescendantNodes().OfType<CatchClauseSyntax>() : [];
        foreach (var clause in catches)
        {
            string caught = clause.Declaration?.Type.ToString() ?? "";
            if (caught.EndsWith("OperationCanceledException", StringComparison.Ordinal)
                || caught.EndsWith("TaskCanceledException", StringComparison.Ordinal)
                || caught.EndsWith("ThreadAbortException", StringComparison.Ordinal)) continue;
            if (clause.Parent is not TryStatementSyntax statement) continue;
            if (!googleCallsByTry.TryGetValue(statement, out var call))
            {
                call = statement.Block.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .FirstOrDefault(IsGoogleCall);
                googleCallsByTry[statement] = call;
            }
            if (call is null || clause.Block.DescendantNodes().Any(node =>
                node is ThrowStatementSyntax or ThrowExpressionSyntax)) continue;
            string? kind = CatchKind(clause.Block);
            if (kind is null) continue;
            int callLine = Line(file.Tree, call);
            result.SwallowedCatches.Add(new SwallowedCatch(file.File, Line(file.Tree, clause),
                Enclosing(clause), caught, kind,
                sourceLines[callLine - 1].ToString().Trim()));
        }

        bool IsGoogleCall(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax access) return false;
            string name = access.Name.Identifier.ValueText;
            if (name is "Search" or "SearchStream" or "SearchAsync" or "SearchStreamAsync"
                || name.StartsWith("Mutate", StringComparison.Ordinal)) return true;
            string receiver = access.Expression.ToString();
            if (receiver.EndsWith("ServiceClient", StringComparison.Ordinal)) return true;
            return access.Expression is IdentifierNameSyntax id
                && (locals.GetValueOrDefault((Scope(invocation), id.Identifier.ValueText))
                    ?? locals.GetValueOrDefault((TypeScope(invocation), id.Identifier.ValueText)))
                    ?.EndsWith("ServiceClient", StringComparison.Ordinal) == true;
        }

        void Record(SyntaxNode node, ExpressionSyntax expression, string value)
        {
            string? receiver = null;
            string confidence = "medium";
            if (expression is IdentifierNameSyntax id)
            {
                var scope = Scope(node);
                if (locals.TryGetValue((scope, id.Identifier.ValueText), out receiver)
                    || locals.TryGetValue((TypeScope(node), id.Identifier.ValueText), out receiver))
                    confidence = "high";
            }
            else if (expression is MemberAccessExpressionSyntax or ConditionalAccessExpressionSyntax or MemberBindingExpressionSyntax)
            {
                string chain = ChainText(expression);
                string root = chain.Split('.')[0];
                string last = chain.Split('.').Last();
                var scope = Scope(node);
                if (locals.ContainsKey((scope, root)) || locals.ContainsKey((TypeScope(node), root))
                    || sdkUsing && last.Length > 0 && char.IsUpper(last[0]))
                    receiver = chain;
            }
            if (receiver is not null)
                result.TypedMembers.Add(new TypedMemberRef(receiver, value, file.File,
                    Line(file.Tree, node), confidence, Enclosing(node), Handled(node, value)));
        }

        string ChainText(ExpressionSyntax expression)
        {
            if (expression is MemberBindingExpressionSyntax binding)
            {
                var owner = binding.Ancestors().OfType<ConditionalAccessExpressionSyntax>()
                    .FirstOrDefault(conditional => conditional.WhenNotNull.Span.Contains(binding.Span));
                return owner is null ? binding.Name.ToString() : ChainText(owner.Expression) + "." + binding.Name;
            }
            return expression.ToString().Replace("?.", ".", StringComparison.Ordinal);
        }
    }

    private static bool IsEnumType(string value) =>
        Regex.IsMatch(value, @"(?:^|\.)[A-Za-z_][A-Za-z_0-9]*Enum\.Types\.[A-Za-z_][A-Za-z_0-9]*$");
    private static string ShortEnumType(string value)
    {
        int index = value.LastIndexOf("Enum.Types.", StringComparison.Ordinal);
        int start = value.LastIndexOf('.', index < 0 ? 0 : index);
        return value[(start + 1)..];
    }
    private static SyntaxNode Scope(SyntaxNode node) =>
        (SyntaxNode?)node.AncestorsAndSelf().OfType<BaseMethodDeclarationSyntax>().FirstOrDefault()
        ?? node.AncestorsAndSelf().OfType<LocalFunctionStatementSyntax>().FirstOrDefault()
        ?? TypeScope(node);
    private static SyntaxNode TypeScope(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().FirstOrDefault() ?? node.SyntaxTree.GetRoot();
    private static string Enclosing(SyntaxNode node) =>
        node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? node.Ancestors().OfType<ConstructorDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText
        ?? node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "";
    internal static string? CatchKind(BlockSyntax block)
    {
        if (block.Statements.Count == 0) return "empty";
        bool Logging(StatementSyntax statement)
        {
            if (statement is not ExpressionStatementSyntax expression
                || expression.Expression is not InvocationExpressionSyntax invocation) return false;
            string callee = invocation.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => "",
            };
            return new[] { "Log", "Write", "Trace", "Print", "Console", "Sentry", "Capture", "Notify", "Add" }
                .Any(word => callee.Contains(word, StringComparison.OrdinalIgnoreCase));
        }
        if (block.Statements.All(Logging)) return "log-only";
        if (block.Statements.Take(block.Statements.Count - 1).All(Logging))
        {
            var last = block.Statements[^1];
            if (last is ContinueStatementSyntax or BreakStatementSyntax) return "return-empty";
            if (last is ReturnStatementSyntax returning && EmptyReturn(returning.Expression))
                return "return-empty";
        }
        return null;
    }

    private static bool EmptyReturn(ExpressionSyntax? expression)
    {
        if (expression is null) return true;
        if (expression is LiteralExpressionSyntax literal)
            return literal.IsKind(SyntaxKind.NullLiteralExpression)
                || literal.IsKind(SyntaxKind.DefaultLiteralExpression)
                || literal.IsKind(SyntaxKind.FalseLiteralExpression)
                || literal.IsKind(SyntaxKind.NumericLiteralExpression) && literal.Token.ValueText == "0"
                || literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText == "";
        if (expression is DefaultExpressionSyntax or CollectionExpressionSyntax) return true;
        if (expression.ToString() == "string.Empty") return true;
        if (expression is ObjectCreationExpressionSyntax creation)
            return (creation.ArgumentList?.Arguments.Count == 0 && creation.Initializer is null
                || creation.Initializer?.Expressions.Count == 0)
                && new[] { "List", "Collection", "Dictionary", "HashSet", "Queue", "Stack", "Set" }
                    .Any(name => creation.Type.ToString().Contains(name, StringComparison.Ordinal));
        if (expression is ArrayCreationExpressionSyntax array)
            return array.Type.RankSpecifiers.Any(rank => rank.Sizes.Any(size => size.ToString() == "0"))
                || array.Initializer?.Expressions.Count == 0;
        if (expression is ImplicitArrayCreationExpressionSyntax implicitArray)
            return implicitArray.Initializer.Expressions.Count == 0;
        if (expression is InvocationExpressionSyntax invocation)
            return invocation.ArgumentList.Arguments.Count == 0
                && invocation.Expression is MemberAccessExpressionSyntax access
                && access.Name.Identifier.ValueText == "Empty"
                && access.Expression.ToString() is "Array" or "Enumerable";
        return false;
    }

}
