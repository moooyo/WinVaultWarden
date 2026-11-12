using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace LocalizationCatalog;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Main(string[] args)
    {
        try
        {
            if (args is ["self-test"])
            {
                SelfTests.Run();
                Console.WriteLine("Localization migration self-tests passed.");
                return 0;
            }
            if (args.Length < 3 || args[0] is not ("inventory" or "apply"))
                throw new ArgumentException("Usage: LocalizationCatalog <inventory|apply> <repository> <catalog.json> [--include-settings] [--only=<relative-file>] [--xaml-only|--csharp-only]");

            var root = Path.GetFullPath(args[1]);
            var catalogPath = Path.GetFullPath(args[2]);
            var files = SourceFiles(root).ToArray();
            if (args[0] == "inventory")
            {
                var catalog = Inventory(root, files);
                Directory.CreateDirectory(Path.GetDirectoryName(catalogPath)!);
                File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, JsonOptions), new UTF8Encoding(false));
                Console.WriteLine($"Catalog: {catalog.Texts.Count} distinct strings, {catalog.CSharpEligibleOccurrences} eligible C# occurrences, {catalog.XamlEligibleProperties} XAML properties, {catalog.Issues.Count} reported issues.");
                return 0;
            }

            var approved = JsonSerializer.Deserialize<CatalogDocument>(File.ReadAllText(catalogPath), JsonOptions)
                ?? throw new InvalidDataException("The catalog is empty.");
            Apply(root, files, approved, args.Skip(3).ToArray());
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        var app = Path.Combine(root, "src", "App");
        return Directory.EnumerateFiles(app, "*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && IsCSharpScope(Path.GetRelativePath(app, file).Replace('\\', '/')))
            .Where(file => !Path.GetRelativePath(app, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part is "bin" or "obj" or "Localization" or "Strings"))
            .Order(StringComparer.Ordinal);
    }

    private static bool IsCSharpScope(string relative) => relative.StartsWith("Views/", StringComparison.Ordinal)
        || relative.StartsWith("ViewModels/", StringComparison.Ordinal)
        || relative.StartsWith("Controls/", StringComparison.Ordinal)
        || relative.StartsWith("Services/", StringComparison.Ordinal)
            && relative is not ("Services/MockVaultUiService.cs" or "Services/MockDeviceUiService.cs" or "Services/DemoVaultSessionService.cs")
        || relative.StartsWith("MainWindow", StringComparison.Ordinal)
        || relative == "App.xaml.cs";

    private static CatalogDocument Inventory(string root, IReadOnlyList<string> files)
    {
        var entries = new Dictionary<string, CatalogText>(StringComparer.Ordinal);
        var maps = new List<XamlElementMap>();
        var issues = new List<MigrationIssue>();
        var csharpCount = 0;
        var xamlCount = 0;
        var constantNames = FindConstantContextNames(files);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var relative = Relative(root, file);
            if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var element in XamlMigration.Read(relative, source, issues))
                {
                    maps.Add(element.Map);
                    foreach (var property in element.Map.Properties)
                    {
                        Add(property.Source, new SourceContext(relative, LineAt(source, element.Start), "xaml", property.Property));
                        xamlCount++;
                    }
                }
                continue;
            }

            var syntax = Parse(source);
            foreach (var node in syntax.DescendantNodes())
            {
                if (!CSharpMigration.IsEligibleTemplate(node, out var template))
                    continue;
                var line = syntax.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
                var blocked = CSharpMigration.BlockReason(node, constantNames);
                Add(template, new SourceContext(relative, line, blocked is null ? "csharp" : "csharp-blocked", null));
                if (blocked is null)
                    csharpCount++;
                else
                    issues.Add(new MigrationIssue(relative, line, blocked, template));
            }
        }
        return new CatalogDocument(1, entries.Values.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList(),
            maps, issues, csharpCount, xamlCount);

        void Add(string text, SourceContext context)
        {
            var key = Key(text);
            if (!entries.TryGetValue(key, out var entry))
                entries.Add(key, entry = new CatalogText(key, text, []));
            else if (entry.Source != text)
                throw new InvalidDataException("A localization key collision was detected.");
            if (!entry.Contexts.Contains(context))
                entry.Contexts.Add(context);
        }
    }

    private static void Apply(string root, IReadOnlyList<string> files, CatalogDocument catalog, string[] options)
    {
        var approved = catalog.Texts.ToDictionary(entry => entry.Key, entry => entry.Source, StringComparer.Ordinal);
        var constantNames = FindConstantContextNames(files);
        var only = options.Where(option => option.StartsWith("--only=", StringComparison.Ordinal))
            .Select(option => option[7..].Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var includeSettings = options.Contains("--include-settings", StringComparer.Ordinal);
        var changes = new List<(string Path, string Content)>();
        foreach (var file in files)
        {
            var relative = Relative(root, file);
            if (only.Count != 0 && !only.Contains(relative))
                continue;
            if (!includeSettings && Path.GetFileName(file).StartsWith("SettingsPage.", StringComparison.OrdinalIgnoreCase))
                continue;
            if (options.Contains("--xaml-only", StringComparer.Ordinal) && !file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                || options.Contains("--csharp-only", StringComparer.Ordinal) && !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var original = File.ReadAllText(file);
            string updated;
            if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                updated = XamlMigration.Apply(relative, original, approved);
            else
                updated = new CSharpMigration(approved, constantNames).Visit(Parse(original))!.ToFullString();
            if (updated != original)
                changes.Add((file, updated));
        }

        // Stage every transformation before writing any file; unknown source text fails the whole operation.
        foreach (var (path, content) in changes)
            File.WriteAllText(path, content, new UTF8Encoding(false));
        Console.WriteLine($"Updated {changes.Count} files. SettingsPage files are {(includeSettings ? "included" : "excluded")}.");
    }

    private static HashSet<string> FindConstantContextNames(IEnumerable<string> files)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            foreach (var identifier in Parse(File.ReadAllText(file)).DescendantNodes().OfType<IdentifierNameSyntax>())
                if (CSharpMigration.IsConstantOnly(identifier) || CSharpMigration.IsLogicOperand(identifier))
                    names.Add(identifier.Identifier.ValueText);
        return names;
    }

    internal static CompilationUnitSyntax Parse(string source) => CSharpSyntaxTree.ParseText(source,
        new CSharpParseOptions(LanguageVersion.Preview)).GetCompilationUnitRoot();

    internal static bool ContainsChinese(string text) => text.Any(character => character is >= '\u3400' and <= '\u9FFF');
    internal static string Key(string source) => "Text" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    internal static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    internal static int LineAt(string source, int index) => source.AsSpan(0, index).Count('\n') + 1;
}

