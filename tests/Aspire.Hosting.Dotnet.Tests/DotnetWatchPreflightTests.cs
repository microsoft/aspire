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
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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
    public async Task ReferencedProjectBuildContextMustRoundTrip()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sharedPath = DotnetWatchProjectGraph.Normalize(Path.Combine(workspace.Path, "shared", "shared.csproj"));
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (_, globalProperties, _, graph) =>
            {
                var root = graph.Projects.Single();
                var rootNode = root.Value with { References = [sharedPath] };
                var sharedProperties = new Dictionary<string, string>(root.Value.Properties)
                {
                    ["REVIEW_FEATURE"] = globalProperties ? "requested" : "project-owned"
                };
                var shared = new DotnetWatchProjectNode(
                    sharedPath,
                    sharedProperties,
                    [],
                    [sharedPath],
                    [Path.Combine(Path.GetDirectoryName(sharedPath)!, "bin"), Path.Combine(Path.GetDirectoryName(sharedPath)!, "obj")]);
                return graph with
                {
                    Projects = new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer)
                    {
                        [root.Key] = rootNode,
                        [sharedPath] = shared
                    }
                };
            }
        };
        using var builder = CreateBuilder(evaluator);
        var service = AddProject(builder, workspace, "service")
            .WithBuildEnvironment("REVIEW_FEATURE", "requested");
        using var app = builder.Build();

        await BeforeStartAsync(app, CancellationToken.None);

        Assert.True(Metadata(service).SuppressBuild);
        Assert.Empty(Model(app).Resources.OfType<DotnetWatchServerResource>());
    }

    [Fact]
    public async Task ExcludedProjectUsesItsOrdinaryGraphForFinalOwnership()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var common = Path.Combine(workspace.Path, "common-output");
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (path, globalProperties, _, graph) =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                var output = name == "incompatible" && !globalProperties
                    ? Path.Combine(workspace.Path, "watcher-only-output")
                    : common;
                var projects = graph.Projects.ToDictionary(pair => pair.Key, pair =>
                {
                    var properties = new Dictionary<string, string>(pair.Value.Properties);
                    if (name == "incompatible")
                    {
                        properties["CUSTOM_CONTEXT"] = globalProperties ? "requested" : "project-owned";
                    }
                    return pair.Value with
                    {
                        Properties = properties,
                        Outputs = [DotnetWatchProjectGraph.Normalize(output)]
                    };
                }, DotnetWatchProjectGraph.PathComparer);
                return graph with { Projects = projects };
            }
        };
        using var builder = CreateBuilder(evaluator);
        var incompatible = AddProject(builder, workspace, "incompatible").WithBuildEnvironment("CUSTOM_CONTEXT", "requested");
        var watched = AddProject(builder, workspace, "watched");
        using var app = builder.Build();

        await BeforeStartAsync(app, CancellationToken.None);

        Assert.Empty(Model(app).Resources.OfType<DotnetWatchServerResource>());
        Assert.Equal(
            [Metadata(incompatible).ProjectPath, Metadata(watched).ProjectPath],
            Model(app).Resources.OfType<DotnetProjectBuildResource>()
                .SelectMany(build => build.ProjectPaths)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task SharedDependencyDiagnosticNamesThePhysicalProjectAndAffectedResources()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var sharedPath = DotnetWatchProjectGraph.Normalize(Path.Combine(workspace.Path, "shared", "shared.csproj"));
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (_, _, _, graph) =>
            {
                var root = graph.Projects.Single();
                var shared = root.Value with
                {
                    Path = sharedPath,
                    References = [],
                    Imports = [sharedPath],
                    Outputs = [Path.Combine(Path.GetDirectoryName(sharedPath)!, "bin")]
                };
                return graph with
                {
                    Projects = new Dictionary<string, DotnetWatchProjectNode>(DotnetWatchProjectGraph.PathComparer)
                    {
                        [root.Key] = root.Value with { References = [sharedPath] },
                        [sharedPath] = shared
                    }
                };
            }
        };
        using var builder = CreateBuilder(evaluator);
        builder.Services.AddLogging(logging => logging.AddFakeLogging());
        AddProject(builder, workspace, "watched").WithWatch();
        AddProject(builder, workspace, "ordinary").WithWatch(false);
        using var app = builder.Build();

        var error = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => BeforeStartAsync(app, CancellationToken.None));

        Assert.Contains("'watched'", error.Message);
        Assert.Contains(app.Services.GetFakeLogCollector().GetSnapshot(), entry =>
            entry.Message.Contains("'ordinary'", StringComparison.Ordinal) &&
            entry.Message.Contains(sharedPath, StringComparison.Ordinal));
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

        var firstArguments = await ArgumentEvaluator.GetArgumentListAsync(first.Resource, app.Services);
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(server, app.Services);
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
    public async Task WatchedResourceStartWaitsForInitialBuildRecovery()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);

        var starting = builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(watched.Resource, app.Services),
            CancellationToken.None);
        Assert.False(starting.IsCompleted);

        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: false, app.Services, CancellationToken.None);
        Assert.False(starting.IsCompleted);

        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await starting.WaitAsync(TimeSpan.FromSeconds(10));
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

    [Fact]
    public async Task BuildCompleteBeforeProcessStartedDoesNotWaitForRestartLock()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var barrier = Assert.Single(watched.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
        var waiting = barrier.Callback(app.Services, CancellationToken.None);

        await DotnetWatchCoordinator.ReportBuildCompleteWhileRestartLockedForTestingAsync(
            watched.Resource, app.Services, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10));

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

        var error = await Assert.ThrowsAsync<DotnetWatchIncompatibleGraphException>(() =>
            DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
                watched.Resource, success: true, app.Services, CancellationToken.None));
        var consumerError = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            waiting.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains(
            changedSdk ? "SDK version changed" : "evaluated output domains changed",
            error.Message);
        Assert.Equal("Watch group 'dotnet-watch' failed. See the 'dotnet-watch' resource logs for details. " +
            "Fix the cause and restart the complete Aspire run.", consumerError.Message);
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
    public async Task TransientEvaluationFailureRetainsLastGoodGraphAndRetriesOnNextEdit()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var attempts = 0;
        evaluator.BeforeEvaluation = _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new DistributedApplicationException("The project file is temporarily invalid.");
            }
            return Task.CompletedTask;
        };

        await DotnetWatchCoordinator.ReportProjectFileChangeForTestingAsync(
            watched.Resource, Metadata(watched).ProjectPath, app.Services, TestContext.Current.CancellationToken);
        var afterFailure = evaluator.EvaluationCount;

        evaluator.BeforeEvaluation = null;
        await DotnetWatchCoordinator.ReportProjectFileChangeForTestingAsync(
            watched.Resource, Metadata(watched).ProjectPath, app.Services, TestContext.Current.CancellationToken);

        Assert.True(evaluator.EvaluationCount > afterFailure);
        Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        Assert.False(Metadata(watched).SuppressBuild);
    }

    [Fact]
    public async Task RefreshOnlyMismatchBetweenWatchContextsIsRetriedOnce()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var unstable = false;
        var evaluations = 0;
        var generatedImport = Path.Combine(workspace.Path, "obj", "watched.csproj.nuget.g.props");
        var evaluator = new DotnetWatchPreflightTestServices
        {
            TransformWithWorkingDirectory = (_, globalProperties, _, _, graph) =>
            {
                if (!unstable || globalProperties)
                {
                    return graph;
                }

                return Interlocked.Increment(ref evaluations) == 2
                    ? WithImports(graph, generatedImport)
                    : graph;
            }
        };
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        unstable = true;

        await DotnetWatchCoordinator.ReportProjectFileChangeForTestingAsync(
            watched.Resource, Path.Combine(workspace.Path, "unowned.props"),
            app.Services, TestContext.Current.CancellationToken);

        Assert.Equal(6, evaluations);
        Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        Assert.False(Metadata(watched).SuppressBuild);
    }

    [Fact]
    public async Task IndependentExcludedBuildDoesNotBlockWatcherPreparation()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        AddProject(builder, workspace, "watched");
        AddProject(builder, workspace, "ordinary").WithWatch(false);
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());

        var arguments = await ArgumentEvaluator.GetArgumentListAsync(server, app.Services).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("--resource", arguments);
    }

    [Fact]
    public async Task IndependentOrdinaryBuildValidationDoesNotReevaluateWatchedGraphs()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluations = new Dictionary<string, int>(DotnetWatchProjectGraph.PathComparer);
        var evaluator = new DotnetWatchPreflightTestServices
        {
            Transform = (path, _, _, graph) =>
            {
                evaluations[path] = evaluations.GetValueOrDefault(path) + 1;
                return graph;
            }
        };
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        var ordinary = AddProject(builder, workspace, "ordinary").WithWatch(false);
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var watchedPath = DotnetWatchProjectGraph.Normalize(Metadata(watched).ProjectPath);
        var ordinaryPath = DotnetWatchProjectGraph.Normalize(Metadata(ordinary).ProjectPath);
        var watchedEvaluations = evaluations[watchedPath];
        var ordinaryEvaluations = evaluations[ordinaryPath];
        var build = Assert.Single(Model(app).Resources.OfType<DotnetProjectBuildResource>());

        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(build, app.Services),
            CancellationToken.None);

        Assert.Equal(watchedEvaluations, evaluations[watchedPath]);
        Assert.Equal(ordinaryEvaluations + 1, evaluations[ordinaryPath]);
    }

    [Fact]
    public async Task MultiRootTempRedirectIsOmittedWhenDefaultTempPreservesTheGraph()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        AddProject(builder, workspace, "first");
        AddProject(builder, workspace, "second");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            server, serviceProvider: app.Services);

        Assert.DoesNotContain(
            DotnetWatchCoordinator.GetTempVariables(OperatingSystem.IsWindows()),
            environment.ContainsKey);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task MultiRootTempRedirectUsesAShortPrivateCoreFxSafePathWhenNeeded()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "CoreFx uses named pipes instead of Unix sockets.");
        using var workspace = TemporaryWorkspace.Create(output);
        var defaultTemp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var evaluator = new DotnetWatchPreflightTestServices
        {
            TransformWithWorkingDirectory = (_, globalProperties, _, workingDirectory, graph) =>
                !globalProperties &&
                DotnetWatchProjectGraph.PathComparer.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory)), defaultTemp)
                    ? graph with { SdkVersion = "10.0.999" }
                    : graph
        };
        using var builder = CreateBuilder(evaluator);
        AddProject(builder, workspace, "first");
        AddProject(builder, workspace, "second");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            server, serviceProvider: app.Services);
        var tempDirectory = environment["TMPDIR"];

        Assert.StartsWith("/tmp/aspw-", tempDirectory, StringComparison.Ordinal);
        Assert.EndsWith("/t0", tempDirectory, StringComparison.Ordinal);
        Assert.Equal(DotnetWatchServerInfrastructure.PrivateDirectoryMode, File.GetUnixFileMode(tempDirectory));
        DotnetWatchServerInfrastructure.ValidateCoreFxPipePath(tempDirectory);
    }

    [Fact]
    public async Task NormalShutdownWaitsForWatchHostCleanupBeforeRemovingRunDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            server, serviceProvider: app.Services);
        var cleanupMarker = environment[DotnetWatchHostProtocol.CleanupMarkerVariable];
        var runDirectory = Path.GetDirectoryName(cleanupMarker)!;
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(server, app.Services),
            TestContext.Current.CancellationToken);
        var acknowledge = Task.Run(async () =>
        {
            await Task.Delay(100);
            await File.WriteAllTextAsync(cleanupMarker, "contained-cleanup-complete");
        });

        await app.DisposeAsync();
        await acknowledge;

        Assert.False(Directory.Exists(runDirectory));
    }

    [Fact]
    public async Task NormalHostStoppingRemovesRunDirectoryWithoutDisposingServices()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            server, serviceProvider: app.Services);
        var runDirectory = Path.GetDirectoryName(environment[DotnetWatchHostProtocol.CleanupMarkerVariable])!;
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();

        foreach (var service in app.Services.GetServices<IHostedService>().OfType<IHostedLifecycleService>())
        {
            await service.StoppingAsync(TestContext.Current.CancellationToken);
        }

        Assert.False(Directory.Exists(runDirectory));
        Assert.Same(notifications, app.Services.GetRequiredService<ResourceNotificationService>());
    }

    [Fact]
    public async Task NormalHostStoppingUsesCleanupAcknowledgementWithoutAStoppedNotification()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new()).WithTestAndResourceLogging(output);
        AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, TestContext.Current.CancellationToken);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            server, serviceProvider: app.Services);
        var cleanupMarker = environment[DotnetWatchHostProtocol.CleanupMarkerVariable];
        var runDirectory = Path.GetDirectoryName(cleanupMarker)!;
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(server, app.Services),
            TestContext.Current.CancellationToken);
        server.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watcher-instance", "instance", 0)]));
        await notifications.PublishUpdateAsync(server, "watcher-instance", snapshot => snapshot with
        {
            State = KnownResourceStates.Running
        });
        Assert.Equal(KnownResourceStates.Running, Assert.Single(notifications.GetCurrentStates(server)).Snapshot.State?.Text);
        var stoppedInstances = new List<string>();
        var stop = new ResourceCommandAnnotation(KnownResourceCommands.StopCommand, "Stop", _ => ResourceCommandState.Enabled,
            async context =>
            {
                stoppedInstances.Add(context.ResourceName);
                await File.WriteAllTextAsync(cleanupMarker, "contained-cleanup-complete", context.CancellationToken);
                return CommandResults.Success();
            }, null, arguments: null, null, null, null, false);
        Assert.Single(server.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>(),
            annotation => annotation.CommandName == KnownResourceCommands.StopCommand).Factory(stop);

        foreach (var service in app.Services.GetServices<IHostedService>().OfType<IHostedLifecycleService>())
        {
            await service.StoppingAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.Equal(["watcher-instance"], stoppedInstances);
        Assert.False(Directory.Exists(runDirectory));
        Assert.Same(notifications, app.Services.GetRequiredService<ResourceNotificationService>());
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

        var error = await Assert.ThrowsAsync<DotnetWatchIncompatibleGraphException>(() => builder.Eventing.PublishAsync(
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
        Assert.Equal("Watch group 'dotnet-watch' failed. See the 'dotnet-watch' resource logs for details. " +
            "Fix the cause and restart the complete Aspire run.", failure.Message);
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

    private static DotnetWatchProjectGraphResult WithImports(DotnetWatchProjectGraphResult graph, string importPath) =>
        graph with
        {
            Projects = graph.Projects.ToDictionary(pair => pair.Key,
                pair => pair.Value with { Imports = [.. pair.Value.Imports, DotnetWatchProjectGraph.Normalize(importPath)] },
                DotnetWatchProjectGraph.PathComparer)
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
