// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Cli.Scripts.Tests;

/// <summary>
/// Provides safe, isolated temporary directory environment for CLI script tests.
/// CRITICAL: All operations must be confined to temp directories to ensure zero risk to user environments.
/// </summary>
public sealed class TestEnvironment : IDisposable
{
    /// <summary>
    /// Root temporary directory for this test instance.
    /// </summary>
    public string TempDirectory { get; }

    /// <summary>
    /// Mock HOME directory within the temp directory.
    /// Can be used to override HOME/USERPROFILE environment variables safely.
    /// </summary>
    public string MockHome { get; }

    /// <summary>
    /// Creates a new isolated test environment with temporary directories.
    /// </summary>
    public TestEnvironment()
    {
        // CRITICAL: Must use Path.GetTempPath() - never user directories
        TempDirectory = Path.Combine(Path.GetTempPath(), $"aspire-test-{Guid.NewGuid():N}");
        MockHome = Path.Combine(TempDirectory, "home");
        
        // Create the directories
        Directory.CreateDirectory(TempDirectory);
        Directory.CreateDirectory(MockHome);
    }

    /// <summary>
    /// Creates a mock gh CLI script that returns fake data for testing.
    /// This allows tests to run without requiring actual GitHub authentication.
    /// </summary>
    public async Task<string> CreateMockGhScriptAsync(ITestOutputHelper testOutput)
    {
        var mockBinDir = Path.Combine(TempDirectory, "mock-bin");
        Directory.CreateDirectory(mockBinDir);

        var isWindows = OperatingSystem.IsWindows();
        var ghScriptPath = Path.Combine(mockBinDir, isWindows ? "gh.cmd" : "gh");

        string scriptContent;
        if (isWindows)
        {
            // Windows batch script
            scriptContent = @"@echo off
REM Mock gh CLI for testing
if ""%1""==""pr"" (
    if ""%2""==""list"" (
        echo [{""number"":12345,""mergedAt"":""2024-01-01T00:00:00Z"",""headRefOid"":""abc123""}]
        exit /b 0
    )
)
if ""%1""==""run"" (
    if ""%2""==""list"" (
        echo [{""databaseId"":987654321,""conclusion"":""success""}]
        exit /b 0
    )
    if ""%2""==""view"" (
        echo {""artifacts"":[{""name"":""cli-native-linux-x64""},{""name"":""built-nugets""},{""name"":""built-nugets-for-linux-x64""}]}
        exit /b 0
    )
)
echo Mock gh: Unknown command
exit /b 0
";
        }
        else
        {
            // Unix shell script
            scriptContent = @"#!/bin/bash
# Mock gh CLI for testing
if [ ""$1"" = ""pr"" ] && [ ""$2"" = ""list"" ]; then
    echo '[{""number"":12345,""mergedAt"":""2024-01-01T00:00:00Z"",""headRefOid"":""abc123""}]'
    exit 0
fi
if [ ""$1"" = ""run"" ]; then
    if [ ""$2"" = ""list"" ]; then
        echo '[{""databaseId"":987654321,""conclusion"":""success""}]'
        exit 0
    fi
    if [ ""$2"" = ""view"" ]; then
        echo '{""artifacts"":[{""name"":""cli-native-linux-x64""},{""name"":""built-nugets""},{""name"":""built-nugets-for-linux-x64""}]}'
        exit 0
    fi
fi
echo ""Mock gh: Unknown command""
exit 0
";
        }

        await File.WriteAllTextAsync(ghScriptPath, scriptContent);

        if (!isWindows)
        {
            // Make the script executable on Unix
            var chmod = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{ghScriptPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            chmod.Start();
            await chmod.WaitForExitAsync();
        }

        testOutput.WriteLine($"Created mock gh script at: {ghScriptPath}");
        return mockBinDir;
    }

    /// <summary>
    /// Cleans up the temporary directory.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(TempDirectory))
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }
}