internal sealed record CatalogDocument(int SchemaVersion, List<CatalogText> Texts, List<XamlElementMap> XamlElements,
    List<MigrationIssue> Issues, int CSharpEligibleOccurrences, int XamlEligibleProperties);
internal sealed record CatalogText(string Key, string Source, List<SourceContext> Contexts);
internal sealed record SourceContext(string File, int Line, string Kind, string? Property);
internal sealed record MigrationIssue(string File, int Line, string Reason, string Source);
internal sealed record XamlElementMap(string File, string Uid, string Element, List<XamlPropertyMap> Properties);
internal sealed record XamlPropertyMap(string Property, string ResourceName, string Key, string Source);

internal sealed class CSharpMigration(IReadOnlyDictionary<string, string> approved, HashSet<string> constantNames)
    : CSharpSyntaxRewriter
{
    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (!IsEligibleTemplate(node, out var template) || BlockReason(node, constantNames) is not null)
            return base.VisitLiteralExpression(node);
        EnsureApproved(template);
        return SyntaxFactory.ParseExpression($"global::App.Localization.UiText.Get(global::App.Localization.UiStringKey.{Program.Key(template)})")
            .WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
    {
        if (!IsEligibleTemplate(node, out var template) || BlockReason(node, constantNames) is not null)
            return base.VisitInterpolatedStringExpression(node);
        EnsureApproved(template);
        var arguments = new List<ArgumentSyntax>
        {
            SyntaxFactory.Argument(SyntaxFactory.ParseExpression($"global::App.Localization.UiStringKey.{Program.Key(template)}")),
        };
        arguments.AddRange(node.Contents.OfType<InterpolationSyntax>()
            .Select(interpolation => SyntaxFactory.Argument((ExpressionSyntax)Visit(interpolation.Expression)!)));
        return SyntaxFactory.InvocationExpression(
                SyntaxFactory.ParseExpression("global::App.Localization.UiText.Format"),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)))
            .WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
    {
        var rewritten = (FieldDeclarationSyntax)base.VisitFieldDeclaration(node)!;
        if (node.Modifiers.Any(SyntaxKind.ConstKeyword) && !rewritten.IsEquivalentTo(node))
        {
            var modifiers = rewritten.Modifiers;
            var constant = modifiers.First(token => token.IsKind(SyntaxKind.ConstKeyword));
            var replacement = SyntaxFactory.Token(SyntaxKind.StaticKeyword)
                .WithLeadingTrivia(constant.LeadingTrivia).WithTrailingTrivia(SyntaxFactory.Space);
            modifiers = modifiers.Replace(constant, replacement)
                .Add(SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space));
            rewritten = rewritten.WithModifiers(modifiers);
        }
        return rewritten;
    }

    public override SyntaxNode? VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node)
    {
        var rewritten = (LocalDeclarationStatementSyntax)base.VisitLocalDeclarationStatement(node)!;
        if (node.Modifiers.Any(SyntaxKind.ConstKeyword) && !rewritten.IsEquivalentTo(node))
        {
            var constant = rewritten.Modifiers.First(token => token.IsKind(SyntaxKind.ConstKeyword));
            rewritten = rewritten.WithModifiers(rewritten.Modifiers.Remove(constant))
                .WithLeadingTrivia(node.GetLeadingTrivia());
        }
        return rewritten;
    }

    private void EnsureApproved(string template)
    {
        if (!approved.TryGetValue(Program.Key(template), out var expected) || expected != template)
            throw new InvalidDataException($"A source string is absent from the approved catalog: {Program.Key(template)}.");
    }

    internal static bool IsEligibleTemplate(SyntaxNode node, out string template) =>
        TryTemplate(node, out template)
        && (Program.ContainsChinese(template) || IsExplicitUiEnglish(node, template));

    private static bool IsExplicitUiEnglish(SyntaxNode node, string template)
    {
        if (!template.Any(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            || template.Contains("://", StringComparison.Ordinal)
            || template.StartsWith("ms-", StringComparison.OrdinalIgnoreCase) && template.Contains(':')
            || template.StartsWith("\\\\", StringComparison.Ordinal)
            || template.Length >= 3 && char.IsAsciiLetter(template[0]) && template[1] == ':'
                && template[2] is '\\' or '/')
            return false;

        var current = node;
        while (current.Parent is ParenthesizedExpressionSyntax
            || current.Parent is BinaryExpressionSyntax binary
                && (binary.IsKind(SyntaxKind.AddExpression) || binary.IsKind(SyntaxKind.CoalesceExpression))
            || current.Parent is ConditionalExpressionSyntax conditional && conditional.Condition != current)
        {
            current = current.Parent!;
        }

        if (current.Parent is AssignmentExpressionSyntax assignment && assignment.Right == current
            && IsUiPropertyName(AssignedName(assignment.Left)))
            return true;
        if (current.Parent is EqualsValueClauseSyntax equals)
        {
            if (equals.Parent is VariableDeclaratorSyntax variable
                && variable.Identifier.ValueText == "label")
                return true;
            if (equals.Parent is PropertyDeclarationSyntax property && IsUiPropertyName(property.Identifier.ValueText))
                return true;
        }
        if (current.Parent is ArrowExpressionClauseSyntax arrow
            && arrow.Parent is PropertyDeclarationSyntax declaration
            && (IsUiPropertyName(declaration.Identifier.ValueText)
                || declaration.Identifier.ValueText.EndsWith("Description", StringComparison.Ordinal)))
            return true;
        if (current.Parent is not ArgumentSyntax argument
            || argument.Parent is not ArgumentListSyntax argumentList
            || argumentList.Parent is not InvocationExpressionSyntax invocation)
            return false;

        var method = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => string.Empty,
        };
        var target = invocation.Expression is MemberAccessExpressionSyntax targetMember
            ? AssignedName(targetMember.Expression)
            : string.Empty;
        var index = argumentList.Arguments.IndexOf(argument);
        var declaringType = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText;
        // These helpers accept display captions only at the specified positions. A general Add
        // or dictionary constructor is deliberately not a UI sink.
        if (declaringType == "RecoveredVaultViewSession" && method == "Add")
            return index == 0;
        if (declaringType == "SshAgentApprovalDialogService" && method == "AddLine")
            return index is 1 or 2;
        if (target == "UiText" && method == "Format")
            return index > 0;
        if (target == "AutomationProperties" && method is "SetName" or "SetHelpText" or "SetItemStatus" or "SetFullDescription")
            return index == 1;
        if (target == "ToolTipService" && method == "SetToolTip")
            return index == 1;
        if (target == "DialogHelper" && method is "ConfirmAsync" or "PromptTextAsync")
            return index >= 1;
        if (target == "UiOperationResult" && method is "Failure" or "Success" or "Cancelled")
            return method == "Failure" && index == 0
                || argument.NameColon?.Name.Identifier.ValueText is "message" or "notice";
        if (method is "ShowFeedback" or "ShowNotice" or "ShowSubpage" or "SetStatus" or "FailOperation"
            or "SetOperationError" or "SetNotice")
            return index == 0;
        if (method == "Complete" && argument.NameColon?.Name.Identifier.ValueText == "message")
            return true;
        return false;
    }

    private static string AssignedName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
        GenericNameSyntax generic => generic.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => string.Empty,
    };

    private static bool IsUiPropertyName(string name) => name is "Text" or "Header" or "Title" or "Description"
        or "Message" or "PrimaryButtonText" or "SecondaryButtonText" or "CloseButtonText" or "PlaceholderText"
        or "OperationError" or "Notice" or "Status" or "Label" or "label" or "Content";

    internal static bool TryTemplate(SyntaxNode node, out string template)
    {
        if (node is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            template = literal.Token.ValueText;
            return true;
        }
        if (node is InterpolatedStringExpressionSyntax interpolated)
        {
            var builder = new StringBuilder();
            var argument = 0;
            foreach (var content in interpolated.Contents)
            {
                if (content is InterpolatedStringTextSyntax text)
                {
                    // The parser exposes escaped braces in ValueText as their literal representation.
                    var value = DecodeInterpolationText(interpolated, text.TextToken.ValueText);
                    builder.Append(value.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
                }
                else if (content is InterpolationSyntax interpolation)
                {
                    builder.Append('{').Append(argument++);
                    if (interpolation.AlignmentClause is { } alignment)
                        builder.Append(',').Append(alignment.Value.ToString());
                    if (interpolation.FormatClause is { } format)
                        builder.Append(':').Append(format.FormatStringToken.ValueText);
                    builder.Append('}');
                }
            }
            template = builder.ToString();
            return true;
        }
        template = string.Empty;
        return false;
    }

    private static string DecodeInterpolationText(InterpolatedStringExpressionSyntax expression, string text)
    {
        // Normal and verbatim interpolated text retains doubled brace escapes in Roslyn tokens.
        // Raw interpolated strings use their own brace-count delimiter and already expose literal text.
        return expression.StringStartToken.Text.Contains("\"\"\"", StringComparison.Ordinal)
            ? text
            : text.Replace("{{", "{", StringComparison.Ordinal).Replace("}}", "}", StringComparison.Ordinal);
    }

    internal static string? BlockReason(SyntaxNode node, HashSet<string> constantNames)
    {
        if (IsConstantOnly(node))
            return "The string occurs in an attribute, pattern, default argument, or other constant-only context.";
        if (IsLogicOperand(node))
            return "The string participates in matching, replacement, a regular expression, or a lookup key and requires semantic review.";
        if (node.Ancestors().OfType<VariableDeclaratorSyntax>().FirstOrDefault() is { } variable
            && (variable.Parent?.Parent is FieldDeclarationSyntax field && field.Modifiers.Any(SyntaxKind.ConstKeyword)
                || variable.Parent?.Parent is LocalDeclarationStatementSyntax local && local.Modifiers.Any(SyntaxKind.ConstKeyword))
            && constantNames.Contains(variable.Identifier.ValueText))
            return "This const is referenced from a constant-only or matching context and needs an explicit API migration.";
        return null;
    }

    internal static bool IsLogicOperand(SyntaxNode node)
    {
        if (node.Parent is BinaryExpressionSyntax binary
            && binary.IsKind(SyntaxKind.EqualsExpression) || node.Parent is BinaryExpressionSyntax unequal
            && unequal.IsKind(SyntaxKind.NotEqualsExpression))
            return true;
        if (node.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault() is { } argument)
        {
            if (argument.Parent?.Parent is InvocationExpressionSyntax invocation)
            {
                var name = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    _ => string.Empty,
                };
                if (name is "Equals" or "Contains" or "StartsWith" or "EndsWith" or "Replace"
                    or "IsMatch" or "Match" or "Matches" or "TryGetValue" or "ContainsKey"
                    or "Remove" or "IndexOf" or "LastIndexOf" or "ToString")
                    return true;
            }
            if (argument.Parent?.Parent is ObjectCreationExpressionSyntax creation
                && creation.Type.ToString().EndsWith("Regex", StringComparison.Ordinal))
                return true;
        }
        if (node.Parent is BracketedArgumentListSyntax
            || node.Ancestors().OfType<ArgumentSyntax>().FirstOrDefault()?.Parent is BracketedArgumentListSyntax)
            return true;
        if (node.Parent is InitializerExpressionSyntax initializer
            && initializer.IsKind(SyntaxKind.ComplexElementInitializerExpression)
            && initializer.Expressions.FirstOrDefault() == node)
            return true;
        return false;
    }

    internal static bool IsConstantOnly(SyntaxNode node) => node.Ancestors().Any(ancestor => ancestor is
        AttributeSyntax or PatternSyntax or SwitchLabelSyntax or EnumMemberDeclarationSyntax
        || ancestor is ParameterSyntax parameter && parameter.Default?.Span.Contains(node.Span) == true
        || ancestor is GotoStatementSyntax statement && statement.IsKind(SyntaxKind.GotoCaseStatement));
}

