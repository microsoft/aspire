// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Templates.Tests;

public class ScriptCommand : ToolCommand
{
    private readonly string _scriptPath;

    public ScriptCommand(string scriptPath, ITestOutputHelper testOutput, string label = "")
        : base(GetExecutable(scriptPath), testOutput, label: string.IsNullOrEmpty(label) ? Path.GetFileName(scriptPath) : label)
    {
        _scriptPath = scriptPath;
    }

    internal static string QuoteBashArgument(string value) => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    protected override string GetFullArgs(params string[] args)
    {
        var fullScriptPath = Path.IsPathRooted(_scriptPath)
            ? _scriptPath
            : Path.Combine(TestUtils.RepoRoot, _scriptPath);

        if (!File.Exists(fullScriptPath))
        {
            throw new FileNotFoundException($"Script not found: {fullScriptPath}");
        }

        return IsBashScript(_scriptPath)
            ? string.Join(" ", [QuoteCommandLineArgument(fullScriptPath), .. args.Select(QuoteCommandLineArgument)])
            : string.Join(" ", ["-File", QuoteCommandLineArgument(fullScriptPath), .. args.Select(QuoteCommandLineArgument)]);
    }

    private static string GetExecutable(string scriptPath)
        => IsBashScript(scriptPath) ? "bash" : "pwsh";

    private static bool IsBashScript(string scriptPath)
        => scriptPath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase);

    private static string QuoteCommandLineArgument(string value) => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
