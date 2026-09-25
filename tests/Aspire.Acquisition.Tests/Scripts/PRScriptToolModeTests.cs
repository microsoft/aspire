// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests for the --install-mode tool / -InstallMode Tool flag on get-aspire-cli-pr.{sh,ps1}.
/// These tests validate parameter handling, validation rules, and dry-run/WhatIf behavior
/// without invoking real 'dotnet tool install'.
/// </summary>
public class PRScriptToolModeTests(ITestOutputHelper testOutput)
{
    private readonly ITestOutputHelper _testOutput = testOutput;

    private static string GetNonHostArchitecture() =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "x64" : "arm64";

    private static async Task<string> CreateLocalDirWithAspireCliPackageAsync(string root, string version = "13.3.0-pr.1234.abc")
    {
        Directory.CreateDirectory(root);
        await FakeArchiveHelper.CreateFakeNupkgAsync(root, "Aspire.Cli", version);
        await FakeArchiveHelper.CreateFakeNupkgAsync(root, "Aspire.Hosting", version);
        return root;
    }

    private async Task<ScriptToolCommand> CreateBashCommandWithMockGhAsync(TestEnvironment env)
    {
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        return cmd;
    }

    private async Task<ScriptToolCommand> CreatePsCommandWithMockGhAsync(TestEnvironment env)
    {
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        return cmd;
    }

    private static async Task<string> CreateMockDotnetScriptAsync(TestEnvironment env, ITestOutputHelper testOutput)
    {
        var mockBinDir = Path.Combine(env.TempDirectory, "mock-dotnet-bin");
        Directory.CreateDirectory(mockBinDir);

        var isWindows = OperatingSystem.IsWindows();
        var dotnetScriptPath = Path.Combine(mockBinDir, isWindows ? "dotnet.cmd" : "dotnet");

        var scriptContent = isWindows
            ? """
                @echo off
                setlocal
                if not "%MOCK_DOTNET_COMMANDS_FILE%"=="" echo %*>> "%MOCK_DOTNET_COMMANDS_FILE%"
                if "%~1"=="--version" (
                    echo 10.0.100
                    exit 0
                )
                if "%~1"=="tool" if "%~2"=="install" goto :tool_install
                if "%~1"=="tool" if "%~2"=="update" goto :tool_update
                echo Mock dotnet: Unknown command: %* 1>&2
                exit 1

                :tool_install
                if "%MOCK_DOTNET_TOOL_INSTALL_FAIL%"=="true" (
                    echo Mock dotnet tool install failed 1>&2
                    exit 42
                )
                exit 0

                :tool_update
                if "%MOCK_DOTNET_TOOL_UPDATE_FAIL%"=="true" (
                    echo Mock dotnet tool update failed 1>&2
                    exit 43
                )
                exit 0
                """
            : """
                #!/usr/bin/env bash
                set -euo pipefail

                if [[ -n "${MOCK_DOTNET_COMMANDS_FILE:-}" ]]; then
                    printf '%s\n' "$*" >> "$MOCK_DOTNET_COMMANDS_FILE"
                fi

                case "${1:-}" in
                    --version)
                        echo "10.0.100"
                        exit 0
                        ;;
                    tool)
                        case "${2:-}" in
                            install)
                                if [[ "${MOCK_DOTNET_TOOL_INSTALL_FAIL:-}" == "true" ]]; then
                                    echo "Mock dotnet tool install failed" >&2
                                    exit 42
                                fi
                                exit 0
                                ;;
                            update)
                                if [[ "${MOCK_DOTNET_TOOL_UPDATE_FAIL:-}" == "true" ]]; then
                                    echo "Mock dotnet tool update failed" >&2
                                    exit 43
                                fi
                                exit 0
                                ;;
                        esac
                        ;;
                esac

                echo "Mock dotnet: Unknown command: $*" >&2
                exit 1
                """;

        await File.WriteAllTextAsync(dotnetScriptPath, scriptContent);

        if (!isWindows)
        {
            FileHelper.MakeExecutable(dotnetScriptPath);
        }

        testOutput.WriteLine($"Created mock dotnet script at: {dotnetScriptPath}");
        return mockBinDir;
    }

