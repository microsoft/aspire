// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Tests.Git;

public partial class GitCommandRunnerTests
{
    [Theory]
    [InlineData(nameof(GitCommandMode.Unattended), true, true)]
    [InlineData(nameof(GitCommandMode.Interactive), false, true)]
    [InlineData(nameof(GitCommandMode.Interactive), true, false)]
    [InlineData(nameof(GitCommandMode.Captured), false, false)]
    public void CreateStartInfo_AskPassPolicyFollowsEffectiveInteractivity(string mode, bool interactiveHost, bool disabled)
    {
        var runner = new GitCommandRunner(interactiveHost
            ? TestHelpers.CreateInteractiveHostEnvironment()
            : TestHelpers.CreateNonInteractiveHostEnvironment(), _loggerFactory.CreateLogger<GitCommandRunner>());
        var info = runner.CreateStartInfo(Environment.CurrentDirectory, ["ls-remote", "--", "repository"], Enum.Parse<GitCommandMode>(mode));

        Assert.Equal(disabled
            ? ["--literal-pathspecs", "-c", "core.askPass=", "ls-remote", "--", "repository"]
            : ["--literal-pathspecs", "ls-remote", "--", "repository"], info.ArgumentList);
        info.Environment.TryGetValue("GIT_ASKPASS", out var gitAskPass);
        info.Environment.TryGetValue("SSH_ASKPASS", out var sshAskPass);
        info.Environment.TryGetValue("SSH_ASKPASS_REQUIRE", out var sshAskPassRequire);
        Assert.Equal(disabled ? "" : Environment.GetEnvironmentVariable("GIT_ASKPASS"), gitAskPass);
        Assert.Equal(disabled ? "" : Environment.GetEnvironmentVariable("SSH_ASKPASS"), sshAskPass);
        Assert.Equal(disabled ? "never" : Environment.GetEnvironmentVariable("SSH_ASKPASS_REQUIRE"), sshAskPassRequire);
        Assert.Equal(disabled || mode == nameof(GitCommandMode.Captured), info.RedirectStandardInput);
    }

