// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Cli;
using Aspire.Hosting.Testing;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIRETESTING001 // Tests the experimental testing API.

[Trait("Partition", "4")]
public class TestingCollectorTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PublicTestingContractIsExperimental()
    {
        Assert.All(new[] { typeof(TestingOptions), typeof(ResourceTestingContext), typeof(TestRunContext),
            typeof(TestResult), typeof(TestOutcome), typeof(TestingAnnotation), typeof(TestResourceBuilderExtensions) },
            type => Assert.Equal("ASPIRETESTING001", type.GetCustomAttribute<ExperimentalAttribute>()!.DiagnosticId));
        Assert.Equal("ASPIRETESTING001",
            typeof(TestResourceBuilderExtensions).GetMethod(nameof(TestResourceBuilderExtensions.WithTesting))!
                .GetCustomAttribute<ExperimentalAttribute>()!.DiagnosticId);
    }

    [Fact]
    public async Task OrdinaryRunConfiguresImmediatelyButDoesNotInvokeCollectorsOrInventOutputPaths()
    {
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        TestingOptions? captured = null;
        var collectorCalled = false;
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(options =>
        {
            captured = options;
            options.CollectResults(_ =>
            {
                collectorCalled = true;
                return Task.CompletedTask;
            });
        });
        Assert.NotNull(captured);
        Assert.False(captured.IsTestRun);
        Assert.Null(captured.OutputPath);
        Assert.Null(captured.RunContext);
        using var notifications = ResourceNotificationServiceTestHelpers.Create();
        using var coordinator = CreateCoordinator(builder, notifications);
        var completion = coordinator.WaitForCompletionAsync(TestContext.Current.CancellationToken);
        await notifications.PublishUpdateAsync(resource.Resource, s => s with { State = KnownResourceStates.Finished, ExitCode = 0 });
        Assert.True(await completion.DefaultTimeout());
        Assert.False(collectorCalled);
    }

    [Theory]
    [InlineData("", TestOutcome.Passed, 0)]
    [InlineData("fixture", (TestOutcome)0, 0)]
    [InlineData("fixture", TestOutcome.Passed, -1)]
    public void InvalidPublishedResultsAreExplicitErrors(string name, TestOutcome outcome, int duration)
    {
        var context = new ResourceTestingContext(new TestRunContext("run-output"), "tests", "resource-output",
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
        resource.WithTesting(options => options.CollectResults(async context =>
        {
            Interlocked.Increment(ref calls);
            context.PublishResult(new TestResult { Name = "live fixture", Outcome = TestOutcome.Passed });
            started.TrySetResult();
            resourceFinished.TrySetResult(await context.WaitForCompletionAsync());
            await release.Task.WaitAsync(context.CancellationToken);
            context.PublishResult(new TestResult { Name = "final fixture", Outcome = TestOutcome.Skipped, Message = "fixture skip" });
        }));
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
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(options =>
            options.CollectResults(context =>
            {
                context.PublishResult(new TestResult { Name = "failed fixture", Outcome = TestOutcome.Failed });
                published.TrySetResult();
                return Task.CompletedTask;
            }));
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
    public async Task CollectorExceptionPreservesPublishedResultsAndWaitsForOtherCollectorsAndResources()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = builder.AddResource(new ContainerResource("first")).WithTesting(options =>
        {
            options.CollectResults(context =>
            {
                context.PublishResult(new TestResult { Name = "before error", Outcome = TestOutcome.Passed });
                failed.TrySetResult();
                throw new IOException("fixture collector failure");
            });
            options.CollectResults(async context =>
            {
                await context.WaitForCompletionAsync();
                context.PublishResult(new TestResult { Name = "other collector", Outcome = TestOutcome.Passed });
            });
        });
        var second = builder.AddResource(new ContainerResource("second")).WithTesting();
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
        builder.AddResource(new ContainerResource("tests")).WithTesting(options =>
            options.CollectResults(async context =>
            {
                started.TrySetResult();
                await release.Task;
                Assert.ThrowsAny<OperationCanceledException>(() =>
                    context.PublishResult(new TestResult { Name = "late fixture", Outcome = TestOutcome.Passed }));
                rejected.TrySetResult();
            }));
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
    public void ResourcePathsArePrivateIsolatedAndAvailableDuringImmediateConfiguration()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        TestingOptions? first = null;
        TestingOptions? second = null;
        builder.AddResource(new ContainerResource("first")).WithTesting(options => first = options);
        builder.AddResource(new ContainerResource("second")).WithTesting(options => second = options);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(("first", "second"), (first.ResourceName, second.ResourceName));
        Assert.NotEqual(first.OutputPath, second.OutputPath);
        Assert.Equal(first.RunContext!.OutputPath, second.RunContext!.OutputPath);
        Assert.True(Directory.Exists(first.OutputPath));
        Assert.True(Directory.Exists(second.OutputPath));
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(mode, File.GetUnixFileMode(first.OutputPath!));
            Assert.Equal(mode, File.GetUnixFileMode(second.OutputPath!));
        }
        Assert.Throws<InvalidOperationException>(() => first.CollectResults(_ => Task.CompletedTask));
    }

    [Fact]
    public async Task AlreadyFinishedResourceUsesTheSameCompletionHelper()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = CreateBuilder(workspace);
        var outcome = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = builder.AddResource(new ContainerResource("tests")).WithTesting(options =>
            options.CollectResults(async context => outcome.TrySetResult(await context.WaitForCompletionAsync())));
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
        var context = new ResourceTestingContext(new TestRunContext(workspace.WorkspaceRoot.FullName),
            "tests", workspace.WorkspaceRoot.FullName, _ => { }, Task.FromResult(passed), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(context.WaitForCompletionAsync);
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