    // ----------------------------------------------------------------------
    // Bash: --install-mode / --force
    // ----------------------------------------------------------------------

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_Help_DescribesInstallModeAndForce()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);

        var result = await cmd.ExecuteAsync("--help");

        result.EnsureSuccessful();
        Assert.Contains("--install-mode", result.Output);
        Assert.Contains("-m, --install-mode", result.Output);
        Assert.Contains("--force", result.Output);
        Assert.Contains("archive", result.Output);
        Assert.Contains("tool", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_InvalidInstallMode_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "--install-mode", "bogus", "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Invalid value for --install-mode", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_LocalDir_DryRun_SkipsArchiveAndShowsDotnetToolInstall()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--dry-run",
            "--skip-path");

        result.EnsureSuccessful();
        Assert.DoesNotContain("Downloading CLI", result.Output);
        Assert.DoesNotContain("cli-native-archives", result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
        Assert.Contains("--add-source", result.Output);
        // Tool mode now populates the hive so `aspire new` can discover the PR
        // version of Aspire.AppHost.Sdk / Aspire.Hosting / Aspire.ProjectTemplates.
        Assert.Contains("Would copy nugets", result.Output);
        Assert.DoesNotContain("config set channel", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ShortInstallModeTool_LocalDir_DryRun_UsesDotnetToolInstall()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "-m", "tool",
            "--dry-run",
            "--skip-path");

        result.EnsureSuccessful();
        Assert.DoesNotContain("Would install CLI archive", result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ShortInstallPath_RemainsInstallPath()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        await FakeArchiveHelper.CreateFakeArchiveAsync(localDir);
        var customPath = Path.Combine(env.TempDirectory, "custom");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "-i", customPath,
            "--dry-run",
            "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains($"Would install CLI archive to: {Path.Combine(customPath, "bin")}", result.Output);
        Assert.DoesNotContain("dotnet tool install", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_PrDryRun_SkipsCliNativeArchivesDownload()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345", "--install-mode", "tool",
            "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
        // Tool mode must NOT download the cli-native-archives artifact.
        Assert.DoesNotContain("cli-native-archives", result.Output);
        // Tool mode DOES download the cross-platform built-nugets artifact so the
        // hive contains Aspire.Hosting / Aspire.AppHost.Sdk / Aspire.ProjectTemplates.
        Assert.Contains("--name built-nugets -D", result.Output);
        Assert.Contains("built-nugets-for-", result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
        Assert.DoesNotContain("config set channel", result.Output);
        Assert.DoesNotContain("route sidecar", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dogfood/pr-12345/bin", result.Output);
        Assert.DoesNotContain(".dotnet/tools", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_RunIdAloneRequiresHiveLabel()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "--run-id", "987654321",
            "--install-mode", "tool",
            "--dry-run",
            "--skip-path",
            "--verbose");

        // Tool mode now populates the hive like archive mode, so it requires
        // either --pr-number or --hive-label (the installed CLI's package
        // channel is baked at build time and won't look in a run-<id> hive).
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Cannot determine hive label", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_RunIdWithHiveLabel_DryRun_PopulatesHive()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "--run-id", "987654321",
            "--hive-label", "pr-99999",
            "--install-mode", "tool",
            "--dry-run",
            "--skip-path",
            "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("--name built-nugets -D", result.Output);
        Assert.Contains("built-nugets-for-", result.Output);
        Assert.Contains("hives/pr-99999/packages", result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_RejectsCrossArchitectureOverride()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "12345",
            "--install-mode", "tool",
            "--arch", GetNonHostArchitecture(),
            "--dry-run",
            "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--install-mode tool cannot target", result.Output);
        Assert.Contains("current host", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_ExplicitInstallPathUsesToolPath()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        var customPath = Path.Combine(env.TempDirectory, "custom");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--install-path", customPath,
            "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains($"dotnet tool install --tool-path {Path.Combine(customPath, "bin")} Aspire.Cli", result.Output);
        Assert.DoesNotContain("dotnet tool install --global Aspire.Cli", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_RejectsHiveOnly()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--hive-only",
            "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--hive-only cannot be combined with --install-mode tool", result.Output);
        Assert.DoesNotContain("dotnet tool install", result.Output);
        Assert.DoesNotContain("dotnet tool update", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ArchiveMode_RejectsForce()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--force",
            "--dry-run", "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--force can only be combined with --install-mode tool", result.Output);
        Assert.DoesNotContain("dotnet tool install --", result.Output);
        Assert.DoesNotContain("dotnet tool update --", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_InstallFailureSuggestsForce()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);
        var dotnetCommandsPath = Path.Combine(env.TempDirectory, "dotnet-commands.txt");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_TOOL_INSTALL_FAIL", "true");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_COMMANDS_FILE", dotnetCommandsPath);

        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--skip-extension",
            "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Failed to install Aspire.Cli dotnet tool", result.Output);
        Assert.Contains("re-run with --force", result.Output);

        var dotnetCommands = await File.ReadAllTextAsync(dotnetCommandsPath, TestContext.Current.CancellationToken);
        Assert.Contains("tool install --global Aspire.Cli", dotnetCommands);
        Assert.DoesNotContain("tool list", dotnetCommands);
        Assert.DoesNotContain("tool update", dotnetCommands);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_ForceUpdatesExistingToolWithAllowDowngrade()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir, "13.3.0-pr.5678.deadbeef");
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);
        var dotnetCommandsPath = Path.Combine(env.TempDirectory, "dotnet-commands.txt");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_COMMANDS_FILE", dotnetCommandsPath);

        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--force",
            "--skip-extension",
            "--skip-path");

        result.EnsureSuccessful();

        var dotnetCommands = await File.ReadAllTextAsync(dotnetCommandsPath, TestContext.Current.CancellationToken);
        Assert.Contains("tool update --global Aspire.Cli --version 13.3.0-pr.5678.deadbeef", dotnetCommands);
        // The hive is populated from --local-dir and the tool is installed from the
        // hive directory (not the raw --local-dir), giving --add-source a durable
        // location for any later `dotnet tool update` invocations. The hive label
        // is auto-detected from the pr-flavored package version suffix (pr-5678).
        var prHive = Path.Combine(env.MockHome, ".aspire", "hives", "pr-5678", "packages");
        Assert.Contains($"--add-source {prHive}", dotnetCommands);
        Assert.Contains("--allow-downgrade", dotnetCommands);
        Assert.True(Directory.Exists(prHive), $"Hive should be populated at {prHive}.");
        Assert.True(File.Exists(Path.Combine(prHive, "Aspire.Hosting.13.3.0-pr.5678.deadbeef.nupkg")),
            "Cross-platform Aspire.Hosting nupkg should be copied to the hive.");
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_ForceUpdateFailureReportsUpdate()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir, "13.3.0-pr.5678.deadbeef");
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);
        var dotnetCommandsPath = Path.Combine(env.TempDirectory, "dotnet-commands.txt");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_TOOL_UPDATE_FAIL", "true");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_COMMANDS_FILE", dotnetCommandsPath);

        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--force",
            "--skip-extension",
            "--skip-path");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Failed to update Aspire.Cli dotnet tool", result.Output);
        Assert.DoesNotContain("re-run with --force", result.Output);

        var dotnetCommands = await File.ReadAllTextAsync(dotnetCommandsPath, TestContext.Current.CancellationToken);
        Assert.Contains("tool update --global Aspire.Cli --version 13.3.0-pr.5678.deadbeef", dotnetCommands);
        Assert.Contains("--allow-downgrade", dotnetCommands);
        Assert.DoesNotContain("tool install", dotnetCommands);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_PrInstall_DoesNotWritePRRouteSidecar()
    {
        using var env = new TestEnvironment();
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);
        var dotnetCommandsPath = Path.Combine(env.TempDirectory, "dotnet-commands.txt");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_COMMANDS_FILE", dotnetCommandsPath);
        cmd.WithEnvironmentVariable("MOCK_GH_DOWNLOAD_FILES", """
            Aspire.Cli.13.3.0-pr.5678.deadbeef.nupkg
            Aspire.Hosting.13.3.0-pr.5678.deadbeef.nupkg
            """);

        var result = await cmd.ExecuteAsync(
            "12345", "--install-mode", "tool",
            "--skip-extension",
            "--skip-path");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-12345", "bin", ".aspire-install.json");
        Assert.False(File.Exists(sidecarPath), $"Tool mode must not write the PR-route sidecar at {sidecarPath}.");
        // Tool mode now populates the PR hive so `aspire new`/`aspire run` can
        // resolve the PR version of Aspire.AppHost.Sdk and friends.
        var prHive = Path.Combine(env.MockHome, ".aspire", "hives", "pr-12345", "packages");
        Assert.True(Directory.Exists(prHive), $"Hive should be populated at {prHive}.");
        Assert.True(File.Exists(Path.Combine(prHive, "Aspire.Hosting.13.3.0-pr.5678.deadbeef.nupkg")),
            "Cross-platform Aspire.Hosting nupkg should be copied to the hive.");

        var dotnetCommands = await File.ReadAllTextAsync(dotnetCommandsPath, TestContext.Current.CancellationToken);
        Assert.Contains("tool install --global Aspire.Cli --version 13.3.0-pr.5678.deadbeef", dotnetCommands);
        Assert.Contains($"--add-source {prHive}", dotnetCommands);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_GlobalInstallDoesNotModifyPath()
    {
        using var env = new TestEnvironment();
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_GH_DOWNLOAD_FILES", """
            Aspire.Cli.13.3.0-pr.5678.deadbeef.nupkg
            Aspire.Hosting.13.3.0-pr.5678.deadbeef.nupkg
            """);

        var result = await cmd.ExecuteAsync(
            "12345", "--install-mode", "tool",
            "--skip-extension");

        result.EnsureSuccessful();
        Assert.DoesNotContain(".dotnet/tools", result.Output);
        Assert.DoesNotContain("Add to your shell profile", result.Output);
        Assert.DoesNotContain("Would add", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_DryRun_DoesNotSkipExtensionByDefault()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345", "--install-mode", "tool",
            "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
        // Extension download/install behavior is preserved in tool mode unless --skip-extension is set.
        Assert.Contains("extension", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Skipping VS Code extension", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_DryRun_SkipExtensionFlagHonored()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345", "--install-mode", "tool",
            "--skip-extension",
            "--dry-run", "--skip-path", "--verbose");

        result.EnsureSuccessful();
        Assert.Contains("Skipping VS Code extension", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_ExplicitInstallPathAddsToolPathToPath()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var expectedToolPath = Path.Combine(customPath, "bin");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--install-path", customPath,
            "--dry-run");

        result.EnsureSuccessful();
        Assert.Contains($"dotnet tool install --tool-path {expectedToolPath} Aspire.Cli", result.Output);
        Assert.Contains($"Would add {expectedToolPath} to PATH", result.Output);
    }

    // ----------------------------------------------------------------------
    // Bash: find_aspire_cli_package_version function tests
    // ----------------------------------------------------------------------

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_FindAspireCliPackageVersion_ReturnsExactVersion()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        await CreateLocalDirWithAspireCliPackageAsync(pkgDir, "13.3.0-pr.5678.deadbeef");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRShell,
            $"find_aspire_cli_package_version '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("13.3.0-pr.5678.deadbeef", result.Output.Trim());
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_FindAspireCliPackageVersion_IgnoresNonPointerPackages()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        await CreateLocalDirWithAspireCliPackageAsync(pkgDir, "13.3.0-pr.5678.deadbeef");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "Aspire.Cli.13.3.0-pr.5678.deadbeef.symbols.nupkg"), "fake-symbols-nupkg-content");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "Aspire.Cli.13.3.0-pr.5678.deadbeef.snupkg"), "fake-symbols-package-content");
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Cli.linux-x64", "13.3.0-pr.5678.deadbeef");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "Aspire.Cli.13_3_0.nupkg"), "fake-invalid-version-content");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRShell,
            $"find_aspire_cli_package_version '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("13.3.0-pr.5678.deadbeef", result.Output.Trim());
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_FindAspireCliPackageVersion_NoMatchFails()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        Directory.CreateDirectory(pkgDir);
        // Only a non-Aspire.Cli package — should fail.
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Hosting", "1.0.0");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "Aspire.Cli.1_0.nupkg"), "fake-invalid-version-content");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRShell,
            $"find_aspire_cli_package_version '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No Aspire.Cli", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_FindAspireCliPackageVersion_MultipleMatchesFails()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        Directory.CreateDirectory(pkgDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Cli", "1.0.0");
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Cli", "2.0.0");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRShell,
            $"find_aspire_cli_package_version '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Multiple Aspire.Cli", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_InstallOrUpdateAspireCliTool_ForceUsesUpdateWithAllowDowngrade()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        await CreateLocalDirWithAspireCliPackageAsync(pkgDir, "13.3.0-pr.5678.deadbeef");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRShell,
            $"DRY_RUN=true; FORCE=true; install_or_update_aspire_cli_tool '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("dotnet tool update --global Aspire.Cli", result.Output);
        Assert.Contains("--allow-downgrade", result.Output);
    }

    // ----------------------------------------------------------------------
    // PowerShell: -InstallMode / -Force
    // ----------------------------------------------------------------------

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_Help_DescribesInstallModeAndForce()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);

        var result = await cmd.ExecuteAsync("-?");

        result.EnsureSuccessful();
        var normalized = System.Text.RegularExpressions.Regex.Replace(result.Output, @"\r?\n\s*", "");
        Assert.Contains("InstallMode", normalized);
        Assert.Contains("Force", normalized);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_InvalidInstallMode_ReturnsError()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreatePsCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync("12345", "-InstallMode", "Bogus", "-WhatIf", "-SkipPath");

        Assert.NotEqual(0, result.ExitCode);
        // PowerShell's [ValidateSet] error mentions the invalid value.
        Assert.Contains("Bogus", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_LocalDir_WhatIf_ShowsDotnetToolInstall()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-InstallMode", "Tool",
            "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
        Assert.Contains("--add-source", result.Output);
        // Tool mode now populates the hive so `aspire new` can discover the PR
        // version of Aspire.AppHost.Sdk / Aspire.Hosting / Aspire.ProjectTemplates.
        Assert.Contains("Copying built nugets", result.Output);
        Assert.DoesNotContain("config set channel", result.Output);
        Assert.DoesNotContain("aspire-cli-linux-", result.Output);
        Assert.DoesNotContain(".tar.gz", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ShortInstallModeTool_LocalDir_WhatIf_UsesDotnetToolInstall()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-m", "Tool",
            "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.DoesNotContain("Would install CLI archive", result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ShortInstallPath_RemainsInstallPath()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        await FakeArchiveHelper.CreateFakeArchiveAsync(localDir);
        var customPath = Path.Combine(env.TempDirectory, "custom");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-i", customPath,
            "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains($"Installing Aspire CLI to {Path.Combine(customPath, "bin")}", result.Output);
        Assert.DoesNotContain("dotnet tool install", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_PrWhatIf_SkipsCliNativeArchivesDownload()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreatePsCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345", "-InstallMode", "Tool",
            "-SkipPath", "-WhatIf",
            "-Verbose");

        result.EnsureSuccessful();
        Assert.DoesNotContain("cli-native-archives", result.Output);
        // Tool mode DOES download the cross-platform built-nugets artifact so the
        // hive contains Aspire.Hosting / Aspire.AppHost.Sdk / Aspire.ProjectTemplates.
        Assert.Contains("--name built-nugets -D", result.Output);
        Assert.Contains("built-nugets-for-", result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
        Assert.DoesNotContain("config set channel", result.Output);
        Assert.DoesNotContain("route sidecar", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dogfood", result.Output);
        Assert.DoesNotContain(".dotnet", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_RunIdAloneRequiresHiveLabel()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreatePsCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "-WorkflowRunId", "987654321",
            "-InstallMode", "Tool",
            "-SkipPath",
            "-WhatIf",
            "-Verbose");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Cannot determine hive label", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_RunIdWithHiveLabel_WhatIf_PopulatesHive()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreatePsCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "-WorkflowRunId", "987654321",
            "-HiveLabel", "pr-99999",
            "-InstallMode", "Tool",
            "-SkipPath",
            "-WhatIf",
            "-Verbose");

        result.EnsureSuccessful();
        Assert.Contains("--name built-nugets -D", result.Output);
        Assert.Contains("built-nugets-for-", result.Output);
        // Path separator differs across platforms; use Path.Combine for portability.
        Assert.Contains(Path.Combine("hives", "pr-99999", "packages"), result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_RejectsCrossArchitectureOverride()
    {
        using var env = new TestEnvironment();
        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);

        var result = await cmd.ExecuteAsync(
            "12345",
            "-InstallMode", "Tool",
            "-Architecture", GetNonHostArchitecture(),
            "-SkipPath",
            "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("-InstallMode Tool cannot target", result.Output);
        Assert.Contains("dotnet tool install resolves RID-specific packages", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_ExplicitInstallPathUsesToolPath()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        var customPath = Path.Combine(env.TempDirectory, "custom");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-InstallMode", "Tool",
            "-InstallPath", customPath,
            "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains($"dotnet tool install --tool-path {Path.Combine(customPath, "bin")} Aspire.Cli", result.Output);
        Assert.DoesNotContain("dotnet tool install --global Aspire.Cli", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_RejectsHiveOnly()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-InstallMode", "Tool",
            "-HiveOnly",
            "-SkipPath", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("-HiveOnly cannot be combined with -InstallMode Tool", result.Output);
        Assert.DoesNotContain("dotnet tool install", result.Output);
        Assert.DoesNotContain("dotnet tool update", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ArchiveMode_RejectsForce()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-Force",
            "-SkipPath", "-WhatIf");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("-Force can only be combined with -InstallMode Tool", result.Output);
        Assert.DoesNotContain("dotnet tool install --", result.Output);
        Assert.DoesNotContain("dotnet tool update --", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_InstallFailureSuggestsForce()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);
        var dotnetCommandsPath = Path.Combine(env.TempDirectory, "dotnet-commands.txt");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_TOOL_INSTALL_FAIL", "true");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_COMMANDS_FILE", dotnetCommandsPath);

        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-InstallMode", "Tool",
            "-SkipExtension",
            "-SkipPath");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("re-run with -Force", result.Output);

        var dotnetCommands = await File.ReadAllTextAsync(dotnetCommandsPath, TestContext.Current.CancellationToken);
        Assert.Contains("tool install --global Aspire.Cli", dotnetCommands);
        Assert.DoesNotContain("tool list", dotnetCommands);
        Assert.DoesNotContain("tool update", dotnetCommands);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_ForceUpdatesExistingToolWithAllowDowngrade()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir, "13.3.0-pr.5678.deadbeef");
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);
        var dotnetCommandsPath = Path.Combine(env.TempDirectory, "dotnet-commands.txt");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_COMMANDS_FILE", dotnetCommandsPath);

        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-InstallMode", "Tool",
            "-Force",
            "-SkipExtension",
            "-SkipPath");

        result.EnsureSuccessful();

        var dotnetCommands = await File.ReadAllTextAsync(dotnetCommandsPath, TestContext.Current.CancellationToken);
        Assert.Contains("tool update --global Aspire.Cli --version 13.3.0-pr.5678.deadbeef", dotnetCommands);
        // The hive is populated from -LocalDir and the tool is installed from the
        // hive directory (not the raw -LocalDir), giving --add-source a durable
        // location for any later `dotnet tool update` invocations. The hive label
        // is auto-detected from the pr-flavored package version suffix (pr-5678).
        var prHive = Path.Combine(env.MockHome, ".aspire", "hives", "pr-5678", "packages");
        Assert.Contains($"--add-source {prHive}", dotnetCommands);
        Assert.Contains("--allow-downgrade", dotnetCommands);
        Assert.True(Directory.Exists(prHive), $"Hive should be populated at {prHive}.");
        Assert.True(File.Exists(Path.Combine(prHive, "Aspire.Hosting.13.3.0-pr.5678.deadbeef.nupkg")),
            "Cross-platform Aspire.Hosting nupkg should be copied to the hive.");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_PrInstall_DoesNotWritePRRouteSidecar()
    {
        using var env = new TestEnvironment();
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);
        var dotnetCommandsPath = Path.Combine(env.TempDirectory, "dotnet-commands.txt");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_DOTNET_COMMANDS_FILE", dotnetCommandsPath);
        cmd.WithEnvironmentVariable("MOCK_GH_DOWNLOAD_FILES", """
            Aspire.Cli.13.3.0-pr.5678.deadbeef.nupkg
            Aspire.Hosting.13.3.0-pr.5678.deadbeef.nupkg
            """);

        var result = await cmd.ExecuteAsync(
            "12345", "-InstallMode", "Tool",
            "-SkipExtension",
            "-SkipPath");

        result.EnsureSuccessful();

        var sidecarPath = Path.Combine(env.MockHome, ".aspire", "dogfood", "pr-12345", "bin", ".aspire-install.json");
        Assert.False(File.Exists(sidecarPath), $"Tool mode must not write the PR-route sidecar at {sidecarPath}.");
        // Tool mode now populates the PR hive so `aspire new`/`aspire run` can
        // resolve the PR version of Aspire.AppHost.Sdk and friends.
        var prHive = Path.Combine(env.MockHome, ".aspire", "hives", "pr-12345", "packages");
        Assert.True(Directory.Exists(prHive), $"Hive should be populated at {prHive}.");
        Assert.True(File.Exists(Path.Combine(prHive, "Aspire.Hosting.13.3.0-pr.5678.deadbeef.nupkg")),
            "Cross-platform Aspire.Hosting nupkg should be copied to the hive.");

        var dotnetCommands = await File.ReadAllTextAsync(dotnetCommandsPath, TestContext.Current.CancellationToken);
        Assert.Contains("tool install --global Aspire.Cli --version 13.3.0-pr.5678.deadbeef", dotnetCommands);
        Assert.Contains($"--add-source {prHive}", dotnetCommands);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_GlobalInstallDoesNotModifyPath()
    {
        using var env = new TestEnvironment();
        var mockGhPath = await env.CreateMockGhScriptAsync(_testOutput);
        var mockDotnetPath = await CreateMockDotnetScriptAsync(env, _testOutput);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        cmd.WithEnvironmentVariable("PATH", $"{mockDotnetPath}{Path.PathSeparator}{mockGhPath}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}");
        cmd.WithEnvironmentVariable("MOCK_GH_DOWNLOAD_FILES", """
            Aspire.Cli.13.3.0-pr.5678.deadbeef.nupkg
            Aspire.Hosting.13.3.0-pr.5678.deadbeef.nupkg
            """);

        var result = await cmd.ExecuteAsync(
            "12345", "-InstallMode", "Tool",
            "-SkipExtension");

        result.EnsureSuccessful();
        Assert.DoesNotContain(".dotnet", result.Output);
        Assert.DoesNotContain("Add to your shell profile", result.Output);
        Assert.DoesNotContain("Added", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_WhatIf_DoesNotSkipExtensionByDefault()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreatePsCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345", "-InstallMode", "Tool",
            "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("extension", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Skipping VS Code extension", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_ExplicitInstallPathAddsToolPathToPath()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        var customPath = Path.Combine(env.TempDirectory, "custom");
        var expectedToolPath = Path.Combine(customPath, "bin");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-InstallMode", "Tool",
            "-InstallPath", customPath,
            "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains($"dotnet tool install --tool-path {expectedToolPath} Aspire.Cli", result.Output);
        Assert.Contains(expectedToolPath, result.Output);
    }

    // ----------------------------------------------------------------------
    // PowerShell: Find-AspireCliPackageVersion function tests
    // ----------------------------------------------------------------------

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_FindAspireCliPackageVersion_ReturnsExactVersion()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        await CreateLocalDirWithAspireCliPackageAsync(pkgDir, "13.3.0-pr.5678.deadbeef");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRPowerShell,
            $"Find-AspireCliPackageVersion -SearchDir '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("13.3.0-pr.5678.deadbeef", result.Output.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_FindAspireCliPackageVersion_IgnoresNonPointerPackages()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        await CreateLocalDirWithAspireCliPackageAsync(pkgDir, "13.3.0-pr.5678.deadbeef");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "Aspire.Cli.13.3.0-pr.5678.deadbeef.symbols.nupkg"), "fake-symbols-nupkg-content");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "Aspire.Cli.13.3.0-pr.5678.deadbeef.snupkg"), "fake-symbols-package-content");
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Cli.linux-x64", "13.3.0-pr.5678.deadbeef");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "Aspire.Cli.13_3_0.nupkg"), "fake-invalid-version-content");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRPowerShell,
            $"Find-AspireCliPackageVersion -SearchDir '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("13.3.0-pr.5678.deadbeef", result.Output.Trim());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_FindAspireCliPackageVersion_NoMatchFails()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        Directory.CreateDirectory(pkgDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Hosting", "1.0.0");
        await File.WriteAllTextAsync(Path.Combine(pkgDir, "Aspire.Cli.1_0.nupkg"), "fake-invalid-version-content");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRPowerShell,
            $"Find-AspireCliPackageVersion -SearchDir '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("No Aspire.Cli", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_FindAspireCliPackageVersion_MultipleMatchesFails()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        Directory.CreateDirectory(pkgDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Cli", "1.0.0");
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Cli", "2.0.0");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRPowerShell,
            $"Find-AspireCliPackageVersion -SearchDir '{pkgDir}'",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Multiple Aspire.Cli", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_InstallAspireCliTool_ForceUsesUpdateWithAllowDowngrade()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        await CreateLocalDirWithAspireCliPackageAsync(pkgDir, "13.3.0-pr.5678.deadbeef");

        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.PRPowerShell,
            $"$Force = $true; Install-AspireCliTool -HiveDir '{pkgDir}' -WhatIf",
            env,
            _testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Contains("dotnet tool update --global Aspire.Cli", result.Output);
        Assert.Contains("--allow-downgrade", result.Output);
    }
}
