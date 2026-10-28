// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.Git;

public class GitCommandRunnerTests(ITestOutputHelper outputHelper) : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddXunit(outputHelper));

    public void Dispose() => _loggerFactory.Dispose();

    private GitCommandRunner CreateRunner() =>
        new(TestHelpers.CreateInteractiveHostEnvironment(), _loggerFactory.CreateLogger<GitCommandRunner>());

    [Fact]
    public void CreateStartInfo_CapturedCommandsUseLiteralArgumentsAndPreserveCredentialHelpers()
    {
        var runner = CreateRunner();
        var startInfo = runner.CreateStartInfo(Environment.CurrentDirectory,
            ["fetch", "--", "-repository with spaces", "HEAD"], GitCommandMode.Captured);

        Assert.Equal("git", startInfo.FileName);
        Assert.Equal(["--literal-pathspecs", "fetch", "--", "-repository with spaces", "HEAD"], startInfo.ArgumentList);
        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("C", startInfo.Environment["LC_ALL"]);
        startInfo.Environment.TryGetValue("GCM_INTERACTIVE", out var credentialManager);
        Assert.Equal(Environment.GetEnvironmentVariable("GCM_INTERACTIVE"), credentialManager);
        Assert.True(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Theory]
    [InlineData("GIT_DIR", true)]
    [InlineData("GIT_WORK_TREE", true)]
    [InlineData("GIT_INDEX_FILE", true)]
    [InlineData("GIT_OBJECT_DIRECTORY", true)]
    [InlineData("GIT_ALTERNATE_OBJECT_DIRECTORIES", true)]
    [InlineData("GIT_COMMON_DIR", true)]
    [InlineData("GIT_NAMESPACE", true)]
    [InlineData("GIT_SHALLOW_FILE", true)]
    [InlineData("git_dir", true)]
    [InlineData("GIT_ASKPASS", false)]
    [InlineData("GIT_SSH_COMMAND", false)]
    [InlineData("GIT_CONFIG_COUNT", false)]
    [InlineData("HOME", false)]
    [InlineData("PATH", false)]
    public void FilterEnvironment_IsolatesRepositoryButPreservesAuthentication(string name, bool removed)
    {
        var environment = new Dictionary<string, string?> { [name] = "test-value" };

        GitCommandRunner.FilterEnvironment(environment, GitCommandMode.Captured);

        Dictionary<string, string?> expected = removed ? [] : new() { [name] = "test-value" };
        Assert.Equal(expected, environment);
    }

    [Theory]
    [InlineData(nameof(GitCommandMode.Interactive))]
    [InlineData(nameof(GitCommandMode.Unattended))]
    public void FilterEnvironment_PreservesConfigurationSourceIsolationPolicy(string mode)
    {
        var environment = new Dictionary<string, string?>
        {
            ["GIT_DIR"] = "other-repository",
            ["GIT_COMMON_DIR"] = "existing-common-directory",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_SSH_COMMAND"] = "configured-ssh"
        };

        GitCommandRunner.FilterEnvironment(environment, Enum.Parse<GitCommandMode>(mode));

        Assert.Equal(new Dictionary<string, string?>
        {
            ["GIT_COMMON_DIR"] = "existing-common-directory",
            ["GIT_CONFIG_COUNT"] = "1",
            ["GIT_SSH_COMMAND"] = "configured-ssh"
        }, environment);
    }

    [Theory]
    [InlineData(nameof(GitCommandMode.Interactive), false)]
    [InlineData(nameof(GitCommandMode.Interactive), true)]
    [InlineData(nameof(GitCommandMode.Unattended), false)]
    [InlineData(nameof(GitCommandMode.Unattended), true)]
    public void FilterEnvironment_UsesDictionaryCaseSensitivityForConfigurationSources(string mode, bool ignoreCase)
    {
        var comparer = ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        string[] repositoryVariables =
        [
            "git_dir", "Git_Work_Tree", "git_index_file", "Git_Object_Directory", "git_alternate_object_directories"
        ];
        var environment = new Dictionary<string, string?>(comparer);
        foreach (var key in repositoryVariables)
        {
            environment[key] = "other-repository";
        }
        environment["GIT_CONFIG_COUNT"] = "1";

        GitCommandRunner.FilterEnvironment(environment, Enum.Parse<GitCommandMode>(mode));

        var expected = new Dictionary<string, string?>(comparer) { ["GIT_CONFIG_COUNT"] = "1" };
        if (!ignoreCase)
        {
            foreach (var key in repositoryVariables)
            {
                expected[key] = "other-repository";
            }
        }
        Assert.Equal(expected, environment);
    }

    [Theory]
    [InlineData("fatal: repository not found", nameof(GitFailure.RepositoryUnavailable))]
    [InlineData("fatal: could not resolve host", nameof(GitFailure.RepositoryUnavailable))]
    [InlineData("fatal: unable to access repository", nameof(GitFailure.RepositoryUnavailable))]
    [InlineData("fatal: could not read from remote repository", nameof(GitFailure.RepositoryUnavailable))]
    [InlineData("fatal: Authentication failed", nameof(GitFailure.AuthenticationFailed))]
    [InlineData("Permission denied (publickey).", nameof(GitFailure.AuthenticationFailed))]
    [InlineData("fatal: could not read Username", nameof(GitFailure.AuthenticationFailed))]
    [InlineData("fatal: could not read Password", nameof(GitFailure.AuthenticationFailed))]
    [InlineData("fatal: unexpected failure", nameof(GitFailure.CommandFailed))]
    public async Task RunProcess_NonzeroExitClassifiesWithoutExposingPrivateDetails(string diagnostic, string expectedFailure)
    {
        var logger = new FakeLogger<GitCommandRunner>();
        var runner = new GitCommandRunner(TestHelpers.CreateNonInteractiveHostEnvironment(), logger);
        var message = $"{diagnostic}: private-repository-secret-marker";
        var startInfo = CreateShellStartInfo($"printf '%s' '{message}' >&2; exit 128",
            $"[Console]::Error.Write('{message}'); exit 128");

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            runner.RunProcessAsync(startInfo, captureCancellationOutput: false, GitCommandMode.Captured, TestContext.Current.CancellationToken));

        var failure = Enum.Parse<GitFailure>(expectedFailure);
        Assert.Equal(failure, exception.Failure);
        Assert.Equal(128, exception.ExitCode);
        Assert.Equal(failure switch
        {
            GitFailure.AuthenticationFailed => ErrorStrings.GitAuthenticationFailed,
            GitFailure.RepositoryUnavailable => ErrorStrings.GitRepositoryUnavailable,
            _ => ErrorStrings.GitCommandFailed
        }, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.Equal(4, logger.Collector.Count);
        Assert.All(logger.Collector.GetSnapshot(), entry => Assert.False(entry.Message.Contains(message, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(nameof(GitCommandMode.Captured), nameof(GitFailure.AuthenticationFailed))]
    [InlineData(nameof(GitCommandMode.Interactive), nameof(GitFailure.CommandFailed))]
    [InlineData(nameof(GitCommandMode.Unattended), nameof(GitFailure.CommandFailed))]
    public void CreateCommandException_PreservesPasswordDiagnosticClassification(string mode, string expectedFailure)
    {
        var exception = GitCommandRunner.CreateCommandException("fatal: could not read Password", 128, Enum.Parse<GitCommandMode>(mode));

        Assert.Equal(Enum.Parse<GitFailure>(expectedFailure), exception.Failure);
    }

    [Fact]
    public async Task RunProcess_MissingExecutableReportsSafeStartupFailure()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var startInfo = new ProcessStartInfo(Path.Combine(workspace.Path, "missing-private-executable"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        var exception = await Assert.ThrowsAsync<GitCommandException>(() =>
            CreateRunner().RunProcessAsync(startInfo, captureCancellationOutput: false, GitCommandMode.Captured, TestContext.Current.CancellationToken));

        Assert.Equal(GitFailure.Unavailable, exception.Failure);
        Assert.Equal(ErrorStrings.GitUnavailable, exception.Message);
        Assert.Null(exception.ExitCode);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public async Task RunProcess_AlreadyCancelledDoesNotStartExecutable()
    {
        var startInfo = new ProcessStartInfo("missing-executable");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateRunner().RunProcessAsync(startInfo, captureCancellationOutput: false, GitCommandMode.Captured, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task RunProcess_PreservesNulDelimitersNewlinesAndPartialLines()
    {
        var startInfo = CreateShellStartInfo("printf 'first\\r\\nsecond\\000last'", "[Console]::Out.Write(\"first`r`nsecond`0last\")");

        var output = await CreateRunner().RunProcessAsync(startInfo, captureCancellationOutput: false, GitCommandMode.Captured, TestContext.Current.CancellationToken);

        Assert.Equal("first\r\nsecond\0last", output);
    }

    [Fact]
    public async Task RunProcess_CapturedCommandsReceiveStandardInputEof()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runner = CreateRunner();
        var startInfo = runner.CreateStartInfo(workspace.Path, ["fetch"], GitCommandMode.Captured);
        var shell = CreateShellStartInfo(
            """input=$(cat); if [ -n "$input" ]; then exit 1; fi; printf EOF""",
            """$inputText = [Console]::In.ReadToEnd(); if ($inputText.Length -ne 0) { exit 1 }; [Console]::Out.Write('EOF')""");
        startInfo.FileName = shell.FileName;
        startInfo.ArgumentList.Clear();
        foreach (var argument in shell.ArgumentList)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var timeoutSource = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(milliseconds: 10_000);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, timeoutSource.Token);

        var output = await runner.RunProcessAsync(startInfo, captureCancellationOutput: false, GitCommandMode.Captured, cancellation.Token);

        Assert.True(startInfo.RedirectStandardInput);
        Assert.Equal("EOF", output);
    }

    private static ProcessStartInfo CreateShellStartInfo(string unixCommand, string windowsCommand)
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(windowsCommand);
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(unixCommand);
        }

        return startInfo;
    }
}
