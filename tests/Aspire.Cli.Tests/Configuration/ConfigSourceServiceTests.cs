// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Aspire.Shared.Telemetry;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Configuration;

public class ConfigSourceServiceTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task CheckProject_ChecksGlobalSourceWithoutSearchingPastNearestConfiguration(bool force, bool localFileExists)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var project = workspace.CreateDirectory("project");
        var global = workspace.CreateDirectory("global");
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, global, CancellationToken.None);
        var globalFile = new FileInfo(Path.Combine(global.FullName, "custom-global.json"));
        File.Move(Path.Combine(global.FullName, AspireConfigFile.FileName), globalFile.FullName);
        var originalGlobal = await File.ReadAllTextAsync(globalFile.FullName);
        var localPath = Path.Combine(project.FullName, AspireConfigFile.FileName);
        const string localConfiguration = """{"custom":"keep"}""";
        string? originalParent = null;
        var parentPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        if (localFileExists)
        {
            await File.WriteAllTextAsync(localPath, localConfiguration);
            await TestConfigSourceServices.SaveBindingAsync(new ConfigSource
            {
                Repository = s_appliedSource.Repository,
                Path = "ignored-parent.json",
                AppliedVersion = "ignored-version"
            }, workspace.WorkspaceRoot, CancellationToken.None);
            originalParent = await File.ReadAllTextAsync(parentPath);
        }
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (source, _) =>
            {
                Assert.True(ConfigSource.HasSameIdentity(s_appliedSource, source));
                return Task.FromResult(("version-2", new JsonObject()));
            },
            InspectConfiguration = (source, _) =>
            {
                Assert.True(ConfigSource.HasSameIdentity(s_appliedSource, source));
                return Task.FromResult("version-2");
            }
        };
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, globalSettingsFile: globalFile);

        await service.CheckProjectAsync(project, force, CancellationToken.None);

        Assert.Equal(force ? 1 : 0, git.Calls);
        Assert.Equal(force ? 0 : 1, git.CheckCalls);
        var actualGlobal = await File.ReadAllTextAsync(globalFile.FullName);
        if (force)
        {
            Assert.Equal("version-2", JsonNode.Parse(actualGlobal)!["configSource"]!["appliedVersion"]!.GetValue<string>());
        }
        else
        {
            Assert.Equal(originalGlobal, actualGlobal);
        }
        Assert.False(File.Exists(Path.Combine(global.FullName, AspireConfigFile.FileName)));
        if (localFileExists)
        {
            Assert.Equal(localConfiguration, await File.ReadAllTextAsync(localPath));
            Assert.Equal(originalParent, await File.ReadAllTextAsync(parentPath));
        }
        else
        {
            Assert.False(File.Exists(localPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckProject_LocalAndGlobalSameFileIsProcessedOnce(bool force)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var globalFile = new FileInfo(Path.Combine(workspace.Path, AspireConfigFile.FileName));
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-2", new JsonObject())),
            InspectConfiguration = (_, _) => Task.FromResult("version-2")
        };
        var interaction = new TestInteractionService();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction, globalSettingsFile: globalFile);

        await service.CheckProjectAsync(workspace.WorkspaceRoot, force, CancellationToken.None);
        service.NotifyIfUpdateAvailable();

        Assert.Equal(force ? 1 : 0, git.Calls);
        Assert.Equal(force ? 0 : 1, git.CheckCalls);
        Assert.Single(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData(false, "same")]
    [InlineData(false, "repository")]
    [InlineData(false, "path")]
    [InlineData(false, "ref")]
    [InlineData(true, "same")]
    [InlineData(true, "repository")]
    [InlineData(true, "path")]
    [InlineData(true, "ref")]
    public async Task CheckProject_LocalAndGlobalSourcesAreSerialAndDeduplicated(bool force, string difference)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var gap = workspace.CreateDirectory("gap");
        var parent = gap.CreateSubdirectory("parent");
        parent.CreateSubdirectory(".git");
        var project = parent.CreateSubdirectory("project");
        var sibling = parent.CreateSubdirectory("sibling");
        var global = workspace.CreateDirectory("global");
        var globalFile = new FileInfo(Path.Combine(global.FullName, AspireConfigFile.FileName));
        var globalSource = new ConfigSource
        {
            Repository = difference == "repository" ? "https://other.example/config" : s_appliedSource.Repository,
            Path = difference == "path" ? "other.json" : s_appliedSource.Path,
            Ref = difference == "ref" ? "other" : s_appliedSource.Ref,
            AppliedVersion = "global-version"
        };
        var ignoredSource = new ConfigSource { Repository = s_appliedSource.Repository, Path = "ignored.json", AppliedVersion = "ignored-version" };
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, project, CancellationToken.None);
        await TestConfigSourceServices.SaveBindingAsync(globalSource, global, CancellationToken.None);
        await TestConfigSourceServices.SaveBindingAsync(ignoredSource, parent, CancellationToken.None);
        await TestConfigSourceServices.SaveBindingAsync(ignoredSource, workspace.WorkspaceRoot, CancellationToken.None);
        await TestConfigSourceServices.SaveBindingAsync(ignoredSource, sibling, CancellationToken.None);
        var siblingPath = Path.Combine(sibling.FullName, AspireConfigFile.FileName);
        var siblingConfiguration = await File.ReadAllTextAsync(siblingPath);
        var originals = new Dictionary<string, string>();
        foreach (var directory in new[] { project, global, parent, workspace.WorkspaceRoot })
        {
            var path = Path.Combine(directory.FullName, AspireConfigFile.FileName);
            await ConfigurationService.SetConfigurationInFileAsync(path, "docs.custom", directory.Name);
            originals.Add(path, await File.ReadAllTextAsync(path));
        }
        var operations = new ConcurrentQueue<ConfigSource>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        async Task RecordAsync(ConfigSource source, CancellationToken token)
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            try
            {
                operations.Enqueue(source);
                if (operations.Count == 1)
                {
                    started.SetResult();
                    await release.Task.WaitAsync(token);
                }
                await Task.Yield();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = async (source, token) =>
            {
                await RecordAsync(source, token);
                return ("version-2", new JsonObject { ["docs"] = new JsonObject { ["llmsTxtUrl"] = "https://updated.example/llms.txt" } });
            },
            InspectConfiguration = async (source, token) =>
            {
                await RecordAsync(source, token);
                return "version-2";
            }
        };
        var interaction = new TestInteractionService();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction, globalSettingsFile: globalFile);

        var pending = service.CheckProjectAsync(project, force, CancellationToken.None);
        await started.Task.DefaultTimeout();
        try
        {
            Assert.Single(operations);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.SetResult();
        }
        await pending.DefaultTimeout();
        service.NotifyIfUpdateAvailable();

        var expectedSources = difference == "same" ? new[] { s_appliedSource } : new[] { s_appliedSource, globalSource };
        Assert.Equal(expectedSources.Length, operations.Count);
        Assert.True(expectedSources.Zip(operations).All(pair => ConfigSource.HasSameIdentity(pair.First, pair.Second)));
        Assert.Equal(force ? expectedSources.Length : 0, git.Calls);
        Assert.Equal(force ? 0 : expectedSources.Length, git.CheckCalls);
        foreach (var directory in new[] { project, global, parent, workspace.WorkspaceRoot })
        {
            var path = Path.Combine(directory.FullName, AspireConfigFile.FileName);
            if (force && (directory == project || directory == global))
            {
                var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                Assert.Equal("version-2", configuration["configSource"]!["appliedVersion"]!.GetValue<string>());
                Assert.Equal(directory.Name, configuration["docs"]!["custom"]!.GetValue<string>());
                Assert.Equal("https://updated.example/llms.txt", configuration["docs"]!["llmsTxtUrl"]!.GetValue<string>());
            }
            else
            {
                Assert.Equal(originals[path], await File.ReadAllTextAsync(path));
            }
        }
        if (!force)
        {
            Assert.Equal(ConfigSourceStrings.UpdateAvailable, Assert.Single(interaction.DisplayedMessages).Message);
        }
        Assert.Equal(siblingConfiguration, await File.ReadAllTextAsync(siblingPath));
        Assert.False(File.Exists(Path.Combine(gap.FullName, AspireConfigFile.FileName)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CheckProject_GlobalFailurePreservesEarlierLocalUpdates(bool force, bool cancel)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var parent = workspace.CreateDirectory("parent");
        var project = parent.CreateSubdirectory("project");
        var global = workspace.CreateDirectory("global");
        var globalFile = new FileInfo(Path.Combine(global.FullName, AspireConfigFile.FileName));
        foreach (var directory in new[] { project, global, parent, workspace.WorkspaceRoot })
        {
            await TestConfigSourceServices.SaveBindingAsync(new ConfigSource
            {
                Repository = s_appliedSource.Repository,
                Path = $"{directory.Name}.json",
                AppliedVersion = "version-1"
            }, directory, CancellationToken.None);
        }
        var originalGlobal = await File.ReadAllTextAsync(globalFile.FullName);
        var parentPath = Path.Combine(parent.FullName, AspireConfigFile.FileName);
        var rootPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var originalParent = await File.ReadAllTextAsync(parentPath);
        var originalRoot = await File.ReadAllTextAsync(rootPath);
        var operations = new ConcurrentQueue<string>();
        void Record(ConfigSource source)
        {
            operations.Enqueue(source.Path);
            if (source.Path == "global.json")
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
                throw new ConfigSourceException("Source unavailable");
            }
        }
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (source, _) =>
            {
                Record(source);
                return Task.FromResult(("version-2", new JsonObject()));
            },
            InspectConfiguration = (source, _) =>
            {
                Record(source);
                return Task.FromResult("version-2");
            }
        };
        var interaction = new TestInteractionService();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction, globalSettingsFile: globalFile);

        if (cancel)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckProjectAsync(project, force, cancellation.Token));
        }
        else if (force)
        {
            var error = await Assert.ThrowsAsync<ConfigSourceException>(() => service.CheckProjectAsync(project, force, cancellation.Token));
            Assert.Equal("Source unavailable", error.Message);
        }
        else
        {
            await service.CheckProjectAsync(project, force, cancellation.Token);
            service.NotifyIfUpdateAvailable();
            Assert.Collection(interaction.DisplayedMessages,
                message => Assert.Equal(ConfigSourceStrings.UpdateAvailable, message.Message),
                message => Assert.Contains("Source unavailable", message.Message));
        }

        Assert.Equal(["project.json", "global.json"], operations);
        Assert.Equal(force ? "version-2" : "version-1", AspireConfigFile.Load(project.FullName)!.ConfigSource!.AppliedVersion);
        Assert.Equal(originalGlobal, await File.ReadAllTextAsync(globalFile.FullName));
        Assert.Equal(originalParent, await File.ReadAllTextAsync(parentPath));
        Assert.Equal(originalRoot, await File.ReadAllTextAsync(rootPath));
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(23, 59, 2)]
    [InlineData(24, 0, 3)]
    public async Task CheckProject_SharedSourceRevalidatesCacheAgainstAllAppliedVersions(int hours, int minutes, int expectedChecks)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var project = workspace.CreateDirectory("project");
        var global = workspace.CreateDirectory("global");
        var globalFile = new FileInfo(Path.Combine(global.FullName, AspireConfigFile.FileName));
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, project, CancellationToken.None);
        var git = new TestConfigSourceRepository();
        var context = workspace.CreateExecutionContext();
        var clock = new AdjustableTimeProvider();
        using (var first = TestConfigSourceServices.Create(context, git: git, clock: clock, globalSettingsFile: globalFile))
        {
            await first.CheckProjectAsync(project, force: false, CancellationToken.None);
        }
        await TestConfigSourceServices.SaveBindingAsync(new ConfigSource
        {
            Repository = s_appliedSource.Repository,
            Path = s_appliedSource.Path,
            Ref = s_appliedSource.Ref,
            AppliedVersion = "version-2"
        }, global, CancellationToken.None);
        git.InspectConfiguration = (_, _) => Task.FromResult("version-3");
        var interaction = new TestInteractionService();
        using (var service = TestConfigSourceServices.Create(context, git: git, clock: clock, interaction: interaction, globalSettingsFile: globalFile))
        {
            await service.CheckProjectAsync(project, force: false, CancellationToken.None);
            service.NotifyIfUpdateAvailable();
        }

        Assert.Equal(2, git.CheckCalls);
        Assert.Equal(ConfigSourceStrings.UpdateAvailable, Assert.Single(interaction.DisplayedMessages).Message);

        clock.UtcNow += TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        interaction = new TestInteractionService();
        using var next = TestConfigSourceServices.Create(context, git: git, clock: clock, interaction: interaction, globalSettingsFile: globalFile);

        await next.CheckProjectAsync(project, force: false, CancellationToken.None);
        next.NotifyIfUpdateAvailable();

        Assert.Equal(expectedChecks, git.CheckCalls);
        Assert.Equal(ConfigSourceStrings.UpdateAvailable, Assert.Single(interaction.DisplayedMessages).Message);
    }

    [Fact]
    public async Task BackgroundCheck_DoesNotBlockAndShutdownCancelsAndJoins()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interaction = new TestInteractionService();
        var git = new TestConfigSourceRepository
        {
            InspectConfiguration = async (_, token) =>
            {
                started.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "version-2";
                }
                finally
                {
                    finished.SetResult();
                }
            }
        };
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction);

        service.CheckForConfigSourceUpdates(workspace.WorkspaceRoot, CancellationToken.None);
        await started.Task.DefaultTimeout();
        Assert.False(finished.Task.IsCompleted);

        await service.StopChecksAsync().DefaultTimeout();

        Assert.True(finished.Task.IsCompletedSuccessfully);
        using var stoppedService = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);
        await stoppedService.StopChecksAsync().DefaultTimeout();
        stoppedService.CheckForConfigSourceUpdates(workspace.WorkspaceRoot, CancellationToken.None);
        await stoppedService.WaitForChecksAsync().DefaultTimeout();
        service.NotifyIfUpdateAvailable();
        Assert.Empty(interaction.DisplayedMessages);
        Assert.Equal(1, git.CheckCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundCheck_UnexpectedFailuresAreObserved(bool cancel)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var git = new TestConfigSourceRepository
        {
            InspectConfiguration = (_, token) =>
            {
                if (cancel)
                {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                throw new InvalidOperationException("Unexpected failure");
            }
        };
        var interaction = new TestInteractionService();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction);

        service.CheckForConfigSourceUpdates(workspace.WorkspaceRoot, cancellation.Token);
        await service.WaitForChecksAsync().DefaultTimeout();
        await service.StopChecksAsync().DefaultTimeout();
        service.NotifyIfUpdateAvailable();

        Assert.Equal(1, git.CheckCalls);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task BackgroundCheck_RepeatedCallsForSameProjectShareOneTask()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var git = new TestConfigSourceRepository();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        service.CheckForConfigSourceUpdates(workspace.WorkspaceRoot, CancellationToken.None);
        var check = service.WaitForChecksAsync();
        service.CheckForConfigSourceUpdates(
            new DirectoryInfo(workspace.Path + Path.DirectorySeparatorChar), CancellationToken.None);
        Assert.Same(check, service.WaitForChecksAsync());
        await check.DefaultTimeout();

        service.CheckForConfigSourceUpdates(workspace.WorkspaceRoot, CancellationToken.None);
        Assert.Same(check, service.WaitForChecksAsync());
        Assert.Equal(1, git.CheckCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundCheck_DifferentProjectIsRejectedBeforeScheduling(bool bound)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        if (bound)
        {
            await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        }
        var other = workspace.CreateDirectory("other-app");
        var git = new TestConfigSourceRepository();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        service.CheckForConfigSourceUpdates(workspace.WorkspaceRoot, CancellationToken.None);
        var error = Assert.Throws<InvalidOperationException>(() =>
            service.CheckForConfigSourceUpdates(other, CancellationToken.None));
        await service.WaitForChecksAsync().DefaultTimeout();

        Assert.Equal("A configuration source service instance cannot be used for multiple projects.", error.Message);
        Assert.Equal(bound ? 1 : 0, git.CheckCalls);
    }

    [Fact]
    public async Task Materialize_DifferentProjectIsRejectedBeforeAcquisition()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);
        await service.MaterializeAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var other = workspace.CreateDirectory("other-app");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MaterializeAsync(s_source, other, CancellationToken.None));

        Assert.Equal("A configuration source service instance cannot be used for multiple projects.", error.Message);
        Assert.Equal(1, git.Calls);
        Assert.False(File.Exists(Path.Combine(other.FullName, AspireConfigFile.FileName)));
    }

    private static readonly ConfigSource s_source = new()
    {
        Repository = "https://git.example/organization/config",
        Path = "teams/platform.json"
    };
    private static readonly ConfigSource s_appliedSource = new()
    {
        Repository = s_source.Repository,
        Path = s_source.Path,
        AppliedVersion = "version-1"
    };

    [Theory]
    [InlineData(false, "\n\n")]
    [InlineData(false, "\r\n\r\n")]
    [InlineData(true, "\n\n")]
    [InlineData(true, "\r\n\r\n")]
    public async Task CheckProject_AllowsCommentsTrailingCommasAndNewlines(bool update, string trailingNewlines)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var content = $$"""
            {
                // This configuration is maintained by the user.
                "configSource": {
                    "repository": "{{s_source.Repository}}",
                    "path": "{{s_source.Path}}",
                    "appliedVersion": "version-1",
                },
                /* Keep local settings when applying a source update. */
                "docs": {
                    "llmsTxtUrl": "https://local.example/llms.txt",
                },
                "custom": "preserved",
            }
            """ + trailingNewlines;
        await File.WriteAllTextAsync(path, content);
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-2", new JsonObject
            {
                ["docs"] = new JsonObject { ["llmsTxtUrl"] = "https://shared.example/llms.txt" }
            }))
        };
        var interaction = new TestInteractionService();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction);

        Assert.Equivalent(s_appliedSource, await service.ReadBindingAsync(path, CancellationToken.None), strict: true);
        await service.CheckProjectAsync(workspace.WorkspaceRoot, force: update, CancellationToken.None);
        service.NotifyIfUpdateAvailable();

        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path), documentOptions: ConfigurationHelper.ParseOptions)!;
        Assert.Equal("preserved", configuration["custom"]!.GetValue<string>());
        Assert.Equal(update ? "https://shared.example/llms.txt" : "https://local.example/llms.txt",
            configuration["docs"]!["llmsTxtUrl"]!.GetValue<string>());
        Assert.Equal(update ? "version-2" : "version-1",
            configuration["configSource"]!["appliedVersion"]!.GetValue<string>());
        Assert.Equal(update ? 1 : 0, git.Calls);
        Assert.Equal(update ? 0 : 1, git.CheckCalls);
        if (!update)
        {
            Assert.Equal(content, await File.ReadAllTextAsync(path));
            Assert.Empty(interaction.DisplayedMessages);
        }
    }

    [Theory]
    [InlineData("https://git.example/organization/config", "teams/platform.json", null, "version-1", true)]
    [InlineData("https://git.example/organization/other", "teams/platform.json", null, "version-1", false)]
    [InlineData("https://git.example/organization/config", "other.json", null, "version-1", false)]
    [InlineData("https://git.example/organization/config", "teams/platform.json", "preview", "version-1", false)]
    [InlineData("https://git.example/organization/config", "teams/platform.json", null, "version-2", true)]
    [InlineData("https://git.example/organization/config", "teams/platform.json", null, null, true)]
    public void HasSameIdentity_UsesRepositoryPathAndRefButNotAppliedVersion(
        string repository, string path, string? gitRef, string? appliedVersion, bool equal)
    {
        var source = new ConfigSource
        {
            Repository = repository,
            Path = path,
            Ref = gitRef,
            AppliedVersion = appliedVersion
        };

        Assert.Equal(equal, ConfigSource.HasSameIdentity(s_appliedSource, source));
        Assert.Equal(equal, ConfigSource.HasSameIdentity(source, s_appliedSource));
    }

    [Theory]
    [InlineData("https://git.example/organization/config")]
    [InlineData("ssh://git@git.example/organization/config")]
    [InlineData("git@git.example:organization/config")]
    public void Validate_AcceptsGitAddresses(string repository)
    {
        new ConfigSource { Repository = repository, Path = s_source.Path }.Validate();
    }

    [Fact]
    public void Validate_AcceptsAbsoluteLocalRepositoryWithSpaces()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var repository = workspace.CreateDirectory("local repository");

        new ConfigSource { Repository = repository.FullName, Path = s_source.Path }.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("local/repo")]
    [InlineData("./local/repo")]
    [InlineData("C:local\\repo")]
    [InlineData("file:///local/repo")]
    [InlineData("https://user:password@git.example/repo")]
    [InlineData("https://git.example/repo?token=example")]
    [InlineData("https://git.example/repo#fragment")]
    [InlineData("git@git.example:repo?token=example")]
    [InlineData("git@git.example:repo#fragment")]
    [InlineData("ext::arbitrary-command")]
    [InlineData("--upload-pack=command")]
    [InlineData("ssh://git:password@git.example/repo")]
    public void Validate_RejectsInvalidRepositories(string repository)
    {
        Assert.Throws<ConfigSourceException>(() => new ConfigSource { Repository = repository, Path = s_source.Path }.Validate());
    }

    [Fact]
    public void Validate_RejectsControlsInAbsoluteLocalRepository()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var repository = System.IO.Path.Combine(workspace.Path, "local\nrepository");

        Assert.Throws<ConfigSourceException>(() => new ConfigSource { Repository = repository, Path = s_source.Path }.Validate());
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute.json")]
    [InlineData("../escape.json")]
    [InlineData("teams/../../escape.json")]
    [InlineData("teams\\platform.json")]
    [InlineData("teams//platform.json")]
    [InlineData("./platform.json")]
    [InlineData("C:/platform.json")]
    public void Validate_RejectsInvalidPaths(string path)
    {
        Assert.Throws<ConfigSourceException>(() => new ConfigSource { Repository = s_source.Repository, Path = path }.Validate());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("main")]
    [InlineData("teams/preview")]
    [InlineData("refs/heads/teams/preview")]
    [InlineData("0123456789abcdef0123456789abcdef01234567")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123")]
    public void Validate_AcceptsBranchOrFullCommit(string? reference)
    {
        new ConfigSource { Repository = s_source.Repository, Path = s_source.Path, Ref = reference }.Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("--upload-pack=command")]
    [InlineData("HEAD")]
    [InlineData("main~1")]
    [InlineData("main:other")]
    [InlineData("main..other")]
    [InlineData("main@{1}")]
    [InlineData("refs/tags/release")]
    [InlineData("branch.lock")]
    [InlineData("branch//child")]
    [InlineData(".hidden/branch")]
    [InlineData("branch\nother")]
    public void Validate_RejectsInvalidRefs(string reference)
    {
        Assert.Equal(ConfigSourceStrings.InvalidRef,
            Assert.Throws<ConfigSourceException>(() => new ConfigSource { Repository = s_source.Repository, Path = s_source.Path, Ref = reference }.Validate()).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectForCreation_ExplicitSourceBypassesDetection(bool detected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var detector = new TestConfigSourceDetector { IsInternal = detected };
        var git = new TestConfigSourceRepository();
        var expected = new ConfigSource { Repository = s_source.Repository, Path = s_source.Path, Ref = "preview" };
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, detector: detector,
            environment: new TestEnvironment(new Dictionary<string, string?>
            {
                [CliConfigNames.InitSourceUrl] = s_source.Repository,
                [CliConfigNames.InitSourcePath] = s_source.Path,
                [CliConfigNames.InitSourceRef] = expected.Ref
            }));

        Assert.Equivalent(expected, await service.ResolveAndAcquireConfigSourceAsync(ParseConsent(true), CancellationToken.None), strict: true);
        Assert.Equal(0, detector.Calls);
        Assert.Equal(1, git.Calls);
        Assert.False(File.Exists(Path.Combine(workspace.Path, AspireConfigFile.FileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectForCreation_PathAndRefWithoutExplicitUrlAreIgnored(bool detected)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git,
            detector: new TestConfigSourceDetector { IsInternal = detected },
            environment: new TestEnvironment(new Dictionary<string, string?>
            {
                [CliConfigNames.InitSourcePath] = "../invalid.json",
                [CliConfigNames.InitSourceRef] = "--invalid",
                ["ASPIRE_INIT_SOURCE"] = "invalid-legacy-variable",
                ["ASPIRE_INIT_CONFIG"] = "invalid-legacy-variable"
            }));

        var selected = await service.ResolveAndAcquireConfigSourceAsync(ParseConsent(detected ? true : null), CancellationToken.None);

        Assert.Equivalent(detected ? new ConfigSource { Repository = ConfigSourceService.MicrosoftRepository, Path = AspireConfigFile.FileName } : null,
            selected, strict: true);
        Assert.Equal(detected ? 1 : 0, git.Calls);
    }

    [Theory]
    [InlineData(InternalMicrosoftDetectorOutcome.Failed)]
    [InlineData(InternalMicrosoftDetectorOutcome.TimedOut)]
    public async Task SelectForCreation_InconclusiveDetectionDoesNotProposeSource(string outcome)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git,
            detector: new TestConfigSourceDetector { IsInternal = true, Outcome = outcome });

        Assert.Null(await service.ResolveAndAcquireConfigSourceAsync(ParseConsent(null), CancellationToken.None));
        Assert.Equal(0, git.Calls);
    }

    [Fact]
    public async Task SelectForCreation_NoCandidateErrorIncludesSourceEnvironmentVariable()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        var error = await Assert.ThrowsAsync<ConfigSourceException>(() =>
            service.ResolveAndAcquireConfigSourceAsync(ParseConsent(true), CancellationToken.None));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, ConfigSourceStrings.NoCandidate, CliConfigNames.InitSourceUrl), error.Message);
        Assert.Equal(0, git.Calls);
    }

    [Fact]
    public async Task SelectForCreation_NonInteractiveRequiresConsent()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(),
            detector: new TestConfigSourceDetector { IsInternal = true });

        Assert.Equal(ConfigSourceStrings.ConsentRequired, (await Assert.ThrowsAsync<ConfigSourceException>(() =>
            service.ResolveAndAcquireConfigSourceAsync(ParseConsent(null), CancellationToken.None))).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectForCreation_InteractiveChoiceControlsAcquisition(bool accepted)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository();
        var interaction = new TestInteractionService
        {
            PromptForSelectionCallback = (_, choices, _, _) =>
            {
                Assert.Equal([ConfigSourceStrings.Accept, ConfigSourceStrings.Decline], choices.Cast<string>());
                return accepted ? ConfigSourceStrings.Accept : ConfigSourceStrings.Decline;
            }
        };
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git,
            detector: new TestConfigSourceDetector { IsInternal = true }, interaction: interaction, interactive: true);

        Assert.Equivalent(accepted ? new ConfigSource { Repository = ConfigSourceService.MicrosoftRepository, Path = AspireConfigFile.FileName } : null,
            await service.ResolveAndAcquireConfigSourceAsync(ParseConsent(null), CancellationToken.None), strict: true);
        Assert.Equal(accepted ? 1 : 0, git.Calls);
    }

    [Fact]
    public async Task SelectForCreation_CancelledPromptDoesNotAcquireOrBind()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository();
        var interaction = new TestInteractionService();
        interaction.SetupCancellationResponse();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git,
            detector: new TestConfigSourceDetector { IsInternal = true }, interaction: interaction, interactive: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ResolveAndAcquireConfigSourceAsync(ParseConsent(null), CancellationToken.None));
        Assert.Equal(0, git.Calls);
        Assert.False(File.Exists(Path.Combine(workspace.Path, AspireConfigFile.FileName)));
    }

    [Fact]
    public async Task Materialize_PreservesProjectSettingsAndCopiesOnlyAllowedLeaves()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var project = workspace.CreateDirectory("created-project");
        var path = Path.Combine(project.FullName, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(path, """
            {"appHost":{"path":"apphost.mts"},"sdk":{"version":"17.0.0"},"profiles":{"default":{"applicationUrl":"https://localhost:1234"}},"docs":{"llmsTxtUrl":"https://local.example/old","custom":"local"},"custom":"local"}
            """);
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-1", JsonNode.Parse("""
                {"DOCS":{"LLMSTXTURL":"https://docs.example/llms.txt","API":{"SITEMAPURL":null,"excluded":"remote"}},"sdk":{"version":"99.0.0"},"features":{"remote":true},"custom":"remote"}
                """)!.AsObject()))
        };
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        await service.MaterializeAsync(s_source, project, CancellationToken.None);

        Assert.Equivalent(s_appliedSource, AspireConfigFile.Load(project.FullName)!.ConfigSource, strict: true);
        Assert.Equal(1, git.Calls);
        await Verify(await File.ReadAllTextAsync(path), "json");
    }

    [Fact]
    public async Task Update_ReplacesSuppliedLocalValueAndPreservesOmittedValue()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await ConfigurationService.SetConfigurationInFileAsync(path, "docs.llmsTxtUrl", "https://local.example/docs");
        await ConfigurationService.SetConfigurationInFileAsync(path, "docs.api.sitemapUrl", "https://local.example/api");
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-2", JsonNode.Parse("""
                {"docs:llmsTxtUrl":"https://remote.example/docs","docs:arbitrary":"excluded"}
                """)!.AsObject()))
        };
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        await service.CheckProjectAsync(workspace.WorkspaceRoot, force: true, CancellationToken.None);

        var config = AspireConfigFile.Load(workspace.Path)!;
        Assert.Equal("https://remote.example/docs", config.Docs!.LlmsTxtUrl);
        Assert.Equal("https://local.example/api", config.Docs.Api!.SitemapUrl);
        Assert.Equal("version-2", config.ConfigSource!.AppliedVersion);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        Assert.Equal(["configSource", "docs"], settings.Select(p => p.Key));
        Assert.Equal(["llmsTxtUrl", "api"], settings["docs"]!.AsObject().Select(p => p.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_PreservesNestedLiteralKeys(bool flattenedSource)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        configuration["docs"] = JsonNode.Parse("""
            {"api":{"sitemapUrl":"https://local.example/sitemap.xml"},"custom":"preserved","custom:key":"literal"}
            """);
        configuration["profiles"] = JsonNode.Parse("""
            {"dev:local":{"environmentVariables":{"Logging:LogLevel:Default":"Debug"}}}
            """);
        configuration["custom:setting"] = "root-flat";
        await File.WriteAllTextAsync(path, configuration.ToJsonString());
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-2", JsonNode.Parse(flattenedSource
                ? """{"docs":{"api:sitemapUrl":"https://remote.example/sitemap.xml"}}"""
                : """{"docs":{"api":{"sitemapUrl":"https://remote.example/sitemap.xml"}}}""")!.AsObject()))
        };
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        await service.CheckProjectAsync(workspace.WorkspaceRoot, force: true, CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var parsed = new ConfigurationManager();
        parsed.AddJsonStream(stream);
        Assert.Equal(flattenedSource ? "https://local.example/sitemap.xml" : "https://remote.example/sitemap.xml",
            parsed["docs:api:sitemapUrl"]);
        Assert.Equal("preserved", parsed["docs:custom"]);
        Assert.Equal("version-2", parsed["configSource:appliedVersion"]);
        var config = AspireConfigFile.Load(workspace.Path)!;
        Assert.Equal(new Dictionary<string, string> { ["Logging:LogLevel:Default"] = "Debug" },
            Assert.Single(config.Profiles!).Value.EnvironmentVariables);
        await Verify(content, "json").UseParameters(flattenedSource);
    }

    [Fact]
    public async Task Update_RejectsCollisionWithNestedLiteralKeyWithoutWriting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        configuration["docs"] = JsonNode.Parse("""{"api:sitemapUrl":"https://local.example/sitemap.xml"}""");
        var original = configuration.ToJsonString();
        await File.WriteAllTextAsync(path, original);
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-2", JsonNode.Parse("""
                {"docs":{"api":{"sitemapUrl":"https://remote.example/sitemap.xml"}}}
                """)!.AsObject()))
        };
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        await Assert.ThrowsAsync<ConfigSourceException>(() =>
            service.CheckProjectAsync(workspace.WorkspaceRoot, force: true, CancellationToken.None));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Update_ReplacesLeavesInTheirExistingCaseInsensitiveSections()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        configuration["DOCS"] = JsonNode.Parse("""
            {"llmsTxtUrl":"https://local.example/llms.txt","API":{"custom":"preserved"}}
            """);
        configuration["docs"] = JsonNode.Parse("""
            {"api":{"SITEMAPURL":"https://local.example/sitemap.xml"}}
            """);
        await File.WriteAllTextAsync(path, configuration.ToJsonString());
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-2", JsonNode.Parse("""
                {"DOCS":{"llmsTxtUrl":"https://remote.example/llms.txt"},"docs":{"api":{"sitemapUrl":null,"excluded":"remote"}}}
                """)!.AsObject()))
        };
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        await service.CheckProjectAsync(workspace.WorkspaceRoot, force: true, CancellationToken.None);

        var content = await File.ReadAllTextAsync(path);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));
        using var parsed = new ConfigurationManager();
        parsed.AddJsonStream(stream);
        Assert.Equal("https://remote.example/llms.txt", parsed["docs:llmsTxtUrl"]);
        Assert.Null(parsed["docs:api:sitemapUrl"]);
        Assert.Equal("preserved", parsed["docs:api:custom"]);
        await Verify(content, "json");
    }

    [Theory]
    [InlineData(23, 59, 1)]
    [InlineData(24, 0, 2)]
    [InlineData(25, 0, 2)]
    public async Task CheckProject_ChecksAtExact24HourBoundary(int hours, int minutes, int expectedChecks)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var context = workspace.CreateExecutionContext();
        var clock = new AdjustableTimeProvider();
        var git = new TestConfigSourceRepository();
        await TestConfigSourceServices.Create(context, git: git, clock: clock).CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        clock.UtcNow += TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);

        await TestConfigSourceServices.Create(context, git: git, clock: clock).CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);

        Assert.Equal(expectedChecks, git.CheckCalls);
        Assert.Equal(0, git.Calls);
    }

    [Fact]
    public async Task CheckProject_NewVersionOnlyWritesCheckMetadataAndReportsOnce()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var previous = await File.ReadAllTextAsync(path);
        var git = new TestConfigSourceRepository { InspectConfiguration = (_, _) => Task.FromResult("version-2") };
        var interaction = new TestInteractionService();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction);

        await service.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        await service.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);

        Assert.Empty(interaction.DisplayedMessages);
        service.NotifyIfUpdateAvailable();

        Assert.Equal(previous, await File.ReadAllTextAsync(path));
        Assert.Equal(0, git.Calls);
        Assert.Equal(1, git.CheckCalls);
        var notice = Assert.Single(interaction.DisplayedMessages);
        Assert.Equal(ConfigSourceStrings.UpdateAvailable, notice.Message);
        Assert.Equal(ConsoleOutput.Error, notice.ConsoleOverride);
        service.NotifyIfUpdateAvailable();
        Assert.Single(interaction.DisplayedMessages);
        Assert.Equal(["check.json"], Directory.GetFiles(service.GetCacheDirectory(s_source)).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(service.GetCacheDirectory(s_source)));
    }

    [Fact]
    public async Task CheckProject_CachedAvailabilityNoticeDoesNotRepeatNetworkCheck()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var context = workspace.CreateExecutionContext();
        var git = new TestConfigSourceRepository { InspectConfiguration = (_, _) => Task.FromResult("version-2") };
        await TestConfigSourceServices.Create(context, git: git).CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        var interaction = new TestInteractionService();

        var service = TestConfigSourceServices.Create(context, git: git, interaction: interaction);
        await service.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        service.NotifyIfUpdateAvailable();

        Assert.Equal(1, git.CheckCalls);
        Assert.Equal(ConfigSourceStrings.UpdateAvailable, Assert.Single(interaction.DisplayedMessages).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckProject_LegacyCheckMetadataRevalidatesMismatchesOnce(bool matchesObservedVersion)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var context = workspace.CreateExecutionContext();
        var clock = new AdjustableTimeProvider();
        var git = new TestConfigSourceRepository { InspectConfiguration = (_, _) => Task.FromResult("version-2") };
        using var first = TestConfigSourceServices.Create(context, git: git, clock: clock);
        await first.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        var checkPath = Path.Combine(first.GetCacheDirectory(s_source), "check.json");
        var tracking = JsonNode.Parse(await File.ReadAllTextAsync(checkPath))!.AsObject();
        tracking.Remove("comparedAppliedVersions");
        tracking["comparedAppliedVersion"] = s_appliedSource.AppliedVersion;
        await File.WriteAllTextAsync(checkPath, tracking.ToJsonString());
        if (matchesObservedVersion)
        {
            await TestConfigSourceServices.SaveBindingAsync(new ConfigSource
            {
                Repository = s_appliedSource.Repository,
                Path = s_appliedSource.Path,
                Ref = s_appliedSource.Ref,
                AppliedVersion = "version-2"
            }, workspace.WorkspaceRoot, CancellationToken.None);
        }

        for (var invocation = 0; invocation < 2; invocation++)
        {
            var interaction = new TestInteractionService();
            using var service = TestConfigSourceServices.Create(context, git: git, clock: clock, interaction: interaction);
            await service.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
            service.NotifyIfUpdateAvailable();

            if (matchesObservedVersion)
            {
                Assert.Empty(interaction.DisplayedMessages);
            }
            else
            {
                Assert.Equal(ConfigSourceStrings.UpdateAvailable, Assert.Single(interaction.DisplayedMessages).Message);
            }
        }

        Assert.Equal(matchesObservedVersion ? 1 : 2, git.CheckCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckProject_MatchingFileVersionDoesNotNotify(bool revalidateCachedObservation)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var context = workspace.CreateExecutionContext();
        var git = new TestConfigSourceRepository();
        if (revalidateCachedObservation)
        {
            using var first = TestConfigSourceServices.Create(context, git: git);
            await first.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
            await TestConfigSourceServices.SaveBindingAsync(
                new ConfigSource { Repository = s_source.Repository, Path = s_source.Path, AppliedVersion = "version-2" },
                workspace.WorkspaceRoot, CancellationToken.None);
        }
        git.Commit = "unrelated-commit";
        git.InspectConfiguration = (_, _) => Task.FromResult(revalidateCachedObservation ? "version-2" : "version-1");
        var interaction = new TestInteractionService();
        using var service = TestConfigSourceServices.Create(context, git: git, interaction: interaction);
        await service.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        service.NotifyIfUpdateAvailable();

        Assert.Equal(revalidateCachedObservation ? 2 : 1, git.CheckCalls);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{")]
    [InlineData("{}")]
    public async Task CheckProject_FailureIsReportedAndDoesNotAdvanceCheckTime(string? invalidTracking)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var context = workspace.CreateExecutionContext();
        var clock = new AdjustableTimeProvider();
        var git = new TestConfigSourceRepository();
        var first = TestConfigSourceServices.Create(context, git: git, clock: clock);
        await first.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        var checkPath = Path.Combine(first.GetCacheDirectory(s_source), "check.json");
        if (invalidTracking is not null)
        {
            await File.WriteAllTextAsync(checkPath, invalidTracking);
        }
        var original = await File.ReadAllTextAsync(checkPath);
        clock.UtcNow += TimeSpan.FromDays(1);
        git.InspectConfiguration = (_, _) => throw new ConfigSourceException(ConfigSourceStrings.AuthenticationFailed);
        var interaction = new TestInteractionService();
        var next = TestConfigSourceServices.Create(context, git: git, clock: clock, interaction: interaction);

        await next.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        await next.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        next.NotifyIfUpdateAvailable();

        Assert.Equal(original, await File.ReadAllTextAsync(checkPath));
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, ConfigSourceStrings.CheckFailed,
            invalidTracking is not null ? ConfigSourceStrings.InvalidCache : ConfigSourceStrings.AuthenticationFailed),
            Assert.Single(interaction.DisplayedMessages).Message);
        Assert.Equal(0, git.Calls);
        Assert.Equal(invalidTracking is not null ? 1 : 2, git.CheckCalls);
    }

    [Fact]
    public async Task CheckProject_DoesNotReadRetainedConfigurationOrPointer()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = workspace.CreateExecutionContext();
        var clock = new AdjustableTimeProvider();
        using var first = TestConfigSourceServices.Create(context, clock: clock);
        await first.MaterializeAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        clock.UtcNow += TimeSpan.FromDays(1);
        var directory = first.GetCacheDirectory(s_source);
        var snapshot = Directory.GetDirectories(directory).Single();
        await File.WriteAllTextAsync(Path.Combine(directory, "current"), "invalid pointer");
        using var heldFile = File.Open(Path.Combine(snapshot, AspireConfigFile.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var interaction = new TestInteractionService();
        var git = new TestConfigSourceRepository();

        using var service = TestConfigSourceServices.Create(context, git: git, clock: clock, interaction: interaction);
        await service.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        service.NotifyIfUpdateAvailable();

        Assert.Equal(0, git.Calls);
        Assert.Equal(1, git.CheckCalls);
        Assert.Empty(interaction.DisplayedMessages);
    }

    [Fact]
    public async Task Acquire_ReusesSnapshotForSameIdentityWithDifferentAppliedVersions()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        var first = await service.AcquireAsync(s_source, force: false, CancellationToken.None);
        var second = await service.AcquireAsync(s_appliedSource, force: false, CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(1, git.Calls);
        Assert.True(ConfigSource.HasSameIdentity(s_appliedSource, second.Metadata.Source));
        Assert.Null(second.Metadata.Source.AppliedVersion);
    }

    [Fact]
    public async Task CheckProject_DifferentProjectIsRejected()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var other = workspace.CreateDirectory("other-app");
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        await TestConfigSourceServices.SaveBindingAsync(new()
        {
            Repository = s_appliedSource.Repository,
            Path = s_appliedSource.Path,
            AppliedVersion = "version-2"
        }, other, CancellationToken.None);
        var git = new TestConfigSourceRepository { InspectConfiguration = (_, _) => Task.FromResult("version-3") };
        var interaction = new TestInteractionService();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction);

        await service.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CheckProjectAsync(other, false, CancellationToken.None));
        service.NotifyIfUpdateAvailable();

        Assert.Equal("A configuration source service instance cannot be used for multiple projects.", error.Message);
        Assert.Equal(1, git.CheckCalls);
        Assert.Equal(ConfigSourceStrings.UpdateAvailable, Assert.Single(interaction.DisplayedMessages).Message);
    }

    [Fact]
    public async Task CheckProject_ConcurrentInvocationsShareOneVersionCheck()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var context = workspace.CreateExecutionContext();
        var git = new TestConfigSourceRepository();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            TestConfigSourceServices.Create(context, git: git).CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None))).DefaultTimeout();

        Assert.Equal(1, git.CheckCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_FailedOrCancelledAcquisitionPreservesAppAndFetchedSnapshot(bool cancel)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var git = new TestConfigSourceRepository();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);
        await service.MaterializeAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var app = await File.ReadAllTextAsync(path);
        var pointer = Path.Combine(service.GetCacheDirectory(s_source), "current");
        var previousPointer = await File.ReadAllTextAsync(pointer);
        git.ReadConfiguration = (_, token) =>
        {
            if (cancel)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            throw new ConfigSourceException("Test acquisition failed");
        };

        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => service.CheckProjectAsync(workspace.WorkspaceRoot, true, cancellation.Token));
        }
        else
        {
            await Assert.ThrowsAsync<ConfigSourceException>(() => service.CheckProjectAsync(workspace.WorkspaceRoot, true, CancellationToken.None));
        }
        Assert.Equal(app, await File.ReadAllTextAsync(path));
        Assert.Equal(previousPointer, await File.ReadAllTextAsync(pointer));
        Assert.Single(Directory.GetDirectories(service.GetCacheDirectory(s_source)));
    }

    [Fact]
    public async Task Materialize_WaitsForWorkspaceWriterAndPreservesInterveningEdits()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = workspace.CreateExecutionContext();
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(path, "{}");
        var fetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) =>
            {
                fetched.SetResult();
                return Task.FromResult(("version-1", new JsonObject
                {
                    ["docs"] = new JsonObject { ["llmsTxtUrl"] = "https://docs.example/llms.txt" }
                }));
            }
        };
        var service = TestConfigSourceServices.Create(context, git: git);
        Task materialize;
        using (var writerLock = await ConfigurationHelper.TryAcquireWorkspaceConfigLockAsync(
            context, new FileInfo(path), NullLogger.Instance, CancellationToken.None))
        {
            Assert.NotNull(writerLock);
            materialize = service.MaterializeAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
            await fetched.Task.DefaultTimeout();
            Assert.False(materialize.IsCompleted);
            await ConfigurationService.SetConfigurationInFileAsync(path, "custom", "intervening edit");
        }
        await materialize.DefaultTimeout();

        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal("intervening edit", configuration["custom"]!.GetValue<string>());
        Assert.Equal("https://docs.example/llms.txt", configuration["docs"]!["llmsTxtUrl"]!.GetValue<string>());
        Assert.Equivalent(s_appliedSource, AspireConfigFile.Load(workspace.Path)!.ConfigSource, strict: true);
    }

    [Fact]
    public async Task Update_RejectsBindingChangedDuringFetch()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var changed = new ConfigSource
        {
            Repository = s_appliedSource.Repository,
            Path = s_appliedSource.Path,
            Ref = "different-branch",
            AppliedVersion = s_appliedSource.AppliedVersion
        };
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = async (_, token) =>
            {
                await TestConfigSourceServices.SaveBindingAsync(changed, workspace.WorkspaceRoot, token);
                return ("version-2", new JsonObject());
            }
        };
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);

        var error = await Assert.ThrowsAsync<ConfigSourceException>(() =>
            service.CheckProjectAsync(workspace.WorkspaceRoot, true, CancellationToken.None));

        Assert.Equal(ConfigSourceStrings.BindingChanged, error.Message);
        Assert.Equivalent(changed, AspireConfigFile.Load(workspace.Path)!.ConfigSource, strict: true);
    }

    [Fact]
    public async Task Update_TrackingPublicationFailureLeavesAppUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var previous = await File.ReadAllTextAsync(path);
        var git = new TestConfigSourceRepository();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);
        git.ReadConfiguration = (_, _) =>
        {
            Directory.CreateDirectory(Path.Combine(service.GetCacheDirectory(s_source), "current"));
            return Task.FromResult(("version-2", new JsonObject()));
        };

        var error = await Record.ExceptionAsync(() =>
            service.CheckProjectAsync(workspace.WorkspaceRoot, true, CancellationToken.None));

        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(1, git.Calls);
        Assert.Equal(previous, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Update_AppFailureAfterFetchKeepsOldAppliedVersionAndNewFetchedTracking()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var previous = await File.ReadAllTextAsync(path);
        var git = new TestConfigSourceRepository { Commit = "commit-2" };
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git);
        FileStream? appLock = null;
        git.ReadConfiguration = (_, _) =>
        {
            appLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return Task.FromResult(("version-2", new JsonObject()));
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                service.CheckProjectAsync(workspace.WorkspaceRoot, true, CancellationToken.None));
        }
        finally
        {
            appLock?.Dispose();
        }

        Assert.Equal(previous, await File.ReadAllTextAsync(path));
        var cache = service.GetCacheDirectory(s_source);
        var snapshot = await File.ReadAllTextAsync(Path.Combine(cache, "current"));
        var metadata = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(cache, snapshot, "metadata.json")),
            JsonSourceGenerationContext.Default.ConfigSourceMetadata)!;
        Assert.Equal("version-2", metadata.Version);
        Assert.Equal("commit-2", metadata.Commit);
    }

    [Fact]
    public async Task Initialization_ExistingBindingDoesNotRequireConsentOrReapplySettings()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await ConfigurationService.SetConfigurationInFileAsync(path, "docs.llmsTxtUrl", "https://local.example/llms.txt");
        var original = await File.ReadAllTextAsync(path);
        var git = new TestConfigSourceRepository();
        var detector = new TestConfigSourceDetector { IsInternal = true };
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [CliConfigNames.InitSourceUrl] = "https://other.example/configuration"
        });
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git,
            environment: environment, detector: detector);

        Assert.Null(await service.SelectForInitializationAsync(workspace.WorkspaceRoot, ParseConsent(null), CancellationToken.None));
        await service.WaitForChecksAsync().DefaultTimeout();

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(0, detector.Calls);
        Assert.Equal(0, git.Calls);
        Assert.Equal(1, git.CheckCalls);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initialization_DoesNotReuseAncestorBinding(bool unboundModernFile)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        var project = workspace.CreateDirectory("project");
        if (unboundModernFile)
        {
            var modernPath = Path.Combine(project.FullName, AspireConfigFile.FileName);
            await File.WriteAllTextAsync(modernPath, "{}");
        }
        var git = new TestConfigSourceRepository();
        var environment = new TestEnvironment(new Dictionary<string, string?>
        {
            [CliConfigNames.InitSourceUrl] = s_source.Repository,
            [CliConfigNames.InitSourcePath] = s_source.Path,
            [CliConfigNames.InitSourceRef] = s_source.Ref
        });
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, environment: environment);

        var source = await service.SelectForInitializationAsync(project, ParseConsent(true), CancellationToken.None);

        Assert.NotNull(source);
        Assert.True(ConfigSource.HasSameIdentity(s_source, source));
        Assert.Equal(1, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Theory]
    [InlineData("https://git.example/config", "HEAD")]
    [InlineData("file:///local/repository", "main")]
    public async Task Initialization_InvalidSavedBindingReportsFailureWithoutRetargeting(string repository, string gitRef)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var invalid = new ConfigSource { Repository = repository, Path = s_source.Path, Ref = gitRef, AppliedVersion = "version-1" };
        await TestConfigSourceServices.SaveBindingAsync(invalid, workspace.WorkspaceRoot, CancellationToken.None);
        var git = new TestConfigSourceRepository();
        var interaction = new TestInteractionService();
        var detector = new TestConfigSourceDetector { IsInternal = true };
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git,
            detector: detector, interaction: interaction);

        Assert.Null(await service.SelectForInitializationAsync(workspace.WorkspaceRoot, ParseConsent(true), CancellationToken.None));
        await service.WaitForChecksAsync().DefaultTimeout();
        Assert.Empty(interaction.DisplayedMessages);
        service.NotifyIfUpdateAvailable();

        Assert.Equivalent(invalid, AspireConfigFile.Load(workspace.Path)!.ConfigSource, strict: true);
        Assert.Equal(0, detector.Calls);
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.Equal(ConsoleOutput.Error, Assert.Single(interaction.DisplayedMessages).ConsoleOverride);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_ForcesFetchAndRetainsOnlyLatestSnapshot(bool changed)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var context = workspace.CreateExecutionContext();
        var clock = new AdjustableTimeProvider();
        var git = new TestConfigSourceRepository();
        using var first = TestConfigSourceServices.Create(context, git: git, clock: clock);
        await first.MaterializeAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var retained = await first.AcquireAsync(s_source, force: false, CancellationToken.None);
        var cache = first.GetCacheDirectory(s_source);
        var stale = Directory.CreateDirectory(Path.Combine(cache, Guid.NewGuid().ToString("N")));
        await File.WriteAllTextAsync(Path.Combine(stale.FullName, "stale.txt"), "obsolete snapshot");
        File.SetAttributes(Path.Combine(stale.FullName, "stale.txt"), FileAttributes.ReadOnly);
        if (changed)
        {
            git.ReadConfiguration = (_, _) => Task.FromResult(("version-2", new JsonObject()));
        }
        clock.UtcNow += TimeSpan.FromSeconds(1);
        var interaction = new TestInteractionService();
        using var next = TestConfigSourceServices.Create(context, git: git, clock: clock, interaction: interaction);

        await next.CheckProjectAsync(workspace.WorkspaceRoot, true, CancellationToken.None);

        Assert.Contains(changed ? ConfigSourceStrings.Refreshed : ConfigSourceStrings.RefreshedUnchanged,
            Assert.Single(interaction.DisplayedMessages).Message);
        Assert.Equal(2, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        var pointer = await File.ReadAllTextAsync(Path.Combine(next.GetCacheDirectory(s_source), "current"));
        Assert.Equal(Path.Combine(cache, pointer), Assert.Single(Directory.GetDirectories(cache)));
        var metadata = JsonSerializer.Deserialize(await File.ReadAllTextAsync(
            Path.Combine(next.GetCacheDirectory(s_source), pointer, "metadata.json")), JsonSourceGenerationContext.Default.ConfigSourceMetadata)!;
        Assert.Equal(clock.UtcNow, metadata.FetchedAt);
        Assert.Equal(changed ? "version-2" : "version-1", metadata.Version);
        Assert.Equal("version-1", retained.Metadata.Version);
    }

    [Fact]
    public async Task Update_AnotherAppKeepsItsAppliedVersionAndReceivesNotice()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var other = workspace.CreateDirectory("other-app");
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, workspace.WorkspaceRoot, CancellationToken.None);
        await TestConfigSourceServices.SaveBindingAsync(s_appliedSource, other, CancellationToken.None);
        var git = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-2", new JsonObject()))
        };
        var context = workspace.CreateExecutionContext();
        await TestConfigSourceServices.Create(context, git: git).CheckProjectAsync(workspace.WorkspaceRoot, true, CancellationToken.None);
        var interaction = new TestInteractionService();

        var service = TestConfigSourceServices.Create(context, git: git, interaction: interaction);
        await service.CheckProjectAsync(other, false, CancellationToken.None);
        service.NotifyIfUpdateAvailable();

        Assert.Equal("version-1", AspireConfigFile.Load(other.FullName)!.ConfigSource!.AppliedVersion);
        Assert.Equal("version-2", AspireConfigFile.Load(workspace.Path)!.ConfigSource!.AppliedVersion);
        Assert.Equal(ConfigSourceStrings.UpdateAvailable, Assert.Single(interaction.DisplayedMessages).Message);
        Assert.Equal(0, git.CheckCalls);
    }

    [Fact]
    public async Task CheckProject_BindingWithoutAppliedVersionRequiresExplicitUpdate()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var git = new TestConfigSourceRepository();
        var interaction = new TestInteractionService();
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, interaction: interaction);

        await service.CheckProjectAsync(workspace.WorkspaceRoot, false, CancellationToken.None);

        service.NotifyIfUpdateAvailable();
        Assert.Equal(0, git.CheckCalls);
        Assert.Equal(0, git.Calls);
        Assert.Equal(ConfigSourceStrings.AppliedVersionMissing, Assert.Single(interaction.DisplayedMessages).Message);
        await service.CheckProjectAsync(workspace.WorkspaceRoot, true, CancellationToken.None);
        Assert.Equal("version-1", AspireConfigFile.Load(workspace.Path)!.ConfigSource!.AppliedVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckProject_NoBindingDoesNotDetectOrAcquire(bool force)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var git = new TestConfigSourceRepository();
        var detector = new TestConfigSourceDetector { IsInternal = true };
        var interaction = new TestInteractionService();
        using var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: git, detector: detector, interaction: interaction);

        await service.CheckProjectAsync(workspace.WorkspaceRoot, force, CancellationToken.None);

        service.NotifyIfUpdateAvailable();
        Assert.Empty(interaction.DisplayedErrors);
        if (force)
        {
            var notice = Assert.Single(interaction.DisplayedMessages);
            Assert.Equal(KnownEmojis.Information, notice.Emoji);
            Assert.Equal(ConfigSourceStrings.NoBinding, notice.Message);
            Assert.Equal(ConsoleOutput.Error, notice.ConsoleOverride);
        }
        else
        {
            Assert.Empty(interaction.DisplayedMessages);
        }
        Assert.Equal(0, detector.Calls);
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.False(File.Exists(Path.Combine(workspace.Path, AspireConfigFile.FileName)));
        Assert.False(Directory.Exists(service.GetCacheDirectory(s_source)));
    }

    [Fact]
    public void TrackingIdentityIncludesPathAndRefButNotAppliedVersion()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var home = workspace.CreateDirectory("custom-home");
        var service = TestConfigSourceServices.Create(workspace.CreateExecutionContext(aspireHomeDirectory: home));

        Assert.StartsWith(Path.Combine(home.FullName, "configsources"), service.GetCacheDirectory(s_source), StringComparison.Ordinal);
        Assert.Equal(service.GetCacheDirectory(s_source), service.GetCacheDirectory(s_appliedSource));
        Assert.NotEqual(service.GetCacheDirectory(s_source),
            service.GetCacheDirectory(new() { Repository = s_source.Repository, Path = "other.json" }));
        Assert.NotEqual(service.GetCacheDirectory(s_source),
            service.GetCacheDirectory(new() { Repository = s_source.Repository, Path = s_source.Path, Ref = "preview" }));
        Assert.NotEqual(
            service.GetCacheDirectory(new() { Repository = s_source.Repository, Path = s_source.Path, Ref = "preview" }),
            service.GetCacheDirectory(new() { Repository = s_source.Repository, Path = s_source.Path, Ref = "main" }));
    }

    private static ParseResult ParseConsent(bool? accepted)
    {
        var command = new Command("new");
        command.Options.Add(ConfigSourceService.s_useSourceOption);
        return command.Parse(accepted is null ? "" : $"--use-config-source {accepted.Value.ToString().ToLowerInvariant()}");
    }
}
