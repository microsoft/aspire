// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Tests for the new --install-mode tool / -InstallMode Tool flag on get-aspire-cli-pr.{sh,ps1}.
/// These tests validate parameter handling, validation rules, and dry-run/WhatIf behavior
/// without invoking real 'dotnet tool install'.
/// </summary>
public class PRScriptToolModeTests(ITestOutputHelper testOutput)
{
    private readonly ITestOutputHelper _testOutput = testOutput;

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
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_PrDryRun_SkipsCliNativeArchivesDownload()
    {
        using var env = new TestEnvironment();
        using var cmd = await CreateBashCommandWithMockGhAsync(env);

        var result = await cmd.ExecuteAsync(
            "12345", "--install-mode", "tool",
            "--dry-run", "--skip-path", "--verbose",
            "--os", "linux", "--arch", "x64");

        result.EnsureSuccessful();
        // Tool mode must NOT download the cli-native-archives artifact.
        Assert.DoesNotContain("cli-native-archives-linux-x64", result.Output);
        // But it should still download built-nugets and built-nugets-for-<rid>.
        Assert.Contains("built-nugets", result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_RejectsExplicitInstallPath()
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

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("--install-path is not supported", result.Output);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
    public async Task Bash_ToolMode_HiveOnlyBypassesValidation()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);
        var customPath = Path.Combine(env.TempDirectory, "custom");

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        // --hive-only should bypass tool-mode-only checks: no rejection of custom install path,
        // no requirement for dotnet, no global-tool conflict check.
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--install-path", customPath,
            "--hive-only",
            "--dry-run", "--skip-path");

        result.EnsureSuccessful();
        Assert.Contains("Skipping CLI installation", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet tool install", result.Output);
        Assert.DoesNotContain("dotnet tool update", result.Output);
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
    public async Task Bash_ToolMode_PathPointsToDotnetToolsDir()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRShell, env, _testOutput);
        // Without --skip-path, tool mode should add the dotnet global tools dir to PATH (not <install-path>/bin).
        var result = await cmd.ExecuteAsync(
            "--local-dir", localDir,
            "--install-mode", "tool",
            "--dry-run");

        result.EnsureSuccessful();
        Assert.Contains(".dotnet/tools", result.Output);
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
    public async Task Bash_FindAspireCliPackageVersion_NoMatchFails()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        Directory.CreateDirectory(pkgDir);
        // Only a non-Aspire.Cli package — should fail.
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Hosting", "1.0.0");
        // And a similarly-named-but-not-version package — should be excluded.
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Cli.PackageManager", "1.0.0");

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
        Assert.DoesNotContain("aspire-cli-linux-", result.Output);
        Assert.DoesNotContain(".tar.gz", result.Output);
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
            "-OS", "linux", "-Architecture", "x64",
            "-Verbose");

        result.EnsureSuccessful();
        Assert.DoesNotContain("cli-native-archives-linux-x64", result.Output);
        Assert.Contains("built-nugets", result.Output);
        Assert.Contains("dotnet tool install --global Aspire.Cli", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_RejectsExplicitInstallPath()
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

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("-InstallPath is not supported", result.Output);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Ps_ToolMode_HiveOnlyBypassesValidation()
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
            "-HiveOnly",
            "-SkipPath", "-WhatIf");

        result.EnsureSuccessful();
        Assert.Contains("Skipping CLI installation", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet tool install", result.Output);
        Assert.DoesNotContain("dotnet tool update", result.Output);
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
    public async Task Ps_ToolMode_PathPointsToDotnetToolsDir()
    {
        using var env = new TestEnvironment();
        var localDir = Path.Combine(env.TempDirectory, "artifacts");
        await CreateLocalDirWithAspireCliPackageAsync(localDir);

        using var cmd = new ScriptToolCommand(ScriptPaths.PRPowerShell, env, _testOutput);
        var result = await cmd.ExecuteAsync(
            "-LocalDir", localDir,
            "-InstallMode", "Tool",
            "-WhatIf");

        result.EnsureSuccessful();
        // The dotnet tools dir uses '.dotnet/tools' on Unix and '.dotnet\tools' on Windows.
        Assert.Matches(@"\.dotnet[\\/]tools", result.Output);
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
    public async Task Ps_FindAspireCliPackageVersion_NoMatchFails()
    {
        using var env = new TestEnvironment();
        var pkgDir = Path.Combine(env.TempDirectory, "pkgs");
        Directory.CreateDirectory(pkgDir);
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Hosting", "1.0.0");
        await FakeArchiveHelper.CreateFakeNupkgAsync(pkgDir, "Aspire.Cli.PackageManager", "1.0.0");

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
}
