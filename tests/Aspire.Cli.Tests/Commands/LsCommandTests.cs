// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Rendering;
using InvocationConfiguration = System.CommandLine.InvocationConfiguration;

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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.NotEqual(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var jsonOutput = string.Join(string.Empty, textWriter.Logs);
        var candidateAppHosts = JsonSerializer.Deserialize(jsonOutput, JsonSourceGenerationContext.RelaxedEscaping.ListCandidateAppHostDisplayInfo);
        Assert.NotNull(candidateAppHosts);

        Assert.Collection(candidateAppHosts,
            first =>
            {
                Assert.Equal(appHostPath1, first.Path);
                Assert.Equal(KnownLanguageId.CSharp, first.Language);
                Assert.Equal("buildable", first.Status);
            },
            second =>
            {
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);

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

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var output = string.Join('\n', textWriter.Logs);
        Assert.Contains("\u001b[32mbuildable", output);
        Assert.Contains("\u001b[93m", output);
        Assert.Contains("possibly-unbuild", output);
    }

    [Fact]
    public async Task LsCommand_TableFormat_InteractiveMode_StreamsCandidateAppHosts()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var interactionService = new TestInteractionService();
        var appHostPath1 = Path.Combine(workspace.WorkspaceRoot.FullName, "App1", "App1.AppHost.csproj");
        var appHostPath2 = Path.Combine(workspace.WorkspaceRoot.FullName, "App2", "App2.AppHost.csproj");
        var appHost1 = new AppHostProjectCandidate(new FileInfo(appHostPath1), KnownLanguageId.CSharp);
        var appHost2 = new AppHostProjectCandidate(new FileInfo(appHostPath2), KnownLanguageId.TypeScript, AppHostProjectCandidateStatus.PossiblyUnbuildable);
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsWithProgressAsyncCallback = (_, _, onCandidateFound, _) =>
            {
                onCandidateFound(appHost1);
                onCandidateFound(appHost2);
                return Task.FromResult(new List<AppHostProjectCandidate>
                {
                    appHost1,
                    appHost2
                });
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.CliHostEnvironmentFactory = _ => TestHelpers.CreateInteractiveHostEnvironment();
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Empty(interactionService.DisplayedRenderables);

        var liveOutputs = interactionService.DisplayedLiveRenderables.Select(RenderToPlainConsole).ToArray();
        Assert.Contains(liveOutputs, output => output.Contains(InteractionServiceStrings.FindingAppHosts));
        Assert.Contains(liveOutputs, output => output.Contains("App1.AppHost.csproj"));
        Assert.Contains(liveOutputs, output => output.Contains("App2.AppHost.csproj"));
        Assert.Contains("App1.AppHost.csproj", liveOutputs[^1]);
        Assert.Contains("App2.AppHost.csproj", liveOutputs[^1]);
    }

    [Fact]
    public async Task LsCommand_WhenCancelled_ReturnsSuccessAndDisplaysCancellation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellationTokenSource = new CancellationTokenSource();
        var interactionService = new TestInteractionService();
        var projectLocator = new TestProjectLocator
        {
            FindAppHostProjectsAsyncCallback = (_, _, cancellationToken) =>
            {
                cancellationTokenSource.Cancel();
                return Task.FromCanceled<List<AppHostProjectCandidate>>(cancellationToken);
            }
        };

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.InteractionServiceFactory = _ => interactionService;
            options.ProjectLocatorFactory = _ => projectLocator;
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls");

        var exitCode = await result.InvokeAsync(new InvocationConfiguration(), cancellationTokenSource.Token).DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(1, interactionService.DisplayCancellationMessageCount);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
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

        Assert.Equal(ExitCodeConstants.Success, exitCode);
        Assert.Equal(AppHostDiscoveryScope.AllFiles, capturedScope);
    }

    [Fact]
    public async Task LsCommand_EmitsProfilingActivities()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // ActivitySource listeners are process-wide, so this test can observe profiling spans
        // from other tests running in parallel. Use a unique session id and filter by it instead
        // of assuming every observed activity belongs to this command invocation.
        var sessionId = $"ls-{Guid.NewGuid():N}";
        var startedActivities = new ConcurrentBag<Activity>();
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
                config[ProfilingTelemetry.EnvironmentVariables.SessionId] = sessionId;
            };
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RootCommand>();
        var result = command.Parse("ls --format json --all");

        var exitCode = await result.InvokeAsync().DefaultTimeout();

        Assert.Equal(ExitCodeConstants.Success, exitCode);

        var lsActivity = Assert.Single(startedActivities, activity => IsActivityFromSession(activity, ProfilingTelemetry.Activities.LsCommand, sessionId));
        Assert.Equal("json", lsActivity.GetTagItem(ProfilingTelemetry.Tags.LsOutputFormat));
        Assert.Equal(true, lsActivity.GetTagItem(ProfilingTelemetry.Tags.LsIncludeAll));
        Assert.Equal(1, lsActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
        Assert.Equal(sessionId, lsActivity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));

        var findActivity = Assert.Single(startedActivities, activity => IsActivityFromSession(activity, ProfilingTelemetry.Activities.LsFindAppHosts, sessionId));
        Assert.Equal(AppHostDiscoveryScope.AllFiles.ToString(), findActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostDiscoveryScope));
        Assert.Equal(1, findActivity.GetTagItem(ProfilingTelemetry.Tags.AppHostCandidateCount));
    }

    private static bool IsActivityFromSession(Activity activity, string operationName, string sessionId)
    {
        return activity.OperationName == operationName &&
            Equals(sessionId, activity.GetTagItem(ProfilingTelemetry.Tags.ProfilingSessionId));
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

    private static string RenderToPlainConsole(IRenderable renderable)
    {
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false }
        });

        console.Profile.Width = int.MaxValue;
        console.Profile.Capabilities.Links = false;
        console.Write(renderable);

        return writer.ToString().Replace("\r\n", "\n");
    }
}
