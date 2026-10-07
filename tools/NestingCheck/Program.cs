using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// Check every hand-written C# function, including tests and release tools.
const int limit = 3;
var root = FindRoot(Environment.CurrentDirectory);
var files = new[] { "src", "tools", "tests" }
    .SelectMany(directory => Directory.EnumerateFiles(Path.Combine(root, directory), "*.cs", SearchOption.AllDirectories))
    .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin"))
    .ToArray();
var errors = new List<string>();
foreach (var path in files)
{
    foreach (var (name, depth) in Inspect(File.ReadAllText(path)))
    {
        if (depth <= limit) continue;
        errors.Add($"{Path.GetRelativePath(root, path)}: {name}: nesting {depth} exceeds {limit}");
    }
}
foreach (var error in errors) Console.Error.WriteLine(error);
Console.WriteLine($"{(errors.Count == 0 ? "PASS" : "FAIL")} nesting: {files.Length} C# files, limit {limit}, {errors.Count} violations");
return errors.Count == 0 ? 0 : 1;

static Dictionary<string, int> Inspect(string source)
{
    var tree = CSharpSyntaxTree.ParseText(source);
    var result = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var node in tree.GetRoot().DescendantNodes().Where(IsFunction))
    {
        var name = $"{FunctionName(node)}:{tree.GetLineSpan(node.Span).StartLinePosition.Line + 1}";
        var depth = 0;
        Walk(node, node, 0, ref depth);
        result[name] = Math.Max(result.GetValueOrDefault(name), depth);
    }
    return result;
}

static void Walk(SyntaxNode owner, SyntaxNode node, int depth, ref int maximum)
{
    if (node != owner && IsFunction(node)) return;
    if (IsControl(node)) depth++;
    maximum = Math.Max(maximum, depth);
    foreach (var child in node.ChildNodes()) Walk(owner, child, depth, ref maximum);
}

static bool IsControl(SyntaxNode node) => node is IfStatementSyntax or
    ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax or
    WhileStatementSyntax or DoStatementSyntax or SwitchStatementSyntax or TryStatementSyntax;

static bool IsFunction(SyntaxNode node) => node is BaseMethodDeclarationSyntax or
    LocalFunctionStatementSyntax or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax;

static string FunctionName(SyntaxNode node)
{
    var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.ValueText ?? "<global>";
    return node switch
    {
        MethodDeclarationSyntax method => $"{type}.{method.Identifier.ValueText}{method.ParameterList}",
        ConstructorDeclarationSyntax constructor => $"{type}.{constructor.Identifier.ValueText}{constructor.ParameterList}",
        LocalFunctionStatementSyntax local => $"{type}.{local.Identifier.ValueText}{local.ParameterList}",
        AccessorDeclarationSyntax accessor => $"{type}.{accessor.Parent?.Parent}::{accessor.Keyword.ValueText}",
        AnonymousFunctionExpressionSyntax lambda => $"{type}.{lambda.Parent?.Kind()}::lambda",
        _ => $"{type}.{node.Kind()}"
    };
}

static string FindRoot(string directory)
{
    for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
        if (Directory.Exists(Path.Combine(current.FullName, ".git"))) return current.FullName;
    throw new DirectoryNotFoundException("Git workspace not found.");
}
