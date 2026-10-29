// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using System.Collections.Concurrent;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

[Trait("Partition", "2")]
public class ResourceCommandServiceTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaunchGroupLateStartPreparesCustomDependencyWithoutInitialState(bool configurationOnly)
    {
        using var builder = CreateBuilder();
        if (configurationOnly)
        {
            builder.Configuration["Aspire:LaunchGroups:0"] = "selected";
            builder.Configuration["Aspire:LaunchGroups:1"] = "other";
        }
        else
        {
            builder.DefineLaunchGroups("selected", "other");
        }
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var dependencyEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDependency = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootEntered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new ConcurrentQueue<string>();
        var dependency = builder.AddResource(new CustomResource("dependency")).WithExplicitStart();
        dependency.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            started.Enqueue(context.ResourceName);
            dependencyEntered.TrySetResult();
            await releaseDependency.Task.WaitAsync(context.CancellationToken);
            await context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
                dependency.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });
            return CommandResults.Success();
        });
        var root = builder.AddResource(new CustomResource("root")).WithExplicitStart().WaitForStart(dependency);
        root.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            var wait = context.Services.GetRequiredService<ResourceNotificationService>()
                .WaitForDependenciesAsync(root.Resource, context.CancellationToken);
            rootEntered.TrySetResult(wait);
            await wait;
            started.Enqueue(context.ResourceName);
            return CommandResults.Success();
        });
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        _ = app.Services.GetRequiredService<ApplicationOrchestrator>();
        await app.Services.GetRequiredService<DcpExecutorEvents>()
            .PublishAsync(new OnResourcesPreparedContext(TestContext.Current.CancellationToken));
        Assert.True(dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(app.ResourceNotifications.TryGetCurrentState(dependency.Resource.Name, out var initial));
        Assert.Null(initial.Snapshot.State);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var start = app.ResourceCommands.ExecuteCommandAsync(root.Resource, KnownResourceCommands.StartCommand, cts.Token);
        try
        {
            var wait = await rootEntered.Task.DefaultTimeout();
            Assert.True(app.ResourceNotifications.TryGetCurrentState(dependency.Resource.Name, out var preparing));
            Assert.Equal(KnownResourceStates.Starting, preparing.Snapshot.State?.Text);
            await dependencyEntered.Task.DefaultTimeout();
            Assert.False(wait.IsCompleted);

            releaseDependency.TrySetResult();
            var result = await start.DefaultTimeout();

            Assert.True(result.Success, result.Message);
            Assert.Equal(["dependency", "root"], started);
        }
        finally
        {
            releaseDependency.TrySetResult();
            await cts.CancelAsync();
            await start.DefaultTimeout();
        }
    }

    [Theory]
    [InlineData("Finished", WaitType.WaitUntilStarted, false)]
    [InlineData("Exited", WaitType.WaitUntilStarted, false)]
    [InlineData("Finished", WaitType.WaitUntilHealthy, false)]
    [InlineData("Exited", WaitType.WaitUntilHealthy, false)]
    [InlineData("Exited", WaitType.WaitUntilHealthy, true)]
    public async Task LaunchGroupLateStartWaitsForStoppedDependencyRestart(string stoppedState, WaitType waitType, bool childDependency)
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("selected", "other");
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var restartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRestart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootEntered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postgres = childDependency ? builder.AddPostgres("dependency").WithLaunchGroup("other") : null;
        IResourceBuilder<IResource> dependency = postgres is not null
            ? postgres
            : builder.AddResource(new CustomResource("dependency")).WithLaunchGroup("other");
        IResourceBuilder<IResource> waitedResource = postgres is not null ? postgres.AddDatabase("database") : dependency;
        dependency.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            restartEntered.TrySetResult();
            await releaseRestart.Task.WaitAsync(context.CancellationToken);
            await context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
                waitedResource.Resource, snapshot => snapshot with
                {
                    State = KnownResourceStates.Running,
                    ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
                });
            return CommandResults.Success();
        });
        var root = builder.AddResource(new CustomResource("root")).WithLaunchGroup("other")
            .WithAnnotation(new WaitAnnotation(waitedResource.Resource, waitType));
        root.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();
            var wait = notifications.WaitForDependenciesAsync(root.Resource, context.CancellationToken);
            rootEntered.TrySetResult(wait);
            await wait;
            return CommandResults.Success();
        });
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        await app.ResourceNotifications.PublishUpdateAsync(dependency.Resource, snapshot => snapshot with
        {
            State = stoppedState,
            ExitCode = 0,
            ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
        });
        await app.ResourceNotifications.PublishUpdateAsync(root.Resource, snapshot => snapshot with { State = KnownResourceStates.NotStarted });
        if (childDependency)
        {
            await app.ResourceNotifications.PublishUpdateAsync(waitedResource.Resource, snapshot => snapshot with
            {
                State = stoppedState,
                ExitCode = 0,
                ResourceReadyEvent = new EventSnapshot(Task.CompletedTask)
            });
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var start = app.ResourceCommands.ExecuteCommandAsync(root.Resource, KnownResourceCommands.StartCommand, cts.Token);
        try
        {
            await restartEntered.Task.DefaultTimeout();
            var wait = await rootEntered.Task.DefaultTimeout();
            Assert.False(wait.IsCompleted);
            Assert.True(app.ResourceNotifications.TryGetCurrentState(dependency.Resource.Name, out var current));
            Assert.Equal(KnownResourceStates.Starting, current.Snapshot.State?.Text);
            Assert.Null(current.Snapshot.ExitCode);
            Assert.True(current.Snapshot.ResourceReadyEvent?.EventTask.IsCompletedSuccessfully);
            Assert.True(app.ResourceNotifications.TryGetCurrentState(waitedResource.Resource.Name, out var waited));
            Assert.Equal(KnownResourceStates.Starting, waited.Snapshot.State?.Text);

            releaseRestart.TrySetResult();
            Assert.True((await start.DefaultTimeout()).Success);
        }
        finally
        {
            releaseRestart.TrySetResult();
            await cts.CancelAsync();
        }
    }

    [Theory]
    [InlineData(true, false, "NotStarted")]
    [InlineData(false, false, "NotStarted")]
    [InlineData(false, true, "NotStarted")]
    [InlineData(true, false, "Finished")]
    [InlineData(false, false, "Finished")]
    [InlineData(false, true, "Finished")]
    public async Task LaunchGroupLateStartsDoNotDeadlockMixedReferenceAndWaitGraph(bool rootSucceeds, bool throwOnFailure, string initialState)
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("selected", "other");
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var started = new ConcurrentQueue<string>();
        var root = builder.AddResource(new CustomResource("root")).WithLaunchGroup("other");
        var dependency = builder.AddResource(new CustomResource("dependency")).WithLaunchGroup("other").WaitForStart(root);
        root.WithReferenceRelationship(dependency);
        foreach (var resource in new[] { dependency, root })
        {
            resource.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
            {
                var notifications = context.Services.GetRequiredService<ResourceNotificationService>();
                await notifications.WaitForDependenciesAsync(resource.Resource, context.CancellationToken);
                started.Enqueue(context.ResourceName);
                if (resource == root && !rootSucceeds)
                {
                    if (throwOnFailure)
                    {
                        throw new InvalidOperationException("Root failed.");
                    }

                    return new ExecuteCommandResult { Success = false, Message = "Root failed." };
                }

                await notifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });
                return CommandResults.Success();
            });
        }
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        foreach (var resource in new[] { dependency, root })
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
            {
                State = initialState,
                ExitCode = initialState == KnownResourceStates.Finished ? 0 : null
            });
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var result = await app.ResourceCommands.ExecuteCommandAsync(root.Resource, KnownResourceCommands.StartCommand, cts.Token).DefaultTimeout();

            Assert.Equal(rootSucceeds, result.Success);
            Assert.Equal(rootSucceeds ? ["root", "dependency"] : ["root"], started);
            if (rootSucceeds)
            {
                Assert.True(app.ResourceNotifications.TryGetCurrentState(dependency.Resource.Name, out var current));
                Assert.Equal(KnownResourceStates.Running, current.Snapshot.State?.Text);
            }
            else
            {
                Assert.False(result.Canceled);
                Assert.Equal("Root failed.", result.Message);
            }
        }
        finally
        {
            await cts.CancelAsync();
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task LaunchGroupOverlappingDirectAndDependentStartsRunOnce(bool directFirst, bool publishRunning)
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("selected", "other");
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var arguments = new ConcurrentQueue<string?>();
        var dependency = builder.AddResource(new CustomResource("dependency")).WithLaunchGroup("other");
        dependency.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            Interlocked.Increment(ref invocationCount);
            arguments.Enqueue(context.Arguments.GetString("message"));
            entered.TrySetResult();
            await release.Task.WaitAsync(context.CancellationToken);
            if (publishRunning)
            {
                await context.Services.GetRequiredService<ResourceNotificationService>()
                    .PublishUpdateAsync(dependency.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });
            }

            return CommandResults.Success();
        }, commandOptions: new CommandOptions
        {
            Arguments = [new InteractionInput { Name = "message", InputType = InputType.Text, Value = "default" }]
        });
        var root = builder.AddResource(new CustomResource("root")).WithLaunchGroup("other")
            .WithReferenceRelationship(dependency)
            .WithCommand(KnownResourceCommands.StartCommand, "Start", _ =>
            {
                rootStarted.TrySetResult();
                return Task.FromResult(CommandResults.Success());
            });
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        foreach (var resource in new[] { dependency, root })
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with { State = KnownResourceStates.NotStarted });
        }

        Task<ExecuteCommandResult> StartDirectAsync() => app.ResourceCommands.ExecuteCommandAsync(
            dependency.Resource, KnownResourceCommands.StartCommand,
            new Dictionary<string, string?> { ["message"] = "explicit" }, TestContext.Current.CancellationToken);
        Task<ExecuteCommandResult> StartDependentAsync() => app.ResourceCommands.ExecuteCommandAsync(
            root.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);

        try
        {
            var firstStart = directFirst ? StartDirectAsync() : StartDependentAsync();
            await entered.Task.DefaultTimeout();
            var overlappingStart = directFirst ? StartDependentAsync() : StartDirectAsync();
            await rootStarted.Task.DefaultTimeout();

            Assert.False(overlappingStart.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref invocationCount));

            release.TrySetResult();
            var results = await Task.WhenAll(firstStart, overlappingStart).DefaultTimeout();

            Assert.All(results, result => Assert.True(result.Success));
            Assert.Equal(1, invocationCount);
            Assert.Equal([directFirst ? "explicit" : "default"], arguments);
            Assert.True(app.ResourceNotifications.TryGetCurrentState(dependency.Resource.Name, out var current));
            Assert.Equal(publishRunning ? KnownResourceStates.Running : KnownResourceStates.Starting, current.Snapshot.State?.Text);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task LaunchGroupOverlappingStartWaitsForPredecessorSnapshotPreparation()
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("selected", "other");
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePreparation = new ManualResetEventSlim();
        var blocked = 0;
        var rootEntered = 0;
        var dependency = builder.AddResource(new CustomResource("dependency")).WithLaunchGroup("other");
        dependency.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            await context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
                dependency.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });
            return CommandResults.Success();
        }, commandOptions: new CommandOptions
        {
            UpdateState = context =>
            {
                if (context.ResourceSnapshot.State?.Text == KnownResourceStates.Starting &&
                    Interlocked.Exchange(ref blocked, 1) == 0)
                {
                    // The start is registered, but notification snapshot publication has not completed.
                    preparing.TrySetResult();
                    releasePreparation.Wait(TestContext.Current.CancellationToken);
                }

                return ResourceCommandState.Enabled;
            }
        });
        var root = builder.AddResource(new CustomResource("root")).WithLaunchGroup("other").WaitForStart(dependency);
        root.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            Interlocked.Exchange(ref rootEntered, 1);
            await context.Services.GetRequiredService<ResourceNotificationService>()
                .WaitForDependenciesAsync(root.Resource, context.CancellationToken);
            return CommandResults.Success();
        });
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        foreach (var resource in new[] { dependency, root })
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
            {
                State = KnownResourceStates.Finished,
                ExitCode = 0
            });
        }

        var directStart = Task.Run(() => app.ResourceCommands.ExecuteCommandAsync(
            dependency.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken));
        try
        {
            await preparing.Task.DefaultTimeout();
            var dependentStart = app.ResourceCommands.ExecuteCommandAsync(
                root.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);
            Assert.False(dependentStart.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref rootEntered));

            releasePreparation.Set();
            var results = await Task.WhenAll(directStart, dependentStart).DefaultTimeout();
            Assert.All(results, result => Assert.True(result.Success, result.Message));
            Assert.Equal(1, Volatile.Read(ref rootEntered));
        }
        finally
        {
            releasePreparation.Set();
            await directStart.DefaultTimeout();
        }
    }

    [Fact]
    public async Task LaunchGroupFailedWaitingStartCanRetryItsDependency()
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("selected", "other");
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var attempts = 0;
        var dependency = builder.AddResource(new CustomResource("dependency")).WithLaunchGroup("other");
        dependency.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return new ExecuteCommandResult { Success = false, Message = "First attempt failed." };
            }

            await context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
                dependency.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });
            return CommandResults.Success();
        });
        var root = builder.AddResource(new CustomResource("root")).WithLaunchGroup("other").WaitForStart(dependency);
        root.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            await context.Services.GetRequiredService<ResourceNotificationService>()
                .WaitForDependenciesAsync(root.Resource, context.CancellationToken);
            return CommandResults.Success();
        });
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        foreach (var resource in new[] { dependency, root })
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
            {
                State = KnownResourceStates.Finished,
                ExitCode = 0
            });
        }

        var first = await app.ResourceCommands.ExecuteCommandAsync(root.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken).DefaultTimeout();
        Assert.False(first.Success);
        Assert.Equal("First attempt failed.", first.Message);
        Assert.True(app.ResourceNotifications.TryGetCurrentState(root.Resource.Name, out var current));
        Assert.Equal(KnownResourceStates.FailedToStart, current.Snapshot.State?.Text);
        var retry = await app.ResourceCommands.ExecuteCommandAsync(root.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(retry.Success, retry.Message);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task LaunchGroupCanceledQueuedStartDoesNotReleaseFollowingDependencyStart()
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("selected", "other");
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rootStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var dependency = builder.AddResource(new CustomResource("dependency")).WithLaunchGroup("other")
            .WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
            {
                Interlocked.Increment(ref invocationCount);
                entered.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
                return CommandResults.Success();
            });
        var root = builder.AddResource(new CustomResource("root")).WithLaunchGroup("other")
            .WithReferenceRelationship(dependency)
            .WithCommand(KnownResourceCommands.StartCommand, "Start", _ =>
            {
                rootStarted.TrySetResult();
                return Task.FromResult(CommandResults.Success());
            });
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        foreach (var resource in new[] { dependency, root })
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with { State = KnownResourceStates.NotStarted });
        }

        using var queuedCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var firstStart = app.ResourceCommands.ExecuteCommandAsync(dependency.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);
            await entered.Task.DefaultTimeout();
            var canceledStart = app.ResourceCommands.ExecuteCommandAsync(dependency.Resource, KnownResourceCommands.StartCommand, queuedCts.Token);
            await queuedCts.CancelAsync();
            Assert.True((await canceledStart.DefaultTimeout()).Canceled);

            var dependentStart = app.ResourceCommands.ExecuteCommandAsync(root.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);
            await rootStarted.Task.DefaultTimeout();

            Assert.False(dependentStart.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref invocationCount));

            release.TrySetResult();
            var results = await Task.WhenAll(firstStart, dependentStart).DefaultTimeout();

            Assert.All(results, result => Assert.True(result.Success));
            Assert.Equal(1, invocationCount);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task LaunchGroupWaitingStartDoesNotBlockForceStartsOrUnrelatedStarts()
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("selected", "other");
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        var resource = builder.AddResource(new CustomResource("waiter")).WithLaunchGroup("other");
        resource.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
        {
            if (Interlocked.Increment(ref invocationCount) == 1)
            {
                await context.Services.GetRequiredService<ResourceNotificationService>()
                    .PublishUpdateAsync(resource.Resource, snapshot => snapshot with { State = KnownResourceStates.Waiting });
                waiting.TrySetResult();
                await release.Task.WaitAsync(context.CancellationToken);
            }
            else
            {
                release.TrySetResult();
            }

            return CommandResults.Success();
        });
        var unrelated = builder.AddResource(new CustomResource("unrelated")).WithLaunchGroup("other")
            .WithCommand(KnownResourceCommands.StartCommand, "Start", _ => Task.FromResult(CommandResults.Success()));
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with { State = KnownResourceStates.NotStarted });
        await app.ResourceNotifications.PublishUpdateAsync(unrelated.Resource, snapshot => snapshot with { State = KnownResourceStates.NotStarted });

        var firstStart = app.ResourceCommands.ExecuteCommandAsync(resource.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);
        await waiting.Task.DefaultTimeout();
        var unrelatedResult = await app.ResourceCommands.ExecuteCommandAsync(unrelated.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken).DefaultTimeout();
        var forceResult = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(unrelatedResult.Success);
        Assert.True(forceResult.Success);
        Assert.True((await firstStart.DefaultTimeout()).Success);
        Assert.Equal(2, invocationCount);
    }

    [Theory]
    [InlineData("Finished", null, false, true)]
    [InlineData("Finished", 0, true, false)]
    [InlineData("Exited", 0, true, false)]
    [InlineData("Exited", 1, true, true)]
    [InlineData("Finished", null, true, true)]
    [InlineData("Unknown", null, false, true)]
    [InlineData("Unknown", null, true, true)]
    public async Task LaunchGroupLateStartsRestartStoppedServicesButPreserveCompletedJobs(string state, int? exitCode, bool completionDependency, bool shouldStart)
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("selected", "other");
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var started = new ConcurrentQueue<string>();
        var dependency = builder.AddResource(new CustomResource("dependency")).WithLaunchGroup("other")
            .WithCommand(KnownResourceCommands.StartCommand, "Start", context =>
            {
                started.Enqueue(context.ResourceName);
                return Task.FromResult(CommandResults.Success());
            });
        var root = builder.AddResource(new CustomResource("root")).WithLaunchGroup("other")
            .WithReferenceRelationship(dependency)
            .WithCommand(KnownResourceCommands.StartCommand, "Start", context =>
            {
                started.Enqueue(context.ResourceName);
                return Task.FromResult(CommandResults.Success());
            });
        if (completionDependency)
        {
            root.WithAnnotation(new WaitAnnotation(dependency.Resource, WaitType.WaitForCompletion));
        }
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        await app.ResourceNotifications.PublishUpdateAsync(dependency.Resource, snapshot => snapshot with { State = state, ExitCode = exitCode });

        var result = await app.ResourceCommands.ExecuteCommandAsync(root.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(shouldStart ? ["dependency", "root"] : ["root"], started.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task LaunchGroupLateStartsIncludeCustomResourceDependencies(bool dependencyRunning, bool dependencySucceeds)
    {
        using var builder = CreateBuilder();
        builder.DefineLaunchGroups("catalog", "basket");
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        var started = new ConcurrentQueue<string>();
        var dependency = builder.AddResource(new CustomResource("dependency")).WithLaunchGroup("basket");
        var first = builder.AddResource(new CustomResource("first")).WithLaunchGroup("basket").WaitForStart(dependency);
        var second = builder.AddResource(new CustomResource("second")).WithLaunchGroup("basket").WaitForStart(dependency);
        foreach (var resource in new[] { dependency, first, second })
        {
            resource.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
            {
                await context.Services.GetRequiredService<ResourceNotificationService>()
                    .WaitForDependenciesAsync(resource.Resource, context.CancellationToken);
                started.Enqueue(context.ResourceName);
                if (resource == dependency && !dependencySucceeds)
                {
                    return new ExecuteCommandResult { Success = false, Message = "Dependency failed." };
                }

                await context.Services.GetRequiredService<ResourceNotificationService>()
                    .PublishUpdateAsync(resource.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });
                return CommandResults.Success();
            });
        }
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        foreach (var resource in new[] { dependency, first, second })
        {
            await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with
            {
                State = resource == dependency && dependencyRunning ? KnownResourceStates.Running : KnownResourceStates.NotStarted
            });
        }

        var results = await Task.WhenAll(
            app.ResourceCommands.ExecuteCommandAsync(first.Resource, KnownResourceCommands.StartCommand),
            app.ResourceCommands.ExecuteCommandAsync(second.Resource, KnownResourceCommands.StartCommand));

        Assert.All(results, result => Assert.Equal(dependencySucceeds || dependencyRunning, result.Success));
        if (!dependencySucceeds && !dependencyRunning)
        {
            Assert.Equal(["dependency", "dependency"], started);
            Assert.All(results, result => Assert.Equal("Dependency failed.", result.Message));
        }
        else
        {
            Assert.Equal(dependencyRunning ? ["first", "second"] : ["dependency", "first", "second"], started.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// Creates a test builder with version checking disabled to prevent the VersionCheckService
    /// from racing with tests that use IInteractionService.
    /// </summary>
    private IDistributedApplicationTestingBuilder CreateBuilder()
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.Configuration[KnownConfigNames.VersionCheckDisabled] = "true";
        return builder;
    }

    [Fact]
    public async Task ExecuteCommandAsync_NoMatchingResource_Failure()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync("NotFoundResourceId", "NotFound");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Resource 'NotFoundResourceId' not found.", result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_ResourceNameMultipleMatches_Failure()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "NotFound");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Resource 'myResource' not found.", result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_NoMatchingCommand_Failure()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "NotFound");

        // Assert
        Assert.False(result.Success);
        Assert.Equal("Command 'NotFound' not available for resource 'myResource'.", result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_NoMatchingCommand_SingleInstance_MessageUsesDisplayName()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0)
            ]));

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "NotFound");

        Assert.False(result.Success);
        Assert.Equal("Command 'NotFound' not available for resource 'myResource'.", result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_NoMatchingCommand_HasReplicas_MessageUsesResourceId()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));
        custom.WithAnnotation(new ReplicaAnnotation(2));

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "NotFound");

        Assert.False(result.Success);
        Assert.Contains("'myResource-abcdwxyz'", result.Message);
        Assert.Contains("'myResource-efghwxyz'", result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_ResourceNameMultipleMatches_Success()
    {
        // Arrange
        using var builder = CreateBuilder();

        var commandResourcesChannel = Channel.CreateUnbounded<string>();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: async e =>
                {
                    await commandResourcesChannel.Writer.WriteAsync(e.ResourceName);
                    return new ExecuteCommandResult { Success = true };
                });

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "mycommand");
        commandResourcesChannel.Writer.Complete();

        // Assert
        Assert.True(result.Success);

        var resolvedResourceNames = custom.Resource.GetResolvedResourceNames().ToList();
        await foreach (var resourceName in commandResourcesChannel.Reader.ReadAllAsync().DefaultTimeout())
        {
            Assert.True(resolvedResourceNames.Remove(resourceName));
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_Success_CalledPerReplica()
    {
        // Arrange
        using var builder = CreateBuilder();

        var commandResourcesChannel = Channel.CreateUnbounded<string>();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: async e =>
                {
                    await commandResourcesChannel.Writer.WriteAsync(e.ResourceName);
                    return new ExecuteCommandResult { Success = true };
                });

        // Act
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");
        commandResourcesChannel.Writer.Complete();

        // Assert
        Assert.True(result.Success);

        var resolvedResourceNames = custom.Resource.GetResolvedResourceNames().ToList();
        Assert.Equal(2, resolvedResourceNames.Count);
        Assert.Contains("myResource-abcdwxyz", resolvedResourceNames);
        Assert.Contains("myResource-efghwxyz", resolvedResourceNames);

        await foreach (var resourceName in commandResourcesChannel.Reader.ReadAllAsync().DefaultTimeout())
        {
            Assert.True(resolvedResourceNames.Remove(resourceName));
        }
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_Failure_CalledPerReplica()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    return Task.FromResult(new ExecuteCommandResult { Success = false, Message = "Failure!" });
                });

        // Act
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);

        var resourceNames = custom.Resource.GetResolvedResourceNames();
        Assert.Equal(2, resourceNames.Length);
        Assert.Equal("myResource-abcdwxyz", resourceNames[0]);
        Assert.Equal("myResource-efghwxyz", resourceNames[1]);

        Assert.Equal($"""
            2 command executions failed.
            Resource '{resourceNames[0]}' failed with error message: Failure!
            Resource '{resourceNames[1]}' failed with error message: Failure!
            """, result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_Canceled_Success()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    return Task.FromResult(CommandResults.Canceled());
                });

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_Canceled_CalledPerReplica()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    return Task.FromResult(CommandResults.Canceled());
                });

        // Act
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_MixedFailureAndCanceled_OnlyFailuresInErrorMessage()
    {
        // Arrange
        using var builder = CreateBuilder();

        var callCount = 0;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1),
            new DcpInstance("myResource-ijklwxyz", "ijklwxyz", 2)
            ]));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    var count = Interlocked.Increment(ref callCount);
                    return Task.FromResult(count switch
                    {
                        1 => CommandResults.Failure("Failure!"),
                        2 => CommandResults.Canceled(),
                        _ => CommandResults.Success()
                    });
                });

        // Act
        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);
        Assert.False(result.Canceled); // Should not be canceled since there was at least one failure

        var resourceNames = custom.Resource.GetResolvedResourceNames();
        Assert.Equal($"""
            1 command executions failed.
            Resource '{resourceNames[0]}' failed with error message: Failure!
            """, result.Message);
    }

    [Fact] 
    public void CommandResults_Canceled_ProducesCorrectResult()
    {
        // Act
        var result = CommandResults.Canceled();

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_OperationCanceledException_Canceled()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    throw new OperationCanceledException("Command was canceled");
                });

        var app = builder.Build();
        await app.StartAsync();

        // Act
        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        // Assert
        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_CommandException_Failure()
    {
        using var builder = CreateBuilder();
        const string diagnostic = "Failed to apply launch configuration. Process fallback is unavailable.";

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(
            name: "mycommand",
            displayName: "My command",
            executeCommand: _ => throw new FailedToApplyEnvironmentException(diagnostic));

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        Assert.False(result.Success);
        Assert.False(result.Canceled);
        Assert.Equal(diagnostic, result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_LegacyCommandName_FallsBackToCurrentName()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: KnownResourceCommands.StartCommand,
                displayName: "Start",
                executeCommand: _ => Task.FromResult(new ExecuteCommandResult { Success = true }));

        var app = builder.Build();
        await app.StartAsync();

        // Act - use the legacy "resource-start" name
        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "resource-start");

        // Assert - should succeed via fallback
        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteCommandAsync_LegacyCommandName_ById_FallsBackToCurrentName()
    {
        // Arrange
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: KnownResourceCommands.StopCommand,
                displayName: "Stop",
                executeCommand: _ => Task.FromResult(new ExecuteCommandResult { Success = true }));

        var app = builder.Build();
        await app.StartAsync();

        // Act - use the legacy "resource-stop" name via resource ID
        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "resource-stop");

        // Assert - should succeed via fallback
        Assert.True(result.Success);
    }

    [Theory]
    [InlineData("set-parameter", "parameter-set")]
    [InlineData("delete-parameter", "parameter-delete")]
    public async Task ExecuteCommandAsync_LegacyParameterCommandName_FallsBackToCurrentName(string currentCommandName, string legacyCommandName)
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: currentCommandName,
                displayName: "Parameter command",
                executeCommand: _ => Task.FromResult(new ExecuteCommandResult { Success = true }));

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, legacyCommandName);

        Assert.True(result.Success);
    }

    [Fact]
    public async Task ExecuteCommandAsync_SuccessWithResult_ReturnsResultData()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "generate-token",
                displayName: "Generate Token",
                executeCommand: _ => Task.FromResult(CommandResults.Success("Generated token.", "{\"token\": \"abc123\"}", CommandResultFormat.Json)));

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "generate-token");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("{\"token\": \"abc123\"}", result.Data.Value);
        Assert.Equal(CommandResultFormat.Json, result.Data.Format);
    }

    [Fact]
    public async Task ExecuteCommandAsync_SuccessWithoutResult_ReturnsNoResultData()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ => Task.FromResult(CommandResults.Success()));

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "mycommand");

        Assert.True(result.Success);
        Assert.Null(result.Data);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithArgumentCollection_PassesArgumentsToCommand()
    {
        using var builder = CreateBuilder();

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    capturedArguments = e.Arguments;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            InputType = InputType.Text
                        },
                        new InteractionInput
                        {
                            Name = "clickCount",
                            InputType = InputType.Number
                        }
                    ]
                });

        var arguments = new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "selector",
                InputType = InputType.Text,
                Value = "#submit"
            },
            new InteractionInput
            {
                Name = "clickCount",
                InputType = InputType.Number,
                Value = "2"
            }
        ]);

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "mycommand", arguments);

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Equal("#submit", capturedArguments.GetString("selector"));
        Assert.Equal(2, capturedArguments.GetInt32("clickCount"));
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithArgumentValuesAndResource_PassesArgumentsToCommand()
    {
        using var builder = CreateBuilder();

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    capturedArguments = e.Arguments;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "message",
                            InputType = InputType.Text,
                            Required = true
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            custom.Resource,
            "mycommand",
            new Dictionary<string, string?> { ["message"] = "hello" },
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Equal("hello", capturedArguments.GetString("message"));
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithArgumentValuesAndResource_DoesNotRequirePublishedResourceState()
    {
        using var builder = CreateBuilder();

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    capturedArguments = e.Arguments;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "message",
                            InputType = InputType.Text,
                            Required = true
                        }
                    ]
                });

        var app = builder.Build();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            custom.Resource,
            "mycommand",
            new Dictionary<string, string?> { ["message"] = "hello" },
            CancellationToken.None).DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Equal("hello", capturedArguments.GetString("message"));
    }

    [Fact]
    public async Task ExecuteCommandAsync_SecretTextArgument_PreservesWhitespace()
    {
        using var builder = CreateBuilder();

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    capturedArguments = e.Arguments;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "password",
                            InputType = InputType.SecretText,
                            Required = true
                        }
                    ]
                });

        var arguments = new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "password",
                InputType = InputType.SecretText,
                Value = "  secret  "
            }
        ]);

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "mycommand", arguments);

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Equal("  secret  ", capturedArguments.GetString("password"));
    }

    [Fact]
    public async Task CreateCommandArguments_WithOrderedArgumentValues_MapsArgumentsByOrder()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ => Task.FromResult(CommandResults.Success()),
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            InputType = InputType.Text
                        },
                        new InteractionInput
                        {
                            Name = "clickCount",
                            InputType = InputType.Number,
                            Value = "1"
                        },
                        new InteractionInput
                        {
                            Name = "snapshotAfter",
                            InputType = InputType.Boolean,
                            Value = "true"
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        IReadOnlyList<string?> argumentValues = ["#submit", "2"];
        var (arguments, errorMessage) = app.ResourceCommands.CreateCommandArguments("myResource", "mycommand", argumentValues);

        Assert.Null(errorMessage);
        Assert.Equal("#submit", arguments.GetString("selector"));
        Assert.Equal(2, arguments.GetInt32("clickCount"));
        Assert.True(arguments.GetBoolean("snapshotAfter"));
    }

    [Fact]
    public async Task CreateCommandArguments_TooManyOrderedArgumentValues_ReturnsError()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ => Task.FromResult(CommandResults.Success()),
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            InputType = InputType.Text
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        IReadOnlyList<string?> argumentValues = ["#submit", "extra"];
        var (arguments, errorMessage) = app.ResourceCommands.CreateCommandArguments("myResource", "mycommand", argumentValues);

        Assert.Equal("Command 'mycommand' accepts 1 argument(s), but 2 were provided.", errorMessage);
        Assert.Null(arguments.GetString("selector"));
    }

    [Fact]
    public async Task CreateCommandArguments_UnknownNamedArgumentValues_ReturnsError()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ => Task.FromResult(CommandResults.Success()),
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            InputType = InputType.Text
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var argumentValues = new Dictionary<string, string?> { ["selecter"] = "#submit" };
        var (arguments, errorMessage) = app.ResourceCommands.CreateCommandArguments("myResource", "mycommand", argumentValues);

        Assert.Equal("Unknown argument 'selecter' for command 'mycommand'.", errorMessage);
        Assert.Null(arguments.GetString("selector"));
    }

    [Fact]
    public async Task CreateCommandArguments_DisabledNamedArgumentValues_ReturnsError()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ => Task.FromResult(CommandResults.Success()),
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "saveToUserSecrets",
                            InputType = InputType.Boolean,
                            Disabled = true
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var argumentValues = new Dictionary<string, string?> { ["saveToUserSecrets"] = "true" };
        var (arguments, errorMessage) = app.ResourceCommands.CreateCommandArguments("myResource", "mycommand", argumentValues);

        Assert.Equal("Argument 'saveToUserSecrets' for command 'mycommand' is disabled.", errorMessage);
        Assert.Equal("true", arguments.GetString("saveToUserSecrets"));
    }

    [Fact]
    public async Task CreateCommandArguments_DynamicDisabledNamedArgumentValues_DoesNotReturnError()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ => Task.FromResult(CommandResults.Success()),
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "item",
                            InputType = InputType.Choice,
                            Disabled = true,
                            DynamicLoading = new InputLoadOptions
                            {
                                DependsOnInputs = ["category"],
                                LoadCallback = context =>
                                {
                                    context.Input.Disabled = false;
                                    return Task.CompletedTask;
                                }
                            }
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var argumentValues = new Dictionary<string, string?> { ["item"] = "banana" };
        var (arguments, errorMessage) = app.ResourceCommands.CreateCommandArguments("myResource", "mycommand", argumentValues);

        Assert.Null(errorMessage);
        Assert.Equal("banana", arguments.GetString("item"));
    }

    [Fact]
    public async Task CreateCommandArguments_DisabledOrderedArgumentValues_ReturnsError()
    {
        using var builder = CreateBuilder();

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ => Task.FromResult(CommandResults.Success()),
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "saveToUserSecrets",
                            InputType = InputType.Boolean,
                            Disabled = true
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        IReadOnlyList<string?> argumentValues = ["true"];
        var (arguments, errorMessage) = app.ResourceCommands.CreateCommandArguments("myResource", "mycommand", argumentValues);

        Assert.Equal("Argument 'saveToUserSecrets' for command 'mycommand' is disabled.", errorMessage);
        Assert.Equal("true", arguments.GetString("saveToUserSecrets"));
    }

    [Fact]
    public async Task ExecuteCommandAsync_NoArguments_PassesEmptyArgumentsToCommand()
    {
        using var builder = CreateBuilder();

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    capturedArguments = e.Arguments;
                    return Task.FromResult(CommandResults.Success());
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "mycommand");

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Empty(capturedArguments);
    }

    [Fact]
    public async Task ExecuteCommandAsync_InvalidBuiltInArgumentValidation_DoesNotExecuteCommand()
    {
        using var builder = CreateBuilder();

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    executed = true;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            Label = "Selector",
                            InputType = InputType.Text,
                            Required = true
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "mycommand");

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Equal("Command argument validation failed.", result.Message);
        Assert.NotNull(result.InvalidArguments);
        var invalidArgument = Assert.Single(result.InvalidArguments);
        Assert.Equal("selector", invalidArgument.Name);
        Assert.Equal("Value is required.", Assert.Single(invalidArgument.ValidationErrors));
    }

    [Fact]
    public async Task ExecuteCommandAsync_LoadsDependentChoiceOptionsBeforeBuiltInValidation()
    {
        using var builder = CreateBuilder();

        var executed = false;
        var loadCount = 0;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ =>
                {
                    executed = true;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "subscription",
                            InputType = InputType.Choice,
                            Required = true,
                            Options = [KeyValuePair.Create("sub-a", "Subscription A")]
                        },
                        new InteractionInput
                        {
                            Name = "location",
                            InputType = InputType.Choice,
                            Required = true,
                            DynamicLoading = new InputLoadOptions
                            {
                                DependsOnInputs = ["subscription"],
                                LoadCallback = context =>
                                {
                                    loadCount++;
                                    Assert.Equal("sub-a", context.AllInputs.GetString("subscription"));
                                    context.Input.Options = [KeyValuePair.Create("westus", "West US")];

                                    return Task.CompletedTask;
                                }
                            }
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions
            {
                ArgumentValues = new Dictionary<string, string?>
                {
                    ["subscription"] = "sub-a",
                    ["location"] = "centralus"
                },
                ArgumentsProvided = true,
                NonInteractive = true
            },
            CancellationToken.None).DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Equal(1, loadCount);
        Assert.NotNull(result.InvalidArguments);
        var invalidArgument = Assert.Single(result.InvalidArguments, argument => argument.ValidationErrors.Count > 0);
        Assert.Equal("location", invalidArgument.Name);
        Assert.Equal("Value must be one of the provided options.", Assert.Single(invalidArgument.ValidationErrors));
    }

    [Fact]
    public async Task ExecuteCommandAsync_SubmittedDynamicArgumentStillDisabledAfterLoading_ReturnsDisabledValidationError()
    {
        using var builder = CreateBuilder();

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ =>
                {
                    executed = true;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "category",
                            InputType = InputType.Choice,
                            Required = true,
                            Options = [KeyValuePair.Create("fruit", "Fruit")]
                        },
                        new InteractionInput
                        {
                            Name = "item",
                            InputType = InputType.Choice,
                            Required = true,
                            Disabled = true,
                            DynamicLoading = new InputLoadOptions
                            {
                                DependsOnInputs = ["category"],
                                LoadCallback = context =>
                                {
                                    context.Input.Disabled = false;
                                    context.Input.Options = [KeyValuePair.Create("banana", "Banana")];

                                    return Task.CompletedTask;
                                }
                            }
                        },
                        new InteractionInput
                        {
                            Name = "priority",
                            InputType = InputType.Choice,
                            Disabled = true,
                            DynamicLoading = new InputLoadOptions
                            {
                                DependsOnInputs = ["item"],
                                LoadCallback = context =>
                                {
                                    context.Input.Disabled = false;
                                    context.Input.Options = [KeyValuePair.Create("express", "Express")];

                                    return Task.CompletedTask;
                                }
                            }
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions
            {
                ArgumentValues = new Dictionary<string, string?>
                {
                    ["category"] = "fruit",
                    ["priority"] = "express"
                },
                ArgumentsProvided = true,
                NonInteractive = true
            },
            CancellationToken.None).DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.NotNull(result.InvalidArguments);

        var itemArgument = Assert.Single(result.InvalidArguments, argument => argument.Name == "item");
        Assert.Equal("Value is required.", Assert.Single(itemArgument.ValidationErrors));

        var priorityArgument = Assert.Single(result.InvalidArguments, argument => argument.Name == "priority");
        Assert.Equal("Argument is disabled.", Assert.Single(priorityArgument.ValidationErrors));
    }

    [Fact]
    public async Task ExecuteCommandAsync_LoadedDynamicArgumentStillDisabledWithDefaultValue_DoesNotReturnDisabledValidationError()
    {
        using var builder = CreateBuilder();

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: context =>
                {
                    capturedArguments = context.Arguments;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "mode",
                            InputType = InputType.Choice,
                            Required = true,
                            Options = [KeyValuePair.Create("isolated", "Isolated")]
                        },
                        new InteractionInput
                        {
                            Name = "profile",
                            InputType = InputType.Choice,
                            Disabled = true,
                            DynamicLoading = new InputLoadOptions
                            {
                                DependsOnInputs = ["mode"],
                                LoadCallback = context =>
                                {
                                    context.Input.Disabled = true;
                                    context.Input.Value = "default";
                                    context.Input.Options = [KeyValuePair.Create("default", "Default")];

                                    return Task.CompletedTask;
                                }
                            }
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions
            {
                ArgumentValues = new Dictionary<string, string?>
                {
                    ["mode"] = "isolated",
                    ["profile"] = "default"
                },
                ArgumentsProvided = true,
                NonInteractive = true
            },
            CancellationToken.None).DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Equal("default", capturedArguments.GetString("profile"));
        Assert.Empty(capturedArguments["profile"].ValidationErrors);
    }

    [Fact]
    public async Task ExecuteCommandAsync_UnknownNamedArgumentValues_DoesNotExecuteCommand()
    {
        using var builder = CreateBuilder();

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: _ =>
                {
                    executed = true;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            InputType = InputType.Text,
                            Required = true
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions
            {
                ArgumentValues = new Dictionary<string, string?> { ["selecter"] = "#submit" },
                ArgumentsProvided = true,
                NonInteractive = true
            },
            CancellationToken.None).DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.Equal("Unknown argument 'selecter' for command 'mycommand'.", result.Message);
        Assert.Null(result.InvalidArguments);
    }

    [Fact]
    public async Task ExecuteCommandAsync_InteractiveWithoutArguments_PromptsForArguments()
    {
        using var builder = CreateBuilder();

        var testInteractionService = new TestInteractionService();
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    capturedArguments = e.Arguments;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Description = "Command description",
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            Label = "Selector",
                            InputType = InputType.Text,
                            Required = true
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var resultTask = app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions { NonInteractive = false },
            CancellationToken.None).DefaultTimeout();

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal("My command", interaction.Title);
        Assert.Equal("Command description", interaction.Message);
        var input = Assert.Single(interaction.Inputs);
        Assert.Equal("selector", input.Name);
        input.Value = "#submit";
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await resultTask;

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.Equal("#submit", capturedArguments.GetString("selector"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExecuteCommandAsync_Arguments_IsolateInputStateAcrossInteractions(bool dismissFirst, bool cancelFirst)
    {
        using var builder = CreateBuilder();

        // Exercise the real interaction lifecycle with prompting enabled, without starting a dashboard in the test.
        builder.Services.AddSingleton<InteractionService>(services => new InteractionService(
            services.GetRequiredService<ILogger<InteractionService>>(),
            new DistributedApplicationOptions(),
            services,
            builder.Configuration,
            services.GetRequiredService<IInteractionFileUploadStore>()));

        var textDefinition = new InteractionInput
        {
            Name = "text",
            InputType = InputType.Text
        };
        var messageDefinition = new InteractionInput
        {
            Name = "message",
            InputType = InputType.Text,
            Value = "default"
        };
        InteractionInputCollection? capturedArguments = null;
        var executionCount = 0;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(
            name: "mycommand",
            displayName: "My command",
            executeCommand: context =>
            {
                capturedArguments = context.Arguments;
                executionCount++;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions { Arguments = [textDefinition, messageDefinition] });

        await using var app = builder.Build();
        await app.StartAsync().DefaultTimeout();
        var interactionService = app.Services.GetRequiredService<InteractionService>();
        InteractionInput? previousInput = null;

        for (var invocation = 0; invocation < 2; invocation++)
        {
            capturedArguments = null;
            using var cts = new CancellationTokenSource();
            var resultTask = app.ResourceCommands.ExecuteCommandAsync(
                "myResource",
                "mycommand",
                new ResourceCommandExecutionOptions { NonInteractive = false },
                cts.Token);

            var interaction = Assert.Single(interactionService.GetCurrentInteractions());
            var inputs = Assert.IsType<Interaction.InputsInteractionInfo>(interaction.InteractionInfo).Inputs;
            var input = inputs["text"];
            Assert.NotSame(textDefinition, input);
            Assert.NotSame(previousInput, input);
            Assert.False(input.Disabled);
            Assert.Equal("default", inputs.GetString("message"));

            input.Disabled = true;
            inputs["message"].Value = $"invocation-{invocation}";
            var canceled = invocation == 0 && (dismissFirst || cancelFirst);
            if (invocation == 0 && cancelFirst)
            {
                cts.Cancel();
            }
            else
            {
                await interactionService.ProcessInteractionFromClientAsync(
                    interaction.InteractionId,
                    (_, _, _) => new InteractionCompletionState { Complete = true, State = canceled ? null : inputs },
                    CancellationToken.None).DefaultTimeout();
            }

            var result = await resultTask.DefaultTimeout();
            Assert.Equal(!canceled, result.Success);
            Assert.Equal(canceled, result.Canceled);
            if (canceled)
            {
                Assert.Null(capturedArguments);
            }
            else
            {
                Assert.NotNull(capturedArguments);
                Assert.Same(input, capturedArguments["text"]);
                Assert.Equal($"invocation-{invocation}", capturedArguments.GetString("message"));
            }

            Assert.Empty(interactionService.GetCurrentInteractions());
            Assert.False(textDefinition.Disabled);
            Assert.Equal("default", messageDefinition.Value);
            previousInput = input;
        }

        Assert.Equal(dismissFirst || cancelFirst ? 1 : 2, executionCount);
    }

    [Fact]
    public async Task ExecuteCommandAsync_InteractiveDisabledDynamicArgumentWithDefaultValue_Succeeds()
    {
        using var builder = CreateBuilder();

        var testInteractionService = new TestInteractionService();
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        InteractionInputCollection? capturedArguments = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(
            name: "mycommand",
            displayName: "My command",
            executeCommand: context =>
            {
                capturedArguments = context.Arguments;
                return Task.FromResult(CommandResults.Success());
            },
            commandOptions: new CommandOptions
            {
                Arguments =
                [
                    new InteractionInput
                    {
                        Name = "generateTraces",
                        InputType = InputType.Boolean,
                        Required = true,
                        Value = "true"
                    },
                    new InteractionInput
                    {
                        Name = "traceCount",
                        InputType = InputType.Number,
                        Required = true,
                        Value = "50000",
                        DynamicLoading = new InputLoadOptions
                        {
                            DependsOnInputs = ["generateTraces"],
                            LoadCallback = context =>
                            {
                                context.Input.Disabled = !context.AllInputs.GetBoolean("generateTraces");
                                return Task.CompletedTask;
                            }
                        }
                    }
                ]
            });

        var app = builder.Build();
        await app.StartAsync();

        var resultTask = app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions { NonInteractive = false },
            CancellationToken.None).DefaultTimeout();

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        interaction.Inputs["generateTraces"].Value = "false";
        interaction.Inputs["traceCount"].Disabled = true;
        interaction.CompletionTcs.SetResult(InteractionResult.Ok(interaction.Inputs));

        var result = await resultTask;

        Assert.True(result.Success);
        Assert.NotNull(capturedArguments);
        Assert.False(capturedArguments.GetBoolean("generateTraces"));
        Assert.Equal(50000, capturedArguments.GetInt32("traceCount"));
        Assert.True(capturedArguments["traceCount"].Disabled);
        Assert.Empty(capturedArguments["traceCount"].ValidationErrors);
    }

    [Fact]
    public async Task ExecuteCommandAsync_NonInteractiveWithoutArguments_DoesNotPrompt()
    {
        using var builder = CreateBuilder();

        var testInteractionService = new TestInteractionService();
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    executed = true;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            Label = "Selector",
                            InputType = InputType.Text,
                            Required = true
                        }
                    ]
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions { NonInteractive = true },
            CancellationToken.None).DefaultTimeout();

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.False(testInteractionService.Interactions.Reader.TryRead(out _));
        Assert.NotNull(result.InvalidArguments);
        var invalidArgument = Assert.Single(result.InvalidArguments);
        Assert.Equal("selector", invalidArgument.Name);
        Assert.Equal("Value is required.", Assert.Single(invalidArgument.ValidationErrors));
    }

    [Fact]
    public async Task ExecuteCommandAsync_NonInteractive_IsAvailableReturnsFalse()
    {
        using var builder = CreateBuilder();

        bool? isAvailableDuringExecution = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    var interactionService = e.Services.GetRequiredService<IInteractionService>();
                    isAvailableDuringExecution = interactionService.IsAvailable;
                    return Task.FromResult(CommandResults.Success());
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions { NonInteractive = true },
            CancellationToken.None).DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(isAvailableDuringExecution);
        Assert.False(isAvailableDuringExecution.Value);
    }

    [Fact]
    public async Task ExecuteCommandAsync_Interactive_IsAvailableNotAffectedByScope()
    {
        using var builder = CreateBuilder();

        bool? isAvailableDuringExecution = null;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    var interactionService = e.Services.GetRequiredService<IInteractionService>();
                    isAvailableDuringExecution = interactionService.IsAvailable;
                    return Task.FromResult(CommandResults.Success());
                });

        var app = builder.Build();

        // Get the baseline IsAvailable value (may be false in test environments where dashboard is disabled)
        var baselineIsAvailable = app.Services.GetRequiredService<IInteractionService>().IsAvailable;

        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            "myResource",
            "mycommand",
            new ResourceCommandExecutionOptions { NonInteractive = false },
            CancellationToken.None).DefaultTimeout();

        Assert.True(result.Success);
        Assert.NotNull(isAvailableDuringExecution);

        // Interactive mode should not change the baseline IsAvailable value
        Assert.Equal(baselineIsAvailable, isAvailableDuringExecution.Value);
    }

    [Fact]
    public async Task ExecuteCommandAsync_PartialArgumentCollection_ValidatesMissingDeclaredArguments()
    {
        using var builder = CreateBuilder();

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    executed = true;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "selector",
                            InputType = InputType.Text,
                            Required = true
                        },
                        new InteractionInput
                        {
                            Name = "target",
                            InputType = InputType.Text,
                            Required = true
                        }
                    ]
                });

        var arguments = new InteractionInputCollection(
        [
            new InteractionInput
            {
                Name = "selector",
                InputType = InputType.Text,
                Value = "#submit"
            }
        ]);

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "mycommand", arguments);

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.NotNull(result.InvalidArguments);
        var invalidArgument = Assert.Single(result.InvalidArguments, argument => argument.ValidationErrors.Count > 0);
        Assert.Equal("target", invalidArgument.Name);
        Assert.Equal("Value is required.", Assert.Single(invalidArgument.ValidationErrors));
    }

    [Fact]
    public async Task ExecuteCommandAsync_InvalidCustomArgumentValidation_DoesNotExecuteCommand()
    {
        using var builder = CreateBuilder();

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "mycommand",
                displayName: "My command",
                executeCommand: e =>
                {
                    executed = true;
                    return Task.FromResult(CommandResults.Success());
                },
                commandOptions: new CommandOptions
                {
                    Arguments =
                    [
                        new InteractionInput
                        {
                            Name = "target",
                            Label = "Target",
                            InputType = InputType.Text,
                            Value = "prod"
                        }
                    ],
                    ValidateArguments = context =>
                    {
                        var target = context.Inputs.Single(argument => argument.Name == "target");
                        context.AddValidationError(target, "Target must not be prod.");

                        return Task.CompletedTask;
                    }
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "mycommand");

        Assert.False(result.Success);
        Assert.False(executed);
        Assert.NotNull(result.InvalidArguments);
        var invalidArgument = Assert.Single(result.InvalidArguments);
        Assert.Equal("target", invalidArgument.Name);
        Assert.Equal("Target must not be prod.", Assert.Single(invalidArgument.ValidationErrors));
    }

    [Fact]
    public async Task ExecuteCommandAsync_HasReplicas_SuccessWithResult_ReturnsFirstResultData()
    {
        using var builder = CreateBuilder();

        var callCount = 0;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithAnnotation(new DcpInstancesAnnotation([
            new DcpInstance("myResource-abcdwxyz", "abcdwxyz", 0),
            new DcpInstance("myResource-efghwxyz", "efghwxyz", 1)
            ]));
        custom.WithCommand(name: "generate-token",
                displayName: "Generate Token",
                executeCommand: e =>
                {
                    var count = Interlocked.Increment(ref callCount);
                    return Task.FromResult(CommandResults.Success("Generated token.", $"token-{count}", CommandResultFormat.Text));
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync(custom.Resource, "generate-token");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.StartsWith("token-", result.Data.Value);
        Assert.Equal(CommandResultFormat.Text, result.Data.Format);
    }

    [Fact]
    public async Task ExecuteCommandAsync_RebuildCommand_ReturnsBuildOutput()
    {
        const string rebuildOutputMarker = "ASPIRE_REBUILD_OUTPUT_MARKER";

        using var workspace = TemporaryWorkspace.Create(testOutputHelper);
        var projectPath = CreateBuildOutputTestProject(workspace.WorkspaceRoot.FullName, rebuildOutputMarker);

        using var builder = CreateBuilder();
        var project = builder.AddProject("myProject", projectPath, options => options.ExcludeLaunchProfile = true);
        using var app = builder.Build();

        await app.StartAsync().DefaultTimeout(TimeSpan.FromMinutes(2));

        var resourceNotificationService = app.Services.GetRequiredService<ResourceNotificationService>();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.LongTimeoutDuration);
        await resourceNotificationService.WaitForResourceAsync(project.Resource.Name, e => KnownResourceStates.BuildableStates.Contains(e.Snapshot.State?.Text), cts.Token).DefaultTimeout(TimeSpan.FromMinutes(2));

        var buildCountPath = Path.Combine(workspace.WorkspaceRoot.FullName, "build-count.txt");
        var result = await app.ResourceCommands.ExecuteCommandAsync(project.Resource, KnownResourceCommands.RebuildCommand).DefaultTimeout(TimeSpan.FromMinutes(2));

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(CommandResultFormat.Text, result.Data.Format);
        Assert.Contains("[build] Building project...", result.Data.Value);
        Assert.Contains(rebuildOutputMarker, result.Data.Value);
        var firstBuildCount = File.ReadAllLines(buildCountPath).Length;

        var secondResult = await app.ResourceCommands.ExecuteCommandAsync(project.Resource, KnownResourceCommands.RebuildCommand).DefaultTimeout(TimeSpan.FromMinutes(2));

        Assert.True(secondResult.Success);
        Assert.NotNull(secondResult.Data);
        Assert.Contains(rebuildOutputMarker, secondResult.Data.Value);
        Assert.Equal(firstBuildCount + 1, File.ReadAllLines(buildCountPath).Length);
    }

    [Fact]
    public void CommandResults_SuccessWithResult_ProducesCorrectResult()
    {
        var result = CommandResults.Success("Success.", "{\"key\": \"value\"}", CommandResultFormat.Json);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("{\"key\": \"value\"}", result.Data.Value);
        Assert.Equal(CommandResultFormat.Json, result.Data.Format);
    }

    [Fact]
    public void CommandResults_SuccessWithResultAndDisplayImmediately_ProducesCorrectResult()
    {
        var result = CommandResults.Success("Success.", "{\"key\": \"value\"}", CommandResultFormat.Json, displayImmediately: true);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("{\"key\": \"value\"}", result.Data.Value);
        Assert.Equal(CommandResultFormat.Json, result.Data.Format);
        Assert.True(result.Data.DisplayImmediately);
    }

    [Fact]
    public void CommandResults_SuccessWithTextResult_DefaultsToText()
    {
        var result = CommandResults.Success("Success.", "hello world");

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("hello world", result.Data.Value);
        Assert.Equal(CommandResultFormat.Text, result.Data.Format);
    }

    private static string CreateBuildOutputTestProject(string directoryPath, string buildOutputMarker)
    {
        var projectPath = Path.Combine(directoryPath, "BuildOutputProject.csproj");
        var programPath = Path.Combine(directoryPath, "Program.cs");

        File.WriteAllText(projectPath,
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>

              <Target Name="EmitAspireRebuildOutputMarker" AfterTargets="Build">
                <WriteLinesToFile File="$(MSBuildProjectDirectory)/build-count.txt" Lines="built" Overwrite="false" />
                <Message Importance="High" Text="{{buildOutputMarker}}" />
              </Target>
            </Project>
            """);

        File.WriteAllText(programPath,
            """
            Console.WriteLine("Hello from rebuild test project.");
            """);

        return projectPath;
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithProgressOptions_CancelsCommandWhenProgressCanceled()
    {
        using var builder = CreateBuilder();

        var testInteractionService = new TestInteractionService();
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "cancelable-command",
                displayName: "Cancelable Command",
                executeCommand: async e =>
                {
                    commandStarted.SetResult();
                    await Task.Delay(Timeout.Infinite, e.CancellationToken);
                    return CommandResults.Success();
                },
                commandOptions: new CommandOptions
                {
                    Progress = new CommandProgressOptions { Message = "Processing...", Title = "Cancelable Command" }
                });

        var app = builder.Build();
        await app.StartAsync();

        var resultTask = app.ResourceCommands.ExecuteCommandAsync("myResource", "cancelable-command");

        // Wait for the Work callback to write the interaction and for the command to start.
        var interaction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionType.Progress, interaction.Type);
        Assert.Equal("Cancelable Command", interaction.Title);
        Assert.Equal("Processing...", interaction.Message);
        await commandStarted.Task.DefaultTimeout();

        // Simulate the user clicking the Cancel button in the progress dialog.
        interaction.CompletionTcs.SetResult(InteractionResult.Cancel<bool>());

        var result = await resultTask.DefaultTimeout();

        Assert.True(testInteractionService.PromptProgressCalled);
        Assert.False(result.Success);
        Assert.True(result.Canceled);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithProgressOptions_ReturnsCanceledWhenCommandHandlesProgressCancellation()
    {
        using var builder = CreateBuilder();

        var testInteractionService = new TestInteractionService();
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "cancelable-command",
                displayName: "Cancelable Command",
                executeCommand: async e =>
                {
                    commandStarted.SetResult();

                    try
                    {
                        await Task.Delay(Timeout.Infinite, e.CancellationToken);
                    }
                    catch (OperationCanceledException) when (e.CancellationToken.IsCancellationRequested)
                    {
                        return CommandResults.Success("Cleanup completed.");
                    }

                    return CommandResults.Success();
                },
                commandOptions: new CommandOptions
                {
                    Progress = new CommandProgressOptions { Message = "Processing..." }
                });

        var app = builder.Build();
        await app.StartAsync();

        var resultTask = app.ResourceCommands.ExecuteCommandAsync("myResource", "cancelable-command");

        var interaction = await testInteractionService.Interactions.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(InteractionType.Progress, interaction.Type);
        await commandStarted.Task.DefaultTimeout();

        interaction.CompletionTcs.SetResult(InteractionResult.Cancel<bool>());

        var result = await resultTask.DefaultTimeout();

        Assert.False(result.Success);
        Assert.True(result.Canceled);
        Assert.Null(result.Message);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithProgressOptions_SkipsProgressWhenNotAvailable()
    {
        using var builder = CreateBuilder();

        var testInteractionService = new TestInteractionService();
        testInteractionService.IsAvailable = false;
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        var executed = false;
        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "progress-command",
                displayName: "Progress Command",
                executeCommand: e =>
                {
                    executed = true;
                    return Task.FromResult(CommandResults.Success("Done"));
                },
                commandOptions: new CommandOptions
                {
                    Progress = new CommandProgressOptions { Message = "Working..." }
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "progress-command");

        Assert.True(result.Success);
        Assert.Equal("Done", result.Message);
        Assert.True(executed);
        Assert.False(testInteractionService.PromptProgressCalled);
    }

    [Fact]
    public async Task ExecuteCommandAsync_WithProgressOptions_SuccessPathPropagatesResult()
    {
        using var builder = CreateBuilder();

        var testInteractionService = new TestInteractionService();
        builder.Services.AddSingleton<IInteractionService>(testInteractionService);

        var custom = builder.AddResource(new CustomResource("myResource"));
        custom.WithCommand(name: "success-command",
                displayName: "Success Command",
                executeCommand: e =>
                {
                    return Task.FromResult(CommandResults.Success("Operation completed successfully."));
                },
                commandOptions: new CommandOptions
                {
                    Progress = new CommandProgressOptions { Message = "Processing..." }
                });

        var app = builder.Build();
        await app.StartAsync();

        var result = await app.ResourceCommands.ExecuteCommandAsync("myResource", "success-command");

        Assert.True(result.Success);
        Assert.Equal("Operation completed successfully.", result.Message);
        Assert.True(testInteractionService.PromptProgressCalled);
    }

    private sealed class CustomResource(string name) : Resource(name), IResourceWithEndpoints, IResourceWithWaitSupport
    {

    }
}
