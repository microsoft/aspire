// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Cli.Npm;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Npm;

[Collection(EnvVarMutatingTestCollection.Name)]
public class NpmRunnerTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PackageRegistry_UsesPublicNpmRegistry()
    {
        var registryConstants = typeof(NpmRunner)
            .GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string) && field.Name.Contains("Registry", StringComparison.Ordinal))
            .Select(field => (string?)field.GetRawConstantValue())
            .ToArray();

        Assert.Contains("https://registry.npmjs.org/", registryConstants);
        Assert.DoesNotContain(registryConstants, value => value?.Contains("pkgs.dev.azure.com", StringComparison.OrdinalIgnoreCase) is true);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_SetsCommonProperties()
    {
        var startInfo = NpmRunner.CreateNpmProcessStartInfo("/usr/bin/npm", ["view", "express", "version"], "/tmp/workdir", new TestEnvironment());

        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal("/tmp/workdir", startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnWindows_WithCmdExtension_UsesCmdExe()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.cmd",
            ["view", "@playwright/cli@0.1.1", "version", "--registry", "https://registry.npmjs.org/"],
            @"C:\temp\workdir", new TestEnvironment());

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Contains("npm.cmd", startInfo.Arguments);
        Assert.Contains("view", startInfo.Arguments);
        Assert.Contains("@playwright/cli@0.1.1", startInfo.Arguments);
        Assert.Contains("version", startInfo.Arguments);
        Assert.Contains("--registry", startInfo.Arguments);
        Assert.StartsWith("/c ", startInfo.Arguments);
        Assert.Equal(@"C:\temp\workdir", startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnWindows_WithCmdExtension_WrapsInOuterQuotes()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.cmd",
            ["view", "express", "version"],
            @"C:\temp", new TestEnvironment());

        // cmd.exe /c requires outer quotes wrapping the entire command:
        // /c ""C:\Program Files\nodejs\npm.cmd" "view" "express" "version""
        var args = startInfo.Arguments;
        Assert.StartsWith(@"/c """, args);
        Assert.EndsWith(@"""", args);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnWindows_WithExeExtension_DoesNotUseCmdExe()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.exe",
            ["view", "express", "version"],
            @"C:\temp", new TestEnvironment());

        Assert.Equal(@"C:\Program Files\nodejs\npm.exe", startInfo.FileName);
        Assert.Equal(["view", "express", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnNonWindows_UsesDirectInvocation()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            "/usr/local/bin/npm",
            ["view", "@playwright/cli@0.1.1", "version"],
            "/tmp/workdir", new TestEnvironment());

        Assert.Equal("/usr/local/bin/npm", startInfo.FileName);
        Assert.Equal(["view", "@playwright/cli@0.1.1", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnNonWindows_CmdExtensionIsIgnored()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        // On non-Windows, even a .cmd path is invoked directly (not via cmd.exe).
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            "/usr/local/bin/npm.cmd",
            ["view", "express", "version"],
            "/tmp", new TestEnvironment());

        Assert.Equal("/usr/local/bin/npm.cmd", startInfo.FileName);
        Assert.Equal(["view", "express", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_WithEmptyArgs_OnNonWindows_ProducesValidStartInfo()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo("/usr/bin/npm", [], "/tmp", new TestEnvironment());

        Assert.Equal("/usr/bin/npm", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_WithEmptyArgs_OnWindows_ProducesValidStartInfo()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        var startInfo = NpmRunner.CreateNpmProcessStartInfo(@"C:\Program Files\nodejs\npm.cmd", [], @"C:\temp", new TestEnvironment());

        Assert.Equal("cmd.exe", startInfo.FileName);
        Assert.Contains("npm.cmd", startInfo.Arguments);
        Assert.Equal(@"C:\temp", startInfo.WorkingDirectory);
    }

    [Fact]
    public async Task InstallGlobalAsync_UsesPublicRegistryForDependencies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-npm-runner-test-");

        try
        {
            WriteFakeNpm(tempDirectory);
            var argumentsPath = Path.Combine(tempDirectory.FullName, "arguments.txt");
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            using var pathOverride = new EnvVarOverride("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{existingPath}");
            using var pathExtensionsOverride = OperatingSystem.IsWindows() ? new EnvVarOverride("PATHEXT", ".CMD") : null;
            using var argumentsPathOverride = new EnvVarOverride("NPM_ARGS_FILE", argumentsPath);
            using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
            var runner = new NpmRunner(new TestEnvironment(), NullLogger<NpmRunner>.Instance, profilingTelemetry);
            var tarballPath = Path.Combine(tempDirectory.FullName, "playwright-cli.tgz");

            var result = await runner.InstallGlobalAsync(tarballPath, TestContext.Current.CancellationToken);

            Assert.True(result);
            Assert.Equal(
                [
                    "install",
                    "-g",
                    tarballPath,
                    "--ignore-scripts",
                    "--registry",
                    "https://registry.npmjs.org/"
                ],
                await File.ReadAllLinesAsync(argumentsPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetLatestVersionAsync_UsesNpmGlobalModeFromNeutralDirectory()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-npm-runner-test-");

        try
        {
            WriteFakeNpm(tempDirectory);
            var argumentsPath = Path.Combine(tempDirectory.FullName, "arguments.txt");
            var workingDirectoryPath = Path.Combine(tempDirectory.FullName, "working-directory.txt");
            var existingPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            using var pathOverride = new EnvVarOverride("PATH", $"{tempDirectory.FullName}{Path.PathSeparator}{existingPath}");
            using var pathExtensionsOverride = OperatingSystem.IsWindows() ? new EnvVarOverride("PATHEXT", ".CMD") : null;
            using var argumentsPathOverride = new EnvVarOverride("NPM_ARGS_FILE", argumentsPath);
            using var workingDirectoryPathOverride = new EnvVarOverride("NPM_CWD_FILE", workingDirectoryPath);
            using var outputOverride = new EnvVarOverride("NPM_OUTPUT", "13.4.6");
            using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
            var runner = new NpmRunner(new TestEnvironment(), NullLogger<NpmRunner>.Instance, profilingTelemetry);

            var result = await runner.GetLatestVersionAsync(
                "@microsoft/aspire-cli",
                TestContext.Current.CancellationToken);

            Assert.Equal("13.4.6", result.ToString());
            Assert.Equal(
                [
                    "view",
                    "@microsoft/aspire-cli@latest",
                    "version",
                    "--global",
                    "--json=false",
                    "--color=false",
                    "--loglevel=error"
                ],
                await File.ReadAllLinesAsync(argumentsPath, TestContext.Current.CancellationToken));

            var workingDirectory = (await File.ReadAllTextAsync(
                workingDirectoryPath,
                TestContext.Current.CancellationToken)).Trim();
            Assert.StartsWith("aspire-npm-", new DirectoryInfo(workingDirectory).Name);
        }
        finally
        {
            tempDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetLatestVersionAsync_CallerCancellationAfterProcessExitWhileOutputDrains_ThrowsOperationCanceledException()
    {
        using var fakeNpm = FakeNpmScript.BuildExitThenHoldOutputOpen(outputHelper, "13.4.6");
        using var environmentScope = fakeNpm.UseEnvironment();
        using var profilingTelemetry = new ProfilingTelemetry(new ConfigurationBuilder().Build());
        var runner = new NpmRunner(new TestEnvironment(), NullLogger<NpmRunner>.Instance, profilingTelemetry);
        using var cancellation = new CancellationTokenSource();

        var getVersionTask = runner.GetLatestVersionAsync("@microsoft/aspire-cli", cancellation.Token);

        await fakeNpm.WaitForParentExitAsync().DefaultTimeout();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => getVersionTask).DefaultTimeout();
    }

    [Fact]
    public void ResolveLatestVersionLookupResult_TimedOutResultBeatsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = Assert.Throws<TimeoutException>(() => NpmRunner.ResolveLatestVersionLookupResult(
            "@microsoft/aspire-cli@latest",
            new ProcessCaptureResult<string>(
                ExitCode: -1,
                Capture: string.Empty,
                FailureKind: ProcessCaptureFailureKind.TimedOut,
                FailureMessage: "Process timed out after 0.1s.",
                Cancelled: false),
            cancellation.Token));

        Assert.Contains("Timed out after", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractLastVersion_SingleVersion_ReturnsTrimmedVersion()
    {
        var result = NpmRunner.TryExtractLastVersion("0.1.1\n", out var version);
        Assert.True(result);
        Assert.Equal("0.1.1", version);
    }

    [Fact]
    public void TryExtractLastVersion_MultipleVersions_ReturnsLastVersion()
    {
        var output = "@playwright/cli@0.1.1 '0.1.1'\n@playwright/cli@0.1.2 '0.1.2'\n@playwright/cli@0.1.3 '0.1.3'\n";
        var result = NpmRunner.TryExtractLastVersion(output, out var version);
        Assert.True(result);
        Assert.Equal("0.1.3", version);
    }

    [Fact]
    public void TryExtractLastVersion_MultipleVersions_WindowsLineEndings_ReturnsLastVersion()
    {
        var output = "@playwright/cli@0.1.1 '0.1.1'\r\n@playwright/cli@0.1.2 '0.1.2'\r\n@playwright/cli@0.1.3 '0.1.3'\r\n";
        var result = NpmRunner.TryExtractLastVersion(output, out var version);
        Assert.True(result);
        Assert.Equal("0.1.3", version);
    }

    [Fact]
    public void TryExtractLastVersion_EmptyString_ReturnsFalse()
    {
        var result = NpmRunner.TryExtractLastVersion("", out var version);
        Assert.False(result);
        Assert.Null(version);
    }

    [Fact]
    public void TryExtractLastVersion_WhitespaceOnly_ReturnsFalse()
    {
        var result = NpmRunner.TryExtractLastVersion("  \n  \n  ", out var version);
        Assert.False(result);
        Assert.Null(version);
    }

    [Fact]
    public void TryExtractLastVersion_SingleVersionNoNewline_ReturnsTrimmedVersion()
    {
        var result = NpmRunner.TryExtractLastVersion("1.2.3", out var version);
        Assert.True(result);
        Assert.Equal("1.2.3", version);
    }

    [Fact]
    public void TryExtractLastVersion_MultipleVersionsWithPrerelease_ReturnsLastVersion()
    {
        var output = "@scope/pkg@1.0.0-alpha '1.0.0-alpha'\n@scope/pkg@1.0.0 '1.0.0'\n";
        var result = NpmRunner.TryExtractLastVersion(output, out var version);
        Assert.True(result);
        Assert.Equal("1.0.0", version);
    }

    private static void WriteFakeNpm(DirectoryInfo directory)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(
                Path.Combine(directory.FullName, "npm.cmd"),
                """
                @echo off
                type nul > "%NPM_ARGS_FILE%"
                :loop
                if "%~1"=="" goto done
                >> "%NPM_ARGS_FILE%" echo %~1
                shift
                goto loop
                :done
                if defined NPM_CWD_FILE cd > "%NPM_CWD_FILE%"
                if defined NPM_OUTPUT echo %NPM_OUTPUT%
                """);
            return;
        }

        var npmPath = Path.Combine(directory.FullName, "npm");
        File.WriteAllText(
            npmPath,
            """
            #!/bin/sh
            printf '%s\n' "$@" > "$NPM_ARGS_FILE"
            if [ -n "$NPM_CWD_FILE" ]; then
              pwd > "$NPM_CWD_FILE"
            fi
            if [ -n "$NPM_OUTPUT" ]; then
              printf '%s\n' "$NPM_OUTPUT"
            fi
            """);
        File.SetUnixFileMode(
            npmPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
