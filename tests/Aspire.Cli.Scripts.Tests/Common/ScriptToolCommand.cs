// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Templates.Tests;
using Xunit;

namespace Aspire.Cli.Scripts.Tests;

/// <summary>
/// Extends ToolCommand to execute shell scripts (bash or PowerShell) with proper argument handling.
/// </summary>
public class ScriptToolCommand : ToolCommand
{
    private readonly string _scriptPath;
    private readonly TestEnvironment _testEnvironment;
    private readonly bool _forceShowBuildOutput;

    /// <summary>
    /// Creates a new command to execute a script.
    /// </summary>
    /// <param name="scriptPath">Relative path to the script from repo root (e.g., "eng/scripts/get-aspire-cli.sh")</param>
    /// <param name="testEnvironment">Test environment providing isolated temp directories</param>
    /// <param name="testOutput">xUnit test output helper</param>
    /// <param name="forceShowBuildOutput">Whether to force showing build output in the test results</param>
    public ScriptToolCommand(
        string scriptPath,
        TestEnvironment testEnvironment,
        ITestOutputHelper testOutput,
        bool forceShowBuildOutput = false)
        : base(GetExecutable(scriptPath), testOutput, label: Path.GetFileName(scriptPath))
    {
        _scriptPath = scriptPath;
        _testEnvironment = testEnvironment;
        _forceShowBuildOutput = forceShowBuildOutput;

        // Set mock HOME to prevent any accidental user directory access
        WithEnvironmentVariable("HOME", _testEnvironment.MockHome);
        WithEnvironmentVariable("USERPROFILE", _testEnvironment.MockHome);
        
        // Disable any real PATH modifications during tests
        WithEnvironmentVariable("ASPIRE_TEST_MODE", "true");
    }

    /// <summary>
    /// Determines the executable (bash or pwsh) based on the script extension.
    /// </summary>
    private static string GetExecutable(string scriptPath)
    {
        return scriptPath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)
            ? "bash"
            : "pwsh";
    }

    /// <summary>
    /// Builds the full command arguments including the script path and user-provided arguments.
    /// </summary>
    protected override string GetFullArgs(params string[] args)
    {
        // Find the repo root
        var repoRoot = TestUtils.FindRepoRoot()?.FullName
            ?? throw new InvalidOperationException("Could not find repository root");

        // Resolve the full script path
        var fullScriptPath = Path.Combine(repoRoot, _scriptPath);
        
        if (!File.Exists(fullScriptPath))
        {
            throw new FileNotFoundException($"Script not found: {fullScriptPath}");
        }

        // For bash: bash script.sh args...
        // For PowerShell: pwsh -File script.ps1 args...
        if (_scriptPath.EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
        {
            // Bash: bash script.sh arg1 arg2
            return $"\"{fullScriptPath}\" {string.Join(" ", args)}";
        }
        else
        {
            // PowerShell: pwsh -File script.ps1 arg1 arg2
            var escapedArgs = args.Select(arg => 
            {
                // Escape PowerShell special characters if needed
                if (arg.Contains(' ') || arg.Contains('"'))
                {
                    return $"\"{arg.Replace("\"", "`\"")}\"";
                }
                return arg;
            });
            return $"-File \"{fullScriptPath}\" {string.Join(" ", escapedArgs)}";
        }
    }
}
