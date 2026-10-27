// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Configuration;

public class ConfigSourceServiceGitTests(ITestOutputHelper outputHelper) : IDisposable
{
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddXunit(outputHelper));

    public void Dispose() => _loggerFactory.Dispose();

    private GitCommandRunner CreateRunner(ICliHostEnvironment hostEnvironment) =>
        new(hostEnvironment, _loggerFactory.CreateLogger<GitCommandRunner>());

    private IGitClient CreateClient(ICliHostEnvironment hostEnvironment) =>
        new GitClient(CreateRunner(hostEnvironment));

    [Theory]
    [InlineData(nameof(GitFailure.Unavailable), false)]
    [InlineData(nameof(GitFailure.Unavailable), true)]
    [InlineData(nameof(GitFailure.AuthenticationFailed), false)]
    [InlineData(nameof(GitFailure.AuthenticationFailed), true)]
    [InlineData(nameof(GitFailure.CommandFailed), false)]
    [InlineData(nameof(GitFailure.CommandFailed), true)]
    [InlineData(nameof(GitFailure.RepositoryUnavailable), false)]
    [InlineData(nameof(GitFailure.RepositoryUnavailable), true)]
    public async Task SharedGitFailures_PreserveConfigurationSourceMessages(string failure, bool check)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var kind = Enum.Parse<GitFailure>(failure);
        var commands = new TestGitCommandRunner
        {
            RunCallback = (_, _) => throw new GitCommandException(kind, 128)
        };
        var client = new GitClient(commands);
        var source = new ConfigSource { Repository = workspace.Path, Path = AspireConfigFile.FileName };

        var expected = kind switch
        {
            GitFailure.Unavailable => ConfigSourceStrings.GitNotFound,
            GitFailure.AuthenticationFailed => ConfigSourceStrings.AuthenticationFailed,
            _ => ConfigSourceStrings.RepositoryUnavailable
        };
        if (check)
        {
            var interaction = await CheckFailureAsync(workspace, client, source, TimeProvider.System, TestContext.Current.CancellationToken);
            Assert.Equal(string.Format(ConfigSourceStrings.CheckFailed, expected), Assert.Single(interaction.DisplayedMessages).Message);
            Assert.Empty(interaction.DisplayedRawText);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConfigSourceException>(() => client.AcquireAsync(source, workspace.Path, TestContext.Current.CancellationToken));
            Assert.Equal(expected, error.Message);
            Assert.Null(error.InnerException);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedGitTimeout_BudgetCoversAllCommands(bool check)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var time = new FakeTimeProvider();
        var commands = new TestGitCommandRunner
        {
            RunCallback = (command, token) =>
            {
                time.Advance(TimeSpan.FromSeconds(10));
                token.ThrowIfCancellationRequested();
                return Task.FromResult(command.Arguments[0] == "ls-remote"
                    ? $"{TestGitCommandRunner.DefaultCommitSha}\tHEAD\n"
                    : TestGitCommandRunner.DefaultCommitSha);
            }
        };
        var client = new GitClient(commands);
        var source = new ConfigSource { Repository = workspace.Path, Path = AspireConfigFile.FileName };

        if (check)
        {
            var interaction = await CheckFailureAsync(workspace, client, source, time, TestContext.Current.CancellationToken);
            Assert.Equal(string.Format(ConfigSourceStrings.CheckFailed, ConfigSourceStrings.GitTimeout), Assert.Single(interaction.DisplayedMessages).Message);
            Assert.Empty(interaction.DisplayedRawText);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConfigSourceException>(() => client.AcquireAsync(source, workspace.Path, TestContext.Current.CancellationToken, time));
            Assert.Equal(ConfigSourceStrings.GitTimeout, error.Message);
            Assert.Null(error.GitOutput);
        }
        Assert.Equal(3, commands.Commands.Count);
    }

    [Theory]
    [InlineData(false, false, "fetch")]
    [InlineData(false, true, "fetch")]
    [InlineData(true, false, "fetch")]
    [InlineData(true, true, "fetch")]
    [InlineData(false, false, "ls-remote")]
    [InlineData(false, true, "ls-remote")]
    [InlineData(true, false, "ls-remote")]
    [InlineData(true, true, "ls-remote")]
    public async Task SharedGitCancellation_DisplaysPartialOutputOnlyForTimeout(bool check, bool userCancellation, string cancelledCommand)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider();
        var commands = new TestGitCommandRunner
        {
            RunCallback = (command, token) =>
            {
                if (command.Arguments[0] == cancelledCommand || command.Arguments[1] == cancelledCommand)
                {
                    if (userCancellation)
                    {
                        cancellation.Cancel();
                    }
                    else
                    {
                        time.Advance(TimeSpan.FromSeconds(30));
                    }
                    throw new GitProcessCanceledException("partial-private-remote-diagnostic", token);
                }
                return Task.FromResult(command.Arguments[0] == "ls-remote"
                    ? $"{TestGitCommandRunner.DefaultCommitSha}\tHEAD\n"
                    : string.Empty);
            }
        };
        var client = new GitClient(commands);
        var source = new ConfigSource { Repository = workspace.Path, Path = AspireConfigFile.FileName };
        Task RunAsync() => check
            ? CheckFailureAsync(workspace, client, source, time, cancellation.Token)
            : client.AcquireAsync(source, workspace.Path, cancellation.Token, time);

        if (userCancellation)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(RunAsync);
            Assert.Equal(new OperationCanceledException(cancellation.Token).Message, error.Message);
        }
        else if (check)
        {
            var interaction = await CheckFailureAsync(workspace, client, source, time, cancellation.Token);
            Assert.Equal(string.Format(ConfigSourceStrings.CheckFailed, ConfigSourceStrings.GitTimeout), Assert.Single(interaction.DisplayedMessages).Message);
            Assert.Equal("partial-private-remote-diagnostic", Assert.Single(interaction.DisplayedRawText).Text);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConfigSourceException>(RunAsync);
            Assert.Equal(ConfigSourceStrings.GitTimeout, error.Message);
            Assert.Equal("partial-private-remote-diagnostic", error.GitOutput);
        }
    }

    private static async Task<TestInteractionService> CheckFailureAsync(
        TemporaryWorkspace workspace, IGitClient git, ConfigSource source, TimeProvider clock, CancellationToken token)
    {
        await TestConfigSourceServices.SaveBindingAsync(new ConfigSource
        {
            Repository = source.Repository,
            Path = source.Path,
            Ref = source.Ref,
            AppliedVersion = "previous-version"
        }, workspace.WorkspaceRoot, token);
        var interaction = new TestInteractionService();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, clock: clock, interaction: interaction);
        await service.CheckProjectAsync(workspace.WorkspaceRoot, force: false, token);
        service.NotifyIfUpdateAvailable();
        return interaction;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RunProcess_DescendantHoldingOutputCannotBlockShutdown(bool parentExits, bool captureTimeoutOutput)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var pidFile = Path.Combine(workspace.Path, "child.pid");
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-Command");
            // The child inherits both output pipes. Either leave its parent running for
            // cancellation, or exit the parent to exercise the bounded post-exit drain.
            startInfo.ArgumentList.Add(
                "[Console]::Error.Write('Enter passphrase: '); [Console]::Out.Write(('x' * 1048576)); " +
                "$child = Start-Process powershell.exe -NoNewWindow -PassThru -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 60'; " +
                $"[IO.File]::WriteAllText('{pidFile.Replace("'", "''")}', [string]$child.Id); " +
                (parentExits ? "exit 0" : "Start-Sleep -Seconds 60"));
        }
        else
        {
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("printf 'Enter passphrase: ' >&2; printf '%1048576s' '' | tr ' ' x; sleep 60 & echo $! > \"$1\"; " + (parentExits ? "exit 0" : "wait"));
            startInfo.ArgumentList.Add("test-parent");
            startInfo.ArgumentList.Add(pidFile);
        }
        using var timeoutSource = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(milliseconds: 30_000);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, timeoutSource.Token);
        var stopwatch = Stopwatch.StartNew();
        void Log(string message) => outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] [{stopwatch.Elapsed}] {message}");
        Log($"Starting captured process '{startInfo.FileName}'; parentExits={parentExits}, captureTimeoutOutput={captureTimeoutOutput}.");
        var run = CreateRunner(TestHelpers.CreateNonInteractiveHostEnvironment()).RunProcessAsync(
            startInfo, captureTimeoutOutput, GitCommandMode.Unattended, cancellation.Token);
        Log($"Captured process launched; task status={run.Status}. Waiting for descendant PID in '{pidFile}'.");
        Process? child = null;
        try
        {
            var pid = await ProcessTestHelpers.WaitForProcessIdAsync(pidFile, cancellation.Token);
            child = Process.GetProcessById(pid);
            Log($"Descendant PID={pid}; waiting for {(parentExits ? "post-exit pipe drain" : "cancellation")}.");

            if (parentExits)
            {
                var error = await Assert.ThrowsAsync<GitCommandException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
                Assert.Equal(GitFailure.RepositoryUnavailable, error.Failure);
                Log("Post-exit pipe drain returned the expected failure.");
            }
            else
            {
                Log("Requesting cancellation.");
                cancellation.Cancel();
                Log("Cancellation requested; waiting for captured process to return.");
                var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
                Log($"Captured process returned {error.GetType().Name}; waiting for descendant {pid} to exit.");
                if (captureTimeoutOutput)
                {
                    var interrupted = Assert.IsType<GitProcessCanceledException>(error);
                    const string prompt = "Enter passphrase: ";
                    Assert.InRange(interrupted.Output.Length, 64 * 1024 + prompt.Length + 1, 1048576 + prompt.Length);
                    Assert.Equal(new string('x', interrupted.Output.Length - prompt.Length) + prompt, interrupted.Output);
                    Assert.Equal(new OperationCanceledException(cancellation.Token).Message, interrupted.Message);
                }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.True(child.HasExited);
                Log($"Descendant {pid} exited.");
            }
        }
        finally
        {
            Log($"Starting cleanup; captured task status={run.Status}.");
            cancellation.Cancel();
            Log("Cleanup cancellation completed.");
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    Log($"Killing descendant tree {child.Id}.");
                    child.Kill(entireProcessTree: true);
                    Log($"Kill returned for descendant {child.Id}; waiting for exit.");
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Log($"Descendant {child.Id} cleanup completed.");
                }
                child.Dispose();
            }
            var cleanupError = await Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
            Log($"Cleanup finished; captured task status={run.Status}, capture exception={cleanupError?.GetType().Name ?? "none"}.");
        }
    }

    [Fact]
    public void CreateStartInfo_AdvisoryFetchNeverOwnsTerminal()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var client = CreateRunner(TestHelpers.CreateInteractiveHostEnvironment());

        var startInfo = client.CreateStartInfo(workspace.Path, ["fetch"], GitCommandMode.Unattended);

        Assert.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", startInfo.Environment["GCM_INTERACTIVE"]);
        Assert.True(startInfo.CreateNoWindow);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.False(startInfo.RedirectStandardInput);
    }

    [Theory]
    [InlineData(false, null, null, true)]
    [InlineData(true, null, null, false)]
    [InlineData(false, "true", null, false)]
    [InlineData(false, null, "true", false)]
    public void CreateStartInfo_CredentialPromptsFollowHostInteractivity(
        bool nonInteractive, string? configuredNonInteractive, string? ci, bool expectedInteractive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var configuration = new ConfigurationManager();
        configuration["ASPIRE_NON_INTERACTIVE"] = configuredNonInteractive;
        configuration["CI"] = ci;
        var hostEnvironment = new CliHostEnvironment(configuration, nonInteractive, isOutputRedirected: true);
        var client = CreateRunner(hostEnvironment);

        var startInfo = client.CreateStartInfo(workspace.Path, ["fetch", "--depth", "1", "--", "https://example.com/configuration", "HEAD"], GitCommandMode.Interactive);

        Assert.Equal(expectedInteractive ? "1" : "0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal(!expectedInteractive, startInfo.CreateNoWindow);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.Equal(!expectedInteractive, startInfo.RedirectStandardOutput);
        Assert.Equal(!expectedInteractive, startInfo.RedirectStandardError);
        Assert.Equal(!expectedInteractive, startInfo.StandardOutputEncoding is not null);
        Assert.Equal(!expectedInteractive, startInfo.StandardErrorEncoding is not null);
        if (!expectedInteractive)
        {
            Assert.Equal("never", startInfo.Environment["GCM_INTERACTIVE"]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateStartInfo_ReferenceResolutionCapturesRefsWithoutHidingInteractivePrompts(bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var runner = CreateRunner(interactive
            ? TestHelpers.CreateInteractiveHostEnvironment()
            : TestHelpers.CreateNonInteractiveHostEnvironment());

        var startInfo = runner.CreateStartInfo(workspace.Path, ["ls-remote", "--", "repository", "HEAD"], GitCommandMode.Interactive);

        Assert.False(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.Equal(!interactive, startInfo.RedirectStandardError);
        Assert.Equal(!interactive, startInfo.StandardErrorEncoding is not null);
        Assert.Equal(interactive ? "1" : "0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
    }

    [Theory]
    [InlineData("init")]
    [InlineData("rev-parse")]
    [InlineData("ls-tree")]
    [InlineData("cat-file")]
    public void CreateStartInfo_LocalQueriesRemainCapturedWhenInteractive(string command)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var client = CreateRunner(TestHelpers.CreateInteractiveHostEnvironment());

        var startInfo = client.CreateStartInfo(workspace.Path, [command], GitCommandMode.Interactive);

        Assert.False(startInfo.RedirectStandardInput);
        Assert.True(startInfo.RedirectStandardOutput);
        Assert.True(startInfo.RedirectStandardError);
        Assert.Equal(System.Text.Encoding.UTF8, startInfo.StandardOutputEncoding);
        Assert.Equal(System.Text.Encoding.UTF8, startInfo.StandardErrorEncoding);
    }

    [Theory]
    [InlineData("aspire.config.json", false)]
    [InlineData("aspire.config.json", true)]
    [InlineData("teams/platform config.json", false)]
    [InlineData("teams/platform config.json", true)]
    public async Task Acquire_ReadsDefaultBranchSnapshotWithoutCheckingOut(string path, bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var custom = new string('x', 70 * 1024) + "\u65e5\u672c\u8a9e";
        await CreateRepositoryAsync(workspace, path, """
            {"DOCS":{"llmsTxtUrl":"https://example.com/llms.txt","API":{"custom":"preserved"}},"docs:api:sitemapUrl":"https://example.com/sitemap.xml",
            """ + "\"features\":{\"test\":true},\"custom\":\"" + custom + "\"}");
        var cache = workspace.CreateDirectory("cache");
        var client = CreateClient(interactive
            ? TestHelpers.CreateInteractiveHostEnvironment()
            : TestHelpers.CreateNonInteractiveHostEnvironment());

        var source = new ConfigSource { Repository = workspace.Path, Path = path };
        source.Validate();
        var (commit, version, configuration) = await client.AcquireAsync(
            source, cache.FullName, TestContext.Current.CancellationToken);

        Assert.Matches("^[a-f0-9]{40,64}$", commit);
        Assert.Equal((await RunGitOutputAsync(workspace.Path, ["rev-parse", $"HEAD:{path}"])).Trim(), version);
        Assert.True(configuration["features"]!["test"]!.GetValue<bool>());
        Assert.Equal(custom, configuration["custom"]!.GetValue<string>());
        Assert.Equal("https://example.com/llms.txt", configuration["DOCS"]!["llmsTxtUrl"]!.GetValue<string>());
        Assert.Equal("https://example.com/sitemap.xml", configuration["DOCS"]!["API"]!["sitemapUrl"]!.GetValue<string>());
        Assert.Equal("preserved", configuration["DOCS"]!["API"]!["custom"]!.GetValue<string>());
        Assert.Equal(["DOCS", "features", "custom"], configuration.Select(property => property.Key));
        Assert.False(File.Exists(Path.Combine(cache.FullName, path)));
    }

    [Fact]
    public async Task Materialize_AppliesConfigurationFromAbsoluteLocalRepositoryWithSpaces()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string path = "teams/platform.json";
        await CreateRepositoryAsync(workspace, path, """{"docs":{"llmsTxtUrl":"https://docs.example/local/llms.txt"}}""");
        var repository = workspace.CreateDirectory("local repository");
        await RunGitOutputAsync(workspace.Path, ["clone", "--", workspace.Path, repository.FullName]);
        var source = new ConfigSource { Repository = repository.FullName, Path = path };
        var project = workspace.CreateDirectory("project");
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(),
            git: CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment()));

        await service.MaterializeAsync(source, project, TestContext.Current.CancellationToken);

        var config = AspireConfigFile.Load(project.FullName)!;
        Assert.Equal("https://docs.example/local/llms.txt", config.Docs!.LlmsTxtUrl);
        Assert.Equal(repository.FullName, config.ConfigSource!.Repository);
        Assert.Equal(path, config.ConfigSource.Path);
        Assert.Equal((await RunGitOutputAsync(repository.FullName, ["rev-parse", $"HEAD:{path}"])).Trim(),
            config.ConfigSource.AppliedVersion);
    }

    [Theory]
    [InlineData("""{"DOCS":{"llmsTxtUrl":"https://example.test/llms.txt"},"docs":{"api":{"sitemapUrl":"https://example.test/sitemap.xml"}}}""")]
    [InlineData("""{"docs":{"api":{"sitemapUrl":"https://example.test/sitemap.xml"}},"DOCS":{"llmsTxtUrl":"https://example.test/llms.txt"}}""")]
    [InlineData("""{"docs":{"llmsTxtUrl":"https://example.test/llms.txt","API":{"custom":"excluded"},"api":{"sitemapUrl":"https://example.test/sitemap.xml"}}}""")]
    [InlineData("""{"DOCS":null,"docs":{"llmsTxtUrl":"https://example.test/llms.txt","api":{"sitemapUrl":"https://example.test/sitemap.xml"}}}""")]
    [InlineData("""{"docs":{"llmsTxtUrl":"https://example.test/llms.txt","api":{"sitemapUrl":"https://example.test/sitemap.xml"}},"docs:llmsTxtUrl":"https://flat.example/llms.txt","docs:api:sitemapUrl":"https://flat.example/sitemap.xml"}""")]
    [InlineData("""{"docs:llmsTxtUrl":"https://example.test/llms.txt","docs:api":{"sitemapUrl":"https://example.test/sitemap.xml","custom:key":"preserved"}}""")]
    [InlineData("""{"DOCS":{"llmsTxtUrl":null,"API":{"SITEMAPURL":null}},"docs:api:sitemapUrl":"https://example.test/sitemap.xml","docs:llmsTxtUrl":"https://example.test/llms.txt"}""")]
    [InlineData("""{"docs:llmsTxtUrl":"https://example.test/llms.txt","DOCS:LLMSTXTURL":"https://flat.example/llms.txt","docs:api:sitemapUrl":"https://example.test/sitemap.xml"}""")]
    public async Task Materialize_ReadsAllowedLeavesAcrossCaseInsensitiveSections(string content)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, content);
        var source = new ConfigSource { Repository = workspace.Path, Path = AspireConfigFile.FileName };
        var project = workspace.CreateDirectory("project");
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(),
            git: CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment()));

        await service.MaterializeAsync(source, project, TestContext.Current.CancellationToken);

        var config = AspireConfigFile.Load(project.FullName)!;
        Assert.Equal("https://example.test/llms.txt", config.Docs!.LlmsTxtUrl);
        Assert.Equal("https://example.test/sitemap.xml", config.Docs.Api!.SitemapUrl);
    }

    [Fact]
    public async Task Acquire_PreservesLiteralDictionaryKeys()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string content = """
            {
              "features": { "feature:name": true },
              "profiles": {
                "dev:local": {
                  "environmentVariables": { "Logging:LogLevel:Default": "Debug" }
                }
              },
              "custom": { "literal:key": "preserved" },
              "docs": { "api:sitemapUrl": "https://example.test/sitemap.xml" }
            }
            """;
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, content);
        var client = CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment());

        var (_, _, configuration) = await client.AcquireAsync(
            new() { Repository = workspace.Path, Path = AspireConfigFile.FileName },
            workspace.CreateDirectory("cache").FullName, TestContext.Current.CancellationToken);

        var config = configuration.Deserialize(JsonSourceGenerationContext.Default.AspireConfigFile)!;
        Assert.Equal(new Dictionary<string, bool> { ["feature:name"] = true }, config.Features);
        Assert.Equal(new Dictionary<string, string> { ["Logging:LogLevel:Default"] = "Debug" },
            Assert.Single(config.Profiles!).Value.EnvironmentVariables);
        Assert.Null(config.Docs!.Api);
        await Verify(configuration.ToJsonString(), "json");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    [InlineData("""{"features":{"test":"not-a-boolean"}}""")]
    [InlineData("""{"features:test":"not-a-boolean"}""")]
    [InlineData("""{"features":{"test":true},"features":{"test":false}}""")]
    [InlineData("""{"DOCS":{"llmsTxtUrl":"https://example.test/first"},"docs":{"LLMSTXTURL":"https://example.test/second"}}""")]
    [InlineData("""{"docs":{"API":{"sitemapUrl":"https://example.test/first"},"api":{"SITEMAPURL":"https://example.test/second"}}}""")]
    [InlineData("""{"docs":{"llmsTxtUrl":"https://example.test/first"},"DOCS":{"LLMSTXTURL":"https://example.test/second"},"docs:llmsTxtUrl":"https://example.test/flat"}""")]
    [InlineData("""{"docs":{"api:sitemapUrl":"https://example.test/first"},"DOCS":{"API:SITEMAPURL":"https://example.test/second"}}""")]
    [InlineData("""{"features":{"feature:name":true,"feature":{"name":false}}}""")]
    [InlineData("""{"configSource:repository":"https://example.com/nested"}""")]
    [InlineData("""{"configSource":{"repository":"https://example.com/nested","path":"config.json"}}""")]
    public async Task Acquire_RejectsInvalidConfiguration(string content)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, content);

        await Assert.ThrowsAsync<ConfigSourceException>(() => CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment()).AcquireAsync(
            new() { Repository = workspace.Path, Path = AspireConfigFile.FileName }, workspace.CreateDirectory("cache").FullName, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Acquire_DistinguishesMissingFile()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, "other.json", "{}");

        var error = await Assert.ThrowsAsync<ConfigSourceException>(() => CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment()).AcquireAsync(
            new() { Repository = workspace.Path, Path = AspireConfigFile.FileName }, workspace.CreateDirectory("cache").FullName, TestContext.Current.CancellationToken));

        Assert.Equal(ConfigSourceStrings.MissingFile, error.Message);
    }

    [Theory]
    [InlineData(4 * 1024 * 1024, true)]
    [InlineData(4 * 1024 * 1024 + 1, false)]
    public async Task Acquire_EnforcesExactConfigurationSizeLimit(int bytes, bool valid)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, "{}".PadRight(bytes));
        var client = CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment());
        var cache = workspace.CreateDirectory("cache");
        var source = new ConfigSource { Repository = workspace.Path, Path = AspireConfigFile.FileName };

        if (valid)
        {
            var (_, _, configuration) = await client.AcquireAsync(source, cache.FullName, TestContext.Current.CancellationToken);
            Assert.Empty(configuration);
        }
        else
        {
            var error = await Assert.ThrowsAsync<ConfigSourceException>(() =>
                client.AcquireAsync(source, cache.FullName, TestContext.Current.CancellationToken));
            Assert.Equal(ConfigSourceStrings.InvalidConfiguration, error.Message);
        }
    }

    [Theory]
    [InlineData("config/preview")]
    [InlineData("refs/heads/config/preview")]
    public async Task Acquire_ReadsSelectedBranchInsteadOfDefaultBranch(string reference)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, """{"custom":"default"}""");
        var originalBranch = (await RunGitOutputAsync(workspace.Path, ["branch", "--show-current"])).Trim();
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "checkout", "-b", "config/preview");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, AspireConfigFile.FileName), """{"custom":"preview"}""");
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "commit", "-am", "Preview configuration");
        var expectedCommit = (await RunGitOutputAsync(workspace.Path, ["rev-parse", "HEAD"])).Trim();
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "checkout", originalBranch);
        var client = CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment());

        var (commit, _, configuration) = await client.AcquireAsync(
            new() { Repository = workspace.Path, Path = AspireConfigFile.FileName, Ref = reference },
            workspace.CreateDirectory("cache").FullName, TestContext.Current.CancellationToken);

        Assert.Equal(expectedCommit, commit);
        Assert.Equal("preview", configuration["custom"]!.GetValue<string>());
    }

    [Fact]
    public async Task Acquire_ReadsPinnedCommitOutsideShallowDefaultBranchTip()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, """{"custom":"pinned"}""");
        var expectedCommit = (await RunGitOutputAsync(workspace.Path, ["rev-parse", "HEAD"])).Trim();
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, AspireConfigFile.FileName), """{"custom":"latest"}""");
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "commit", "-am", "New configuration");
        var client = CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment());

        var (commit, _, configuration) = await client.AcquireAsync(
            new() { Repository = workspace.Path, Path = AspireConfigFile.FileName, Ref = expectedCommit },
            workspace.CreateDirectory("cache").FullName, TestContext.Current.CancellationToken);

        Assert.Equal(expectedCommit, commit);
        Assert.Equal("pinned", configuration["custom"]!.GetValue<string>());
    }

    [Fact]
    public async Task Check_IdentifiesFileChangesWithoutMaterializingJsonAndHonorsPins()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, "{}");
        var source = new ConfigSource { Repository = workspace.Path, Path = AspireConfigFile.FileName };
        var runner = CreateRunner(TestHelpers.CreateNonInteractiveHostEnvironment());
        var commands = new TestGitCommandRunner
        {
            RunCallback = (command, token) => runner.RunAsync(command.WorkingDirectory, command.Arguments, command.Mode, token)
        };
        var client = new GitClient(commands);
        var original = await client.CheckAsync(source, workspace.CreateDirectory("first-check").FullName, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "unrelated.txt"), "Unrelated change");
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "add", "unrelated.txt");
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "commit", "-m", "Unrelated commit");

        var unchanged = await client.CheckAsync(source, workspace.CreateDirectory("unchanged-check").FullName, TestContext.Current.CancellationToken);

        Assert.NotEqual(original.Commit, unchanged.Commit);
        Assert.Equal(original.Version, unchanged.Version);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, AspireConfigFile.FileName), "invalid JSON is not read during a version check");
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "commit", "-am", "Change configuration");
        var directory = workspace.CreateDirectory("changed-check");
        var changed = await client.CheckAsync(source, directory.FullName, TestContext.Current.CancellationToken);
        var pinned = await client.CheckAsync(new() { Repository = source.Repository, Path = source.Path, Ref = original.Commit },
            workspace.CreateDirectory("pinned-check").FullName, TestContext.Current.CancellationToken);

        Assert.NotEqual(original.Version, changed.Version);
        Assert.Equal(original, pinned);
        Assert.Equal(
            Enumerable.Repeat(new[] { "ls-remote", "init", "fetch", "rev-parse", "ls-tree" }, 3).SelectMany(sequence => sequence)
                .Concat(["init", "fetch", "rev-parse", "ls-tree"]),
            commands.Commands.Select(command => command.Arguments[0].StartsWith("--git-dir=", StringComparison.Ordinal)
                ? command.Arguments[1] : command.Arguments[0]));
    }

    [Theory]
    [InlineData("missing-branch", false)]
    [InlineData("missing-branch", true)]
    [InlineData("0000000000000000000000000000000000000000", false)]
    [InlineData("0000000000000000000000000000000000000000", true)]
    public async Task Acquire_UnavailableRefFailsWithoutFallingBack(string reference, bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, "{}");
        var client = CreateClient(interactive
            ? TestHelpers.CreateInteractiveHostEnvironment()
            : TestHelpers.CreateNonInteractiveHostEnvironment());

        var error = await Assert.ThrowsAsync<ConfigSourceException>(() => client.AcquireAsync(
            new() { Repository = workspace.Path, Path = AspireConfigFile.FileName, Ref = reference },
            workspace.CreateDirectory("cache").FullName, TestContext.Current.CancellationToken));

        Assert.Equal(ConfigSourceStrings.RepositoryUnavailable, error.Message);
    }

    [Fact]
    public async Task Acquire_RejectsSymlinkConfiguration()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, "target.json", "{}");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, AspireConfigFile.FileName), "target.json");
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "add", AspireConfigFile.FileName);
        // Write a symlink-mode tree entry directly so the test does not require Windows
        // developer mode or elevated filesystem symlink privileges.
        var entry = await RunGitOutputAsync(workspace.Path, ["ls-files", "--stage", AspireConfigFile.FileName]);
        var objectId = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "update-index", "--cacheinfo", $"120000,{objectId},{AspireConfigFile.FileName}");
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "commit", "-m", "Add symlink configuration");

        var error = await Assert.ThrowsAsync<ConfigSourceException>(() => CreateClient(TestHelpers.CreateNonInteractiveHostEnvironment()).AcquireAsync(
            new() { Repository = workspace.Path, Path = AspireConfigFile.FileName }, workspace.CreateDirectory("cache").FullName, TestContext.Current.CancellationToken));
        Assert.Equal(ConfigSourceStrings.MissingFile, error.Message);
    }

    [Theory]
    [InlineData("sha1", "default", false)]
    [InlineData("sha1", "branch", false)]
    [InlineData("sha1", "commit", false)]
    [InlineData("sha256", "default", false)]
    [InlineData("sha256", "branch", false)]
    [InlineData("sha256", "commit", false)]
    [InlineData("sha1", "branch", true)]
    [InlineData("sha256", "default", true)]
    public async Task AcquireAndCheck_MatchRepositoryObjectFormat(string objectFormat, string selector, bool interactive)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await CreateRepositoryAsync(workspace, AspireConfigFile.FileName, """{"custom":"original"}""", objectFormat);
        var originalCommit = (await RunGitOutputAsync(workspace.Path, ["rev-parse", "HEAD"])).Trim();
        var originalVersion = (await RunGitOutputAsync(workspace.Path, ["rev-parse", $"HEAD:{AspireConfigFile.FileName}"])).Trim();
        var branch = (await RunGitOutputAsync(workspace.Path, ["branch", "--show-current"])).Trim();
        var source = new ConfigSource
        {
            Repository = workspace.Path,
            Path = AspireConfigFile.FileName,
            Ref = selector switch
            {
                "branch" => branch,
                "commit" => originalCommit,
                _ => null
            }
        };
        var runner = CreateRunner(interactive
            ? TestHelpers.CreateInteractiveHostEnvironment()
            : TestHelpers.CreateNonInteractiveHostEnvironment());
        var commands = new TestGitCommandRunner
        {
            RunCallback = (command, cancellationToken) => runner.RunAsync(command.WorkingDirectory, command.Arguments, command.Mode, cancellationToken)
        };
        var client = new GitClient(commands);
        // SHA-256 object names are longer; keep this fixture's cache below Windows MAX_PATH.
        using var service = TestConfigSourceServices.Create(
            workspace.CreateExecutionContext(aspireHomeDirectory: workspace.CreateDirectory("h")), git: client);
        var token = TestContext.Current.CancellationToken;

        var acquired = await service.AcquireAsync(source, force: true, token);
        var checkedVersion = await client.CheckAsync(source, workspace.CreateDirectory("first-check").FullName, token);

        Assert.Equal(originalCommit, acquired.Metadata.Commit);
        Assert.Equal(originalVersion, acquired.Metadata.Version);
        Assert.Equal("original", acquired.Configuration["custom"]!.GetValue<string>());
        Assert.Equal((originalCommit, originalVersion), checkedVersion);
        Assert.Equal(objectFormat == "sha256" ? 64 : 40, acquired.Metadata.Version.Length);

        await File.WriteAllTextAsync(Path.Combine(workspace.Path, AspireConfigFile.FileName), """{"custom":"updated"}""", token);
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "commit", "-am", "Update configuration");
        var expectedCommit = selector == "commit" ? originalCommit : (await RunGitOutputAsync(workspace.Path, ["rev-parse", "HEAD"])).Trim();
        var expectedVersion = selector == "commit" ? originalVersion : (await RunGitOutputAsync(workspace.Path, ["rev-parse", $"HEAD:{AspireConfigFile.FileName}"])).Trim();

        var refreshed = await service.AcquireAsync(source, force: true, token);
        var updatedCheck = await client.CheckAsync(source, workspace.CreateDirectory("updated-check").FullName, token);

        Assert.Equal(expectedCommit, refreshed.Metadata.Commit);
        Assert.Equal(expectedVersion, refreshed.Metadata.Version);
        Assert.Equal(selector == "commit" ? "original" : "updated", refreshed.Configuration["custom"]!.GetValue<string>());
        Assert.Equal((expectedCommit, expectedVersion), updatedCheck);
        Assert.Equal(Enumerable.Repeat($"--object-format={objectFormat}", 4),
            commands.Commands.Where(command => command.Arguments[0] == "init").Select(command => command.Arguments[2]));
        GitCommandMode[] expectedResolutionModes = selector == "commit" ? []
            : [GitCommandMode.Interactive, GitCommandMode.Unattended, GitCommandMode.Interactive, GitCommandMode.Unattended];
        Assert.Equal(expectedResolutionModes,
            commands.Commands.Where(command => command.Arguments[0] == "ls-remote").Select(command => command.Mode));
    }

    private async Task CreateRepositoryAsync(TemporaryWorkspace workspace, string path, string json, string objectFormat = "sha1")
    {
        await GitTestHelper.EnsureGitAvailableAsync(outputHelper);
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "init", $"--object-format={objectFormat}");
        await GitTestHelper.ConfigureGitIdentityAsync(workspace.Path, outputHelper);
        var configPath = Path.Combine(workspace.Path, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, json);
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "add", "--", path);
        await GitTestHelper.RunGitAsync(workspace.Path, outputHelper, "commit", "-m", "Add configuration");
    }

    private static async Task<string> RunGitOutputAsync(string directory, string[] arguments)
    {
        var result = await System.Diagnostics.Process.RunAndCaptureTextAsync(new("git", arguments)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }, TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitStatus.ExitCode);
        return result.StandardOutput;
    }
}
