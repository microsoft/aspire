// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.TestUtilities;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

[RequiresTools(["pwsh"])]
public class CompletionPowerShellTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task InstallCompletions_PreservesProfileAndSafelyActivates(bool dogfood, bool unicodeProfile)
    {
        using var env = new TestEnvironment();
        var home = Path.Combine(env.MockHome, "it's $home ` [test]");
        var cli = CreateFakeCli(home);
        var profile = Path.Combine(home, "edition profile", "profile.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        const string original = "# My existing profile without a trailing newline";
        var encoding = unicodeProfile ? Encoding.Unicode : new UTF8Encoding(false);
        File.WriteAllText(profile, original, encoding);
        var originalBytes = File.ReadAllBytes(profile);
        var completion = CompletionPath(home, cli, dogfood);
        var invocation = $"Install-AspireCliCompletions -CliPath '{Quote(cli)}'" + (dogfood ? "" : " -Persist $true");
        using var cmd = new ScriptFunctionCommand(
            dogfood ? ScriptPaths.PRPowerShell : ScriptPaths.ReleasePowerShell,
            $$"""
            Set-Variable HOME '{{Quote(home)}}' -Force
            $PROFILE = @{ CurrentUserAllHosts = '{{Quote(profile)}}' }
            function Test-ElevatedCompletionSession { $false }
            {{invocation}}
            $env:FAKE_COMPLETION_GENERATION = 'upgraded'
            {{invocation}}
            if (-not (Test-Path -LiteralPath '{{Quote(completion)}}')) { throw 'Missing completion file' }
            {{(dogfood ? $". '{Quote(completion)}'" : $". '{Quote(profile)}'")}}
            if ($global:AspireCompletionLoaded -ne 'upgraded') { throw 'Updated completion not sourced' }
            Remove-Item -LiteralPath '{{Quote(completion)}}'
            {{(dogfood ? "" : $". '{Quote(profile)}'")}}
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var contents = File.ReadAllText(profile);
        Assert.Equal(originalBytes, File.ReadAllBytes(profile)[..originalBytes.Length]);
        Assert.Equal(dogfood ? 0 : 1, contents.Split("# Aspire CLI completions").Length - 1);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(completion)!, ".aspire-completions-*"));
    }

    [Theory]
    [InlineData(false, "fail")]
    [InlineData(false, "empty")]
    [InlineData(false, "skip")]
    [InlineData(false, "whatif")]
    [InlineData(true, "fail")]
    [InlineData(true, "empty")]
    [InlineData(true, "skip")]
    [InlineData(true, "whatif")]
    public async Task InstallCompletions_UnsuccessfulOrOptedOut_PreservesExistingFiles(bool dogfood, string mode)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.MockHome, "profile.ps1");
        var completion = CompletionPath(env.MockHome, cli, dogfood);
        Directory.CreateDirectory(Path.GetDirectoryName(completion)!);
        File.WriteAllText(completion, "# working completions");
        File.WriteAllText(profile, "# existing profile");
        using var cmd = new ScriptFunctionCommand(
            dogfood ? ScriptPaths.PRPowerShell : ScriptPaths.ReleasePowerShell,
            $$"""
            Set-Variable HOME '{{Quote(env.MockHome)}}' -Force
            $PROFILE = @{ CurrentUserAllHosts = '{{Quote(profile)}}' }
            $SkipCompletions = ${{(mode == "skip" ? "true" : "false")}}
            $env:FAKE_COMPLETION_MODE = '{{mode}}'
            Install-AspireCliCompletions -CliPath '{{Quote(cli)}}' {{(dogfood ? "" : "-Persist $true")}} {{(mode == "whatif" ? "-WhatIf" : "")}}
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# working completions", File.ReadAllText(completion));
        Assert.Equal("# existing profile", File.ReadAllText(profile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(completion)!, ".aspire-completions-*"));
        if (mode is "skip" or "whatif")
        {
            Assert.False(File.Exists(Path.Combine(env.MockHome, "called")));
        }
        else
        {
            Assert.Contains("CLI installation is unaffected", result.Output);
        }
    }

    [Fact]
    public async Task InstallCompletions_ProfileOutsideHome_IsNotModified()
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.TempDirectory, "outside-profile.ps1");
        File.WriteAllText(profile, "# outside home");
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.ReleasePowerShell,
            $$"""
            Set-Variable HOME '{{Quote(env.MockHome)}}' -Force
            $PROFILE = @{ CurrentUserAllHosts = '{{Quote(profile)}}' }
            function Test-ElevatedCompletionSession { $false }
            Install-AspireCliCompletions -CliPath '{{Quote(cli)}}' -Persist $true
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# outside home", File.ReadAllText(profile));
        Assert.Contains("CurrentUserAllHosts", result.Output);
    }

    [Theory]
    [InlineData(false, 4)]
    [InlineData(false, 5)]
    [InlineData(false, 6)]
    [InlineData(true, 4)]
    [InlineData(true, 5)]
    [InlineData(true, 6)]
    public async Task InstallCompletions_PrePowerShell7_LeavesFilesUntouchedAndExplainsPwsh(bool dogfood, int majorVersion)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.MockHome, "profile.ps1");
        var completion = CompletionPath(env.MockHome, cli, dogfood);
        Directory.CreateDirectory(Path.GetDirectoryName(completion)!);
        File.WriteAllText(completion, "# working completions");
        File.WriteAllText(profile, "# existing profile");
        using var cmd = new ScriptFunctionCommand(
            dogfood ? ScriptPaths.PRPowerShell : ScriptPaths.ReleasePowerShell,
            $$"""
            Set-Variable HOME '{{Quote(env.MockHome)}}' -Force
            $PROFILE = @{ CurrentUserAllHosts = '{{Quote(profile)}}' }
            # Simulate the engine version in this isolated test process only.
            $PSVersionTable.PSVersion = [Version]'{{majorVersion}}.0'
            Install-AspireCliCompletions -CliPath '{{Quote(cli)}}' {{(dogfood ? "" : "-Persist $true")}}
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# working completions", File.ReadAllText(completion));
        Assert.Equal("# existing profile", File.ReadAllText(profile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(completion)!, ".aspire-completions-*"));
        Assert.False(File.Exists(Path.Combine(env.MockHome, "called")));
        Assert.Contains("require PowerShell 7 or later", result.Output);
        Assert.Contains("Open pwsh, put aspire on PATH", result.Output);
        Assert.Contains("aspire completions script pwsh", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallCompletions_SkipPath_GeneratesArtifactWithoutProfileMutation(bool dogfood)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.MockHome, "profile.ps1");
        File.WriteAllText(profile, "# existing profile");
        using var cmd = new ScriptFunctionCommand(
            dogfood ? ScriptPaths.PRPowerShell : ScriptPaths.ReleasePowerShell,
            $$"""
            Set-Variable HOME '{{Quote(env.MockHome)}}' -Force
            $PROFILE = @{ CurrentUserAllHosts = '{{Quote(profile)}}' }
            $SkipPath = $true
            Install-AspireCliCompletions -CliPath '{{Quote(cli)}}' {{(dogfood ? "" : "-Persist $true")}}
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# existing profile", File.ReadAllText(profile));
        Assert.True(File.Exists(CompletionPath(env.MockHome, cli, dogfood: true)));
        Assert.Contains("activate completions manually", result.Output);
    }

    [Theory]
    [InlineData("signed")]
    [InlineData("elevated")]
    public async Task InstallCompletions_ProtectedProfile_IsNotModified(string protection)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.MockHome, "profile.ps1");
        var original = protection == "signed" ? "# profile\n# SIG # Begin signature block\n# signature\n# SIG # End signature block\n" : "# profile";
        File.WriteAllText(profile, original);
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.ReleasePowerShell,
            $$"""
            Set-Variable HOME '{{Quote(env.MockHome)}}' -Force
            $PROFILE = @{ CurrentUserAllHosts = '{{Quote(profile)}}' }
            function Test-ElevatedCompletionSession { ${{(protection == "elevated" ? "true" : "false")}} }
            Install-AspireCliCompletions -CliPath '{{Quote(cli)}}' -Persist $true
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal(original, File.ReadAllText(profile));
        Assert.True(File.Exists(CompletionPath(env.MockHome, cli, dogfood: false)));
        Assert.Contains(protection == "signed" ? "Signed PowerShell profile left untouched" : "Elevated install", result.Output);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallCompletions_AllSigned_PreservesFilesAndRequiresManualSigning(bool dogfood)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.MockHome, "profile.ps1");
        var completion = CompletionPath(env.MockHome, cli, dogfood);
        Directory.CreateDirectory(Path.GetDirectoryName(completion)!);
        File.WriteAllText(completion, "# existing signed completions");
        File.WriteAllText(profile, "# existing profile");
        using var cmd = new ScriptFunctionCommand(
            dogfood ? ScriptPaths.PRPowerShell : ScriptPaths.ReleasePowerShell,
            $$"""
            Set-Variable HOME '{{Quote(env.MockHome)}}' -Force
            $PROFILE = @{ CurrentUserAllHosts = '{{Quote(profile)}}' }
            function Get-ExecutionPolicy { 'AllSigned' }
            Install-AspireCliCompletions -CliPath '{{Quote(cli)}}' {{(dogfood ? "" : "-Persist $true")}}
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# existing signed completions", File.ReadAllText(completion));
        Assert.Equal("# existing profile", File.ReadAllText(profile));
        Assert.False(File.Exists(Path.Combine(env.MockHome, "called")));
        Assert.Contains("AllSigned execution policy", result.Output);
        Assert.Contains("Generate and sign", result.Output);
    }

    private static string CreateFakeCli(string home)
    {
        Directory.CreateDirectory(home);
        var cli = Path.Combine(home, "fake aspire.ps1");
        File.WriteAllText(cli, """
            if (($args -join ' ') -ne 'completions script pwsh') { exit 2 }
            [IO.File]::WriteAllText((Join-Path $HOME 'called'), 'called')
            if ($env:FAKE_COMPLETION_MODE -eq 'fail') { 'error output is not a script'; exit 1 }
            if ($env:FAKE_COMPLETION_MODE -eq 'empty') { exit 0 }
            $value = if ($env:FAKE_COMPLETION_GENERATION) { $env:FAKE_COMPLETION_GENERATION } else { 'loaded' }
            '$global:AspireCompletionLoaded = ''' + $value + ''''
            exit 0
            """);
        return cli;
    }

    private static string CompletionPath(string home, string cli, bool dogfood) =>
        Path.Combine(dogfood ? Path.GetDirectoryName(cli)! : Path.Combine(home, ".aspire"), "completions", "aspire.ps1");

    private static string Quote(string value) => value.Replace("'", "''");
}

[SkipOnPlatform(TestPlatforms.Windows, "Bash script tests require bash shell")]
public class CompletionShellTests(ITestOutputHelper testOutput)
{
    [Theory]
    [InlineData(false, "bash")]
    [InlineData(false, "zsh")]
    [InlineData(false, "fish")]
    [InlineData(true, "bash")]
    [InlineData(true, "zsh")]
    [InlineData(true, "fish")]
    public async Task InstallCompletions_UsesUserShellPathsWithoutOverwritingProfile(bool dogfood, string shell)
    {
        using var env = new TestEnvironment();
        var home = Path.Combine(env.MockHome, "it's $home ` [test]");
        Directory.CreateDirectory(home);
        var cli = CreateFakeCli(home);
        var profile = shell switch
        {
            "zsh" => Path.Combine(home, "z dot", ".zshrc"),
            "fish" => Path.Combine(home, "xdg config", "fish", "conf.d", "aspire-completions.fish"),
            _ => Path.Combine(home, ".bashrc")
        };
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        File.WriteAllText(profile, "# existing profile without trailing newline");
        var completion = Path.Combine(dogfood ? home : Path.Combine(home, ".aspire"), "completions", $"aspire.{shell}");
        var invocation = $"install_completions '{Quote(cli)}'" + (dogfood ? "" : " true");
        using var cmd = new ScriptFunctionCommand(
            dogfood ? ScriptPaths.PRShell : ScriptPaths.ReleaseShell,
            $$"""
            set -euo pipefail
            HOME='{{Quote(home)}}'
            SHELL='/bin/{{shell}}'
            ZDOTDIR="$HOME/z dot"
            XDG_CONFIG_HOME="$HOME/xdg config"
            {{invocation}}
            export FAKE_COMPLETION_GENERATION=upgraded
            {{invocation}}
            test -s '{{Quote(completion)}}'
            {{(dogfood || shell == "fish" ? "" : $"source '{Quote(profile)}'; test \"$ASPIRE_COMPLETION_LOADED\" = upgraded")}}
            rm '{{Quote(completion)}}'
            {{(dogfood || shell == "fish" ? "" : $"source '{Quote(profile)}'")}}
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var contents = File.ReadAllText(profile);
        Assert.StartsWith("# existing profile without trailing newline", contents);
        Assert.Equal(dogfood ? 0 : 1, contents.Split("# Aspire CLI completions").Length - 1);
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(completion)!, ".aspire-completions-*"));
    }

    [Theory]
    [InlineData(false, "fail")]
    [InlineData(false, "empty")]
    [InlineData(false, "skip")]
    [InlineData(false, "dryrun")]
    [InlineData(false, "unsupported")]
    [InlineData(false, "unset")]
    [InlineData(true, "fail")]
    [InlineData(true, "empty")]
    [InlineData(true, "skip")]
    [InlineData(true, "dryrun")]
    [InlineData(true, "unsupported")]
    [InlineData(true, "unset")]
    public async Task InstallCompletions_UnsuccessfulOrOptedOut_PreservesExistingFiles(bool dogfood, string mode)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var completion = Path.Combine(dogfood ? env.MockHome : Path.Combine(env.MockHome, ".aspire"), "completions", "aspire.bash");
        Directory.CreateDirectory(Path.GetDirectoryName(completion)!);
        File.WriteAllText(completion, "# working completions");
        var profile = Path.Combine(env.MockHome, ".bashrc");
        File.WriteAllText(profile, "# existing profile");
        using var cmd = new ScriptFunctionCommand(
            dogfood ? ScriptPaths.PRShell : ScriptPaths.ReleaseShell,
            $$"""
            set -euo pipefail
            SHELL='{{(mode == "unsupported" ? "/bin/tcsh" : mode == "unset" ? "" : "/bin/bash")}}'
            SKIP_COMPLETIONS={{(mode == "skip" ? "true" : "false")}}
            DRY_RUN={{(mode == "dryrun" ? "true" : "false")}}
            export FAKE_COMPLETION_MODE='{{mode}}'
            install_completions '{{Quote(cli)}}' {{(dogfood ? "" : "true")}}
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# working completions", File.ReadAllText(completion));
        Assert.Equal("# existing profile", File.ReadAllText(profile));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(completion)!, ".aspire-completions-*"));
        if (mode is "skip" or "dryrun" or "unsupported" or "unset")
        {
            Assert.False(File.Exists(Path.Combine(env.MockHome, "called")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallCompletions_OutsideHomeProfileOrSymlink_IsNotModified(bool symlink)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var outside = Path.Combine(env.TempDirectory, ".zshrc");
        File.WriteAllText(outside, "# outside profile");
        if (symlink)
        {
            File.CreateSymbolicLink(Path.Combine(env.MockHome, ".zshrc"), outside);
        }
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.ReleaseShell,
            $$"""
            set -euo pipefail
            SHELL=/bin/zsh
            ZDOTDIR='{{Quote(symlink ? env.MockHome : env.TempDirectory)}}'
            install_completions '{{Quote(cli)}}' true
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# outside profile", File.ReadAllText(outside));
        Assert.Contains("CLI installation is unaffected", result.Output);
    }

    [Theory]
    [InlineData(".bash_profile")]
    [InlineData(".bash_login")]
    [InlineData(".profile")]
    public async Task InstallCompletions_Bash_RegistersExistingLoginProfile(string loginProfile)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.MockHome, loginProfile);
        File.WriteAllText(profile, "# login profile");
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.ReleaseShell,
            $$"""
            set -euo pipefail
            SHELL=/bin/bash
            install_completions '{{Quote(cli)}}' true
            install_completions '{{Quote(cli)}}' true
            source '{{Quote(profile)}}'
            test "$ASPIRE_COMPLETION_LOADED" = loaded
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        var content = File.ReadAllText(profile);
        Assert.StartsWith("# login profile", content);
        Assert.Equal(1, content.Split("# Aspire CLI completions").Length - 1);
        Assert.True(File.Exists(Path.Combine(env.MockHome, ".bashrc")));
    }

    [Fact]
    public async Task InstallCompletions_NewBashLoginProfile_PreservesPathSetup()
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        File.WriteAllText(Path.Combine(env.MockHome, ".bashrc"), "export ASPIRE_TEST_BASHRC_LOADED=true\n");
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.ReleaseShell,
            $$"""
            set -euo pipefail
            SHELL=/bin/bash
            install_completions '{{Quote(cli)}}' true
            source "$HOME/.bash_profile"
            test "$ASPIRE_TEST_BASHRC_LOADED" = true
            test "$ASPIRE_COMPLETION_LOADED" = loaded
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallCompletions_SkipPath_GeneratesArtifactWithoutProfileMutation(bool dogfood)
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.MockHome, ".bashrc");
        File.WriteAllText(profile, "# existing profile");
        using var cmd = new ScriptFunctionCommand(
            dogfood ? ScriptPaths.PRShell : ScriptPaths.ReleaseShell,
            $$"""
            set -euo pipefail
            SHELL=/bin/bash
            SKIP_PATH=true
            install_completions '{{Quote(cli)}}' {{(dogfood ? "" : "true")}}
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# existing profile", File.ReadAllText(profile));
        Assert.True(File.Exists(Path.Combine(env.MockHome, "completions", "aspire.bash")));
        Assert.False(File.Exists(Path.Combine(env.MockHome, ".bash_profile")));
        Assert.Contains("activate completions manually", result.Output);
    }

    [Fact]
    public async Task InstallCompletions_Elevated_DoesNotRegisterPathOrCompletions()
    {
        using var env = new TestEnvironment();
        var cli = CreateFakeCli(env.MockHome);
        var profile = Path.Combine(env.MockHome, ".bashrc");
        File.WriteAllText(profile, "# existing profile");
        using var cmd = new ScriptFunctionCommand(
            ScriptPaths.ReleaseShell,
            $$"""
            set -euo pipefail
            SHELL=/bin/bash
            is_elevated_install() { return 0; }
            add_to_shell_profile "$HOME/bin" '$HOME/bin'
            install_completions '{{Quote(cli)}}' true
            """,
            env, testOutput);

        var result = await cmd.ExecuteAsync();

        result.EnsureSuccessful();
        Assert.Equal("# existing profile", File.ReadAllText(profile));
        Assert.False(File.Exists(Path.Combine(env.MockHome, ".bash_profile")));
        Assert.True(File.Exists(Path.Combine(env.MockHome, ".aspire", "completions", "aspire.bash")));
        Assert.Contains("Elevated install", result.Output);
    }

    private static string CreateFakeCli(string home)
    {
        var cli = Path.Combine(home, "fake aspire");
        File.WriteAllText(cli, """
            #!/usr/bin/env bash
            [[ "$1 $2" == "completions script" ]] || exit 2
            printf called > "$HOME/called"
            if [[ "${FAKE_COMPLETION_MODE:-}" == fail ]]; then echo 'error output is not a script'; exit 1; fi
            if [[ "${FAKE_COMPLETION_MODE:-}" == empty ]]; then exit 0; fi
            echo "ASPIRE_COMPLETION_LOADED=${FAKE_COMPLETION_GENERATION:-loaded}"
            """);
        FileHelper.MakeExecutable(cli);
        return cli;
    }

    private static string Quote(string value) => value.Replace("'", "'\\''");
}
