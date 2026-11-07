// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001, ASPIREPROJECTS001

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Tests.Helpers;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchPreflightTests(ITestOutputHelper output)
{
    [Fact]
    public async Task UnavailablePayloadKeepsOrdinaryBuildBeforeHandoff()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new() { PayloadFailure = "The bundled payload is missing." });
        var service = AddProject(builder, workspace, "service");
        using var app = builder.Build();

        await BeforeStartAsync(app, CancellationToken.None);

        Assert.Empty(Model(app).Resources.OfType<DotnetWatchServerResource>());
        Assert.True(Metadata(service).SuppressBuild);
        Assert.Single(Model(app).Resources.OfType<DotnetProjectBuildResource>());
        service.Resource.AddLifeCycleCommands();
        Assert.Equal(ResourceCommandState.Enabled, Command(service, KnownResourceCommands.RebuildCommand).UpdateState(
            StateContext(app.Services, KnownResourceStates.Running)));
    }

    [Fact]
    public async Task ExplicitWatchRequirementFailsWithoutTransferringBuild()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new() { PayloadFailure = "The SDK cannot run the bundled payload." });
        var service = AddProject(builder, workspace, "service").WithWatch();
        using var app = builder.Build();

        var error = await Assert.ThrowsAsync<DistributedApplicationException>(() => BeforeStartAsync(app, CancellationToken.None));

        Assert.Contains("WithWatch(true)", error.Message);
        Assert.Contains("service", error.Message);
        Assert.True(Metadata(service).SuppressBuild);
    }

    [Fact]
    public async Task OverlapWithOrdinaryBuildExcludesTheSharingComponent()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var common = Path.Combine(workspace.Path, "common-output");
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (_, _, _, graph) => WithOutputs(graph, common)
        };
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        var ordinary = AddProject(builder, workspace, "ordinary").WithWatch(false);
        using var app = builder.Build();

        await BeforeStartAsync(app, CancellationToken.None);

        Assert.Empty(Model(app).Resources.OfType<DotnetWatchServerResource>());
        Assert.True(Metadata(watched).SuppressBuild);
        Assert.True(Metadata(ordinary).SuppressBuild);
        Assert.Equal(2, Assert.Single(Model(app).Resources.OfType<DotnetProjectBuildResource>()).ProjectPaths.Count);
    }

    [Fact]
    public async Task DuplicateEntryPointsShareOnePhysicalRootWithoutLosingResources()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var first = AddProject(builder, workspace, "first");
        var second = builder.AddDotnetProject("second", Metadata(first).ProjectPath);
        using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());

        var arguments = await ArgumentEvaluator.GetArgumentListAsync(server, app.Services);

        Assert.Equal(1, arguments.Count(argument => argument == "--resource"));
        Assert.False(Metadata(first).SuppressBuild);
        Assert.False(Metadata(second).SuppressBuild);
        foreach (var resource in new[] { first, second })
        {
            resource.Resource.AddLifeCycleCommands();
            var restart = Command(resource, KnownResourceCommands.RestartCommand);
            Assert.Equal(ResourceCommandState.Disabled, restart.UpdateState(StateContext(app.Services, KnownResourceStates.Running)));
            var result = await restart.ExecuteCommand(CommandContext(resource.Resource.Name, app.Services));
            Assert.False(result.Success);
            Assert.Equal(DotnetWatchMember.AmbiguousRestartExplanation, result.Message);
        }
    }

    [Fact]
    public async Task LowerPrecedenceBuildEnvironmentMustRoundTrip()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (_, globalProperties, _, graph) =>
            {
                var projects = graph.Projects.ToDictionary(pair => pair.Key, pair =>
                {
                    var properties = new Dictionary<string, string>(pair.Value.Properties)
                    {
                        ["CUSTOM_CONTEXT"] = globalProperties ? "requested" : "project-value"
                    };
                    return pair.Value with { Properties = properties };
                }, DotnetWatchProjectGraph.PathComparer);
                return graph with { Projects = projects };
            }
        };
        using var builder = CreateBuilder(evaluator);
        var service = AddProject(builder, workspace, "service").WithBuildEnvironment("CUSTOM_CONTEXT", "requested");
        using var app = builder.Build();

        await BeforeStartAsync(app, CancellationToken.None);

        Assert.True(Metadata(service).SuppressBuild);
        Assert.Empty(Model(app).Resources.OfType<DotnetWatchServerResource>());
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task SymlinkedEntryPointsShareOnePhysicalRootAndLaunchGate()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Directory symlinks require additional privileges.");
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var first = AddProject(builder, workspace, "first");
        var alias = Path.Combine(workspace.Path, "alias");
        Directory.CreateSymbolicLink(alias, Path.GetDirectoryName(Metadata(first).ProjectPath)!);
        var second = builder.AddDotnetProject("second", Path.Combine(alias, "first.csproj"));
        using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());

        var arguments = await ArgumentEvaluator.GetArgumentListAsync(server, app.Services);
        var firstArguments = await ArgumentEvaluator.GetArgumentListAsync(first.Resource, app.Services);
        var secondArguments = await ArgumentEvaluator.GetArgumentListAsync(second.Resource, app.Services);

        Assert.Equal(1, arguments.Count(argument => argument == "--resource"));
        Assert.Equal(firstArguments[firstArguments.IndexOf("--entrypoint") + 1],
            secondArguments[secondArguments.IndexOf("--entrypoint") + 1]);
        Assert.Equal(firstArguments[firstArguments.IndexOf("--launch-gate") + 1],
            secondArguments[secondArguments.IndexOf("--launch-gate") + 1]);
    }

    [Fact]
    public async Task DisposalCancelsAndJoinsInFlightPreparation()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        AddProject(builder, workspace, "service");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        evaluator.BeforeEvaluation = async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        var preparing = ArgumentEvaluator.GetArgumentListAsync(server, app.Services).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await app.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
    }

    [Theory]
    [InlineData(".fsproj")]
    [InlineData(".vbproj")]
    public async Task OtherDotnetLanguagesRetainOrdinaryBuildOwnership(string extension)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var path = Path.Combine(workspace.Path, $"service{extension}");
        DotnetWatchCoordinatorTests.CreateProjectFile(path);
        var service = builder.AddDotnetProject("service", path);
        using var app = builder.Build();

        await BeforeStartAsync(app, CancellationToken.None);

        Assert.True(Metadata(service).SuppressBuild);
        Assert.Single(Model(app).Resources.OfType<DotnetProjectBuildResource>());
        Assert.Empty(Model(app).Resources.OfType<DotnetWatchServerResource>());
    }

    [Fact]
    public async Task OrdinaryRebuildReevaluatesItsCurrentOutputsBeforeExecution()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var collision = false;
        var watchedOutput = Path.Combine(workspace.Path, "watched", "bin");
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (path, _, _, graph) => collision && Path.GetFileNameWithoutExtension(path) == "ordinary"
                ? WithOutputs(graph, watchedOutput) : graph
        };
        using var builder = CreateBuilder(evaluator);
        AddProject(builder, workspace, "watched");
        var ordinary = AddProject(builder, workspace, "ordinary").WithWatch(false);
        using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var called = false;
        var original = new ResourceCommandAnnotation("rebuild", "Rebuild", _ => ResourceCommandState.Enabled, _ =>
        {
            called = true;
            return Task.FromResult(CommandResults.Success());
        }, null, arguments: null, null, null, null, false);
        var guarded = ordinary.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
            .Single(annotation => annotation.CommandName == KnownResourceCommands.RebuildCommand).Factory(original);
        var before = evaluator.EvaluationCount;
        collision = true;

        var result = await guarded.ExecuteCommand(CommandContext(ordinary.Resource.Name, app.Services));

        Assert.False(result.Success);
        Assert.Contains("overlap", result.Message);
        Assert.False(called);
        Assert.True(evaluator.EvaluationCount > before);
    }

    [Fact]
    public async Task InitialSuccessfulBuildCapturesRestoredImportsBeforeOrdinaryRebuild()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var restored = false;
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (path, _, _, graph) => restored && Path.GetFileNameWithoutExtension(path) == "watched"
                ? graph with
                {
                    Projects = graph.Projects.ToDictionary(pair => pair.Key,
                        pair => pair.Value with
                        {
                            Imports = [.. pair.Value.Imports,
                                Path.Combine(Path.GetDirectoryName(path)!, "obj", "watched.csproj.nuget.g.props"),
                                Path.Combine(Path.GetDirectoryName(path)!, "obj", "watched.csproj.nuget.g.targets")]
                        }, DotnetWatchProjectGraph.PathComparer)
                } : graph
        };
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        var ordinary = AddProject(builder, workspace, "ordinary").WithWatch(false);
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(Metadata(watched).ProjectPath)!, "obj"));
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var barrier = Assert.Single(watched.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
        var waiting = barrier.Callback(app.Services, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        restored = true;

        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(watched.Resource, success: true, app.Services, CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));

        var calls = 0;
        var original = new ResourceCommandAnnotation("rebuild", "Rebuild", _ => ResourceCommandState.Enabled, _ =>
        {
            calls++;
            return Task.FromResult(CommandResults.Success());
        }, null, arguments: null, null, null, null, false);
        var guarded = ordinary.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
            .Single(annotation => annotation.CommandName == KnownResourceCommands.RebuildCommand).Factory(original);

        var result = await guarded.ExecuteCommand(CommandContext(ordinary.Resource.Name, app.Services));

        output.WriteLine($"Ordinary Rebuild: success={result.Success}, calls={calls}, message={result.Message}");
        Assert.True(result.Success, result.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task InitialBuildInventoryValidationFinishesBeforeReleasingConsumers()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var barrier = Assert.Single(watched.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
        var waiting = barrier.Callback(app.Services, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        evaluator.BeforeEvaluation = async cancellationToken =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var reporting = DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(waiting.IsCompleted);
        Assert.False(reporting.IsCompleted);
        release.TrySetResult();

        await reporting.WaitAsync(TimeSpan.FromSeconds(10));
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidInitialBuildInventoryFailsBeforeReleasingConsumers(bool changedSdk)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var invalidated = false;
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (_, _, _, graph) => !invalidated ? graph
                : changedSdk ? graph with { SdkVersion = "10.0.999" }
                : WithOutputs(graph, Path.Combine(workspace.Path, "changed-output"))
        };
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var barrier = Assert.Single(watched.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
        var waiting = barrier.Callback(app.Services, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        invalidated = true;

        var error = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
                watched.Resource, success: true, app.Services, CancellationToken.None));
        var consumerError = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            waiting.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("SDK or output/build context changed", error.Message);
        Assert.Contains("Initial watch build inventory validation failed", consumerError.Message);
        Assert.False(Metadata(watched).SuppressBuild);
    }

    [Fact]
    public async Task CanceledInitialBuildInventoryCaptureDoesNotReleaseConsumers()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var barrier = Assert.Single(watched.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
        var waiting = barrier.Callback(app.Services, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        evaluator.BeforeEvaluation = async cancellationToken =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        };
        using var captureCancellation = new CancellationTokenSource();

        var reporting = DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, captureCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await captureCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reporting);
        Assert.False(waiting.IsCompleted);
        evaluator.BeforeEvaluation = null;

        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await waiting.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OrdinaryInitialBuildValidatesItsActualCallbackSnapshotBeforeWriting()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var watchedOutput = Path.Combine(workspace.Path, "watched", "bin");
        var ordinaryOutput = Path.Combine(workspace.Path, "ordinary-output");
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (_, _, environment, graph) => environment.TryGetValue("WATCH_OUTPUT", out var path)
                ? WithOutputs(graph, path) : graph
        };
        using var builder = CreateBuilder(evaluator);
        AddProject(builder, workspace, "watched");
        var evaluations = 0;
        AddProject(builder, workspace, "ordinary").WithWatch(false)
            .WithBuildEnvironment(context =>
                context.EnvironmentVariables["WATCH_OUTPUT"] = ++evaluations == 1 ? ordinaryOutput : watchedOutput);
        using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        Assert.Equal(1, evaluations);
        var build = Assert.Single(Model(app).Resources.OfType<DotnetProjectBuildResource>());

        var error = await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(build, app.Services), CancellationToken.None));

        Assert.Contains("overlap", error.Message);
        Assert.Equal(2, evaluations);
    }

    [Fact]
    public async Task OrdinaryOnlyBuildInputChangeFencesTheWatchGroup()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var collision = false;
        var watchedOutput = Path.Combine(workspace.Path, "watched", "bin");
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (path, _, _, graph) => collision && Path.GetFileNameWithoutExtension(path) == "ordinary"
                ? WithOutputs(graph, watchedOutput) : graph
        };
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        var ordinary = AddProject(builder, workspace, "ordinary").WithWatch(false);
        using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var barrier = watched.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>().Single();
        var waiting = barrier.Callback(app.Services, CancellationToken.None);
        Assert.False(waiting.IsCompleted);
        collision = true;

        await File.WriteAllTextAsync(Metadata(ordinary).ProjectPath, "<Project><!-- output context changed --></Project>");

        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Contains("overlap", failure.Message);
        Assert.False(Metadata(watched).SuppressBuild);
        Assert.True(Metadata(ordinary).SuppressBuild);
    }

    private static IDistributedApplicationTestingBuilder CreateBuilder(DotnetWatchPreflightTestServices evaluator)
    {
        var builder = TestDistributedApplicationBuilder.Create(["AppHost:Run:WatchEnabled=true"]);
        builder.Services.AddSingleton<IDotnetWatchPreflight>(evaluator);
        return builder;
    }

    private static IResourceBuilder<DotnetProjectResource> AddProject(
        IDistributedApplicationBuilder builder, TemporaryWorkspace workspace, string name)
    {
        var path = Path.Combine(workspace.Path, name, $"{name}.csproj");
        DotnetWatchCoordinatorTests.CreateProjectFile(path);
        return builder.AddDotnetProject(name, path);
    }

    private static DotnetWatchProjectGraphResult WithOutputs(DotnetWatchProjectGraphResult graph, string outputPath) =>
        graph with
        {
            Projects = graph.Projects.ToDictionary(pair => pair.Key,
                pair => pair.Value with { Outputs = [DotnetWatchProjectGraph.Normalize(outputPath)] }, DotnetWatchProjectGraph.PathComparer)
        };

    private static IProjectMetadata Metadata(IResourceBuilder<DotnetProjectResource> resource) =>
        resource.Resource.Annotations.OfType<IProjectMetadata>().Single();

    private static DistributedApplicationModel Model(DistributedApplication app) =>
        app.Services.GetRequiredService<DistributedApplicationModel>();

    private static ResourceCommandAnnotation Command(IResourceBuilder<DotnetProjectResource> resource, string name) =>
        resource.Resource.Annotations.OfType<ResourceCommandAnnotation>().Single(command => command.Name == name);

    private static UpdateCommandStateContext StateContext(IServiceProvider services, string state) => new()
    {
        Services = services,
        ResourceSnapshot = new() { ResourceType = "Executable", Properties = [], State = state }
    };

    private static ExecuteCommandContext CommandContext(string id, IServiceProvider services) => new()
    {
        ResourceName = id,
        Services = services,
        CancellationToken = CancellationToken.None,
        Logger = NullLogger.Instance,
        Arguments = new([])
    };

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task BeforeStartAsync(DistributedApplication app, CancellationToken cancellationToken);
}
