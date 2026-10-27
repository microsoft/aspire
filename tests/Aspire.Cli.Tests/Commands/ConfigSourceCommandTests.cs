// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Documentation.ApiDocs;
using Aspire.Cli.Documentation.Docs;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Commands;

public class ConfigSourceCommandTests(ITestOutputHelper outputHelper)
{
    private static readonly ConfigSource s_source = new()
    {
        Repository = "https://git.example/configuration",
        Path = "aspire.config.json",
        AppliedVersion = "version-1"
    };

    [Theory]
    [InlineData("stop", true, false)]
    [InlineData("ps --format json", false, false)]
    [InlineData("stop", true, true)]
    [InlineData("ps --format json", false, true)]
    public async Task CompletedCheck_NotifiesAtExitAfterCliUpdateUnlessMachineReadable(string commandLine, bool notify, bool timeout)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var interaction = new TestInteractionService();
        var cliNoticeShown = false;
        var notifier = new TestCliUpdateNotifier
        {
            NotifyIfUpdateAvailableCallback = () =>
            {
                Assert.Equal(0, interaction.DisplayedMessages.Count(message => message.Message == ConfigSourceStrings.UpdateAvailable));
                Assert.Empty(interaction.DisplayedRawText);
                cliNoticeShown = true;
            }
        };
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.CliUpdateNotifierFactory = _ => notifier;
        }).BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        var prompt = new string('x', 70 * 1024) + "Enter passphrase: ";
        git.InspectConfiguration = (_, _) => timeout
            ? throw new ConfigSourceException(ConfigSourceStrings.GitTimeout) { GitOutput = prompt }
            : Task.FromResult("version-2");
        var service = provider.GetRequiredService<ConfigSourceService>();
        service.CheckForConfigSourceUpdates(workspace.WorkspaceRoot, CancellationToken.None);
        await service.WaitForChecksAsync().DefaultTimeout();
        Assert.Empty(interaction.DisplayedMessages);
        Assert.Empty(interaction.DisplayedRawText);

        await provider.GetRequiredService<RootCommand>().Parse(commandLine).InvokeAsync().DefaultTimeout();

        Assert.Equal(notify, cliNoticeShown);
        var expectedMessage = timeout
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ConfigSourceStrings.CheckFailed, ConfigSourceStrings.GitTimeout)
            : ConfigSourceStrings.UpdateAvailable;
        Assert.Equal(notify ? 1 : 0, interaction.DisplayedMessages.Count(message => message.Message == expectedMessage));
        if (notify && timeout)
        {
            Assert.Equal((prompt[^(64 * 1024)..], (ConsoleOutput?)ConsoleOutput.Error), Assert.Single(interaction.DisplayedRawText));
            service.NotifyIfUpdateAvailable();
            Assert.Single(interaction.DisplayedRawText);
        }
        else if (!notify)
        {
            Assert.Equal(("[]", (ConsoleOutput?)ConsoleOutput.Standard), Assert.Single(interaction.DisplayedRawText));
        }
        else
        {
            Assert.Empty(interaction.DisplayedRawText);
        }
    }

    [Fact]
    public async Task ProjectSelection_DoesNotWaitForAdvisoryCheck()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var projectFile = new FileInfo(Path.Combine(workspace.Path, "apphost.cs"));
        await File.WriteAllTextAsync(projectFile.FullName, "#:sdk Aspire.AppHost.Sdk");
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.InspectConfiguration = async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "version-2";
            }
            finally
            {
                stopped.SetResult();
            }
        };

        var project = await provider.GetRequiredService<IProjectLocator>().UseOrFindAppHostProjectFileAsync(
            projectFile, createSettingsFile: false, CancellationToken.None).DefaultTimeout();
        await started.Task.DefaultTimeout();

        Assert.Equal(projectFile.FullName, project?.FullName);
        Assert.False(stopped.Task.IsCompleted);
        await provider.GetRequiredService<RootCommand>().Parse("config list").InvokeAsync().DefaultTimeout();
        Assert.True(stopped.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Docs_UsesMaterializedConfigurationWithoutAcquisition(bool localOverride)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string sharedUrl = "https://shared.example.test/llms-full.txt";
        const string localUrl = "https://local.example.test/llms-full.txt";
        var acquired = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-1", new JsonObject
            {
                ["docs"] = new JsonObject { ["llmsTxtUrl"] = sharedUrl }
            }))
        };
        await TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: acquired)
            .MaterializeAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        if (localOverride)
        {
            await ConfigurationService.SetConfigurationInFileAsync(
                Path.Combine(workspace.Path, AspireConfigFile.FileName), "docs.llmsTxtUrl", localUrl);
        }
        using var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("# Shared Document\n\n[Related](/guide/)\n")
        };
        string? requestedUrl = null;
        using var handler = new MockHttpMessageHandler(response, request => requestedUrl = request.RequestUri!.AbsoluteUri);
        using var httpClient = new HttpClient(handler);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IDocsFetcher>(provider => new DocsFetcher(httpClient,
            provider.GetRequiredService<IDocsCache>(), provider.GetRequiredService<IConfiguration>(), NullLogger<DocsFetcher>.Instance));
        using var provider = services.BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        var root = provider.GetRequiredService<RootCommand>();
        Assert.Equal(0, git.Calls);

        var result = root.Parse("docs get shared-document --format json");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());

        Assert.Equal(localOverride ? localUrl : sharedUrl, requestedUrl);
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApiDocs_UsesMaterializedConfigurationWithoutAcquisition(bool localOverride)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string sharedUrl = "https://shared.example.test/sitemap.xml";
        const string localUrl = "https://local.example.test/sitemap.xml";
        var acquired = new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-1", new JsonObject
            {
                ["docs"] = new JsonObject { ["api"] = new JsonObject { ["sitemapUrl"] = sharedUrl } }
            }))
        };
        await TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: acquired)
            .MaterializeAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var expectedUrl = localOverride ? localUrl : sharedUrl;
        if (localOverride)
        {
            await ConfigurationService.SetConfigurationInFileAsync(
                Path.Combine(workspace.Path, AspireConfigFile.FileName), "docs.api.sitemapUrl", localUrl);
        }
        var requests = new List<string>();
        using var handler = new MockHttpMessageHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri.AbsoluteUri.EndsWith(".xml", StringComparison.Ordinal) ? """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
                      <url><loc>https://aspire.dev/reference/api/csharp/aspire.hosting/</loc></url>
                    </urlset>
                    """ : "# Aspire.Hosting\n\n[Related](/guide/)\n")
            };
        });
        using var httpClient = new HttpClient(handler);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        services.AddSingleton<IApiDocsFetcher>(provider => new ApiDocsFetcher(httpClient,
            provider.GetRequiredService<IApiDocsCache>(), provider.GetRequiredService<IConfiguration>(), NullLogger<ApiDocsFetcher>.Instance));
        using var provider = services.BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        var root = provider.GetRequiredService<RootCommand>();
        Assert.Equal(0, git.Calls);

        var result = root.Parse("docs api get csharp/aspire.hosting --format json");
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());

        Assert.Equal([expectedUrl, $"{new Uri(expectedUrl).GetLeftPart(UriPartial.Authority)}/reference/api/csharp/aspire.hosting.md"], requests);
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Fact]
    public async Task UpdateConfigSource_RefreshesFreshCacheWithoutUpdatingPackages()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.PackagingServiceFactory = _ => new TestPackagingService
            {
                GetChannelsAsyncCallback = _ => throw new InvalidOperationException("Refresh-only mode must not resolve package channels.")
            };
        });
        using var provider = services.BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        var service = provider.GetRequiredService<ConfigSourceService>();
        await service.CheckProjectAsync(workspace.WorkspaceRoot, force: false, CancellationToken.None);
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("update --config-source --yes --non-interactive");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(1, git.Calls);
        Assert.Equal(1, git.CheckCalls);
        Assert.False(Assert.IsType<UpdateCommand>(result.CommandResult.Command).RequiresCliPackageMetadata);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 64 * 1024)]
    [InlineData(true, 64 * 1024 + 1)]
    public async Task UpdateConfigSource_AcquisitionFailureRemainsCommandFailure(bool timeout, int outputLength)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        var prompt = outputLength == 0 ? "Enter passphrase: " : new string('x', outputLength - 1) + "?";
        git.ReadConfiguration = (_, _) => throw new ConfigSourceException(timeout ? ConfigSourceStrings.GitTimeout : ConfigSourceStrings.AuthenticationFailed)
        {
            GitOutput = timeout ? prompt : null
        };
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var previous = await File.ReadAllTextAsync(path);
        var result = provider.GetRequiredService<RootCommand>().Parse("update --config-source --yes --non-interactive");

        Assert.NotEqual(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());

        Assert.Equal(1, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.Equal(previous, await File.ReadAllTextAsync(path));
        if (timeout)
        {
            var expectedOutput = outputLength > 64 * 1024 ? prompt[^(64 * 1024)..] : prompt;
            Assert.Equal((expectedOutput, (ConsoleOutput?)ConsoleOutput.Error), Assert.Single(interaction.DisplayedRawText));
        }
        else
        {
            Assert.Empty(interaction.DisplayedRawText);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ref")]
    [InlineData("repository")]
    [InlineData("path")]
    [InlineData("both")]
    public async Task ProjectSelection_CheckFailureDoesNotFailOrReplaceSavedSettings(string? invalidField)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(new()
        {
            Repository = s_source.Repository,
            Path = s_source.Path,
            Ref = invalidField == "ref" ? "HEAD" : null,
            AppliedVersion = s_source.AppliedVersion
        }, workspace.WorkspaceRoot, CancellationToken.None);
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await ConfigurationService.SetConfigurationInFileAsync(path, "docs.llmsTxtUrl", "https://local.example/llms.txt");
        await ConfigurationService.SetConfigurationInFileAsync(path, "appHost.path", "apphost.cs");
        if (invalidField is "repository" or "path" or "both")
        {
            var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var binding = configuration["configSource"]!.AsObject();
            foreach (var field in invalidField == "both" ? new[] { "repository", "path" } : [invalidField])
            {
                Assert.True(binding.Remove(field));
            }
            await File.WriteAllTextAsync(path, configuration.ToJsonString());
        }
        var previous = await File.ReadAllTextAsync(path);
        var projectFile = new FileInfo(Path.Combine(workspace.Path, "apphost.cs"));
        await File.WriteAllTextAsync(projectFile.FullName, "#:sdk Aspire.AppHost.Sdk");
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.InspectConfiguration = (_, _) => throw new ConfigSourceException(ConfigSourceStrings.AuthenticationFailed);

        var project = await provider.GetRequiredService<IProjectLocator>().UseOrFindAppHostProjectFileAsync(
            projectFile, createSettingsFile: true, CancellationToken.None);
        await provider.GetRequiredService<ConfigSourceService>().WaitForChecksAsync().DefaultTimeout();
        Assert.Equal(projectFile.FullName, project?.FullName);
        var result = provider.GetRequiredService<RootCommand>().Parse("config get docs.llmsTxtUrl");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(previous, await File.ReadAllTextAsync(path));
        Assert.Equal(0, git.Calls);
        Assert.Equal(invalidField is not null ? 0 : 1, git.CheckCalls);
        var expectedError = invalidField switch
        {
            null => ConfigSourceStrings.AuthenticationFailed,
            "ref" => ConfigSourceStrings.InvalidRef,
            _ => ConfigSourceStrings.InvalidBinding
        };
        var warning = Assert.Single(interaction.DisplayedMessages, message => message.ConsoleOverride is Aspire.Cli.Interaction.ConsoleOutput.Error);
        Assert.Equal(string.Format(System.Globalization.CultureInfo.CurrentCulture, ConfigSourceStrings.CheckFailed,
            expectedError), warning.Message);
        if (invalidField is not null)
        {
            var refresh = provider.GetRequiredService<RootCommand>().Parse("update --config-source --yes --non-interactive");
            Assert.Equal(CliExitCodes.InvalidCommand, await refresh.InvokeAsync().DefaultTimeout());
            Assert.Equal(expectedError, Assert.Single(interaction.DisplayedErrors));
            Assert.Equal(0, git.Calls);
            Assert.Equal(previous, await File.ReadAllTextAsync(path));
        }
    }

    [Fact]
    public void UpdateConfigSource_NonInteractiveRequiresYes()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("update --config-source --non-interactive");

        Assert.Single(result.Errors);
    }

    [Theory]
    [InlineData("--self")]
    [InlineData("--migrate")]
    [InlineData("--channel daily")]
    [InlineData("--quality stable")]
    [InlineData("--nuget-config-dir .")]
    public void UpdateConfigSource_RejectsIncompatibleOptions(string option)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse($"update --config-source {option}");

        Assert.Equal(ConfigSourceStrings.IncompatibleOptions, Assert.Single(result.Errors).Message);
    }

    [Theory]
    [InlineData(false, "implicit")]
    [InlineData(false, "file")]
    [InlineData(false, "directory")]
    [InlineData(true, "implicit")]
    [InlineData(true, "file")]
    [InlineData(true, "directory")]
    public async Task ProjectSelection_ProcessesAncestorSourceForChildAppHost(bool refresh, string selection)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var project = workspace.CreateDirectory("AppHost");
        var projectFile = new FileInfo(Path.Combine(project.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(projectFile.FullName, "#:sdk Aspire.AppHost.Sdk");
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var rootPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await ConfigurationService.SetConfigurationInFileAsync(rootPath, "appHost.path", "AppHost/apphost.cs");
        var original = await File.ReadAllTextAsync(rootPath);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.InspectConfiguration = (_, _) => Task.FromResult("version-2");
        git.ReadConfiguration = (_, _) => Task.FromResult(("version-2", new JsonObject()));
        var requested = selection switch
        {
            "file" => projectFile,
            "directory" => new FileInfo(project.FullName),
            _ => null
        };

        if (refresh)
        {
            var argument = requested is null ? "" : $" --apphost \"{requested.FullName}\"";
            var result = provider.GetRequiredService<RootCommand>().Parse($"update --config-source{argument} --yes --non-interactive");
            Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
            Assert.Equal("version-2", AspireConfigFile.Load(workspace.Path)!.ConfigSource!.AppliedVersion);
        }
        else
        {
            var selected = await provider.GetRequiredService<IProjectLocator>().UseOrFindAppHostProjectFileAsync(
                requested, createSettingsFile: false, CancellationToken.None);
            Assert.Equal(projectFile.FullName, selected?.FullName);
            await provider.GetRequiredService<ConfigSourceService>().WaitForChecksAsync().DefaultTimeout();
            var service = provider.GetRequiredService<ConfigSourceService>();
            service.NotifyIfUpdateAvailable();
            Assert.Equal(ConfigSourceStrings.UpdateAvailable, Assert.Single(interaction.DisplayedMessages).Message);
            Assert.Equal(original, await File.ReadAllTextAsync(rootPath));
        }

        Assert.Equal(refresh ? 1 : 0, git.Calls);
        Assert.Equal(refresh ? 0 : 1, git.CheckCalls);
        Assert.False(File.Exists(Path.Combine(project.FullName, AspireConfigFile.FileName)));
    }

    [Theory]
    [InlineData("missing.cs")]
    [InlineData("missing.csproj")]
    [InlineData("missing-directory")]
    [InlineData("missing-directory/apphost.cs")]
    public async Task UpdateConfigSource_MissingExplicitPathFailsWithoutRefreshingBindings(string requestedPath)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var localPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var originalLocalConfiguration = await File.ReadAllTextAsync(localPath);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var globalFile = new FileInfo(provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(isGlobal: true));
        globalFile.Directory!.Create();
        await TestConfigSourceServices.SaveBindingAsync(s_source, globalFile.Directory, CancellationToken.None);
        File.Move(Path.Combine(globalFile.Directory.FullName, AspireConfigFile.FileName), globalFile.FullName);
        var originalGlobalConfiguration = await File.ReadAllTextAsync(globalFile.FullName);
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.ReadConfiguration = (_, _) => Task.FromResult(("version-2", new JsonObject()));
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"update --config-source --apphost \"{Path.Combine(workspace.Path, requestedPath)}\" --yes --non-interactive");
        Assert.Empty(result.Errors);

        Assert.Equal(CliExitCodes.FailedToFindProject, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(InteractionServiceStrings.ProjectOptionDoesntExist, Assert.Single(interaction.DisplayedErrors));
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.Equal(0, Assert.IsType<TestConfigSourceDetector>(provider.GetRequiredService<IInternalMicrosoftDetector>()).Calls);
        Assert.Equal(originalLocalConfiguration, await File.ReadAllTextAsync(localPath));
        Assert.Equal(originalGlobalConfiguration, await File.ReadAllTextAsync(globalFile.FullName));
    }

    [Fact]
    public async Task UpdateConfigSource_ExplicitAppHostRefreshesNearestBindingOnly()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var project = workspace.CreateDirectory("selected-project");
        await File.WriteAllTextAsync(Path.Combine(project.FullName, "apphost.cs"), "#:sdk Aspire.AppHost.Sdk");
        await TestConfigSourceServices.SaveBindingAsync(
            new ConfigSource { Repository = "https://parent.example/configuration", Path = "parent.json", AppliedVersion = "parent-version" },
            workspace.WorkspaceRoot, CancellationToken.None);
        var parentPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await ConfigurationService.SetConfigurationInFileAsync(parentPath, "appHost.path", "selected-project/apphost.cs");
        var parentConfiguration = await File.ReadAllTextAsync(parentPath);
        await TestConfigSourceServices.SaveBindingAsync(s_source, project, CancellationToken.None);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"update --config-source --apphost \"{Path.Combine(project.FullName, "apphost.cs")}\" --yes --non-interactive");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(1, Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>()).Calls);
        Assert.Equal(0, Assert.IsType<TestConfigSourceDetector>(provider.GetRequiredService<IInternalMicrosoftDetector>()).Calls);
        Assert.Equal(parentConfiguration, await File.ReadAllTextAsync(parentPath));
        Assert.Equal("version-1", AspireConfigFile.Load(project.FullName)!.ConfigSource!.AppliedVersion);
    }

    [Fact]
    public async Task UpdateConfigSource_MissingBindingSucceedsWithoutDetectionOrAcquisition()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse("update --config-source --yes --non-interactive");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Empty(interaction.DisplayedErrors);
        var notice = Assert.Single(interaction.DisplayedMessages);
        Assert.Equal(ConfigSourceStrings.NoBinding, notice.Message);
        Assert.Equal(ConsoleOutput.Error, notice.ConsoleOverride);
        Assert.Equal(0, Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>()).Calls);
        Assert.Equal(0, Assert.IsType<TestConfigSourceDetector>(provider.GetRequiredService<IInternalMicrosoftDetector>()).Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateConfigSource_RefreshesGlobalBindingWhenNearestConfigurationHasNone(bool explicitAppHost)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var localPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(localPath, "{}");
        if (explicitAppHost)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "apphost.cs"), "#:sdk Aspire.AppHost.Sdk");
        }
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var globalFile = new FileInfo(provider.GetRequiredService<IConfigurationService>().GetSettingsFilePath(isGlobal: true));
        globalFile.Directory!.Create();
        await TestConfigSourceServices.SaveBindingAsync(s_source, globalFile.Directory, CancellationToken.None);
        File.Move(Path.Combine(globalFile.Directory.FullName, AspireConfigFile.FileName), globalFile.FullName);
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.ReadConfiguration = (_, _) => Task.FromResult(("version-2", new JsonObject()));
        var appHostArgument = explicitAppHost ? $" --apphost \"{Path.Combine(workspace.Path, "apphost.cs")}\"" : "";
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"update --config-source{appHostArgument} --yes --non-interactive");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());

        Assert.Equal(1, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.Equal("{}", await File.ReadAllTextAsync(localPath));
        var globalConfiguration = JsonNode.Parse(await File.ReadAllTextAsync(globalFile.FullName))!;
        Assert.Equal("version-2", globalConfiguration["configSource"]!["appliedVersion"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpdateConfigSource_RefreshesNearestConfigurationForConfiguredProject(bool bound, bool childConfigExists)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var interaction = new TestInteractionService();
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var selected = workspace.CreateDirectory("selected");
        var selectedFile = new FileInfo(Path.Combine(selected.FullName, "apphost.cs"));
        var selectedSource = new ConfigSource { Repository = s_source.Repository, Path = "selected.json", AppliedVersion = s_source.AppliedVersion };
        if (bound)
        {
            await TestConfigSourceServices.SaveBindingAsync(selectedSource, selected, CancellationToken.None);
        }
        else if (childConfigExists)
        {
            await File.WriteAllTextAsync(Path.Combine(selected.FullName, AspireConfigFile.FileName), "{}");
        }
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
            options.ProjectLocatorFactory = _ => new TestProjectLocator
            {
                GetAppHostFromSettingsAsyncCallback = _ => Task.FromResult<FileInfo?>(selectedFile)
            };
        });
        using var provider = services.BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.ReadConfiguration = (source, _) =>
        {
            Assert.True(ConfigSource.HasSameIdentity(selectedSource, source) || ConfigSource.HasSameIdentity(s_source, source));
            Assert.Null(source.AppliedVersion);
            return Task.FromResult(("version-1", new JsonObject()));
        };
        var result = provider.GetRequiredService<RootCommand>().Parse("update --config-source --yes --non-interactive");

        var hasBinding = bound || !childConfigExists;
        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Empty(interaction.DisplayedErrors);
        if (!hasBinding)
        {
            Assert.Equal(ConfigSourceStrings.NoBinding, Assert.Single(interaction.DisplayedMessages).Message);
        }
        Assert.Equal(hasBinding ? 1 : 0, git.Calls);
        Assert.Equal(bound || childConfigExists ? s_source.AppliedVersion : "version-1", AspireConfigFile.Load(workspace.Path)!.ConfigSource!.AppliedVersion);
        Assert.Equal(bound, AspireConfigFile.Load(selected.FullName)?.ConfigSource is not null);
    }

    [Fact]
    public async Task ProjectSelection_ChecksAncestorSourceWithoutCopyingItsBinding()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var inheritedUrl = "https://parent.example/llms.txt";
        var initial = TestConfigSourceServices.Create(workspace.CreateExecutionContext(), git: new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-1", new JsonObject
            {
                ["docs"] = new JsonObject { ["llmsTxtUrl"] = inheritedUrl }
            }))
        });
        await initial.MaterializeAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var parentPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await ConfigurationService.SetConfigurationInFileAsync(parentPath, "appHost.path", "A/apphost.cs");
        var parentConfiguration = await File.ReadAllTextAsync(parentPath);
        var selected = workspace.CreateDirectory("B");
        var projectFile = new FileInfo(Path.Combine(selected.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(projectFile.FullName, "#:sdk Aspire.AppHost.Sdk");
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());

        var project = await provider.GetRequiredService<IProjectLocator>().UseOrFindAppHostProjectFileAsync(
            projectFile, createSettingsFile: false, CancellationToken.None);
        var sources = provider.GetRequiredService<ConfigSourceService>();
        await sources.WaitForChecksAsync().DefaultTimeout();

        Assert.Equal(projectFile.FullName, project?.FullName);
        var inherited = await provider.GetRequiredService<IConfigurationService>().GetConfigurationFromDirectoryAsync(
            "docs.llmsTxtUrl", selected, continueSearchWhenKeyMissing: true, cancellationToken: CancellationToken.None);
        Assert.Equal(inheritedUrl, inherited);
        await sources.CheckProjectAsync(selected, force: true, CancellationToken.None);
        Assert.Equal(1, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.Equal(parentConfiguration, await File.ReadAllTextAsync(parentPath));
        Assert.False(File.Exists(Path.Combine(selected.FullName, AspireConfigFile.FileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectSelection_ChecksResolvedProjectSource(bool explicitProject)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var selected = workspace.CreateDirectory("selected");
        var projectFile = new FileInfo(Path.Combine(selected.FullName, "apphost.cs"));
        await File.WriteAllTextAsync(projectFile.FullName, "#:sdk Aspire.AppHost.Sdk");
        var selectedSource = new ConfigSource { Repository = s_source.Repository, Path = "selected.json", AppliedVersion = s_source.AppliedVersion };
        await TestConfigSourceServices.SaveBindingAsync(selectedSource, selected, CancellationToken.None);
        await ConfigurationService.SetConfigurationInFileAsync(
            Path.Combine(workspace.Path, AspireConfigFile.FileName), "appHost.path", "selected/apphost.cs");
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.InspectConfiguration = (source, _) =>
        {
            Assert.True(ConfigSource.HasSameIdentity(selectedSource, source));
            Assert.Null(source.AppliedVersion);
            return Task.FromResult("version-1");
        };
        var project = await provider.GetRequiredService<IProjectLocator>().UseOrFindAppHostProjectFileAsync(
            explicitProject ? projectFile : null, createSettingsFile: false, CancellationToken.None);
        await provider.GetRequiredService<ConfigSourceService>().WaitForChecksAsync().DefaultTimeout();

        Assert.Equal(projectFile.FullName, project?.FullName);
        Assert.Equal(1, git.CheckCalls);
        Assert.Equal(0, git.Calls);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task UpdateConfigSource_ExplicitDirectoryResolvesAppHostBeforeBindingFallback(bool hasAppHost, bool bound)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        var parentPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        var parentConfiguration = await File.ReadAllTextAsync(parentPath);
        var requestedDirectory = workspace.CreateDirectory("selected");
        var projectDirectory = hasAppHost ? requestedDirectory.CreateSubdirectory("AppHost") : requestedDirectory;
        if (hasAppHost)
        {
            await File.WriteAllTextAsync(Path.Combine(projectDirectory.FullName, "apphost.cs"), "#:sdk Aspire.AppHost.Sdk");
            await ConfigurationService.SetConfigurationInFileAsync(parentPath, "appHost.path", "selected/AppHost/apphost.cs");
            parentConfiguration = await File.ReadAllTextAsync(parentPath);
        }
        var selectedSource = new ConfigSource
        {
            Repository = "https://selected.example/configuration",
            Path = "selected.json",
            AppliedVersion = "version-1"
        };
        var selectedPath = Path.Combine(projectDirectory.FullName, AspireConfigFile.FileName);
        if (bound)
        {
            await TestConfigSourceServices.SaveBindingAsync(selectedSource, projectDirectory, CancellationToken.None);
        }
        else
        {
            await File.WriteAllTextAsync(selectedPath, "{}");
        }
        var originalConfiguration = await File.ReadAllTextAsync(selectedPath);
        var interaction = new TestInteractionService();
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interaction;
        }).BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.ReadConfiguration = (source, _) =>
        {
            Assert.True(ConfigSource.HasSameIdentity(selectedSource, source));
            return Task.FromResult(("version-2", new JsonObject()));
        };
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"update --config-source --apphost \"{requestedDirectory.FullName}\" --yes --non-interactive");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());

        Assert.Empty(interaction.DisplayedErrors);
        Assert.Equal(bound ? 1 : 0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
        Assert.Equal(0, Assert.IsType<TestConfigSourceDetector>(provider.GetRequiredService<IInternalMicrosoftDetector>()).Calls);
        Assert.Equal(parentConfiguration, await File.ReadAllTextAsync(parentPath));
        if (bound)
        {
            Assert.Equal("version-2", AspireConfigFile.Load(projectDirectory.FullName)!.ConfigSource!.AppliedVersion);
        }
        else
        {
            var notice = Assert.Single(interaction.DisplayedMessages);
            Assert.Equal(ConfigSourceStrings.NoBinding, notice.Message);
            Assert.Equal(ConsoleOutput.Error, notice.ConsoleOverride);
            Assert.Equal(originalConfiguration, await File.ReadAllTextAsync(selectedPath));
        }
    }

    [Theory]
    [InlineData("run")]
    [InlineData("start")]
    [InlineData("restore")]
    public async Task ProjectCommands_DoNotCheckSourceWhenProjectResolutionFails(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var missingProject = Path.Combine(workspace.Path, "missing.csproj");
        var result = provider.GetRequiredService<RootCommand>().Parse($"{commandLine} --apphost \"{missingProject}\"");
        Assert.Empty(result.Errors);

        Assert.NotEqual(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());

        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        Assert.Equal(0, git.CheckCalls);
        Assert.Equal(0, git.Calls);
    }

    [Theory]
    [InlineData("config get features.shared")]
    [InlineData("config list --all")]
    [InlineData("config set --global features.shared true")]
    public async Task CommandsWithoutProjectResolution_DoNotCheckSavedSource(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(commandLine);
        Assert.Empty(result.Errors);

        await result.InvokeAsync().DefaultTimeout();

        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Theory]
    [InlineData("update --help")]
    [InlineData("new --help")]
    [InlineData("init --help")]
    public async Task Help_DoesNotAcquireSavedSource(string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await TestConfigSourceServices.SaveBindingAsync(s_source, workspace.WorkspaceRoot, CancellationToken.None);
        using var provider = CliTestHelper.CreateServiceCollection(workspace, outputHelper).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(commandLine);

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(0, Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>()).Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task New_PersistsSourceOnlyInCreatedProjectWhenAccepted(bool accepted, bool localTemplate)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = Path.Combine(workspace.Path, "created-project");
        if (localTemplate)
        {
            var config = workspace.CreateDirectory("template/.template.config");
            await File.WriteAllTextAsync(Path.Combine(config.FullName, "template.json"), """
                { "identity": "ConfigSource.Local", "name": "Local template", "shortName": "local" }
                """);
            await File.WriteAllTextAsync(Path.Combine(workspace.Path, "template", AspireConfigFile.FileName), "{}");
        }
        var templateOptions = localTemplate
            ? "--template-path template"
            : "aspire-empty --language csharp --version 17.0.0";
        var services = CreateCreationServices(workspace);
        using var provider = services.BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"new {templateOptions} --name CreatedProject --output \"{output}\" --suppress-agent-init --use-config-source {accepted.ToString().ToLowerInvariant()} --non-interactive");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equivalent(accepted ? s_source : null, AspireConfigFile.Load(output)?.ConfigSource, strict: true);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(output, AspireConfigFile.FileName)))!;
        Assert.Equal(accepted ? "https://docs.example/llms.txt" : null, configuration["docs"]?["llmsTxtUrl"]?.GetValue<string>());
        Assert.Equal(accepted ? 1 : 0, Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>()).Calls);
        Assert.False(File.Exists(Path.Combine(workspace.Path, AspireConfigFile.FileName)));
    }

    [Fact]
    public async Task Init_PersistsAcceptedSourceAlongsideSkeleton()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var provider = CreateCreationServices(workspace).BuildServiceProvider();
        var result = provider.GetRequiredService<RootCommand>().Parse(
            "init --file-based --language csharp --suppress-agent-init --use-config-source true --non-interactive");

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equivalent(s_source, AspireConfigFile.Load(workspace.Path)!.ConfigSource, strict: true);
        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, AspireConfigFile.FileName)))!;
        Assert.Equal("https://docs.example/llms.txt", configuration["docs"]!["llmsTxtUrl"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(workspace.Path, "apphost.cs")));
        Assert.Equal(1, Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>()).Calls);
    }

    [Fact]
    public async Task Init_ExistingBindingChecksWithoutReapplyingOrRetargeting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string command = "init --file-based --language csharp --suppress-agent-init --use-config-source true --non-interactive";
        using (var original = CreateCreationServices(workspace).BuildServiceProvider())
        {
            Assert.Equal(CliExitCodes.Success, await original.GetRequiredService<RootCommand>().Parse(command).InvokeAsync().DefaultTimeout());
        }
        var path = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await ConfigurationService.SetConfigurationInFileAsync(path, "docs.llmsTxtUrl", "https://local.example/llms.txt");
        var services = CreateCreationServices(workspace);
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?>
        {
            [CliConfigNames.InitSourceUrl] = "https://other.example/configuration"
        }));
        using var provider = services.BuildServiceProvider();

        Assert.Equal(CliExitCodes.Success, await provider.GetRequiredService<RootCommand>().Parse(
            "init --file-based --language csharp --suppress-agent-init --non-interactive").InvokeAsync().DefaultTimeout());

        var configuration = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal("https://local.example/llms.txt", configuration["docs"]!["llmsTxtUrl"]!.GetValue<string>());
        Assert.Equivalent(s_source, configuration["configSource"]!.Deserialize(JsonSourceGenerationContext.Default.ConfigSource), strict: true);
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        Assert.Equal(0, git.Calls);
        Assert.Equal(0, git.CheckCalls);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    public async Task Init_SolutionModeUsesAppHostConfigurationAndRefreshesIt(bool existingAppHost, bool appHostConfig, bool existingBinding)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Test.sln"), "Fake solution file");
        var childPath = Path.Combine(workspace.Path, "Test.AppHost");
        var projectPath = Path.Combine(childPath, "Test.AppHost.csproj");
        var rootConfigPath = Path.Combine(workspace.Path, AspireConfigFile.FileName);
        await File.WriteAllTextAsync(rootConfigPath, """
            {"appHost":{"path":"Test.AppHost/Test.AppHost.csproj"},"custom":"solution","docs":{"llmsTxtUrl":"https://solution.example/llms.txt"}}
            """);
        if (appHostConfig)
        {
            await TestConfigSourceServices.SaveBindingAsync(new ConfigSource
            {
                Repository = "https://solution.example/configuration",
                Path = "solution.json",
                AppliedVersion = "solution-version"
            }, workspace.WorkspaceRoot, CancellationToken.None);
        }
        const string templateConfig = """
            {"appHost":{"path":"Test.AppHost.csproj"},"profiles":{"custom":{"applicationUrl":"https://localhost:1234"}},"custom":"template"}
            """;
        if (existingAppHost)
        {
            Directory.CreateDirectory(childPath);
            await File.WriteAllTextAsync(projectPath, "<Project />");
            if (appHostConfig)
            {
                await File.WriteAllTextAsync(Path.Combine(childPath, AspireConfigFile.FileName), templateConfig);
            }
        }
        var configDirectory = appHostConfig ? new DirectoryInfo(childPath) : workspace.WorkspaceRoot;
        var configPath = Path.Combine(configDirectory.FullName, AspireConfigFile.FileName);
        if (existingBinding)
        {
            await TestConfigSourceServices.SaveBindingAsync(s_source, configDirectory, CancellationToken.None);
            await ConfigurationService.SetConfigurationInFileAsync(configPath, "docs.llmsTxtUrl", "https://local.example/llms.txt");
        }
        var originalRootConfiguration = await File.ReadAllTextAsync(rootConfigPath);
        var originalConfiguration = existingBinding ? await File.ReadAllTextAsync(configPath) : null;
        var services = CreateCreationServices(workspace);
        services.AddSingleton(workspace.CreateExecutionContext(identityChannel: "default"));
        var cache = new FakeNuGetPackageCache
        {
            GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
                Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>(
                    [new() { Id = "Aspire.ProjectTemplates", Source = "nuget.org", Version = "17.0.0" }])
        };
        services.AddSingleton<Aspire.Cli.Packaging.IPackagingService>(new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<Aspire.Cli.Packaging.PackageChannel>>(
                [Aspire.Cli.Packaging.PackageChannel.CreateImplicitChannel(cache, new TestFeatures(), NullLogger.Instance)])
        });
        services.AddSingleton<Aspire.Cli.DotNet.IDotNetCliRunner>(new TestDotNetCliRunner
        {
            NewProjectAsyncCallback = (_, name, outputPath, _, _) =>
            {
                Directory.CreateDirectory(outputPath);
                File.WriteAllText(Path.Combine(outputPath, $"{name}.csproj"), "<Project />");
                File.WriteAllText(Path.Combine(outputPath, AspireConfigFile.FileName), templateConfig);
                return 0;
            }
        });
        services.AddSingleton<Aspire.Cli.Certificates.ICertificateService>(provider => new TestCertificateService
        {
            EnsureCertificatesTrustedAsyncCallback = async cancellationToken =>
            {
                // Keep init alive until its advisory check finishes instead of racing shutdown.
                await provider.GetRequiredService<ConfigSourceService>().WaitForChecksAsync().DefaultTimeout();
                return await new TestCertificateService().EnsureCertificatesTrustedAsync(cancellationToken);
            }
        });
        if (existingBinding)
        {
            services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?>
            {
                [CliConfigNames.InitSourceUrl] = "https://other.example/configuration"
            }));
        }
        using var provider = services.BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        ConfigSource? checkedSource = null;
        git.InspectConfiguration = (source, _) =>
        {
            checkedSource = source;
            return Task.FromResult("version-2");
        };
        var command = existingBinding
            ? "init --language csharp --suppress-agent-init --non-interactive"
            : "init --language csharp --suppress-agent-init --use-config-source true --non-interactive";

        var exitCode = await provider.GetRequiredService<RootCommand>().Parse(command).InvokeAsync().DefaultTimeout();
        Assert.True(exitCode == CliExitCodes.Success, string.Join(Environment.NewLine,
            Assert.IsType<TestInteractionService>(provider.GetRequiredService<IInteractionService>()).DisplayedErrors));
        var sources = provider.GetRequiredService<ConfigSourceService>();
        await sources.WaitForChecksAsync().DefaultTimeout();

        var configuration = AspireConfigFile.Load(configDirectory.FullName)!;
        Assert.Equivalent(s_source, configuration.ConfigSource, strict: true);
        Assert.Equal(existingBinding ? "https://local.example/llms.txt" : "https://docs.example/llms.txt", configuration.Docs?.LlmsTxtUrl);
        Assert.True(Directory.Exists(childPath));
        Assert.Equal(appHostConfig, File.Exists(Path.Combine(childPath, AspireConfigFile.FileName)));
        Assert.Equal(appHostConfig ? "Test.AppHost.csproj" : "Test.AppHost/Test.AppHost.csproj", configuration.AppHost!.Path);
        if (appHostConfig)
        {
            Assert.Equal("https://localhost:1234", configuration.Profiles!["custom"].ApplicationUrl);
            Assert.Equal(originalRootConfiguration, await File.ReadAllTextAsync(rootConfigPath));
        }
        Assert.Equal(existingBinding ? 0 : 1, git.Calls);
        Assert.Equal(existingBinding ? 1 : 0, git.CheckCalls);
        if (existingBinding)
        {
            Assert.NotNull(checkedSource);
            Assert.True(ConfigSource.HasSameIdentity(s_source, checkedSource));
            Assert.Equal(originalConfiguration, await File.ReadAllTextAsync(configPath));
        }
        else
        {
            Assert.Null(checkedSource);
        }

        using var refreshProvider = CreateCreationServices(workspace).BuildServiceProvider();
        var refreshGit = Assert.IsType<TestConfigSourceRepository>(refreshProvider.GetRequiredService<IGitClient>());
        refreshGit.ReadConfiguration = (source, _) =>
        {
            Assert.True(ConfigSource.HasSameIdentity(s_source, source));
            return Task.FromResult(("version-2", new JsonObject
            {
                ["docs"] = new JsonObject { ["llmsTxtUrl"] = "https://updated.example/llms.txt" }
            }));
        };
        var refresh = refreshProvider.GetRequiredService<RootCommand>().Parse(
            "update --config-source --yes --non-interactive");

        Assert.Equal(CliExitCodes.Success, await refresh.InvokeAsync().DefaultTimeout());

        var refreshed = AspireConfigFile.Load(configDirectory.FullName)!;
        Assert.Equal("version-2", refreshed.ConfigSource!.AppliedVersion);
        Assert.Equal("https://updated.example/llms.txt", refreshed.Docs!.LlmsTxtUrl);
        Assert.Equal(configuration.AppHost.Path, refreshed.AppHost!.Path);
        Assert.Equal(appHostConfig, File.Exists(Path.Combine(childPath, AspireConfigFile.FileName)));
        if (appHostConfig)
        {
            Assert.Equal("https://localhost:1234", refreshed.Profiles!["custom"].ApplicationUrl);
            Assert.Equal(originalRootConfiguration, await File.ReadAllTextAsync(rootConfigPath));
        }
        Assert.Equal(1, refreshGit.Calls);
        Assert.Equal(0, refreshGit.CheckCalls);
    }

    [Theory]
    [InlineData("aspire-empty --language csharp")]
    [InlineData("--template-path template")]
    public async Task New_AcquisitionFailureDoesNotCreateProjectOrFallBack(string templateOptions)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var output = Path.Combine(workspace.Path, "created-project");
        using var provider = CreateCreationServices(workspace).BuildServiceProvider();
        var git = Assert.IsType<TestConfigSourceRepository>(provider.GetRequiredService<IGitClient>());
        git.ReadConfiguration = (_, _) => throw new ConfigSourceException(ConfigSourceStrings.AuthenticationFailed);
        var result = provider.GetRequiredService<RootCommand>().Parse(
            $"new {templateOptions} --output \"{output}\" --use-config-source true --non-interactive");

        Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.False(Directory.Exists(output));
        Assert.Equal(1, git.Calls);
        Assert.Equal(ConfigSourceStrings.AuthenticationFailed,
            Assert.Single(Assert.IsType<TestInteractionService>(provider.GetRequiredService<Aspire.Cli.Interaction.IInteractionService>()).DisplayedErrors));
    }

    private IServiceCollection CreateCreationServices(TemporaryWorkspace workspace)
    {
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliExecutionContextFactory = _ => workspace.CreateExecutionContext(identityChannel: "stable");
            options.InteractionServiceFactory = _ => new TestInteractionService();
        });
        services.AddSingleton<IEnvironment>(new TestEnvironment(new Dictionary<string, string?>
        {
            [CliConfigNames.InitSourceUrl] = s_source.Repository
        }));
        services.AddSingleton<IGitClient>(new TestConfigSourceRepository
        {
            ReadConfiguration = (_, _) => Task.FromResult(("version-1", new JsonObject
            {
                ["docs"] = new JsonObject { ["llmsTxtUrl"] = "https://docs.example/llms.txt" },
                ["sdk"] = new JsonObject { ["version"] = "99.0.0" }
            }))
        });
        return services;
    }
}
