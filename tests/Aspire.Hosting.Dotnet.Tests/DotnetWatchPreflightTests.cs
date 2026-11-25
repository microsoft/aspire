// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001, ASPIREPROJECTS001

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Dotnet.Tests.Helpers;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Aspire.Shared.DotnetWatch;
using Microsoft.AspNetCore.InternalTesting;
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
        foreach (var (resource, sibling) in new[] { (first, "second"), (second, "first") })
        {
            resource.Resource.AddLifeCycleCommands();
            Assert.Equal("Replace the watcher of watch group 'dotnet-watch' with a fresh one that builds from source, and restart the group's services. " +
                $"Use after a crash, a change that hot reload can't apply, or a missed change, or when Restart doesn't help. This also restarts '{sibling}'.",
                Command(resource, KnownResourceCommands.RebuildCommand).DisplayDescription);
            var restart = Command(resource, KnownResourceCommands.RestartCommand);
            Assert.Equal(ResourceCommandState.Disabled, restart.UpdateState(StateContext(app.Services, KnownResourceStates.Running)));
            var result = await restart.ExecuteCommand(CommandContext(resource.Resource.Name, app.Services));
            Assert.False(result.Success);
            Assert.Equal("Watch group 'dotnet-watch' is starting or changing. Try again once 'first', 'second' are running, " +
                "or use Rebuild to restart the group with a fresh build.", result.Message);
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
        watched.Resource.AddLifeCycleCommands();
        var rebuild = Command(watched, KnownResourceCommands.RebuildCommand);
        Assert.Equal(ResourceCommandState.Disabled, rebuild.UpdateState(StateContext(app.Services, KnownResourceStates.FailedToStart)));
        var refused = await rebuild.ExecuteCommand(CommandContext("watched", app.Services));
        Assert.False(refused.Success);
        Assert.Equal(consumerError.Message, refused.Message);
    }

    [Fact]
    public async Task RebuildRecoversARecoverableGroupFailureWithOneFreshWatcher()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.AddLifeCycleCommands();
        var rebuild = Command(watched, KnownResourceCommands.RebuildCommand);
        var barrier = Assert.Single(watched.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
        var waiting = barrier.Callback(app.Services, CancellationToken.None);
        evaluator.BeforeEvaluation = _ => throw new DistributedApplicationException("The restored imports couldn't be evaluated.");

        await Assert.ThrowsAnyAsync<DistributedApplicationException>(() => DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None));
        var failure = await Assert.ThrowsAsync<DistributedApplicationException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("Watch group 'dotnet-watch' failed. See the 'dotnet-watch' resource logs for details. " +
            "Use Rebuild on one of its services ('watched') to restart the group with a fresh watcher, or restart the complete Aspire run.",
            failure.Message);
        Assert.Equal(ResourceCommandState.Enabled, rebuild.UpdateState(StateContext(app.Services, KnownResourceStates.FailedToStart)));
        evaluator.BeforeEvaluation = null;

        // Concurrent invocations (one per replica, or several users) share one fresh watcher.
        var first = rebuild.ExecuteCommand(CommandContext("watched", app.Services));
        var second = rebuild.ExecuteCommand(CommandContext("watched", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: false, app.Services, CancellationToken.None);

        foreach (var result in await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30)))
        {
            Assert.False(result.Success);
            Assert.Equal("The fresh build of watch group 'dotnet-watch' failed, so its services ('watched') aren't running. " +
                "Fix the errors and save; the watcher rebuilds and starts them automatically. " +
                "To see the errors, open 'dotnet-watch' Console logs in the dashboard, or run 'aspire logs dotnet-watch --tail 50'.",
                result.Message);
        }
        Assert.False(serverStarts.Reader.TryRead(out _));

        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await barrier.Callback(app.Services, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task StartOfANeverLaunchedInstanceRecoversItsFailedGroupAndThenStartsIt()
    {
        // The group failed before the instance's first start got past the build, so the instance never launched. Start
        // replaces the group's watcher, which only relaunches instances, and then runs the instance's first start as
        // Aspire's start does, outside the group's restart lock.
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        var starts = Channel.CreateUnbounded<string>();
        var start = Override(watched, KnownResourceCommands.StartCommand, context =>
        {
            starts.Writer.TryWrite(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        await FailTheWatchGroupBeforeItsFirstBuildAsync(app, watched, evaluator);

        var starting = start.ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Started 'watched-abc' with a fresh build of watch group 'dotnet-watch'.", result.Message);
        Assert.Equal("watched-abc", await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(starts.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StopWhileAStartWaitsForItsGroupsRecoveryCancelsThatStart()
    {
        // A Start of an instance that hasn't launched recovers the instance's failed watch group first, and then starts
        // the instance. A Stop meanwhile cancels that start: the group still recovers, and the instance doesn't start.
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var executor = new TestDcpExecutor { OnHasDcpObject = _ => false };
        builder.Services.AddSingleton<IDcpExecutor>(executor);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        executor.AddResource(watched.Resource, "watched-abc");
        var starts = Channel.CreateUnbounded<string>();
        var start = Override(watched, KnownResourceCommands.StartCommand, context =>
        {
            starts.Writer.TryWrite(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        var stop = Override(watched, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(null, null));
        await FailTheWatchGroupBeforeItsFirstBuildAsync(app, watched, evaluator);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", snapshot => snapshot with { State = KnownResourceStates.FailedToStart });

        var starting = start.ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        var stopped = await stop.ExecuteCommand(CommandContext("watched-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(stopped.Success, stopped.Message);
        Assert.Equal("Stopped 'watched-abc' before it started.", stopped.Message);
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Equal("Restarted watch group 'dotnet-watch' with a fresh build, but 'watched-abc' was stopped before it started.", result.Message);
        Assert.False(starts.Reader.TryRead(out _));
        Assert.True(notifications.TryGetCurrentState("watched-abc", out var current));
        Assert.Equal(KnownResourceStates.NotStarted, current.Snapshot.State?.Text);
    }

    [Fact]
    public async Task StartAfterAStopCanceledAStartThatWaitedForTheGroupsRecoveryIsNotLeftToThatStart()
    {
        // The Stop canceled the earlier Start's claim on the instance's first start, so a later Start doesn't report that
        // the instance is already starting: while the group recovers, it asks to start the instance again afterwards.
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var executor = new TestDcpExecutor { OnHasDcpObject = _ => false };
        builder.Services.AddSingleton<IDcpExecutor>(executor);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        executor.AddResource(watched.Resource, "watched-abc");
        var starts = Channel.CreateUnbounded<string>();
        var start = Override(watched, KnownResourceCommands.StartCommand, context =>
        {
            starts.Writer.TryWrite(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        var stop = Override(watched, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(null, null));
        await FailTheWatchGroupBeforeItsFirstBuildAsync(app, watched, evaluator);

        var first = start.ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True((await stop.ExecuteCommand(CommandContext("watched-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30))).Success);
        var during = await start.ExecuteCommand(CommandContext("watched-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        var result = await first.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(during.Success);
        Assert.Equal("Watch group 'dotnet-watch' is changing. Start 'watched-abc' again when it's done.", during.Message);
        Assert.True(result.Canceled, result.Message);
        Assert.False(starts.Reader.TryRead(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStartCanceledWhileItsGroupRecoveredDoesNotReportALaterStartAsItsOwn(bool launched)
    {
        // A Stop canceled the Start's first start while the group recovered, and another start of the instance is in
        // progress, or already launched it, when the recovery ends. That start isn't this command's, so the command
        // reports the cancellation.
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var executor = new TestDcpExecutor { OnHasDcpObject = _ => false };
        builder.Services.AddSingleton<IDcpExecutor>(executor);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        executor.AddResource(watched.Resource, "watched-abc");
        var start = Override(watched, KnownResourceCommands.StartCommand, _ => Task.FromResult(CommandResults.Success()));
        var stop = Override(watched, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(null, null));
        await FailTheWatchGroupBeforeItsFirstBuildAsync(app, watched, evaluator);

        var starting = start.ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True((await stop.ExecuteCommand(CommandContext("watched-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30))).Success);
        Func<CustomResourceSnapshot, CustomResourceSnapshot> laterStart = launched
            ? _ => Raw(KnownResourceStates.Running, 4321)
            : snapshot => snapshot with { State = KnownResourceStates.Starting };
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", laterStart);
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Equal("Restarted watch group 'dotnet-watch' with a fresh build, but 'watched-abc' was stopped before it started.", result.Message);
    }

    [Fact]
    public async Task AStartCanceledWhileItWaitedForAPendingStartDoesNotReportALaterLaunchAsItsOwn()
    {
        // Aspire's first start of the instance was pending when the Start recovered the group, so the command waits for
        // that start to run. A Stop cancels the command's start, and a later start launches the instance before the
        // command checks on it again. That launch isn't this command's, so the command reports the cancellation.
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var executor = new TestDcpExecutor { OnHasDcpObject = _ => false };
        builder.Services.AddSingleton<IDcpExecutor>(executor);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        executor.AddResource(watched.Resource, "watched-abc");
        var start = Override(watched, KnownResourceCommands.StartCommand, _ => Task.FromResult(CommandResults.Success()));
        var stop = Override(watched, KnownResourceCommands.StopCommand, _ => Task.FromResult(CommandResults.Success()));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(null, null));
        await FailTheWatchGroupBeforeItsFirstBuildAsync(app, watched, evaluator);
        // Stands in for Aspire's first start, which waits for the instance's dependencies.
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", snapshot => snapshot with { State = KnownResourceStates.Starting });
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", snapshot => snapshot with { State = KnownResourceStates.Waiting });
        executor.OnCancelPendingStart = _ => Task.FromResult(PendingStartCancellation.Canceled);
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DotnetWatchCoordinator.SetReloadPollDelayForTesting(watched.Resource, async (_, cancellationToken) =>
        {
            polling.TrySetResult();
            await nextPoll.Task.WaitAsync(cancellationToken);
        });

        var starting = start.ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        // Between two of the command's checks, the Stop cancels its start, and a later start launches the instance.
        await polling.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var stopped = await stop.ExecuteCommand(CommandContext("watched-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 4321));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        nextPoll.SetResult();
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(stopped.Success, stopped.Message);
        Assert.Equal("Stopped 'watched-abc' before it started.", stopped.Message);
        Assert.False(result.Success, result.Message);
        Assert.True(result.Canceled);
        Assert.Equal("Restarted watch group 'dotnet-watch' with a fresh build, but 'watched-abc' was stopped before it started.", result.Message);
    }

    [Fact]
    public async Task StartOfALaunchedInstanceWhoseRelaunchWasCanceledRelaunchesItWithAFreshWatcher()
    {
        // The instance ran, and a later relaunch of it was canceled before DCP launched its proxy, so it shows NotStarted.
        // Its dependencies were satisfied when it first ran, so the fresh watcher that its Start uses relaunches it.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        var starts = Channel.CreateUnbounded<string>();
        var start = Override(watched, KnownResourceCommands.StartCommand, async context =>
        {
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Running, 4321));
            starts.Writer.TryWrite(context.ResourceName);
            return CommandResults.Success();
        });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 1234));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", snapshot => snapshot with { State = KnownResourceStates.NotStarted });

        var starting = start.ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        Assert.Equal("watched-abc", await starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Started 'watched-abc' with a fresh build of watch group 'dotnet-watch'.", result.Message);
    }

    [Fact]
    public async Task UnconfirmedCleanupMakesARecoverableFailureUnrecoverableAndCorrectsItsNotice()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        builder.Services.AddLogging(logging => logging.AddFakeLogging());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        // Preparing the watcher's environment makes the failure wait for the watcher's cleanup acknowledgement,
        // which nothing writes here, as when the owner was killed.
        await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(server, serviceProvider: app.Services);
        await FakeStoppedWatcherAsync(app);
        watched.Resource.AddLifeCycleCommands();
        var rebuild = Command(watched, KnownResourceCommands.RebuildCommand);
        evaluator.BeforeEvaluation = _ => throw new DistributedApplicationException("The restored imports couldn't be evaluated.");

        await Assert.ThrowsAnyAsync<DistributedApplicationException>(() => DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None));
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => rebuild.UpdateState(StateContext(app.Services, KnownResourceStates.FailedToStart)) == ResourceCommandState.Disabled,
            "The unconfirmed cleanup didn't make the failure unrecoverable.", retries: 12);

        const string unrecoverable = "Watch group 'dotnet-watch' failed. See the 'dotnet-watch' resource logs for details. " +
            "Fix the cause and restart the complete Aspire run.";
        var noticeId = DotnetWatchCoordinator.NoticeEventId(WatchNoticeKind.InfrastructureFailed);
        Assert.Equal(
            [
                "Watch group 'dotnet-watch' failed. See the 'dotnet-watch' resource logs for details. " +
                "Use Rebuild on one of its services ('watched') to restart the group with a fresh watcher, or restart the complete Aspire run.",
                unrecoverable
            ],
            app.Services.GetFakeLogCollector().GetSnapshot().Where(entry => entry.Id == noticeId).Select(entry => entry.Message));
        var refused = await rebuild.ExecuteCommand(CommandContext("watched", app.Services));
        Assert.False(refused.Success);
        Assert.Equal(unrecoverable, refused.Message);
    }

    [Fact]
    public async Task RebuildKeepsServicesRunningWhenTheProjectCannotBeEvaluated()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.AddLifeCycleCommands();
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        evaluator.BeforeEvaluation = _ => throw new DistributedApplicationException("The project file is temporarily invalid.");

        var result = await Command(watched, KnownResourceCommands.RebuildCommand)
            .ExecuteCommand(CommandContext("watched", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Equal("The project couldn't be evaluated, so watch group 'dotnet-watch' wasn't restarted and its services kept running: " +
            "The project file is temporarily invalid. Fix the project and try again.", result.Message);
        Assert.False(serverStarts.Reader.TryRead(out _));
        Assert.Equal(ResourceCommandState.Enabled, Command(watched, KnownResourceCommands.RebuildCommand)
            .UpdateState(StateContext(app.Services, KnownResourceStates.Running)));
    }

    [Fact]
    public async Task StartAfterStopRestartsOnlyThatInstanceWithAFreshWatcher()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var instanceStarts = Channel.CreateUnbounded<string>();
        var stop = Override(watched, KnownResourceCommands.StopCommand, async context =>
        {
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Finished, null));
            return CommandResults.Success();
        });
        var start = Override(watched, KnownResourceCommands.StartCommand, async context =>
        {
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Running, 4321));
            instanceStarts.Writer.TryWrite(context.ResourceName);
            return CommandResults.Success();
        });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 1234));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        Assert.True((await stop.ExecuteCommand(CommandContext("watched-abc", app.Services))).Success);
        Assert.True(notifications.TryGetCurrentState("watched-abc", out var stopped));
        Assert.Equal(KnownResourceStates.Exited, stopped.Snapshot.State?.Text);

        var starting = start.ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(notifications.TryGetCurrentState("watched-abc", out var waiting));
        Assert.Equal(KnownResourceStates.Waiting, waiting.Snapshot.State?.Text);
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        Assert.Equal("watched-abc", await instanceStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Started 'watched-abc' with a fresh build of watch group 'dotnet-watch'.", result.Message);
        Assert.True(notifications.TryGetCurrentState("watched-abc", out var running));
        Assert.Equal(KnownResourceStates.Running, running.Snapshot.State?.Text);
        Assert.False(serverStarts.Reader.TryRead(out _));
        Assert.False(instanceStarts.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StartAfterStopDoesNotWaitAgainForADependencyThatFinished()
    {
        // A dependency that ran to completion, such as EF migrations that run on start, doesn't run again. Waiting for
        // it again would hold the relaunch, and with it the watch group, forever.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var migrations = builder.AddExecutable("migrations", "dotnet", workspace.Path);
        var watched = AddProject(builder, workspace, "watched").WaitFor(migrations);
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        // Resolving the orchestrator subscribes its dependency wait to each start's BeforeResourceStartedEvent.
        _ = app.Services.GetRequiredService<ApplicationOrchestrator>();
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(migrations.Resource, snapshot => snapshot with { State = KnownResourceStates.Finished });
        var instanceStarts = Channel.CreateUnbounded<string>();
        var stop = Override(watched, KnownResourceCommands.StopCommand, async context =>
        {
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Finished, null));
            return CommandResults.Success();
        });
        var start = Override(watched, KnownResourceCommands.StartCommand, async context =>
        {
            // Stands in for DCP, which publishes the instance's BeforeResourceStartedEvent before it runs the instance.
            await builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(watched.Resource, app.Services) { ResourceInstanceId = context.ResourceName },
                context.CancellationToken);
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Running, 4321));
            instanceStarts.Writer.TryWrite(context.ResourceName);
            return CommandResults.Success();
        });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 1234));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        Assert.True((await stop.ExecuteCommand(CommandContext("watched-abc", app.Services))).Success);

        var starting = start.ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        Assert.Equal("watched-abc", await instanceStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        var result = await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        // Starts that aren't relaunches by the coordinator still wait for the dependencies.
        var filter = watched.Resource.Annotations.OfType<ResourceDependencyWaitFilterAnnotation>().Last();
        Assert.True(filter.ShouldWait("watched-abc"));
        Assert.True(filter.ShouldWait("watched-never-started"));
    }

    [Fact]
    public async Task StopDuringARelaunchStillSkipsTheDependencyWait()
    {
        // Stop records its intent before it waits for the group's lock, which the relaunch holds. The relaunch must
        // not start waiting for a dependency that finished, or Stop and the group wait for each other forever. The
        // Rebuild then reports that the instance stays stopped instead of waiting for it to run.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var migrations = builder.AddExecutable("migrations", "dotnet", workspace.Path);
        var watched = AddProject(builder, workspace, "watched").WaitFor(migrations);
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        _ = app.Services.GetRequiredService<ApplicationOrchestrator>();
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        watched.Resource.AddLifeCycleCommands();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(migrations.Resource, snapshot => snapshot with { State = KnownResourceStates.Finished });
        var stop = Override(watched, KnownResourceCommands.StopCommand, async context =>
        {
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Finished, null));
            return CommandResults.Success();
        });
        var stopping = new TaskCompletionSource<Task<ExecuteCommandResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = Override(watched, KnownResourceCommands.StartCommand, async context =>
        {
            // A user's Stop arrives while the coordinator relaunches the instance under the group's lock.
            stopping.TrySetResult(stop.ExecuteCommand(CommandContext(context.ResourceName, app.Services)));
            await builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(watched.Resource, app.Services) { ResourceInstanceId = context.ResourceName },
                context.CancellationToken);
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Running, 4321));
            return CommandResults.Success();
        });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 1234));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);

        var rebuilding = Command(watched, KnownResourceCommands.RebuildCommand).ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        var result = await rebuilding.WaitAsync(TimeSpan.FromSeconds(30));
        var stopResult = await (await stopping.Task.WaitAsync(TimeSpan.FromSeconds(30))).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Restarted watch group 'dotnet-watch' with a fresh build. 'watched-abc' stays stopped; use Start to run it.", result.Message);
        Assert.True(stopResult.Success, stopResult.Message);
        Assert.True(notifications.TryGetCurrentState("watched-abc", out var stopped));
        Assert.Equal(KnownResourceStates.Exited, stopped.Snapshot.State?.Text);
    }

    [Fact]
    public async Task RecoveryRelaunchesAnInstanceWhoseProxyFailedBeforeItRanWithoutWaitingAgainForItsDependencies()
    {
        // Aspire waits for an instance's dependencies before DCP launches its proxy, so a proxy that failed before it
        // ran still satisfied them. The proxy's failure fails the group; recovering it relaunches the instance, which
        // waiting again for a dependency that ran to completion would hold, and with it the watch group, forever.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        builder.Services.AddLogging(logging => logging.AddFakeLogging());
        var migrations = builder.AddExecutable("migrations", "dotnet", workspace.Path);
        var watched = AddProject(builder, workspace, "watched").WaitFor(migrations);
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        _ = app.Services.GetRequiredService<ApplicationOrchestrator>();
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        watched.Resource.AddLifeCycleCommands();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(migrations.Resource, snapshot => snapshot with { State = KnownResourceStates.Finished });
        Override(watched, KnownResourceCommands.StartCommand, async context =>
        {
            await builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(watched.Resource, app.Services) { ResourceInstanceId = context.ResourceName },
                context.CancellationToken);
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Running, 4321));
            return CommandResults.Success();
        });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Starting, 1234));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.FailedToStart, null));
        var noticeId = DotnetWatchCoordinator.NoticeEventId(WatchNoticeKind.InfrastructureFailed);
        await AsyncTestHelpers.AssertIsTrueRetryAsync(
            () => app.Services.GetFakeLogCollector().GetSnapshot().Any(entry => entry.Id == noticeId),
            "The proxy's failure didn't fail the watch group.");

        var rebuilding = Command(watched, KnownResourceCommands.RebuildCommand).ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        var result = await rebuilding.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Rebuilt 'watched-abc' with a fresh watcher for watch group 'dotnet-watch'.", result.Message);
    }

    [Fact]
    public async Task RestartRelaunchesTheProjectsOfItsGroupThatOthersWaitForFirst()
    {
        // The dependent is declared first, but a restart relaunches the group's projects after the projects of the group
        // that they wait for, so that each finds its dependencies running again.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var dependent = AddProject(builder, workspace, "dependent");
        var dependency = AddProject(builder, workspace, "dependency");
        dependent.WaitFor(dependency);
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        var commands = await RunWithADependentWaitingToStartAsync(app, dependent, dependency);
        Assert.True((await commands.StartDependent.ExecuteCommand(CommandContext("dependent-abc", app.Services))).Success);
        Assert.Equal("dependent-abc", await commands.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(dependent.Resource, app.Services);
        var rebuild = Override(dependent, KnownResourceCommands.RebuildCommand, _ => Task.FromResult(CommandResults.Success()));

        var rebuilding = rebuild.ExecuteCommand(CommandContext("dependent-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            dependent.Resource, success: true, app.Services, CancellationToken.None);
        Assert.Equal("dependency-def", await commands.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal("dependent-abc", await commands.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(dependent.Resource, app.Services);
        var result = await rebuilding.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Rebuilt 'dependent-abc' with a fresh watcher for watch group 'dotnet-watch'; also restarted 'dependency'.", result.Message);
    }

    [Fact]
    public async Task StopOfAnInstanceThatHasNotLaunchedCancelsItsStartWithoutTheWatchGroup()
    {
        // An instance that waits to start has no proxy that the group's watcher works with. Stopping it cancels its start
        // without stopping anything or recording a manual stop, so its next Start is Aspire's start again rather than a
        // restart of the group, which would wait for the instance's dependencies while it holds the group.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var executor = new TestDcpExecutor();
        builder.Services.AddSingleton<IDcpExecutor>(executor);
        var dependent = AddProject(builder, workspace, "dependent");
        var dependency = AddProject(builder, workspace, "dependency");
        dependent.WaitFor(dependency);
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        var commands = await RunWithADependentWaitingToStartAsync(app, dependent, dependency);
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        executor.AddResource(dependent.Resource, "dependent-abc");
        var canceled = false;
        executor.OnCancelPendingStart = async reference =>
        {
            if (canceled)
            {
                return PendingStartCancellation.None;
            }
            canceled = true;
            // As the executor does, show that the instance didn't start.
            await notifications.PublishUpdateAsync(reference.ModelResource, reference.DcpResourceName,
                snapshot => snapshot with { State = KnownResourceStates.NotStarted });
            return PendingStartCancellation.Canceled;
        };
        executor.OnHasDcpObject = _ => false;

        var stopped = await commands.StopDependent.ExecuteCommand(CommandContext("dependent-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));
        var stoppedAgain = await commands.StopDependent.ExecuteCommand(CommandContext("dependent-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(stopped.Success, stopped.Message);
        Assert.Equal("Stopped 'dependent-abc' before it started.", stopped.Message);
        Assert.True(stoppedAgain.Success, stoppedAgain.Message);
        Assert.Equal("'dependent-abc' isn't running.", stoppedAgain.Message);
        Assert.False(commands.Stops.Reader.TryRead(out _));
        Assert.True(notifications.TryGetCurrentState("dependent-abc", out var notStarted));
        Assert.Equal(KnownResourceStates.NotStarted, notStarted.Snapshot.State?.Text);

        var started = await commands.StartDependent.ExecuteCommand(CommandContext("dependent-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(started.Success, started.Message);
        Assert.Equal("dependent-abc", await commands.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(serverStarts.Reader.TryRead(out _));
    }

    [Fact]
    public async Task StopOfAnInstanceWhoseStartReachedDcpStopsItAndItsNextStartRelaunchesIt()
    {
        // Once an instance's start reaches DCP, its dependencies were satisfied and DCP is launching its proxy, so Stop
        // stops it like any launched instance. Its next Start restarts the group, which relaunches it without waiting for
        // its dependencies again, even before DCP reported the proxy: waiting for the stopped dependency would hold the
        // group forever.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var executor = new TestDcpExecutor();
        builder.Services.AddSingleton<IDcpExecutor>(executor);
        var dependent = AddProject(builder, workspace, "dependent");
        var dependency = AddProject(builder, workspace, "dependency");
        dependent.WaitFor(dependency);
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        var commands = await RunWithADependentWaitingToStartAsync(app, dependent, dependency);
        executor.AddResource(dependent.Resource, "dependent-abc");
        executor.OnCancelPendingStart = _ => Task.FromResult(PendingStartCancellation.Committed);
        Assert.True((await commands.StopDependency.ExecuteCommand(CommandContext("dependency-def", app.Services))).Success);

        var stopped = await commands.StopDependent.ExecuteCommand(CommandContext("dependent-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(stopped.Success, stopped.Message);
        Assert.Equal("dependent-abc", await commands.Stops.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));

        var starting = commands.StartDependent.ExecuteCommand(CommandContext("dependent-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            dependent.Resource, success: true, app.Services, CancellationToken.None);
        Assert.Equal("dependent-abc", await commands.Starts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(dependent.Resource, app.Services);
        var started = await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(started.Success, started.Message);
        Assert.Equal("Started 'dependent-abc' with a fresh build of watch group 'dotnet-watch'.", started.Message);
    }

    [Fact]
    public async Task RebuildOfANeverStartedInstanceDoesNotWaitForItToStart()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched").WithExplicitStart();
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        watched.Resource.AddLifeCycleCommands();
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);

        var rebuilding = Command(watched, KnownResourceCommands.RebuildCommand).ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        var result = await rebuilding.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Restarted watch group 'dotnet-watch' with a fresh build. 'watched-abc' wasn't running, so it wasn't started; use Start to run it.",
            result.Message);
    }

    [Fact]
    public async Task RebuildWaitsForAFirstStartThatWaitedForAFailedBuild()
    {
        // A first start waits for the watcher's build, and keeps waiting when that build fails. A fresh watcher's
        // successful build continues the start, so Rebuild reports on the instance instead of calling it not started.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        watched.Resource.AddLifeCycleCommands();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        // Stands in for Aspire's first start: DCP publishes the instance before creating its proxy, then Aspire publishes
        // Starting and waits in the instance's BeforeResourceStartedEvent.
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => new CustomResourceSnapshot
        {
            ResourceType = "Executable",
            State = null,
            Properties = [new("executable.pid", null)]
        });
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", snapshot => snapshot with { State = KnownResourceStates.Starting });
        var firstStart = builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(watched.Resource, app.Services) { ResourceInstanceId = "watched-abc" }, CancellationToken.None);
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: false, app.Services, CancellationToken.None);
        Assert.False(firstStart.IsCompleted);

        var rebuilding = Command(watched, KnownResourceCommands.RebuildCommand).ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await firstStart.WaitAsync(TimeSpan.FromSeconds(30));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 1234));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        var result = await rebuilding.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Rebuilt 'watched-abc' with a fresh watcher for watch group 'dotnet-watch'.", result.Message);
    }

    [Fact]
    public async Task RebuildReportsAFirstStartThatFailedWhileTheGroupWasRebuilt()
    {
        // The first start was pending when Rebuild arrived, and it fails, for example because a dependency failed, before
        // the fresh build succeeds. Rebuild reports that the start failed rather than that the instance wasn't started.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        watched.Resource.AddLifeCycleCommands();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => new CustomResourceSnapshot
        {
            ResourceType = "Executable",
            State = null,
            Properties = [new("executable.pid", null)]
        });
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", snapshot => snapshot with { State = KnownResourceStates.Starting });
        var firstStart = builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(watched.Resource, app.Services) { ResourceInstanceId = "watched-abc" }, CancellationToken.None);
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: false, app.Services, CancellationToken.None);

        var rebuilding = Command(watched, KnownResourceCommands.RebuildCommand).ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", snapshot => snapshot with { State = KnownResourceStates.FailedToStart });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await firstStart.WaitAsync(TimeSpan.FromSeconds(30));
        var result = await rebuilding.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success, result.Message);
        Assert.Equal(
            "'watched-abc' was starting when watch group 'dotnet-watch' was rebuilt, but its start failed, for example because a resource it " +
            "waits for failed. See its state and console logs, then use Start to try again.",
            result.Message);
    }

    [Fact]
    public async Task RebuildReportsAFirstStartThatFinishedBeforeTheBuildOutcome()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        watched.Resource.AddLifeCycleCommands();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(null, null));
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", snapshot => snapshot with
        {
            State = KnownResourceStates.Starting
        });

        var rebuilding = Command(watched, KnownResourceCommands.RebuildCommand).ExecuteCommand(CommandContext("watched-abc", app.Services));
        await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));
        // The build outcome and the first start wake independent continuations. Deliver the start observations first
        // so the command deterministically sees a completed first start, rather than a still-pending one.
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 1234));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        var result = await rebuilding.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(result.Success, result.Message);
        Assert.Equal("Rebuilt 'watched-abc' with a fresh watcher for watch group 'dotnet-watch'.", result.Message);
    }

    [Fact]
    public async Task LaunchOfAStoppedProjectReplacesTheWatcherAndKeepsTheInstanceStopped()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var serverStarts = await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var stop = Override(watched, KnownResourceCommands.StopCommand, async context =>
        {
            await notifications.PublishUpdateAsync(watched.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Finished, null));
            return CommandResults.Success();
        });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 1234));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        Assert.True((await stop.ExecuteCommand(CommandContext("watched-abc", app.Services))).Success);
        Assert.False(serverStarts.Reader.TryRead(out _));

        // The watcher relaunches a crashed application on the next edit even after its proxy is gone.
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);

        Assert.Equal("watcher-instance", await serverStarts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.True(notifications.TryGetCurrentState("watched-abc", out var stopped));
        Assert.Equal(KnownResourceStates.Exited, stopped.Snapshot.State?.Text);
    }

    [Fact]
    public async Task RebuildReportsAFailedRefreshDuringWhichAStoppedProjectWasLaunched()
    {
        // While a Rebuild's refresh stops the group's services, the old watcher launches a stopped project. Purging that
        // launch requests another fresh watcher, which commands that arrive later join. When the refresh then fails, the
        // Rebuild that started it reports the failure instead of waiting for a refresh that never completes.
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = CreateBuilder(new());
        var watched = AddProject(builder, workspace, "watched");
        var stopped = AddProject(builder, workspace, "stopped");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        await FakeStoppedWatcherAsync(app);
        watched.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watched-abc", "abc", 0)]));
        stopped.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("stopped-def", "def", 0)]));
        watched.Resource.AddLifeCycleCommands();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var stop = Override(stopped, KnownResourceCommands.StopCommand, async context =>
        {
            await notifications.PublishUpdateAsync(stopped.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Finished, null));
            return CommandResults.Success();
        });
        Override(watched, KnownResourceCommands.StopCommand, async _ =>
        {
            await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(stopped.Resource, app.Services);
            return CommandResults.Failure("The service couldn't be stopped.");
        });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None);
        await notifications.PublishUpdateAsync(watched.Resource, "watched-abc", _ => Raw(KnownResourceStates.Running, 1234));
        await notifications.PublishUpdateAsync(stopped.Resource, "stopped-def", _ => Raw(KnownResourceStates.Running, 2345));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(watched.Resource, app.Services);
        Assert.True((await stop.ExecuteCommand(CommandContext("stopped-def", app.Services))).Success);

        var result = await Command(watched, KnownResourceCommands.RebuildCommand)
            .ExecuteCommand(CommandContext("watched-abc", app.Services)).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Equal("Watch group 'dotnet-watch' failed. See the 'dotnet-watch' resource logs for details. " +
            "Use Rebuild on one of its services ('watched', 'stopped') to restart the group with a fresh watcher, or restart the complete Aspire run.",
            result.Message);
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
    public async Task AnEditSavedWhileEvaluationFailsIsEvaluatedAfterTheFailure()
    {
        // The watcher reports each saved edit once. An edit saved while the previous one fails evaluation joins the
        // failing pass, which must hand it to a new pass rather than drop it.
        using var workspace = TemporaryWorkspace.Create(output);
        var evaluator = new DotnetWatchPreflightTestServices();
        using var builder = CreateBuilder(evaluator);
        var watched = AddProject(builder, workspace, "watched");
        await using var app = builder.Build();
        await BeforeStartAsync(app, CancellationToken.None);
        var corrected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        evaluator.BeforeEvaluation = cancellationToken =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                _ = DotnetWatchCoordinator.ReportProjectFileChangeForTestingAsync(
                    watched.Resource, Path.Combine(workspace.Path, "corrected.props"), app.Services, CancellationToken.None);
                throw new DistributedApplicationException("The project file is temporarily invalid.");
            }
            corrected.TrySetResult();
            return Task.CompletedTask;
        };

        await DotnetWatchCoordinator.ReportProjectFileChangeForTestingAsync(
            watched.Resource, Path.Combine(workspace.Path, "invalid.props"), app.Services, TestContext.Current.CancellationToken);
        await corrected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // Joins the pass that evaluates the corrected edit, and returns when no pass is left.
        await DotnetWatchCoordinator.ReportProjectFileChangeForTestingAsync(
            watched.Resource, Path.Combine(workspace.Path, "unowned.props"), app.Services, TestContext.Current.CancellationToken);

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
            // Only a build started in the default temp directory selects another SDK; the root-bound watcher doesn't.
            TransformBuild = (_, buildDirectory, graph) =>
                DotnetWatchProjectGraph.PathComparer.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(buildDirectory)), defaultTemp)
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

    /// <summary>
    /// Gives the watch server one exited DCP instance and an original Start command that records each start, so a
    /// fresh watcher can be started without DCP. The test reports the watcher's build through the test hooks.
    /// </summary>
    private static async Task<Channel<string>> FakeStoppedWatcherAsync(DistributedApplication app)
    {
        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        server.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("watcher-instance", "instance", 0)]));
        await app.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(server, "watcher-instance", snapshot => snapshot with
        {
            State = KnownResourceStates.Exited
        });
        var starts = Channel.CreateUnbounded<string>();
        var start = new ResourceCommandAnnotation(KnownResourceCommands.StartCommand, "Start", _ => ResourceCommandState.Enabled,
            context =>
            {
                starts.Writer.TryWrite(context.ResourceName);
                return Task.FromResult(CommandResults.Success());
            }, null, arguments: null, null, null, null, false);
        Assert.Single(server.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>(),
            annotation => annotation.CommandName == KnownResourceCommands.StartCommand).Factory(start);
        return starts;
    }

    /// <summary>
    /// Fails the watch group of <paramref name="watched"/> before its first build completes, with a failure that a fresh
    /// watcher can recover from. A first start of the project, which waits for that build, fails without launching.
    /// </summary>
    private static async Task FailTheWatchGroupBeforeItsFirstBuildAsync(
        DistributedApplication app, IResourceBuilder<DotnetProjectResource> watched, DotnetWatchPreflightTestServices evaluator)
    {
        var barrier = Assert.Single(watched.Resource.Annotations.OfType<DotnetProgramBuildCompletionAnnotation>());
        var waiting = barrier.Callback(app.Services, CancellationToken.None);
        evaluator.BeforeEvaluation = _ => throw new DistributedApplicationException("The restored imports couldn't be evaluated.");
        await Assert.ThrowsAnyAsync<DistributedApplicationException>(() => DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            watched.Resource, success: true, app.Services, CancellationToken.None));
        await Assert.ThrowsAsync<DistributedApplicationException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        evaluator.BeforeEvaluation = null;
    }

    private sealed record DependencyCommands(
        ResourceCommandAnnotation StartDependent,
        ResourceCommandAnnotation StopDependent,
        ResourceCommandAnnotation StartDependency,
        ResourceCommandAnnotation StopDependency,
        Channel<string> Starts,
        Channel<string> Stops);

    /// <summary>
    /// Runs <paramref name="dependency"/> while <paramref name="dependent"/>, which waits for it, waits to start: DCP
    /// hasn't launched the dependent. The original commands stand in for DCP and record the instances that they start
    /// and stop. A start of the dependent stands in for Aspire's start: unless the coordinator relaunches it, it waits for
    /// the dependency to run.
    /// </summary>
    private static async Task<DependencyCommands> RunWithADependentWaitingToStartAsync(
        DistributedApplication app, IResourceBuilder<DotnetProjectResource> dependent, IResourceBuilder<DotnetProjectResource> dependency)
    {
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        var starts = Channel.CreateUnbounded<string>();
        var stops = Channel.CreateUnbounded<string>();
        dependent.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("dependent-abc", "abc", 0)]));
        dependency.Resource.Annotations.Add(new DcpInstancesAnnotation([new DcpInstance("dependency-def", "def", 0)]));
        // DCP reports a stopped proxy later; until it does, the instance has no reported proxy.
        var stopDependent = Override(dependent, KnownResourceCommands.StopCommand, context =>
        {
            stops.Writer.TryWrite(context.ResourceName);
            return Task.FromResult(CommandResults.Success());
        });
        var startDependent = Override(dependent, KnownResourceCommands.StartCommand, async context =>
        {
            var filter = dependent.Resource.Annotations.OfType<ResourceDependencyWaitFilterAnnotation>().Last();
            if (filter.ShouldWait(context.ResourceName))
            {
                await notifications.WaitForResourceAsync(dependency.Resource.Name, KnownResourceStates.Running, context.CancellationToken);
            }
            await notifications.PublishUpdateAsync(dependent.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Running, 4321));
            starts.Writer.TryWrite(context.ResourceName);
            return CommandResults.Success();
        });
        var stopDependency = Override(dependency, KnownResourceCommands.StopCommand, async context =>
        {
            await notifications.PublishUpdateAsync(dependency.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Finished, null));
            return CommandResults.Success();
        });
        var startDependency = Override(dependency, KnownResourceCommands.StartCommand, async context =>
        {
            await notifications.PublishUpdateAsync(dependency.Resource, context.ResourceName, _ => Raw(KnownResourceStates.Running, 5432));
            await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(dependency.Resource, app.Services);
            starts.Writer.TryWrite(context.ResourceName);
            return CommandResults.Success();
        });
        await DotnetWatchCoordinator.ReportBuildCompleteForTestingAsync(
            dependency.Resource, success: true, app.Services, CancellationToken.None);
        await notifications.PublishUpdateAsync(dependency.Resource, "dependency-def", _ => Raw(KnownResourceStates.Running, 1234));
        await DotnetWatchCoordinator.ReportProcessStartedForTestingAsync(dependency.Resource, app.Services);
        // DCP describes each instance before Aspire starts it; Aspire then waits for the dependency.
        await notifications.PublishUpdateAsync(dependent.Resource, "dependent-abc", _ => Raw(null, null));
        await notifications.PublishUpdateAsync(dependent.Resource, "dependent-abc", snapshot => snapshot with { State = KnownResourceStates.Waiting });
        return new(startDependent, stopDependent, startDependency, stopDependency, starts, stops);
    }

    /// <summary>Applies the watched project's lifecycle override to an original command that the test controls.</summary>
    private static ResourceCommandAnnotation Override(
        IResourceBuilder<DotnetProjectResource> resource, string name, Func<ExecuteCommandContext, Task<ExecuteCommandResult>> execute)
    {
        var original = new ResourceCommandAnnotation(name, name, _ => ResourceCommandState.Enabled,
            execute, null, arguments: null, null, null, null, false);
        return resource.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
            .Last(annotation => annotation.CommandName == name).Factory(original);
    }

    /// <summary>A raw DCP executable snapshot of a watched instance's proxy.</summary>
    private static CustomResourceSnapshot Raw(string? state, int? pid) => new()
    {
        ResourceType = "Executable",
        State = state,
        Properties = [new("executable.pid", pid)]
    };

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task BeforeStartAsync(DistributedApplication app, CancellationToken cancellationToken);
}
