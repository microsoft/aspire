// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Templates.Tests;
using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

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
        // Override XDG_CONFIG_HOME to prevent scripts that consult
        // ${XDG_CONFIG_HOME:-$HOME/.config} from reading a real profile
        // outside the temp home when the developer has XDG_CONFIG_HOME set.
        WithEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_testEnvironment.MockHome, ".config"));

        // Default timeout to prevent hanging tests — individual tests can override via WithTimeout()
        WithTimeout(TimeSpan.FromSeconds(60));
    }

    private static string GetExecutable(string scriptPath)
    {
        return scriptPath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ? "bash" : "pwsh";
    }

    protected override string GetFullArgs(params string[] args)
    {
        var repoRoot = TestUtils.RepoRoot;
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
        var wrapperContent = $$"""
            #!/bin/bash
            # Save current shell options
            _saved_opts=$(set +o)
            _saved_shopt=$(shopt -p 2>/dev/null || true)

            # Override the readonly builtin so that re-sourcing the script doesn't
            # fail on 'readonly VAR=value' when VAR was already declared readonly
            # in a previous source invocation. This suppresses ALL readonly errors —
            # not just redeclaration — but in practice the only readonly failures
            # in these scripts are redeclaration, and narrowing to that specific
            # case would require fragile stderr parsing across bash versions.
            readonly() { builtin readonly "$@" 2>/dev/null || true; }

            source "{{fullScriptPath}}"

            # Remove our readonly override
            unset -f readonly

            # Restore original shell options
            eval "$_saved_opts" 2>/dev/null || true
            eval "$_saved_shopt" 2>/dev/null || true

            {{_functionExpression}}
            """;
        File.WriteAllText(tempScript, wrapperContent);

        // Make executable on Unix
        FileHelper.MakeExecutable(tempScript);

        return $"\"{tempScript}\"";
    }

    private string BuildPowerShellArgs(string fullScriptPath)
    {
        // Use PowerShell's AST parser to extract only function definitions and
        // top-level variable assignments ($Script:* constants that functions depend on).
        // This avoids fragile string-based stripping of comment markers and control-flow
        // blocks, and works regardless of script formatting or structure.
        var tempScript = Path.Combine(_testEnvironment.TempDirectory, $"test-func-{Guid.NewGuid():N}.ps1");

        // Escape single quotes in the path for PowerShell single-quoted strings
        var escapedScriptPath = fullScriptPath.Replace("'", "''");

        var scriptContent = $$"""
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile(
                '{{escapedScriptPath}}', [ref]$tokens, [ref]$errors)

            if ($errors.Count -gt 0) {
                throw "Script has $($errors.Count) parse error(s): $($errors[0].Message)"
            }

            # Walk top-level statements directly. FindAll with a Parent check doesn't
            # work here because the Parent of top-level statements is the EndBlock
            # (NamedBlockAst), not the ScriptBlockAst. Extract only assignments and
            # function definitions — skip if/try blocks that contain main execution logic.
            foreach ($stmt in $ast.EndBlock.Statements) {
                if ($stmt -is [System.Management.Automation.Language.AssignmentStatementAst] -or
                    $stmt -is [System.Management.Automation.Language.FunctionDefinitionAst]) {
                    . ([ScriptBlock]::Create($stmt.Extent.Text))
                }
            }

            {{_functionExpression}}
            """;

        File.WriteAllText(tempScript, scriptContent);

        return $"-NoProfile -File \"{tempScript}\"";
    }
}
