// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace Aspire.Cli.Scripts.Tests;

/// <summary>
/// Creates URL-pattern-aware mock gh scripts that support PR lookup, workflow run discovery,
/// artifact listing, gh run download, and error injection via environment variables.
/// </summary>
public static class EnhancedMockGhHelper
{
    public static async Task<string> CreateEnhancedMockGhAsync(
        TestEnvironment env,
        ITestOutputHelper testOutput,
        string? fakeArchiveDir = null)
    {
        var mockBinDir = Path.Combine(env.TempDirectory, "enhanced-mock-bin");
        Directory.CreateDirectory(mockBinDir);

        var logFile = GetLogPath(env);

        if (OperatingSystem.IsWindows())
        {
            var ghScriptPath = Path.Combine(mockBinDir, "gh.cmd");
            var content = CreateWindowsMockGh(logFile, fakeArchiveDir);
            await File.WriteAllTextAsync(ghScriptPath, content);
        }
        else
        {
            var ghScriptPath = Path.Combine(mockBinDir, "gh");
            var content = CreateUnixMockGh(logFile, fakeArchiveDir);
            await File.WriteAllTextAsync(ghScriptPath, content);
            await MakeExecutableAsync(ghScriptPath);
        }

        testOutput.WriteLine($"Created enhanced mock gh at: {mockBinDir}");
        testOutput.WriteLine($"Mock gh log at: {logFile}");

        return mockBinDir;
    }

    public static string GetLogPath(TestEnvironment env) => Path.Combine(env.TempDirectory, "mock-gh.log");

    public static async Task<string[]> ReadLogAsync(TestEnvironment env)
    {
        var logPath = GetLogPath(env);
        if (!File.Exists(logPath))
        {
            return [];
        }
        return await File.ReadAllLinesAsync(logPath);
    }

    private static async Task MakeExecutableAsync(string path)
    {
        using var chmod = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{path}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        chmod.Start();
        await chmod.WaitForExitAsync();
    }

