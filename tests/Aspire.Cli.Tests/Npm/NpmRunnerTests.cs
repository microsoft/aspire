// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Cli.Npm;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.Acquisition;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Npm;

[Collection(EnvVarMutatingTestCollection.Name)]
public class NpmRunnerTests
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
        using var stdin = File.OpenNullHandle();
        var startInfo = NpmRunner.CreateNpmProcessStartInfo("/usr/bin/npm", ["view", "express", "version"], "/tmp/workdir", new TestEnvironment(), stdin);

        // npm must see EOF on stdin rather than the CLI's terminal (#16791).
        Assert.Same(stdin, startInfo.StandardInputHandle);
        Assert.False(startInfo.RedirectStandardInput);
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

        using var stdin = File.OpenNullHandle();
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.cmd",
            ["view", "@playwright/cli@0.1.1", "version", "--registry", "https://registry.npmjs.org/"],
            @"C:\temp\workdir", new TestEnvironment(), stdin);

        Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
        Assert.Equal(@"C:\Program Files\nodejs\npm.cmd", startInfo.Environment["ASPIRE_COMMAND_SHIM_PATH"]);
        Assert.Equal(
            ["view", "@playwright/cli@0.1.1", "version", "--registry", "https://registry.npmjs.org/"],
            Enumerable.Range(0, 5).Select(index => startInfo.Environment[$"ASPIRE_COMMAND_SHIM_ARGUMENT_{index}"]));
        Assert.StartsWith("/D /V:OFF /S /C ", startInfo.Arguments);
        Assert.Equal(@"C:\temp\workdir", startInfo.WorkingDirectory);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnWindows_WithCmdExtension_WrapsInOuterQuotes()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        using var stdin = File.OpenNullHandle();
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.cmd",
            ["view", "express", "version"],
            @"C:\temp", new TestEnvironment(), stdin);

        var args = startInfo.Arguments;
        Assert.Equal(
            "/D /V:OFF /S /C \"\"%ASPIRE_COMMAND_SHIM_PATH%\" \"%ASPIRE_COMMAND_SHIM_ARGUMENT_0%\" \"%ASPIRE_COMMAND_SHIM_ARGUMENT_1%\" \"%ASPIRE_COMMAND_SHIM_ARGUMENT_2%\"\"",
            args);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnWindows_WithExeExtension_DoesNotUseCmdExe()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        using var stdin = File.OpenNullHandle();
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            @"C:\Program Files\nodejs\npm.exe",
            ["view", "express", "version"],
            @"C:\temp", new TestEnvironment(), stdin);

        Assert.Equal(@"C:\Program Files\nodejs\npm.exe", startInfo.FileName);
        Assert.Equal(["view", "express", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnNonWindows_UsesDirectInvocation()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        using var stdin = File.OpenNullHandle();
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            "/usr/local/bin/npm",
            ["view", "@playwright/cli@0.1.1", "version"],
            "/tmp/workdir", new TestEnvironment(), stdin);

        Assert.Equal("/usr/local/bin/npm", startInfo.FileName);
        Assert.Equal(["view", "@playwright/cli@0.1.1", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_OnNonWindows_CmdExtensionIsIgnored()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        // On non-Windows, even a .cmd path is invoked directly (not via cmd.exe).
        using var stdin = File.OpenNullHandle();
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(
            "/usr/local/bin/npm.cmd",
            ["view", "express", "version"],
            "/tmp", new TestEnvironment(), stdin);

        Assert.Equal("/usr/local/bin/npm.cmd", startInfo.FileName);
        Assert.Equal(["view", "express", "version"], startInfo.ArgumentList);
        Assert.Empty(startInfo.Arguments);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_WithEmptyArgs_OnNonWindows_ProducesValidStartInfo()
    {
        Assert.SkipUnless(!RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Non-Windows-only test.");

        using var stdin = File.OpenNullHandle();
        var startInfo = NpmRunner.CreateNpmProcessStartInfo("/usr/bin/npm", [], "/tmp", new TestEnvironment(), stdin);

        Assert.Equal("/usr/bin/npm", startInfo.FileName);
        Assert.Empty(startInfo.ArgumentList);
    }

    [Fact]
    public void CreateNpmProcessStartInfo_WithEmptyArgs_OnWindows_ProducesValidStartInfo()
    {
        Assert.SkipUnless(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), "Windows-only test.");

        using var stdin = File.OpenNullHandle();
        var startInfo = NpmRunner.CreateNpmProcessStartInfo(@"C:\Program Files\nodejs\npm.cmd", [], @"C:\temp", new TestEnvironment(), stdin);

        Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", startInfo.FileName);
        Assert.Equal(@"C:\Program Files\nodejs\npm.cmd", startInfo.Environment["ASPIRE_COMMAND_SHIM_PATH"]);
        Assert.Equal("/D /V:OFF /S /C \"\"%ASPIRE_COMMAND_SHIM_PATH%\"\"", startInfo.Arguments);
        Assert.Equal(@"C:\temp", startInfo.WorkingDirectory);
    }

    [Fact]
    public async Task InstallGlobalAsync_UsesPublicRegistryForDependencies()
    {
        var tempDirectory = Directory.CreateTempSubdirectory("aspire-npm-%TEMP%! test-");

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
            var tarballPath = Path.Combine(tempDirectory.FullName, "playwright-%PATH%! cli.tgz");

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
                if "%~1"=="" exit /b 0
                >> "%NPM_ARGS_FILE%" echo %~1
                shift
                goto loop
                """);
            return;
        }

        var npmPath = Path.Combine(directory.FullName, "npm");
        File.WriteAllText(
            npmPath,
            """
            #!/bin/sh
            printf '%s\n' "$@" > "$NPM_ARGS_FILE"
            """);
        File.SetUnixFileMode(
            npmPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }
}