    [Theory]
    [InlineData("git", true)]
    [InlineData("core", true)]
    [InlineData("ssh", true)]
    [InlineData("git", false)]
    [InlineData("core", false)]
    [InlineData("ssh", false)]
    [RequiresTools(["git"])]
    public async Task RunProcess_RealGit401DisablesAskPassFallbacksButCapturedModePreservesThem(string mechanism, bool unattended)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var server = new GitAuthenticationServer();
        var marker = Path.Combine(workspace.Path, "askpass-called");
        var helper = Path.Combine(workspace.Path, "askpass.sh");
        // Synthetic credentials exercise callbacks only; the server always rejects them.
        await File.WriteAllTextAsync(helper, $"#!/bin/sh\nprintf called >> '{ShellPath(marker)}'\nprintf probe-only\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var helperCommand = helper;
        var config = Path.Combine(workspace.Path, "gitconfig");
        await File.WriteAllTextAsync(config, "[credential]\nhelper =\n"
            + (mechanism == "core" ? $"[core]\naskPass = \"{helperCommand.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n" : ""));
        var mode = unattended ? GitCommandMode.Unattended : GitCommandMode.Captured;
        var runner = CreateRunner();
        var info = runner.CreateStartInfo(workspace.Path, ["ls-remote", "--", server.Repository], mode);
        IsolateGit(info, workspace.Path, config);
        if (mechanism == "git")
        {
            info.Environment["GIT_ASKPASS"] = helperCommand;
        }
        else if (mechanism == "ssh")
        {
            info.Environment["SSH_ASKPASS"] = helperCommand;
        }
        if (unattended)
        {
            GitCommandRunner.DisableAskPass(info.Environment);
        }
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(milliseconds: 15_000);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, timeout.Token);

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            runner.RunProcessAsync(info, captureCancellationOutput: false, mode, cancellation.Token));

        Assert.Equal(GitFailure.AuthenticationFailed, exception.Failure);
        Assert.Equal(!unattended, File.Exists(marker));
        Assert.Equal(unattended ? 1 : 2, server.Requests);
        Assert.Equal("[credential]\nhelper =\n"
            + (mechanism == "core" ? $"[core]\naskPass = \"{helperCommand.Replace("\\", "\\\\", StringComparison.Ordinal)}\"\n" : ""), await File.ReadAllTextAsync(config));
    }

    [Fact]
    [RequiresTools(["git"])]
    public async Task RunProcess_RealGit401PreservesNoninteractiveCredentialHelpers()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var server = new GitAuthenticationServer();
        var marker = Path.Combine(workspace.Path, "credential-operations");
        var helper = Path.Combine(workspace.Path, "credential.sh");
        await File.WriteAllTextAsync(helper,
            $"#!/bin/sh\ncat >/dev/null\nprintf '%s\\n' \"$1\" >> '{ShellPath(marker)}'\n"
            + "if [ \"$1\" = get ]; then printf 'username=probe\\npassword=probe-only\\n\\n'; fi\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var config = Path.Combine(workspace.Path, "gitconfig");
        var configuration = $"[credential]\nhelper =\nhelper = !'{ShellPath(helper)}'\n";
        await File.WriteAllTextAsync(config, configuration);
        var runner = CreateRunner();
        var info = runner.CreateStartInfo(workspace.Path, ["ls-remote", "--", server.Repository], GitCommandMode.Unattended);
        IsolateGit(info, workspace.Path, config);
        GitCommandRunner.DisableAskPass(info.Environment);
        using var timeout = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(milliseconds: 15_000);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, timeout.Token);

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            runner.RunProcessAsync(info, captureCancellationOutput: false, GitCommandMode.Unattended, cancellation.Token));

        Assert.Equal(GitFailure.AuthenticationFailed, exception.Failure);
        Assert.Equal(["get", "erase"], await File.ReadAllLinesAsync(marker));
        Assert.Equal(2, server.Requests);
        Assert.Equal("never", info.Environment["GCM_INTERACTIVE"]);
        Assert.Equal(configuration, await File.ReadAllTextAsync(config));
    }

    [Theory]
    [InlineData("ssh://git@example.test/repository", true)]
    [InlineData("git+ssh://example.test/repository", true)]
    [InlineData("ssh+git://example.test/repository", true)]
    [InlineData("git@example.test:repository", true)]
    [InlineData("example.test:repository", true)]
    [InlineData("[::1]:repository", true)]
    [InlineData("https://example.test/repository", false)]
    [InlineData("file:///repository", false)]
    [InlineData("C:\\repository", false)]
    [InlineData("/repository:local", false)]
    [InlineData("./repository:local", false)]
    public void UsesSsh_RecognizesTransportWithoutMistakingLocalPaths(string repository, bool ssh) =>
        Assert.Equal(ssh, GitCommandRunner.UsesSsh(repository));

    [Theory]
    [InlineData("ssh", "ssh")]
    [InlineData("\"/path with spaces/ssh\"", "/path with spaces/ssh")]
    [InlineData("'/path with spaces/ssh'", "/path with spaces/ssh")]
    public void GetSshExecutable_AcceptsBareExecutables(string command, string expected) =>
        Assert.Equal(expected, GitCommandRunner.GetSshExecutable(command));

    [Theory]
    [InlineData("")]
    [InlineData("ssh -i identity")]
    [InlineData("ssh -o BatchMode=no")]
    [InlineData("ssh; other-command")]
    [InlineData("$SSH_COMMAND")]
    [InlineData("ssh | other-command")]
    public void GetSshExecutable_RejectsCommandsWithoutInterpretingShellSyntax(string command)
    {
        var exception = Assert.Throws<GitCommandException>(() => GitCommandRunner.GetSshExecutable(command));
        Assert.Equal(GitFailure.UnattendedSshUnsupported, exception.Failure);
        Assert.Equal(ErrorStrings.GitUnattendedSshUnsupported, exception.Message);
    }

    [Theory]
    [InlineData("https://example.test/repository", "GIT_SSH_COMMAND", "ssh -i identity", false)]
    [InlineData("ssh://git@example.test/repository", "GIT_SSH_COMMAND", "ssh -i identity", true)]
    [InlineData("ssh://git@example.test/repository", "GIT_SSH", "custom-wrapper", true)]
    [InlineData("ssh://git@example.test/repository", "GIT_SSH", "", true)]
    [InlineData("ssh://git@example.test/repository", "core.sshCommand", "ssh -i identity", true)]
    [InlineData("ssh://git@example.test/repository", "core.sshCommand", "", true)]
    [InlineData("ssh://git@example.test/repository", "GIT_SSH_COMMAND", "plink", true)]
    [RequiresTools(["git"])]
    public async Task ConfigureUnattendedSsh_RejectsCustomLaunchersOnlyForSsh(string repository, string setting, string launcher, bool rejected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = Path.Combine(workspace.Path, "gitconfig");
        var configuration = setting == "core.sshCommand" ? $"[core]\nsshCommand = {launcher}\n" : "";
        await File.WriteAllTextAsync(config, configuration);
        string[] arguments = ["ls-remote", "--", repository];
        var runner = CreateRunner();
        var info = runner.CreateStartInfo(workspace.Path, arguments, GitCommandMode.Unattended);
        IsolateGit(info, workspace.Path, config);
        if (setting != "core.sshCommand")
        {
            info.Environment[setting] = launcher;
        }

        if (rejected)
        {
            var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
                runner.ConfigureUnattendedSshAsync(info, arguments, TestContext.Current.CancellationToken));
            Assert.Equal(GitFailure.UnattendedSshUnsupported, exception.Failure);
        }
        else
        {
            await runner.ConfigureUnattendedSshAsync(info, arguments, TestContext.Current.CancellationToken);
            Assert.Equal(launcher, info.Environment[setting]);
        }
        Assert.Equal(configuration, await File.ReadAllTextAsync(config));
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "GIT_SSH")]
    [InlineData(false, "GIT_SSH_COMMAND")]
    [InlineData(false, "core.sshCommand")]
    [RequiresTools(["git", "ssh"])]
    public async Task ConfigureUnattendedSsh_VerifiesOpenSshAndAppliesBatchModeAfterUrlRewrites(bool rewritten, string? setting)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var config = Path.Combine(workspace.Path, "gitconfig");
        var configuration = (rewritten ? "[url \"ssh://git@example.test/\"]\ninsteadOf = https://example.test/\n" : "")
            + (setting == "core.sshCommand" ? "[core]\nsshCommand = ssh\n" : "");
        await File.WriteAllTextAsync(config, configuration);
        string[] arguments = ["ls-remote", "--", rewritten ? "https://example.test/repository" : "ssh://git@example.test/repository"];
        var runner = CreateRunner();
        var info = runner.CreateStartInfo(workspace.Path, arguments, GitCommandMode.Unattended);
        IsolateGit(info, workspace.Path, config);
        info.Environment["SSH_AUTH_SOCK"] = "preserved-agent";
        if (setting is not null && setting != "core.sshCommand")
        {
            info.Environment[setting] = "ssh";
        }

        await runner.ConfigureUnattendedSshAsync(info, arguments, TestContext.Current.CancellationToken);

        var ssh = PathLookupHelper.ResolveExecutablePath("ssh");
        Assert.Equal($"'{ssh.Replace("'", "'\\''", StringComparison.Ordinal)}' -o BatchMode=yes", info.Environment["GIT_SSH_COMMAND"]);
        Assert.Equal("ssh", info.Environment["GIT_SSH_VARIANT"]);
        Assert.Equal("preserved-agent", info.Environment["SSH_AUTH_SOCK"]);
        Assert.Equal(configuration, await File.ReadAllTextAsync(config));
    }

    private static string ShellPath(string path) => path.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "'\\''", StringComparison.Ordinal);

    private static void IsolateGit(ProcessStartInfo info, string home, string config)
    {
        info.Environment["HOME"] = home;
        info.Environment["USERPROFILE"] = home;
        info.Environment["GIT_CONFIG_GLOBAL"] = config;
        info.Environment["GIT_CONFIG_SYSTEM"] = config;
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_COUNT"] = "0";
        info.Environment.Remove("GIT_CONFIG_PARAMETERS");
        info.Environment.Remove("GIT_ASKPASS");
        info.Environment.Remove("SSH_ASKPASS");
        info.Environment.Remove("GIT_SSH");
        info.Environment.Remove("GIT_SSH_COMMAND");
        info.Environment.Remove("GIT_SSH_VARIANT");
        info.Environment["NO_PROXY"] = "127.0.0.1";
    }
}