    private static string CreateUnixMockGh(string logFile, string? fakeArchiveDir)
    {
        var archiveSetup = fakeArchiveDir is not null
            ? $"FAKE_ARCHIVE_DIR=\"{fakeArchiveDir}\""
            : "FAKE_ARCHIVE_DIR=\"\"";

        return $@"#!/bin/bash
# Enhanced mock gh CLI for testing
LOG_FILE=""{logFile}""
{archiveSetup}

# Log all invocations
echo ""gh $*"" >> ""$LOG_FILE""

# Error injection via environment variables
if [ ""${{MOCK_GH_FAIL_AUTH:-}}"" = ""1"" ]; then
    echo ""error: authentication required"" >&2
    echo ""Try running: gh auth login"" >&2
    exit 1
fi

if [ ""$1"" = ""--version"" ]; then
    echo ""gh version 2.50.0 (mock)""
    exit 0
fi

if [ ""$1"" = ""auth"" ] && [ ""$2"" = ""status"" ]; then
    echo ""github.com""
    echo ""  ✓ Logged in to github.com""
    exit 0
fi

if [ ""$1"" = ""api"" ]; then
    ENDPOINT=""$2""

    if [ ""${{MOCK_GH_NO_RUNS:-}}"" = ""1"" ] && echo ""$ENDPOINT"" | grep -q ""actions/workflows""; then
        echo '{{""workflow_runs"":[]}}'
        exit 0
    fi

    if [ ""${{MOCK_GH_NO_ARTIFACTS:-}}"" = ""1"" ] && echo ""$ENDPOINT"" | grep -q ""artifacts""; then
        echo '{{""artifacts"":[]}}'
        exit 0
    fi

    # PR lookup: repos/OWNER/REPO/pulls/NUMBER
    if echo ""$ENDPOINT"" | grep -qE ""repos/[^/]+/[^/]+/pulls/[0-9]+""; then
        JQ_FILTER=""""
        shift 2
        while [ $# -gt 0 ]; do
            if [ ""$1"" = ""--jq"" ]; then
                JQ_FILTER=""$2""
                shift 2
            else
                shift
            fi
        done
        if [ ""$JQ_FILTER"" = "".head.sha"" ]; then
            echo ""abc123def456""
        else
            echo '{{""number"":12345,""head"":{{""sha"":""abc123def456""}}}}'
        fi
        exit 0
    fi

    # Workflow runs: repos/OWNER/REPO/actions/workflows/.../runs
    if echo ""$ENDPOINT"" | grep -qE ""actions/workflows/.*/runs""; then
        JQ_FILTER=""""
        shift 2
        while [ $# -gt 0 ]; do
            if [ ""$1"" = ""--jq"" ]; then
                JQ_FILTER=""$2""
                shift 2
            else
                shift
            fi
        done
        if [ -n ""$JQ_FILTER"" ]; then
            echo ""987654321""
        else
            echo '{{""workflow_runs"":[{{""id"":987654321,""conclusion"":""success"",""head_sha"":""abc123def456"",""event"":""pull_request"",""name"":""Build""}}]}}'
        fi
        exit 0
    fi

    # Default API response
    echo '{{}}'
    exit 0
fi

if [ ""$1"" = ""run"" ]; then
    if [ ""$2"" = ""list"" ]; then
        echo '[{{""databaseId"":987654321,""conclusion"":""success"",""headSha"":""abc123def456""}}]'
        exit 0
    fi
    if [ ""$2"" = ""view"" ]; then
        echo '{{""artifacts"":[{{""name"":""cli-native-linux-x64""}},{{""name"":""cli-native-osx-arm64""}},{{""name"":""cli-native-win-x64""}},{{""name"":""built-nugets""}},{{""name"":""built-nugets-for-linux-x64""}},{{""name"":""aspire-extension""}}]}}'
        exit 0
    fi
    if [ ""$2"" = ""download"" ]; then
        DEST_DIR=""""
        shift 2
        while [ $# -gt 0 ]; do
            if [ ""$1"" = ""-D"" ]; then
                DEST_DIR=""$2""
                shift 2
            else
                shift
            fi
        done
        if [ -n ""$DEST_DIR"" ]; then
            mkdir -p ""$DEST_DIR""
            if [ -n ""$FAKE_ARCHIVE_DIR"" ] && [ -d ""$FAKE_ARCHIVE_DIR"" ]; then
                cp ""$FAKE_ARCHIVE_DIR""/* ""$DEST_DIR/"" 2>/dev/null || true
            else
                echo ""mock-archive-content"" > ""$DEST_DIR/placeholder.tar.gz""
            fi
        fi
        exit 0
    fi
fi

if [ ""$1"" = ""pr"" ] && [ ""$2"" = ""list"" ]; then
    echo '[{{""number"":12345,""mergedAt"":""2024-01-01T00:00:00Z"",""headRefOid"":""abc123def456""}}]'
    exit 0
fi

echo ""Mock gh: Unknown command: $*"" >&2
exit 1
";
    }

    private static string CreateWindowsMockGh(string logFile, string? _)
    {
        return $@"@echo off
echo gh %* >> ""{logFile}""
if ""%1""==""--version"" (
    echo gh version 2.50.0 ^(mock^)
    exit /b 0
)
if ""%1""==""api"" (
    echo {{}}
    exit /b 0
)
if ""%1""==""run"" (
    if ""%2""==""list"" (
        echo [{{""databaseId"":987654321,""conclusion"":""success""}}]
        exit /b 0
    )
    if ""%2""==""view"" (
        echo {{""artifacts"":[{{""name"":""cli-native-win-x64""}},{{""name"":""built-nugets""}}]}}
        exit /b 0
    )
)
if ""%1""==""pr"" (
    if ""%2""==""list"" (
        echo [{{""number"":12345,""mergedAt"":""2024-01-01T00:00:00Z"",""headRefOid"":""abc123def456""}}]
        exit /b 0
    )
)
echo Mock gh: Unknown command: %* 1>&2
exit /b 1
";
    }
}
