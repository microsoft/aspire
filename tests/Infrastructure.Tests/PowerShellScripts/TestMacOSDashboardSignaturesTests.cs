// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class TestMacOSDashboardSignaturesTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly string _scriptPath;
    private readonly string _codeSignPath;
    private readonly ITestOutputHelper _output;

    public TestMacOSDashboardSignaturesTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "test-macos-dashboard-signatures.ps1");
        _codeSignPath = CreateFakeCodeSign();
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PassesWhenNativeLibrariesMatchDashboardTeamId()
    {
        var dashboardPath = CreateDashboardPayload();

        var result = await RunScript(dashboardPath, dashboardTeamId: "TEAM123", libraryTeamId: "TEAM123");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("macOS Dashboard signature validation passed for 2 native libraries with Team ID 'TEAM123'.", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenNativeLibraryTeamIdDiffers()
    {
        var dashboardPath = CreateDashboardPayload();

        var result = await RunScript(dashboardPath, dashboardTeamId: "TEAM123", libraryTeamId: "not set");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("does not have a TeamIdentifier", result.Output);
    }

    private async Task<CommandResult> RunScript(string dashboardPath, string dashboardTeamId, string libraryTeamId)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithEnvironmentVariable("FAKE_DASHBOARD_TEAM_ID", dashboardTeamId)
            .WithEnvironmentVariable("FAKE_LIBRARY_TEAM_ID", libraryTeamId)
            .WithTimeout(TimeSpan.FromMinutes(1));

        return await cmd.ExecuteAsync(
            "-DashboardPath", $"\"{dashboardPath}\"",
            "-CodeSignPath", $"\"{_codeSignPath}\"");
    }

    private string CreateDashboardPayload()
    {
        var payloadDirectory = Directory.CreateDirectory(Path.Combine(_workspace.Path, "payload"));
        var dashboardPath = Path.Combine(payloadDirectory.FullName, "Aspire.Dashboard");
        File.WriteAllText(dashboardPath, "dashboard");
        File.WriteAllText(Path.Combine(payloadDirectory.FullName, "libe_sqlite3.dylib"), "sqlite");
        File.WriteAllText(Path.Combine(payloadDirectory.FullName, "libhex1binterop.dylib"), "hex1b");
        return dashboardPath;
    }

    private string CreateFakeCodeSign()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var path = Path.Combine(_workspace.Path, "codesign.cmd");
            File.WriteAllText(path,
                """
                @echo off
                set "last="
                :loop
                if "%~1"=="" goto output
                set "last=%~1"
                shift
                goto loop
                :output
                echo Executable=%last% 1>&2
                echo %last% | findstr /C:"Aspire.Dashboard" >nul
                if errorlevel 1 (
                  echo TeamIdentifier=%FAKE_LIBRARY_TEAM_ID% 1>&2
                ) else (
                  echo TeamIdentifier=%FAKE_DASHBOARD_TEAM_ID% 1>&2
                )
                """);
            return path;
        }

        var scriptPath = Path.Combine(_workspace.Path, "codesign");
        File.WriteAllText(scriptPath,
            """
            #!/bin/sh
            last=
            for arg in "$@"; do
              last="$arg"
            done
            echo "Executable=$last" >&2
            case "$last" in
              *Aspire.Dashboard) echo "TeamIdentifier=$FAKE_DASHBOARD_TEAM_ID" >&2 ;;
              *) echo "TeamIdentifier=$FAKE_LIBRARY_TEAM_ID" >&2 ;;
            esac
            """);
        File.SetUnixFileMode(
            scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return scriptPath;
    }
}
