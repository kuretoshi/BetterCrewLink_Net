using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// The upstream v3.2.16 check caps control nesting at three. Existing .NET
// methods predate that rule, so compare them with the last beta; new methods
// are strict and changed methods may not become deeper.
const int limit = 3;
const string baselineRef = "v3.2.14-net-beta.1";
var root = FindRoot(Environment.CurrentDirectory);
var tracked = Git(root, "diff", "--name-only", baselineRef, "--", "src", "tools");
var untracked = Git(root, "ls-files", "--others", "--exclude-standard", "--", "src", "tools");
var changed = (tracked + "\n" + untracked)
    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
    .Select(path => path.Trim())
    .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
    .Distinct(StringComparer.Ordinal)
    .ToArray();
var errors = new List<string>();
foreach (var path in changed)
{
    var currentPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    if (!File.Exists(currentPath)) continue;
    var current = Inspect(File.ReadAllText(currentPath));
    var original = TryGit(root, "show", $"{baselineRef}:{path}");
    var baseline = original is null ? new Dictionary<string, int>() : Inspect(original);
    foreach (var (name, depth) in current)
    {
        if (depth <= limit || baseline.GetValueOrDefault(name) >= depth) continue;
        errors.Add($"{path}: {name}: nesting {depth} exceeds {limit} (baseline {baseline.GetValueOrDefault(name)})");
    }
}
foreach (var error in errors) Console.Error.WriteLine(error);
Console.WriteLine($"{(errors.Count == 0 ? "PASS" : "FAIL")} nesting: {changed.Length} changed C# files, limit {limit}, {errors.Count} regressions");
return errors.Count == 0 ? 0 : 1;

static Dictionary<string, int> Inspect(string source)
{
    var tree = CSharpSyntaxTree.ParseText(source);
    var result = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var node in tree.GetRoot().DescendantNodes().Where(IsFunction))
    {
        var name = FunctionName(node);
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

static string Git(string root, params string[] arguments) =>
    TryGit(root, arguments) ?? throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed");

static string? TryGit(string root, params string[] arguments)
{
    var start = new ProcessStartInfo("git")
    {
        WorkingDirectory = root,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start git.");
    var output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0 ? output : null;
}
