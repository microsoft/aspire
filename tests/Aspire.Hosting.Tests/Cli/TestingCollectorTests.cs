// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Helpers;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.DotNet.RemoteExecutor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIRETESTING001 // Tests the experimental testing API.

[Trait("Partition", "4")]
public class TestingCollectorTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("event", false, false)]
    [InlineData("event", false, true)]
    [InlineData("event", true, false)]
    [InlineData("event", true, true)]
    [InlineData("lifecycle", false, false)]
    [InlineData("lifecycle", false, true)]
    [InlineData("lifecycle", true, false)]
    [InlineData("lifecycle", true, true)]
    [InlineData("pipeline", false, false)]
    [InlineData("pipeline", false, true)]
    [InlineData("pipeline", true, false)]
    [InlineData("pipeline", true, true)]
    public async Task DiscoveryIncludesTestsAddedOrAnnotatedDuringBeforeStart(string phase, bool addResource, bool existingTest)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var late = addResource ? null : builder.AddParameter("late", "value");
        var collectorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hooksCompleted = false;
        if (existingTest)
        {
            builder.AddParameter("early", "value").WithTesting();
        }

        async Task ConfigureTestAsync(ResourceNotificationService notifications)
        {
            late ??= builder.AddParameter("late", "value");
            late.WithTesting(async context =>
            {
                Assert.True(hooksCompleted);
                collectorStarted.TrySetResult();
                Assert.True(await context.WaitForCompletionAsync());
                context.PublishResult(new TestResult { Name = "late-result", Outcome = TestOutcome.Passed });
            });
            var annotation = Assert.Single(late.Resource.Annotations.OfType<TestingAnnotation>());
            Assert.Equal(Path.Combine(builder.Configuration[KnownConfigNames.TestOutputPath]!, "late"), annotation.OutputPath);
            Assert.True(Directory.Exists(annotation.OutputPath));
            await notifications.PublishUpdateAsync(late.Resource,
                snapshot => snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });
        }

        switch (phase)
        {
            case "event":
                builder.Eventing.Subscribe<BeforeStartEvent>((e, _) => ConfigureTestAsync(e.Services.GetRequiredService<ResourceNotificationService>()));
                break;
            case "lifecycle":
#pragma warning disable CS0618 // Existing lifecycle hooks remain supported model-mutation points.
                builder.Services.AddLifecycleHook(services => new CallbackLifecycleHook((_, _) => ConfigureTestAsync(services.GetRequiredService<ResourceNotificationService>())));
#pragma warning restore CS0618
                break;
            case "pipeline":
                builder.Pipeline.AddStep("configure-test", context => ConfigureTestAsync(context.Services.GetRequiredService<ResourceNotificationService>()), requiredBy: WellKnownPipelineSteps.BeforeStart);
                break;
        }
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.BeforeStart, _ =>
        {
            hooksCompleted = true;
            return Task.CompletedTask;
        });
        await using var app = builder.Build();
        var notifications = app.ResourceNotifications;
        if (existingTest)
        {
            await notifications.PublishUpdateAsync(builder.Resources.Single(resource => resource.Name == "early"),
                snapshot => snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });
        }

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        await collectorStarted.Task.DefaultTimeout();
        var events = new ConcurrentQueue<TestRunEvent>();
        await ReadAsync(app.Services.GetRequiredService<TestRunCoordinator>(), events).DefaultTimeout();

        Assert.True(events.Last().Passed);
        Assert.Equal(existingTest ? ["early", "late"] : ["late"],
            events.Where(item => item.Resource?.Outcome is "passed" or "failed").Select(item => item.Resource!.ResourceName).Order());
        Assert.Equal("late-result", Assert.Single(events, item => item.Result is not null).Result!.Name);
    }

    [Fact]
    public async Task NoTestsAfterBeforeStartHooksFailsExplicitly()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var tests = builder.AddParameter("tests", "value").WithTesting();
        var hooksCompleted = false;
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.BeforeStart, _ =>
        {
            tests.Resource.Annotations.Remove(Assert.Single(tests.Resource.Annotations.OfType<TestingAnnotation>()));
            hooksCompleted = true;
            return Task.CompletedTask;
        });
        await using var app = builder.Build();
        await app.ResourceNotifications.PublishUpdateAsync(tests.Resource,
            snapshot => snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken).DefaultTimeout();

        Assert.True(hooksCompleted);
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() =>
            app.Services.GetRequiredService<TestRunCoordinator>().WaitForCompletionAsync(TestContext.Current.CancellationToken));
        Assert.Equal("No test resources were found. Mark a resource with WithTesting() before running aspire test.", exception.Message);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, false)]
    [InlineData(DistributedApplicationOperation.Publish, true)]
    public async Task OrdinaryRunAndPublishDoNotStartTestDiscovery(DistributedApplicationOperation operation, bool supplyPath)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(operation, outputHelper);
        if (supplyPath)
        {
            builder.Configuration[KnownConfigNames.TestOutputPath] = workspace.WorkspaceRoot.FullName;
        }
        var early = builder.AddParameter("early", "value").WithTesting();
        await using var app = builder.Build();
        await app.ResourceNotifications.PublishUpdateAsync(early.Resource,
            snapshot => snapshot with { State = KnownResourceStates.Finished, ExitCode = 0 });

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken).DefaultTimeout();
        var late = builder.AddParameter("late", "value").WithTesting();
        await app.ResourceNotifications.PublishUpdateAsync(late.Resource,
            snapshot => snapshot with { State = KnownResourceStates.Finished, ExitCode = 1 });

        // An eager startup snapshot would contain only the passing first resource.
        Assert.False(await app.Services.GetRequiredService<TestRunCoordinator>()
            .WaitForCompletionAsync(TestContext.Current.CancellationToken).DefaultTimeout());
    }

    [Fact]
    public void PublicTestingContractIsExperimental()
    {
        Assert.All(new[] { typeof(ResourceTestingContext), typeof(TestingContext),
            typeof(TestResult), typeof(TestOutcome), typeof(TestingAnnotation), typeof(TestResourceBuilderExtensions) },
            type => Assert.Equal("ASPIRETESTING001", type.GetCustomAttribute<ExperimentalAttribute>()!.DiagnosticId));
        Assert.Equal("ASPIRETESTING001",
            typeof(TestResourceBuilderExtensions).GetMethod(nameof(TestResourceBuilderExtensions.WithTesting))!
                .GetCustomAttribute<ExperimentalAttribute>()!.DiagnosticId);
    }

    [Fact]
    public async Task OrdinaryRunDoesNotInvokeRuntimeCollectorOrInventOutputPaths()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var collectorCalled = false;
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(_ =>
        {
            collectorCalled = true;
            return Task.CompletedTask;
        });
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.False(collectorCalled);
        Assert.Null(annotation.OutputPath);
        Assert.Null(annotation.TestingContext);
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.True(await completion.DefaultTimeout());
        Assert.False(collectorCalled);
    }

    [Fact]
    public async Task RuntimeCollectorStartsOnlyAtExecutionWithPreallocatedOutputPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var started = new TaskCompletionSource<ResourceTestingContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(async context =>
        {
            started.TrySetResult(context);
            await context.WaitForCompletionAsync();
            await File.WriteAllTextAsync(Path.Combine(context.OutputPath, "result.txt"), "artifact", context.CancellationToken);
        });
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.False(started.Task.IsCompleted);
        Assert.True(Directory.Exists(annotation.OutputPath));
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        var context = await started.Task.DefaultTimeout();
        Assert.Same(annotation.TestingContext, context.TestingContext);
        Assert.Equal(annotation.OutputPath, context.OutputPath);
        Assert.Equal("tests", context.ResourceName);
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });

        Assert.True(await completion.DefaultTimeout());
        Assert.Equal("artifact", await File.ReadAllTextAsync(Path.Combine(context.OutputPath, "result.txt")));
        Assert.Throws<OperationCanceledException>(() =>
            context.PublishResult(new TestResult { Name = "late fixture", Outcome = TestOutcome.Passed }));
    }

    [Theory]
    [InlineData("", TestOutcome.Passed, 0)]
    [InlineData("fixture", (TestOutcome)(-1), 0)]
    [InlineData("fixture", (TestOutcome)7, 0)]
    [InlineData("fixture", TestOutcome.Passed, -1)]
    public void InvalidPublishedResultsAreExplicitErrors(string name, TestOutcome outcome, int duration)
    {
        var context = new ResourceTestingContext(new TestingContext("run-output"), "tests", "resource-output",
            _ => throw new InvalidOperationException("Invalid results must not be published."), Task.FromResult(true), CancellationToken.None);
        Assert.ThrowsAny<ArgumentException>(() => context.PublishResult(new TestResult
        {
            Name = name, Outcome = outcome, Duration = TimeSpan.FromMilliseconds(duration)
        }));
        context.Close();
        Assert.Throws<InvalidOperationException>(() =>
            context.PublishResult(new TestResult { Name = "fixture", Outcome = TestOutcome.Passed }));
    }

    [Fact]
    public void DefaultOutcomeIsFailedAndOutcomeRemainsRequired()
    {
        TestResult? published = null;
        var context = new ResourceTestingContext(new TestingContext("run-output"), "tests", "resource-output",
            result => published = result, Task.FromResult(true), CancellationToken.None);

        context.PublishResult(new TestResult { Name = "fixture", Outcome = default });

        Assert.Equal(TestOutcome.Failed, Assert.IsType<TestResult>(published).Outcome);
        Assert.NotNull(typeof(TestResult).GetProperty(nameof(TestResult.Outcome))!
            .GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
    }

    [Theory]
    [InlineData(TestOutcome.Failed, "failed", false)]
    [InlineData(TestOutcome.Passed, "passed", true)]
    [InlineData(TestOutcome.Skipped, "skipped", true)]
    [InlineData(TestOutcome.Inconclusive, "inconclusive", false)]
    [InlineData(TestOutcome.Error, "error", false)]
    [InlineData(TestOutcome.TimedOut, "timedOut", false)]
    [InlineData(TestOutcome.Cancelled, "cancelled", false)]
    public async Task PublishedOutcomeControlsAggregateWithoutChangingProcessOutcome(TestOutcome outcome, string wireOutcome, bool passed)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(context =>
        {
            context.PublishResult(new TestResult { Name = "fixture", Outcome = outcome });
            return Task.CompletedTask;
        });
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });

        Assert.Equal(passed, await completion.DefaultTimeout());
        var events = new ConcurrentQueue<TestRunEvent>();
        await ReadAsync(coordinator, events).DefaultTimeout();
        Assert.Equal(wireOutcome, Assert.Single(events, e => e.Result is not null).Result!.Outcome);
        Assert.Equal("completed", Assert.Single(events, e => e.Collection?.Status == "completed").Collection!.Status);
        Assert.Contains(events, e => e.Resource is { Outcome: "passed", ExitCode: 0 });
        Assert.Equal(passed, events.Last().Passed);
    }

    [Fact]
    public async Task RepeatedCallsPreserveArtifactPathAndReplaceRuntimeCollector()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = builder.AddResource(new ContainerResource("tests"));
        var originalCollectorCalled = false;
        resource.WithTesting(context =>
        {
            originalCollectorCalled = true;
            context.PublishResult(new TestResult { Name = "original", Outcome = TestOutcome.Failed });
            return Task.CompletedTask;
        });
        var first = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        await File.WriteAllTextAsync(Path.Combine(first.OutputPath!, "result.txt"), "artifact");
        resource.WithTesting(async context =>
        {
            await context.WaitForCompletionAsync();
            Assert.Equal(first.OutputPath, context.OutputPath);
            Assert.Same(first.TestingContext, context.TestingContext);
            Assert.Equal("artifact", await File.ReadAllTextAsync(
                Path.Combine(context.OutputPath, "result.txt"), context.CancellationToken));
            context.PublishResult(new TestResult { Name = "replacement", Outcome = TestOutcome.Passed });
        });
        var replacement = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.NotSame(first, replacement);
        Assert.Same(first.TestingContext, replacement.TestingContext);
        Assert.Equal(first.OutputPath, replacement.OutputPath);
        Assert.Equal(first.OutputPath, Assert.Single(Directory.GetDirectories(
            first.TestingContext!.OutputPath)));
        Assert.False(originalCollectorCalled);

        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.True(await completion.DefaultTimeout());
        Assert.False(originalCollectorCalled);
        var events = new ConcurrentQueue<TestRunEvent>();
        await ReadAsync(coordinator, events).DefaultTimeout();
        Assert.Equal("replacement", Assert.Single(events, e => e.Result is not null).Result!.Name);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MarkerOnlyReplacementPreservesOutputContextAndRemovesCollectors(bool configureCollector)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var collectorCalled = false;
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(configureCollector
            ? _ =>
            {
                collectorCalled = true;
                return Task.CompletedTask;
            }
            : null);
        var first = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        resource.WithTesting();
        var replacement = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.NotSame(first, replacement);
        Assert.Same(first.TestingContext, replacement.TestingContext);
        Assert.Equal(first.OutputPath, replacement.OutputPath);
        Assert.Null(replacement.Collector);
        Assert.Equal(first.OutputPath, Assert.Single(Directory.GetDirectories(
            first.TestingContext!.OutputPath)));

        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.True(await completion.DefaultTimeout());
        Assert.False(collectorCalled);
        var events = new ConcurrentQueue<TestRunEvent>();
        await ReadAsync(coordinator, events).DefaultTimeout();
        var collection = Assert.Single(events, e => e.Collection is not null).Collection!;
        Assert.Equal(("notConfigured", 0, replacement.OutputPath), (collection.Status, collection.CollectorCount, collection.OutputPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinaryRunReplacementDoesNotInventOutputContextOrInvokeCollectors(bool markerOnly)
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var collectorCalled = false;
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(_ =>
        {
            collectorCalled = true;
            return Task.CompletedTask;
        });
        var first = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        resource.WithTesting(markerOnly ? null : _ =>
        {
            collectorCalled = true;
            return Task.CompletedTask;
        });
        var replacement = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.NotSame(first, replacement);
        Assert.Null(replacement.TestingContext);
        Assert.Null(replacement.OutputPath);
        Assert.Equal(markerOnly, replacement.Collector is null);

        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.True(await completion.DefaultTimeout());
        Assert.False(collectorCalled);
    }

    [Fact]
    public async Task LivePublicationIsReplayedAndCompletionJoinsCollectorsAndAllReplicas()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resource = builder.AddResource(new ContainerResource("tests"));
        resource.Resource.Annotations.Add(new ReplicaAnnotation(2));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resourceFinished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        resource.WithTesting(async context =>
        {
            Interlocked.Increment(ref calls);
            Assert.Equal(Path.Combine(context.TestingContext.OutputPath, "tests"), context.OutputPath);
            Assert.Equal(["tests"], Directory.GetDirectories(context.TestingContext.OutputPath).Select(Path.GetFileName));
            context.PublishResult(new TestResult { Name = "live fixture", Outcome = TestOutcome.Passed });
            started.TrySetResult();
            resourceFinished.TrySetResult(await context.WaitForCompletionAsync());
            await release.Task.WaitAsync(context.CancellationToken);
            context.PublishResult(new TestResult { Name = "final fixture", Outcome = TestOutcome.Skipped, Message = "fixture skip" });
        });
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        coordinator.Start(TestContext.Current.CancellationToken);
        await started.Task.DefaultTimeout();

        var events = new ConcurrentQueue<TestRunEvent>();
        var liveSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = ReadAsync(coordinator, events, item =>
        {
            if (item.Result is { Name: "live fixture" })
            {
                liveSeen.TrySetResult();
            }
        });
        await liveSeen.Task.DefaultTimeout();
        Assert.False(resourceFinished.Task.IsCompleted);
        await notifications.PublishUpdateAsync(resource.Resource, "tests-1", s => s with { State = KnownResourceStates.Exited, ExitCode = 0 });
        Assert.False(resourceFinished.Task.IsCompleted);
        await notifications.PublishUpdateAsync(resource.Resource, "tests-2", s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.True(await resourceFinished.Task.DefaultTimeout());
        Assert.False(stream.IsCompleted);
        release.TrySetResult();
        await stream.DefaultTimeout();

        Assert.True(events.Last().Passed);
        Assert.Collection(events.Where(e => e.Result is not null),
            first => Assert.Equal(("tests", "live fixture", "passed"), (first.Result!.ResourceName, first.Result.Name, first.Result.Outcome)),
            second => Assert.Equal(("tests", "final fixture", "skipped"), (second.Result!.ResourceName, second.Result.Name, second.Result.Outcome)));
        var replay = new ConcurrentQueue<TestRunEvent>();
        await ReadAsync(coordinator, replay).DefaultTimeout();
        Assert.Equal(events.ToArray(), replay.ToArray());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task EarlyCollectorReturnDoesNotFinishAResourceAndPublishedFailureCannotPass()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(context =>
        {
            context.PublishResult(new TestResult { Name = "failed fixture", Outcome = default });
            published.TrySetResult();
            return Task.CompletedTask;
        });
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await published.Task.DefaultTimeout();
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });

        Assert.False(await completion.DefaultTimeout());
        var events = new ConcurrentQueue<TestRunEvent>();
        await ReadAsync(coordinator, events).DefaultTimeout();
        Assert.Contains(events, e => e.Resource is { Outcome: "passed" });
        Assert.Contains(events, e => e.Result is { Outcome: "failed" });
        Assert.False(events.Last().Passed);
    }

    [Fact]
    public async Task ReturnedCollectorIsClosedBeforeResourceCompletionAndCannotHideProcessFailure()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var started = new TaskCompletionSource<ResourceTestingContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(context =>
        {
            context.PublishResult(new TestResult { Name = "fixture", Outcome = TestOutcome.Passed });
            started.TrySetResult(context);
            return Task.CompletedTask;
        });
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var events = new ConcurrentQueue<TestRunEvent>();
        var collected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = ReadAsync(coordinator, events, item =>
        {
            if (item.Collection is { Status: "completed" })
            {
                collected.TrySetResult();
            }
        });
        var context = await started.Task.DefaultTimeout();
        await collected.Task.DefaultTimeout();
        Assert.False(stream.IsCompleted);
        Assert.False(context.CancellationToken.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(() =>
            context.PublishResult(new TestResult { Name = "late fixture", Outcome = TestOutcome.Failed }));
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 1 });
        await stream.DefaultTimeout();

        Assert.False(events.Last().Passed);
        Assert.Equal("passed", Assert.Single(events, item => item.Result is not null).Result!.Outcome);
        Assert.Contains(events, item => item.Resource is { Outcome: "failed" });
    }

    [Fact]
    public async Task CollectorExceptionPreservesPublishedResultsAndWaitsForOtherCollectorsAndResources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = builder.AddResource(new ContainerResource("first")).WithTesting(context =>
        {
            context.PublishResult(new TestResult { Name = "before error", Outcome = TestOutcome.Passed });
            failed.TrySetResult();
            throw new IOException("fixture collector failure");
        });
        var second = builder.AddResource(new ContainerResource("second")).WithTesting(async context =>
        {
            await context.WaitForCompletionAsync();
            context.PublishResult(new TestResult { Name = "other collector", Outcome = TestOutcome.Passed });
        });
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await failed.Task.DefaultTimeout();
        await notifications.PublishUpdateAsync(first.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.False(completion.IsCompleted);
        await notifications.PublishUpdateAsync(second.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });

        Assert.False(await completion.DefaultTimeout());
        var events = new ConcurrentQueue<TestRunEvent>();
        await ReadAsync(coordinator, events).DefaultTimeout();
        Assert.Equal(2, events.Count(e => e.Result is not null));
        var failure = Assert.Single(events, e => e.Collection is { Status: "failed" }).Collection!;
        Assert.Equal("first", failure.ResourceName);
        Assert.Contains("fixture collector failure", failure.Error);
    }

    [Fact]
    public async Task CancelledNonCooperativeCollectorCannotPublishAfterFinalization()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rejected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        builder.AddResource(new ContainerResource("tests")).WithTesting(async context =>
        {
            started.TrySetResult();
            await release.Task;
            Assert.ThrowsAny<OperationCanceledException>(() =>
                context.PublishResult(new TestResult { Name = "late fixture", Outcome = TestOutcome.Passed }));
            rejected.TrySetResult();
        });
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(cancellation.Token);
        await started.Task.DefaultTimeout();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.DefaultTimeout());
        release.TrySetResult();
        await rejected.Task.DefaultTimeout();
    }

    [Fact]
    public void ResourcePathsArePrivateIsolatedAndAllocatedBeforeExecution()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var firstResource = builder.AddResource(new ContainerResource("first")).WithTesting();
        var secondResource = builder.AddResource(new ContainerResource("second")).WithTesting();
        var first = Assert.Single(firstResource.Resource.Annotations.OfType<TestingAnnotation>());
        var second = Assert.Single(secondResource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Equal(Path.Combine(first.TestingContext!.OutputPath, "first"), first.OutputPath);
        Assert.Equal(Path.Combine(second.TestingContext!.OutputPath, "second"), second.OutputPath);
        Assert.NotEqual(first.OutputPath, second.OutputPath);
        Assert.Equal(first.TestingContext!.OutputPath, second.TestingContext!.OutputPath);
        Assert.True(Directory.Exists(first.OutputPath));
        Assert.True(Directory.Exists(second.OutputPath));
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(mode, File.GetUnixFileMode(first.OutputPath!));
            Assert.Equal(mode, File.GetUnixFileMode(second.OutputPath!));
        }
    }

    [Theory]
    [InlineData("0100")]
    [InlineData("0700")]
    public void RestrictiveUmaskStillCreatesAccessibleOwnerOnlyResourceDirectories(string umask)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var runPath = Path.Combine(workspace.WorkspaceRoot.FullName, "test-output");
        SocketPermissionHelper.CreateDirectory(runPath, repairExisting: false);
        var launcher = Path.Combine(workspace.WorkspaceRoot.FullName, "with-umask");
        File.WriteAllText(launcher, $"#!/bin/sh\numask {umask}\nexec \"$ASPIRE_TEST_CHILD_EXECUTABLE\" \"$@\"\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        // Change umask only in a child so parallel tests keep their process-wide permissions.
        using var invocation = RemoteExecutor.Invoke(static runPath =>
        {
            using var builder = TestDistributedApplicationBuilder.Create();
            builder.Configuration[KnownConfigNames.TestOutputPath] = runPath;
            var resource = builder.AddResource(new ContainerResource("tests")).WithTesting();
            var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());

            Assert.Equal(Path.Combine(runPath, "tests"), annotation.OutputPath);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                    File.GetUnixFileMode(annotation.OutputPath!));
            }
            Assert.Empty(Directory.GetFileSystemEntries(annotation.OutputPath!));
        }, runPath, new RemoteInvokeOptions { Start = false });
        var startInfo = invocation.Process.StartInfo;
        startInfo.Environment["ASPIRE_TEST_CHILD_EXECUTABLE"] = startInfo.FileName;
        startInfo.FileName = launcher;
        invocation.Process.Start();
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("nested/resource")]
    [InlineData("nested\\resource")]
    [InlineData("summary.json")]
    [InlineData(".hidden")]
    [InlineData("trailing.")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("aUx")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    public void UnsafeResourceOutputNamesFailBeforeConfiguration(string name)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        // CreateResourceBuilder also permits custom resources that bypass AddResource validation.
        var resource = builder.CreateResourceBuilder(new ContainerResource(name));
        var configured = false;

        Assert.Throws<ArgumentException>(() => resource.WithTesting(_ =>
        {
            configured = true;
            return Task.CompletedTask;
        }));

        Assert.False(configured);
        Assert.Empty(Directory.GetFileSystemEntries(builder.Configuration[KnownConfigNames.TestOutputPath]!));
    }

    [Theory]
    [InlineData("tests", false)]
    [InlineData("TESTS", false)]
    [InlineData("tests", true)]
    public void ExistingResourceOutputPathsAreNeverAdopted(string existingName, bool isFile)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var runPath = builder.Configuration[KnownConfigNames.TestOutputPath]!;
        var existingPath = Path.Combine(runPath, existingName);
        if (isFile)
        {
            File.WriteAllText(existingPath, "existing");
        }
        else
        {
            SocketPermissionHelper.CreateDirectory(existingPath, repairExisting: false);
            File.WriteAllText(Path.Combine(existingPath, "artifact.txt"), "existing");
        }
        var resource = builder.AddResource(new ContainerResource("tests"));

        Assert.Throws<IOException>(() => resource.WithTesting());

        Assert.Equal([existingName], Directory.GetFileSystemEntries(runPath).Select(Path.GetFileName));
        Assert.Equal("existing", File.ReadAllText(isFile ? existingPath : Path.Combine(existingPath, "artifact.txt")));
        Assert.Empty(resource.Resource.Annotations.OfType<TestingAnnotation>());
    }

    [Fact]
    public void LinkedResourceOutputPathIsRejectedWithoutTouchingTarget()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Ordinary Windows test accounts cannot always create symbolic links.
        }
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var target = workspace.CreateDirectory("unrelated");
        var runPath = builder.Configuration[KnownConfigNames.TestOutputPath]!;
        Directory.CreateSymbolicLink(Path.Combine(runPath, "tests"), target.FullName);
        var mode = File.GetUnixFileMode(target.FullName);

        Assert.Throws<IOException>(() => builder.AddResource(new ContainerResource("tests")).WithTesting());

        Assert.Equal(mode, File.GetUnixFileMode(target.FullName));
        Assert.Empty(target.GetFileSystemInfos());
        Assert.Equal(["tests"], Directory.GetFileSystemEntries(runPath).Select(Path.GetFileName));
    }

    [Fact]
    public async Task SeparateRunsUseTheSameShortResourceNameWithoutSharingOutput()
    {
        using var firstWorkspace = TemporaryWorkspace.Create(outputHelper);
        using var secondWorkspace = TemporaryWorkspace.Create(outputHelper);
        using var firstBuilder = CreateBuilder(firstWorkspace);
        using var secondBuilder = CreateBuilder(secondWorkspace);
        var name = new string('a', ModelName.DefaultMaxLength);
        var paths = await Task.WhenAll(new[] { firstBuilder, secondBuilder }.Select(builder => Task.Run(() =>
        {
            var resource = builder.AddResource(new ContainerResource(name)).WithTesting();
            return Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>()).OutputPath!;
        })));

        Assert.NotEqual(paths[0], paths[1]);
        Assert.All(paths, path =>
        {
            Assert.Equal(name, Path.GetFileName(path));
            Assert.Equal("test-output", Path.GetFileName(Path.GetDirectoryName(path)));
            Assert.True(Directory.Exists(path));
        });
    }

    [Fact]
    public async Task ConcurrentCaseInsensitiveResourceNamesCannotShareOutput()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var resources = new[] { "tests", "TESTS" }
            .Select(name => builder.CreateResourceBuilder(new ContainerResource(name))).ToArray();

        var errors = await Task.WhenAll(resources.Select(resource =>
            Task.Run(() => Record.Exception(() => resource.WithTesting()))));

        Assert.Single(errors, error => error is null);
        Assert.IsAssignableFrom<IOException>(Assert.Single(errors, error => error is not null));
        var runPath = builder.Configuration[KnownConfigNames.TestOutputPath]!;
        var outputDirectory = Assert.Single(Directory.GetFileSystemEntries(runPath));
        Assert.True(Directory.Exists(outputDirectory));
        Assert.Equal("tests", Path.GetFileName(outputDirectory), ignoreCase: true);
        Assert.Single(resources.SelectMany(resource => resource.Resource.Annotations.OfType<TestingAnnotation>()));
    }

    [Fact]
    public void OrdinaryRunDoesNotApplyTestingFilesystemRestrictions()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var resource = builder.AddResource(new ContainerResource("CON")).WithTesting();
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Null(annotation.OutputPath);
        Assert.Null(annotation.TestingContext);
    }

    [Fact]
    public async Task AlreadyFinishedResourceUsesTheSameCompletionHelper()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(async context =>
            outcome.TrySetResult(await context.WaitForCompletionAsync()));
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        using var coordinator = CreateCoordinator(builder, notifications);

        Assert.True(await coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken).DefaultTimeout());
        Assert.True(await outcome.Task.DefaultTimeout());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledCompletionHelperDoesNotReturnAnAlreadyTerminalOutcome(bool passed)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var context = new ResourceTestingContext(new TestingContext(workspace.WorkspaceRoot.FullName),
            "tests", workspace.WorkspaceRoot.FullName, _ => { }, Task.FromResult(passed), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.WaitForCompletionAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunDeadlineCancelsCompletionWaitEvenWithUncancelledExplicitToken(bool supplyCallerToken)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var timeProvider = new FakeTimeProvider();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10), timeProvider);
        using var caller = new CancellationTokenSource();
        var sharedCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ResourceTestingContext(new TestingContext(workspace.WorkspaceRoot.FullName),
            "tests", workspace.WorkspaceRoot.FullName, _ => { }, sharedCompletion.Task, deadline.Token);

        var waiting = context.WaitForCompletionAsync(supplyCallerToken ? caller.Token : default);
        timeProvider.Advance(TimeSpan.FromSeconds(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(caller.IsCancellationRequested);
        Assert.False(sharedCompletion.Task.IsCompleted);
    }

    [Fact]
    public async Task CallerCancellationDoesNotCancelRunOrOtherCompletionWaiters()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var run = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        using var otherCaller = new CancellationTokenSource();
        var sharedCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new ResourceTestingContext(new TestingContext(workspace.WorkspaceRoot.FullName),
            "tests", workspace.WorkspaceRoot.FullName, _ => { }, sharedCompletion.Task, run.Token);
        var localWait = context.WaitForCompletionAsync(caller.Token);
        var defaultWait = context.WaitForCompletionAsync();
        var otherWait = context.WaitForCompletionAsync(otherCaller.Token);

        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => localWait);
        Assert.False(run.IsCancellationRequested);
        Assert.False(sharedCompletion.Task.IsCompleted);
        Assert.False(defaultWait.IsCompleted);
        Assert.False(otherWait.IsCompleted);
        sharedCompletion.SetResult(true);
        Assert.True(await defaultWait.DefaultTimeout());
        Assert.True(await otherWait.DefaultTimeout());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledCallerDoesNotReceiveAlreadyCompletedOutcome(bool runCanBeCancelled)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var run = new CancellationTokenSource();
        using var caller = new CancellationTokenSource();
        var context = new ResourceTestingContext(new TestingContext(workspace.WorkspaceRoot.FullName),
            "tests", workspace.WorkspaceRoot.FullName, _ => { }, Task.FromResult(true),
            runCanBeCancelled ? run.Token : CancellationToken.None);
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.WaitForCompletionAsync(caller.Token));
        Assert.False(run.IsCancellationRequested);
    }

    private IDistributedApplicationTestingBuilder CreateBuilder(TemporaryWorkspace workspace)
    {
        var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "test-output");
        SocketPermissionHelper.CreateDirectory(path, repairExisting: false);
        builder.Configuration[KnownConfigNames.TestOutputPath] = path;
        return builder;
    }

    private static TestRunCoordinator CreateCoordinator(IDistributedApplicationTestingBuilder builder, ResourceNotificationService notifications) =>
        new(new DistributedApplicationModel(builder.Resources), notifications, TimeProvider.System, NullLogger<TestRunCoordinator>.Instance, new ResourceLoggerService());

    private static async Task ReadAsync(TestRunCoordinator coordinator, ConcurrentQueue<TestRunEvent> events, Action<TestRunEvent>? observed = null)
    {
        await foreach (var item in coordinator.WatchAsync(TestContext.Current.CancellationToken))
        {
            events.Enqueue(item);
            observed?.Invoke(item);
        }
    }
}
