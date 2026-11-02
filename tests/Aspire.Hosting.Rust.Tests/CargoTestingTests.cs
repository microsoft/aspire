// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

#pragma warning disable ASPIRETESTING001
#pragma warning disable ASPIRECOMMAND001

namespace Aspire.Hosting.Rust.Tests;

public class CargoTestingTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PublicContractIsExperimentalAndExported()
    {
        var method = typeof(CargoTestingExtensions).GetMethod(nameof(CargoTestingExtensions.WithCargoTesting))!;
        foreach (var member in new MemberInfo[] { typeof(CargoTestingExtensions), method })
        {
            var experimental = member.GetCustomAttribute<ExperimentalAttribute>()!;
            Assert.Equal("ASPIRETESTING001", experimental.DiagnosticId);
            Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", experimental.UrlFormat);
        }
        Assert.NotNull(method.GetCustomAttribute<AspireExportAttribute>());
        Assert.Equal(typeof(IResourceBuilder<ExecutableResource>), method.ReturnType);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyCargoIsRequiredAndArgumentsAndEnvironmentRemainUnchanged(bool testing)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        if (testing)
        {
            builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        }
        var original = builder.AddExecutable("tests", "cargo", workspace.Path,
            "+stable", "test", "--workspace", "--package=fixture", "--profile=test",
            "--manifest-path=Cargo.toml", "--features=integration", "--no-fail-fast", "--", "--nocapture");
        var resource = original.WithCargoTesting().WithEnvironment("CUSTOM", "value");
        Assert.Same(original, resource);
        Assert.Collection(resource.Resource.Annotations.OfType<RequiredCommandAnnotation>(),
            command => Assert.Equal("cargo", command.Command));
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore,
            Assert.Single(resource.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            new DistributedApplicationModel(builder.Resources)), TestContext.Current.CancellationToken);
        Assert.Equal(["+stable", "test", "--workspace", "--package=fixture", "--profile=test",
            "--manifest-path=Cargo.toml", "--features=integration", "--no-fail-fast", "--", "--nocapture"],
            await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services));
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(resource.Resource, serviceProvider: app.Services);
        Assert.Equal("value", environment["CUSTOM"]);
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.NotNull(annotation.Collector);
        Assert.Equal(testing, annotation.TestingContext is not null);
        Assert.Equal(testing ? Path.Combine(workspace.Path, "output", "tests") : null, annotation.OutputPath);
        if (testing)
        {
            Assert.Equal(Path.Combine(workspace.Path, "output"), annotation.TestingContext!.OutputPath);
            Assert.Empty(Directory.GetFiles(annotation.OutputPath!));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureWithoutAnyReportPublishesNothingAndPreservesExecutionOutcome(bool passed)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddExecutable("tests", "cargo", workspace.Path, "test").WithCargoTesting();
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = false;
        var context = new ResourceTestingContext(annotation.TestingContext!, "tests", annotation.OutputPath!,
            _ => Assert.Fail("Cargo must not invent per-test outcomes."), completion.Task,
            TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (_, _) =>
            {
                captured = true;
                return Task.CompletedTask;
            }
        };
        Assert.Same(annotation.TestingContext, context.TestingContext);
        Assert.Equal(Path.Combine(workspace.Path, "output"), context.TestingContext.OutputPath);
        Assert.Equal(Path.Combine(context.TestingContext.OutputPath, "tests"), context.OutputPath);
        var collection = annotation.Collector!(context);
        Assert.False(collection.IsCompleted);
        completion.SetResult(passed);
        await collection;
        Assert.True(captured);
        Assert.Equal(passed, await context.WaitForCompletionAsync());
    }

    [Fact]
    public async Task ReplacingOrClearingCollectorPreservesContextPathAndLaunchGuards()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddExecutable("tests", "cargo", workspace.Path, "test").WithCargoTesting();
        var original = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Func<ResourceTestingContext, Task> replacement = _ => throw new InvalidOperationException("Runtime only.");
        resource.WithTesting(replacement);
        var replaced = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Same(replacement, replaced.Collector);
        Assert.Same(original.TestingContext, replaced.TestingContext);
        Assert.Equal(original.OutputPath, replaced.OutputPath);
        resource.WithTesting();
        var marker = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        Assert.Null(marker.Collector);
        Assert.Same(original.TestingContext, marker.TestingContext);
        Assert.Equal(original.OutputPath, marker.OutputPath);
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeStartEvent(app.Services,
            new DistributedApplicationModel(builder.Resources)), TestContext.Current.CancellationToken);
        Assert.Equal(["test"], await ArgumentEvaluator.GetArgumentListAsync(resource.Resource, app.Services));
        Assert.Empty(Directory.GetFiles(marker.OutputPath!));
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledLocalWaitDoesNotCancelSharedCargoCapture()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddExecutable("tests", "cargo", workspace.Path, "test").WithCargoTesting();
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = 0;
        var context = new ResourceTestingContext(annotation.TestingContext!, "tests", annotation.OutputPath!,
            _ => Assert.Fail("Cargo must not invent per-test outcomes."), completion.Task,
            TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (_, _) =>
            {
                Interlocked.Increment(ref captures);
                return Task.CompletedTask;
            }
        };
        var collection = annotation.Collector!(context);
        Assert.Same(collection, context.CaptureOutputAsync());
        using var localCancellation = new CancellationTokenSource();
        var localWait = context.WaitForCompletionAsync(localCancellation.Token);
        localCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => localWait);
        Assert.False(collection.IsCompleted);
        completion.SetResult(false);
        await collection;
        Assert.Equal(1, captures);
        Assert.False(await context.WaitForCompletionAsync());
    }

    [Fact]
    public async Task RunCancellationStopsCargoCaptureWithoutPublishingResults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddExecutable("tests", "cargo", workspace.Path, "test").WithCargoTesting();
        var annotation = Assert.Single(resource.Resource.Annotations.OfType<TestingAnnotation>());
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var context = new ResourceTestingContext(annotation.TestingContext!, "tests", annotation.OutputPath!,
            _ => Assert.Fail("Cargo must not invent per-test outcomes."), completion.Task, cancellation.Token)
        {
            CaptureOutputCallback = (_, _) => throw new InvalidOperationException("Execution has not finished.")
        };
        var collection = annotation.Collector!(context);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collection);
        Assert.False(completion.Task.IsCompleted);
    }

    [Theory]
    [InlineData("nextest")]
    [InlineData("run")]
    [InlineData("build")]
    public void NonTestEntrypointsFail(string command)
    {
        Assert.Throws<ArgumentException>(() => CargoTestingExtensions.ValidateArguments([command]));
    }

    [Theory]
    [InlineData("terminal")]
    [InlineData("replicas")]
    [InlineData("command")]
    public async Task UnsupportedLaunchFailsBeforeExecution(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddExecutable("tests", scenario == "command" ? "other" : "cargo",
            workspace.Path, "test").WithCargoTesting();
        if (scenario != "command")
        {
            resource.Resource.Annotations.Add(scenario == "terminal"
                ? new TerminalAnnotation(new TerminalOptions()) : new ReplicaAnnotation(2));
        }
        await using var app = builder.Build();
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeStartEvent(app.Services, new DistributedApplicationModel(builder.Resources)),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SecondStartFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(outputHelper);
        builder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] = Path.Combine(workspace.Path, "output");
        var resource = builder.AddExecutable("tests", "cargo", workspace.Path, "test").WithCargoTesting();
        await using var app = builder.Build();
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource.Resource, app.Services),
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(resource.Resource, app.Services), TestContext.Current.CancellationToken));
    }
}