internal static partial class XamlMigration
{
    private static readonly HashSet<string> Properties = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Description", "PlaceholderText", "Title", "Message", "Label", "Caption",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText", "OnContent", "OffContent",
        "AutomationProperties.Name", "AutomationProperties.HelpText", "AutomationProperties.ItemStatus",
        "AutomationProperties.FullDescription", "AutomationProperties.LocalizedControlType", "ToolTipService.ToolTip",
    };

    internal static IEnumerable<XamlElement> Read(string file, string source, List<MigrationIssue>? issues = null)
    {
        var comments = Comments().Matches(source).Select(match => (match.Index, End: match.Index + match.Length)).ToArray();
        foreach (Match tag in Tags().Matches(source))
        {
            if (comments.Any(comment => tag.Index >= comment.Index && tag.Index < comment.End))
                continue;
            // Window is not a FrameworkElement. MRT's x:Uid property injection cannot assign
            // Window.Title; localized window titles must be assigned in code after XAML loads.
            if (tag.Groups["name"].Value.Split(':').Last() == "Window")
                continue;
            var attributes = Attributes().Matches(tag.Groups["attributes"].Value)
                .Cast<Match>().Select(match => new XamlAttribute(match.Groups["name"].Value,
                    Decode(match.Groups["value"].Value, match.Groups["quote"].Value))).ToArray();
            var property = attributes.FirstOrDefault(attribute => attribute.Name == "Property")?.Value;
            var textProperties = attributes.Where(attribute => (Properties.Contains(attribute.Name)
                    || attribute.Name == "Value" && property is not null && Properties.Contains(property))
                && !attribute.Value.TrimStart().StartsWith('{')
                && attribute.Value.Any(char.IsLetter)).ToArray();
            if (textProperties.Length == 0)
                continue;
            var uid = attributes.FirstOrDefault(attribute => attribute.Name == "x:Uid")?.Value;
            if (uid is not null && uid.TrimStart().StartsWith('{'))
            {
                issues?.Add(new MigrationIssue(file, Program.LineAt(source, tag.Index),
                    "A dynamic x:Uid cannot be migrated automatically.", uid));
                continue;
            }
            uid ??= "L" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file + "\n"
                + tag.Groups["name"].Value + "\n"
                + string.Join("\n", textProperties.OrderBy(attribute => attribute.Name, StringComparer.Ordinal)
                    .Select(attribute => attribute.Name + "=" + attribute.Value)))))[..16];
            var mappings = textProperties.Select(attribute => new XamlPropertyMap(attribute.Name,
                uid + "." + ResourceProperty(attribute.Name), Program.Key(attribute.Value), attribute.Value)).ToList();
            yield return new XamlElement(tag.Index, tag.Groups["name"].Index + tag.Groups["name"].Length,
                attributes.Any(attribute => attribute.Name == "x:Uid"),
                new XamlElementMap(file, uid, tag.Groups["name"].Value, mappings));
        }
    }

    internal static string Apply(string file, string source, IReadOnlyDictionary<string, string> approved)
    {
        source = Tags().Replace(source, tag => tag.Groups["name"].Value.Split(':').Last() == "Window"
            ? Regex.Replace(tag.Value, "\\s+x:Uid\\s*=\\s*(?:\"[^\"]*\"|'[^']*')", string.Empty,
                RegexOptions.CultureInvariant)
            : tag.Value);
        var elements = Read(file, source).ToArray();
        foreach (var mapping in elements.SelectMany(element => element.Map.Properties))
            if (!approved.TryGetValue(mapping.Key, out var expected) || expected != mapping.Source)
                throw new InvalidDataException($"An XAML source string is absent from the approved catalog: {file}: {mapping.Key}.");
        var result = new StringBuilder(source);
        foreach (var element in elements.Where(element => !element.HasUid).OrderByDescending(element => element.Insertion))
            result.Insert(element.Insertion, $" x:Uid=\"{element.Map.Uid}\"");
        return result.ToString();
    }

    private static string ResourceProperty(string property) => property.StartsWith("AutomationProperties.", StringComparison.Ordinal)
        ? "[using:Microsoft.UI.Xaml.Automation]" + property
        : property == "ToolTipService.ToolTip" ? "[using:Microsoft.UI.Xaml.Controls]" + property : property;

    private static string Decode(string value, string quote) => XElement.Parse($"<x v={quote}{value}{quote}/>").Attribute("v")!.Value;

    [GeneratedRegex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Comments();
    [GeneratedRegex("<(?<name>[A-Za-z_][\\w.:+-]*)(?<attributes>(?:\"[^\"]*\"|'[^']*'|[^'\"<>])*)>", RegexOptions.CultureInvariant)]
    private static partial Regex Tags();
    [GeneratedRegex("(?<name>[\\w:.-]+)\\s*=\\s*(?<quote>[\"'])(?<value>.*?)\\k<quote>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex Attributes();
}

internal sealed record XamlAttribute(string Name, string Value);
internal sealed record XamlElement(int Start, int Insertion, bool HasUid, XamlElementMap Map);

internal static class SelfTests
{
    internal static void Run()
    {
        var examples = new[]
        {
            ("$\"共有{x}项\"", "共有{0}项"),
            ("$\"中文{{{value,8:N2}}}\"", "中文{{{0,8:N2}}}"),
            ("$@\"中文\\path {{literal}} {value,-4}\"", "中文\\path {{literal}} {0,-4}"),
            ("\"中文\\n\\\"quoted\\\"\"", "中文\n\"quoted\""),
        };
        foreach (var (input, expected) in examples)
        {
            if (!CSharpMigration.TryTemplate(SyntaxFactory.ParseExpression(input), out var actual) || actual != expected)
                throw new InvalidOperationException($"Template mismatch: {input}: expected {expected}, got {actual}.");
        }

        const string csharp = "class C { private const string A = \"中文\"; public string M(int n) { const string b=\"中文\"; return $\"共有{n}项\" + b + A; } }";
        var templates = new[] { "中文", "共有{0}项" }.ToDictionary(Program.Key, text => text, StringComparer.Ordinal);
        var rewritten = new CSharpMigration(templates, []).Visit(Program.Parse(csharp))!.ToFullString();
        if (!rewritten.Contains("static readonly string A", StringComparison.Ordinal)
            || rewritten.Contains("const string b", StringComparison.Ordinal)
            || !rewritten.Contains("UiText.Format", StringComparison.Ordinal)
            || Program.Parse(rewritten).ContainsDiagnostics)
            throw new InvalidOperationException("The C# rewrite did not preserve valid declaration syntax.");

        const string xaml = "<Page xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"><!-- <Button Content=\"错误\"/> --><Button Content=\"中文\" AutomationProperties.Name=\"中文名称\" Click=\"OnClick\"/><TextBlock Text=\"{Binding Name}\"/></Page>";
        var elements = XamlMigration.Read("Example.xaml", xaml).ToArray();
        if (elements.Length != 1 || elements[0].Map.Properties.Count != 2)
            throw new InvalidOperationException("The XAML inventory included a comment or binding.");
        var xamlApproved = elements.SelectMany(element => element.Map.Properties)
            .ToDictionary(property => property.Key, property => property.Source, StringComparer.Ordinal);
        var localized = XamlMigration.Apply("Example.xaml", xaml, xamlApproved);
        if (XamlMigration.Apply("Example.xaml", localized, xamlApproved) != localized
            || !localized.Contains("Click=\"OnClick\"", StringComparison.Ordinal)
            || !localized.Contains("Text=\"{Binding Name}\"", StringComparison.Ordinal))
            throw new InvalidOperationException("The XAML rewrite was not idempotent or altered application wiring.");

        const string window = "<Window xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" x:Uid=\"OldWindow\" Title=\"Brand\"><FieldRow Label=\"中文\"/></Window>";
        var windowElements = XamlMigration.Read("Window.xaml", window).ToArray();
        if (windowElements.Length != 1 || windowElements[0].Map.Element != "FieldRow")
            throw new InvalidOperationException("Window.Title must not be assigned through FrameworkElement resource injection.");
        var windowMap = windowElements.SelectMany(element => element.Map.Properties)
            .ToDictionary(property => property.Key, property => property.Source, StringComparer.Ordinal);
        var windowResult = XElement.Parse(XamlMigration.Apply("Window.xaml", window, windowMap));
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        if (windowResult.Attribute(xamlNamespace + "Uid") is not null
            || windowResult.Attribute("Title")?.Value != "Brand"
            || windowResult.Element("FieldRow")?.Attribute(xamlNamespace + "Uid") is null)
            throw new InvalidOperationException("Window migration must preserve its title and localize child captions only.");

        const string stable = "class C { string M(string value) => value == \"中文\" ? \"显示\" : \"不同\"; }";
        var stableMap = new[] { "显示", "不同" }.ToDictionary(Program.Key, text => text, StringComparer.Ordinal);
        var stableResult = new CSharpMigration(stableMap, []).Visit(Program.Parse(stable))!.ToFullString();
        if (!stableResult.Contains("value == \"中文\"", StringComparison.Ordinal))
            throw new InvalidOperationException("A comparison operand was rewritten.");

        const string comments = "class C { string M() {\n// retained comment\nconst string message = \"中文\"; return message; } }";
        var commentsResult = new CSharpMigration(templates, []).Visit(Program.Parse(comments))!.ToFullString();
        if (!commentsResult.Contains("// retained comment", StringComparison.Ordinal))
            throw new InvalidOperationException("The local const migration discarded leading comments.");

        const string englishUi = "class C { void M() { dialog.Title = flag ? \"Register\" : \"Unregister\"; ShowFeedback(\"The operation failed.\"); AutomationProperties.SetName(control, \"Approve\"); var result = UiOperationResult<int>.Failure(\"Not available\"); var path = \"api/ciphers\"; dialog.Text = \"https://example.com\"; throw new InvalidOperationException(\"Internal diagnostic\"); } }";
        var englishSources = Program.Parse(englishUi).DescendantNodes()
            .Select(node => IsEnglishCandidate(node, out var value) ? value : null)
            .Where(value => value is not null).ToArray();
        if (!englishSources.Order(StringComparer.Ordinal).SequenceEqual(new[]
            { "Approve", "Not available", "Register", "The operation failed.", "Unregister" }.Order(StringComparer.Ordinal)))
            throw new InvalidOperationException("Explicit English UI extraction included diagnostics or missed UI text.");

        VerifyCompiledInterpolation();
    }

    private static bool IsEnglishCandidate(SyntaxNode node, out string template) =>
        CSharpMigration.IsEligibleTemplate(node, out template) && !Program.ContainsChinese(template);

    private static void VerifyCompiledInterpolation()
    {
        const string source = "public static class Example { public static string Render(int value) => $\"中文{{{value,8:N2}}}\"; }";
        const string template = "中文{{{0,8:N2}}}";
        var key = Program.Key(template);
        var rewritten = new CSharpMigration(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = template }, [])
            .Visit(Program.Parse(source))!.ToFullString();
        var stub = "namespace App.Localization { public enum UiStringKey { " + key
            + " } public static class UiText { public static string Format(UiStringKey key, params object?[] values) => System.String.Format(System.Globalization.CultureInfo.InvariantCulture, "
            + JsonSerializer.Serialize(template) + ", values); } }";
        var assemblies = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
            ?? throw new InvalidOperationException("Runtime metadata paths are unavailable."))
            .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("LocalizationMigrationSelfTest",
            [Program.Parse(rewritten).SyntaxTree, Program.Parse(stub).SyntaxTree], assemblies,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        if (!emitted.Success)
            throw new InvalidOperationException("The interpolation rewrite did not compile: "
                + string.Join("; ", emitted.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)));
        var assembly = System.Reflection.Assembly.Load(output.ToArray());
        var actual = (string)assembly.GetType("Example")!.GetMethod("Render")!.Invoke(null, [12])!;
        var expected = "中文{   12.00}";
        if (actual != expected)
            throw new InvalidOperationException($"The compiled interpolation changed output: {actual}.");
    }
}
