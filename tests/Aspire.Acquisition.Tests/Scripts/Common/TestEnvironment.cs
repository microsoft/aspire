// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

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
        TempDirectory = Directory.CreateTempSubdirectory("aspire-test-").FullName;
        MockHome = Path.Combine(TempDirectory, "home");

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
            // Windows batch script — uses top-level goto dispatch and 'exit'
            // (not 'exit /b') throughout. Two CMD bugs are avoided:
            //  1. 'exit /b' inside parenthesized if () blocks is unreliable
            //     and may fall through to subsequent commands.
            //  2. When PowerShell invokes a .cmd file whose arguments contain
            //     '&' (e.g. API URLs like "?event=pull_request&head_sha=abc"),
            //     CMD interprets '&' as a command separator and tries to run
            //     the text after '&' as a separate command. Using 'exit'
            //     (without /b) terminates the entire CMD process, preventing
            //     the spurious second command from executing. This is safe
            //     because PowerShell spawns a fresh cmd.exe for each .cmd
            //     invocation.
            scriptContent = """
                @echo off
                setlocal
                if "%~1"=="--version" goto :ver
                if "%~1"=="api" goto :api
                if "%~1"=="pr" goto :pr
                if "%~1"=="run" goto :run
                goto :unknown

                :ver
                echo gh version 2.50.0 (mock)
                exit 0

                :api
                rem Parse endpoint and --jq flag to return realistic responses
                set "ENDPOINT=%~2"
                set "JQ_FILTER="
                set "IDX=2"
                :api_parse
                shift
                if "%~2"=="" goto :api_dispatch
                if "%~2"=="--jq" (
                    set "JQ_FILTER=%~3"
                    shift
                    goto :api_parse
                )
                goto :api_parse

                :api_dispatch
                echo %ENDPOINT% | findstr /C:"/pulls/" >nul 2>&1
                if not errorlevel 1 (
                    if defined JQ_FILTER (
                        echo abc123def456789012345678901234567890abcd
                    ) else (
                        echo {"head":{"sha":"abc123def456789012345678901234567890abcd"}}
                    )
                    exit 0
                )
                echo %ENDPOINT% | findstr /C:"/actions/workflows/" >nul 2>&1
                if not errorlevel 1 (
                    if defined JQ_FILTER (
                        echo 987654321
                    ) else (
                        echo {"workflow_runs":[{"id":987654321,"conclusion":"success"}]}
                    )
                    exit 0
                )
                echo {}
                exit 0

                :pr
                if "%~2"=="list" goto :pr_list
                goto :unknown

                :pr_list
                echo [{"number":12345,"mergedAt":"2024-01-01T00:00:00Z","headRefOid":"abc123"}]
                exit 0

                :run
                if "%~2"=="list" goto :run_list
                if "%~2"=="view" goto :run_view
                if "%~2"=="download" goto :run_dl
                goto :unknown

                :run_list
                echo [{"databaseId":987654321,"conclusion":"success"}]
                exit 0

                :run_view
                echo {"artifacts":[{"name":"cli-native-linux-x64"},{"name":"built-nugets"},{"name":"built-nugets-for-linux-x64"}]}
                exit 0

                :run_dl
                set "DLDIR="
                shift
                shift
                :parse_dl
                if "%~1"=="" goto :do_dl
                if "%~1"=="-D" (
                    set "DLDIR=%~2"
                    shift
                    shift
                    goto :parse_dl
                )
                shift
                goto :parse_dl
                :do_dl
                if defined DLDIR (
                    if not exist "%DLDIR%" mkdir "%DLDIR%"
                    echo fake-archive> "%DLDIR%\fake-archive.tar.gz"
                )
                exit 0

                :unknown
                echo Mock gh: Unknown command: %* 1>&2
                exit 1
                """;
        }
        else
        {
            // Unix shell script
            scriptContent = """
                #!/bin/bash
                # Mock gh CLI for testing
                if [ "$1" = "--version" ]; then
                    echo "gh version 2.50.0 (mock)"
                    exit 0
                fi
                if [ "$1" = "pr" ] && [ "$2" = "list" ]; then
                    echo '[{"number":12345,"mergedAt":"2024-01-01T00:00:00Z","headRefOid":"abc123"}]'
                    exit 0
                fi
                if [ "$1" = "run" ]; then
                    if [ "$2" = "list" ]; then
                        echo '[{"databaseId":987654321,"conclusion":"success"}]'
                        exit 0
                    fi
                    if [ "$2" = "view" ]; then
                        echo '{"artifacts":[{"name":"cli-native-linux-x64"},{"name":"built-nugets"},{"name":"built-nugets-for-linux-x64"}]}'
                        exit 0
                    fi
                    if [ "$2" = "download" ]; then
                        # Parse -D <dir> from args
                        download_dir=""
                        shift 2
                        while [ $# -gt 0 ]; do
                            case "$1" in
                                -D) download_dir="$2"; shift 2 ;;
                                *) shift ;;
                            esac
                        done
                        if [ -n "$download_dir" ]; then
                            mkdir -p "$download_dir"
                            # Create files listed in MOCK_GH_DOWNLOAD_FILES (newline-separated)
                            if [ -n "${MOCK_GH_DOWNLOAD_FILES:-}" ]; then
                                echo "$MOCK_GH_DOWNLOAD_FILES" | while IFS= read -r fname; do
                                    [ -n "$fname" ] && echo "fake-archive" > "$download_dir/$fname"
                                done
                            fi
                        fi
                        exit 0
                    fi
                fi
                if [ "$1" = "api" ]; then
                    # Parse endpoint and --jq flag to return realistic responses
                    endpoint="$2"
                    jq_filter=""
                    shift 2
                    while [ $# -gt 0 ]; do
                        case "$1" in
                            --jq) jq_filter="$2"; shift 2 ;;
                            *) shift ;;
                        esac
                    done

                    # PR head SHA lookup: repos/.../pulls/<number>
                    if echo "$endpoint" | grep -q "/pulls/"; then
                        if [ -n "$jq_filter" ]; then
                            echo "abc123def456789012345678901234567890abcd"
                        else
                            echo '{"head":{"sha":"abc123def456789012345678901234567890abcd"}}'
                        fi
                        exit 0
                    fi

                    # Workflow run lookup: repos/.../actions/workflows/...
                    if echo "$endpoint" | grep -q "/actions/workflows/"; then
                        if [ -n "$jq_filter" ]; then
                            echo "987654321"
                        else
                            echo '{"workflow_runs":[{"id":987654321,"conclusion":"success"}]}'
                        fi
                        exit 0
                    fi

                    # Default for other API calls
                    echo '{}'
                    exit 0
                fi
                echo "Mock gh: Unknown command: $*" >&2
                exit 1
                """;
        }

        await File.WriteAllTextAsync(ghScriptPath, scriptContent);

        if (!isWindows)
        {
            FileHelper.MakeExecutable(ghScriptPath);
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
