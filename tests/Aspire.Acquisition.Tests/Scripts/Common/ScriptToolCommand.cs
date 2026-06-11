// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;
using Aspire.Templates.Tests;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Extends ToolCommand to execute shell scripts (bash or PowerShell) with proper argument handling.
/// </summary>
public class ScriptToolCommand : ToolCommand
{
    private readonly string _scriptPath;
    private readonly TestEnvironment _testEnvironment;

    /// <summary>
    /// Creates a new command to execute a script.
    /// </summary>
    /// <param name="scriptPath">Relative path to the script from repo root (e.g., "eng/scripts/get-aspire-cli.sh")</param>
    /// <param name="testEnvironment">Test environment providing isolated temp directories</param>
    /// <param name="testOutput">xUnit test output helper</param>
    public ScriptToolCommand(
        string scriptPath,
        TestEnvironment testEnvironment,
        ITestOutputHelper testOutput)
        : base(GetExecutable(scriptPath), testOutput, label: Path.GetFileName(scriptPath))
    {
        _scriptPath = scriptPath;
        _testEnvironment = testEnvironment;

        // Set mock HOME to prevent any accidental user directory access
        WithEnvironmentVariable("HOME", _testEnvironment.MockHome);
        WithEnvironmentVariable("USERPROFILE", _testEnvironment.MockHome);
        // Override XDG_CONFIG_HOME to prevent scripts that consult
        // ${XDG_CONFIG_HOME:-$HOME/.config} from reading a real profile
        // outside the temp home when the developer has XDG_CONFIG_HOME set.
        WithEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(_testEnvironment.MockHome, ".config"));

        // Disable any real PATH modifications during tests
        WithEnvironmentVariable("ASPIRE_TEST_MODE", "true");

        // Default timeout to prevent hanging tests — individual tests can override via WithTimeout()
        WithTimeout(TimeSpan.FromSeconds(60));
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
            // Bash: bash script.sh arg1 arg2 — quote args containing spaces
            var escapedArgs = args.Select(arg =>
            {
                if (arg.Contains(' ') || arg.Contains('"'))
                {
                    return $"\"{arg.Replace("\"", "\\\"")}\"";
                }
                return arg;
            });
            return $"\"{fullScriptPath}\" {string.Join(" ", escapedArgs)}";
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
