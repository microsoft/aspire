// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class LsCommandTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task LsCommand_Help_Works()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --help");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_WhenNoCandidates_ReturnsSuccess()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("Json")]
    [InlineData("JSON")]
    public async Task LsCommand_FormatOption_IsCaseInsensitive(string format)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse($"ls --format {format}");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_FormatOption_RejectsInvalidValue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format invalid");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.NotEqual(CliExitCodes.Success, exitCode);
    }

    [Fact]
    public async Task LsCommand_JsonFormat_ReturnsCandidateAppHosts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, _) => Task.FromResult(new List<AppHostProjectCandidate>
            {
                new(new FileInfo(appHostPath1), KnownLanguageId.CSharp),
                new(new FileInfo(appHostPath2), KnownLanguageId.TypeScript, AppHostProjectCandidateStatus.PossiblyUnbuildable)
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        var candidateAppHosts = JsonSerializer.Deserialize(jsonOutput, JsonSourceGenerationContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
        Assert.NotNull(candidateAppHosts);

        Assert.Collection(candidateAppHosts,
            first =>
            {
                Assert.Equal(Path.Combine("App1", "App1.AppHost.csproj"), first.RelativePath);
                Assert.Equal(appHostPath1, first.Path);
                Assert.Equal(KnownLanguageId.CSharp, first.Language);
                Assert.Equal("buildable", first.Status);
            },
            second =>
            {
                Assert.Equal(Path.Combine("App2", "App2.AppHost.csproj"), second.RelativePath);
                Assert.Equal(appHostPath2, second.Path);
                Assert.Equal(KnownLanguageId.TypeScript, second.Language);
                Assert.Equal("possibly-unbuildable", second.Status);
            });
    }

    [Fact]
    public async Task LsCommand_JsonFormat_WhenNoCandidates_ReturnsEmptyArray()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        using var document = JsonDocument.Parse(jsonOutput);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(0, document.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task LsCommand_TableFormat_ColorsStatus()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var textWriter = new TestOutputTextWriter(outputHelper);
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, _) => Task.FromResult(new List<AppHostProjectCandidate>
            {
                new(new FileInfo(appHostPath1), KnownLanguageId.CSharp),
                new(new FileInfo(appHostPath2), KnownLanguageId.TypeScript, AppHostProjectCandidateStatus.PossiblyUnbuildable)
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.OutputTextWriter = textWriter;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var output = string.Join('\n', textWriter.Logs);
        Assert.Contains("\u001b[32mbuildable", output);
        Assert.Contains("\u001b[93m", output);
        Assert.Contains("possibly-unbuild", output);
    }

    [Fact]
    public async Task LsCommand_DefaultsToFilteredScope()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        AppHostDiscoveryScope? capturedScope = null;
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, scope, _) =>
            {
                capturedScope = scope;
                return Task.FromResult(new List<AppHostProjectCandidate>());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(AppHostDiscoveryScope.DefaultFiltered, capturedScope);
    }

    [Fact]
    public async Task LsCommand_AllFlag_PassesAllFilesScope()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        AppHostDiscoveryScope? capturedScope = null;
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, scope, _) =>
            {
                capturedScope = scope;
                return Task.FromResult(new List<AppHostProjectCandidate>());
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(AppHostDiscoveryScope.AllFiles, capturedScope);
    }

    [Fact]
    public async Task LsCommand_EmitsProfilingActivities()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var startedActivities = new List<Activity>();
        using var listener = CreateProfilingActivityListener(startedActivities.Add);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "App", "App.AppHost.csproj");
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, _) => Task.FromResult(new List<AppHostProjectCandidate>
            {
                new(new FileInfo(appHostPath), KnownLanguageId.CSharp)
            })
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => projectLocator;
            options.ConfigurationCallback = config =>
            {
                config[ProfilingTelemetry.EnvironmentVariables.Enabled] = "true";
                config[ProfilingTelemetry.EnvironmentVariables.SessionId] = "session-1";
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);

        var lsActivity = Assert.Single(startedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.LsCommand);
        Assert.Equal("json", lsActivity.GetTagItem(ProfilingTelemetry.Tags.LsOutputFormat));
        Assert.Equal(true, lsActivity.GetTagItem(ProfilingTelemetry.Tags.LsIncludeAll));
        Assert.Equal(1, lsActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
        Assert.Equal("session-1", lsActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));

        var findActivity = Assert.Single(startedActivities, activity => activity.OperationName == ProfilingTelemetry.Activities.LsFindAppHosts);
        Assert.Equal(AppHostDiscoveryScope.AllFiles.ToString(), findActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryScope));
        Assert.Equal(1, findActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
    }

    private static ActivityListener CreateProfilingActivityListener(Action<Activity> activityStarted)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ProfilingTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activityStarted
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
