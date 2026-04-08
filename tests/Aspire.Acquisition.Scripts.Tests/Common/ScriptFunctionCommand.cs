// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Templates.Tests;
using Xunit;

namespace Aspire.Acquisition.Scripts.Tests;

/// <summary>
/// Sources a script and calls an individual function, enabling unit-level testing of script internals.
/// Uses a temporary wrapper script to avoid quoting and scope issues with inline commands.
/// </summary>
public class ScriptFunctionCommand : ToolCommand
{
    private readonly string _scriptPath;
    private readonly string _functionExpression;
    private readonly TestEnvironment _testEnvironment;

    /// <summary>
    /// Creates a command that sources a script and calls a function.
    /// </summary>
    /// <param name="scriptPath">Relative path to the script from repo root.</param>
    /// <param name="functionExpression">
    /// The full function call expression.
    /// For bash: <c>"construct_aspire_cli_url '' 'release' 'linux-x64' 'tar.gz'"</c>
    /// For PS1: <c>"Get-AspireCliUrl -Quality 'release' -RuntimeIdentifier 'linux-x64' -Extension 'tar.gz'"</c>
    /// </param>
    /// <param name="testEnvironment">Test environment providing isolated temp directories.</param>
    /// <param name="testOutput">xUnit test output helper.</param>
    public ScriptFunctionCommand(
        string scriptPath,
        string functionExpression,
        TestEnvironment testEnvironment,
        ITestOutputHelper testOutput)
        : base(GetExecutable(scriptPath), testOutput, label: $"{Path.GetFileName(scriptPath)}:func")
    {
        _scriptPath = scriptPath;
        _functionExpression = functionExpression;
        _testEnvironment = testEnvironment;

        WithEnvironmentVariable("HOME", _testEnvironment.MockHome);
        WithEnvironmentVariable("USERPROFILE", _testEnvironment.MockHome);
    }

    private static string GetExecutable(string scriptPath)
    {
        return scriptPath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ? "bash" : "pwsh";
    }

    protected override string GetFullArgs(params string[] args)
    {
        var repoRoot = TestUtils.FindRepoRoot()?.FullName
            ?? throw new InvalidOperationException("Could not find repository root");
        var fullScriptPath = Path.Combine(repoRoot, _scriptPath);

        if (!File.Exists(fullScriptPath))
        {
            throw new FileNotFoundException($"Script not found: {fullScriptPath}");
        }

        if (_scriptPath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            return BuildBashArgs(fullScriptPath);
        }
        else
        {
            return BuildPowerShellArgs(fullScriptPath);
        }
    }

    private string BuildBashArgs(string fullScriptPath)
    {
        // Write a temp bash script that sources the target and calls the function.
        // Sourcing works cleanly because bash scripts use a BASH_SOURCE guard that
        // skips main() when sourced from another script.
        //
        // We save/restore shell options and guard readonly variables so that:
        //  - The sourced script's `set -euo pipefail` doesn't leak into the wrapper
        //  - Re-sourcing the script doesn't fail on `readonly` redeclaration
        var tempScript = Path.Combine(_testEnvironment.TempDirectory, $"test-func-{Guid.NewGuid():N}.sh");
        var wrapperContent =
            "#!/bin/bash\n" +
            "# Save current shell options\n" +
            "_saved_opts=$(set +o)\n" +
            "_saved_shopt=$(shopt -p 2>/dev/null || true)\n" +
            "\n" +
            "# Allow readonly redeclaration to be silently ignored\n" +
            "readonly() { builtin readonly \"$@\" 2>/dev/null || true; }\n" +
            "\n" +
            $"source \"{fullScriptPath}\"\n" +
            "\n" +
            "# Remove our readonly override\n" +
            "unset -f readonly\n" +
            "\n" +
            "# Restore original shell options\n" +
            "eval \"$_saved_opts\" 2>/dev/null || true\n" +
            "eval \"$_saved_shopt\" 2>/dev/null || true\n" +
            "\n" +
            $"{_functionExpression}\n";
        File.WriteAllText(tempScript, wrapperContent);

        // Make executable on Unix
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempScript,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return $"\"{tempScript}\"";
    }

    private string BuildPowerShellArgs(string fullScriptPath)
    {
        // PowerShell scripts have their main execution block at the bottom and an early
        // "if ($Help)" exit check before most function definitions. To load only the
        // function definitions without running the main block:
        //   1. Read the script content
        //   2. Strip the main execution block from the bottom
        //   3. Strip the early "if ($Help)" block (if present) that would exit/return
        //   4. Create a ScriptBlock from the stripped content and dot-source it
        var isPrScript = _scriptPath.Contains("pr", StringComparison.OrdinalIgnoreCase);
        var tempScript = Path.Combine(_testEnvironment.TempDirectory, $"test-func-{Guid.NewGuid():N}.ps1");

        // Each script uses a different marker before its main execution try/catch block
        var mainMarker = isPrScript ? "# Main Execution" : "# Run main function";

        // Escape single quotes in the path for PowerShell single-quoted strings
        var escapedScriptPath = fullScriptPath.Replace("'", "''");

        var scriptContent = $$"""
            $content = Get-Content '{{escapedScriptPath}}' -Raw

            # Strip the main execution block at the bottom of the script
            $mainIdx = $content.IndexOf('{{mainMarker}}')
            if ($mainIdx -gt 0) { $content = $content.Substring(0, $mainIdx) }

            # Strip the early "if ($Help)" block that exits before function definitions
            $helpIdx = $content.IndexOf('if ($Help) {')
            if ($helpIdx -gt 0) {
                $endIdx = $content.IndexOf("`n}", $helpIdx)
                if ($endIdx -gt 0) { $content = $content.Substring(0, $helpIdx) + $content.Substring($endIdx + 2) }
            }

            $sb = [ScriptBlock]::Create($content)
            . $sb
            {{_functionExpression}}
            """;

        File.WriteAllText(tempScript, scriptContent);

        return $"-NoProfile -File \"{tempScript}\"";
    }
}
