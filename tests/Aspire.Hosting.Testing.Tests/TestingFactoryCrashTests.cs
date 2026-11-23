// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Aspire.Hosting.Testing.Tests;

// Tests that DistributedApplicationTestingBuilder throws exceptions at the right times when the app crashes.
public class TestingFactoryCrashTests
{
    [Theory]
    [RequiresFeature(TestFeature.ContainerRuntime)]
    [InlineData("before-build")]
    [InlineData("after-build")]
    [InlineData("after-start")]
    [InlineData("after-shutdown")]
    public async Task CrashTests(string crashArg)
    {
        var timeout = TimeSpan.FromMinutes(5);
        using var cts = new CancellationTokenSource(timeout);

        var factory = new DistributedApplicationFactory(typeof(Projects.TestingAppHost1_AppHost), [$"--crash-{crashArg}"]);

        if (crashArg is "before-build" or "after-build")
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.StartAsync().WaitAsync(cts.Token));
            Assert.Contains(crashArg, exception.Message);
        }
        else
        {
            await factory.StartAsync().WaitAsync(cts.Token);
        }

        await factory.DisposeAsync().AsTask().WaitAsync(cts.Token);
    }

    // Regression test for https://github.com/microsoft/aspire/issues/18690. The AppHost builds with a
    // SynchronizationContext installed, which stops continuations completed from inside Build() from being inlined on
    // the entry-point thread. The factory must still record the built application before Build() returns, because
    // code that runs after Build() (here, a crash) would otherwise fault the factory first.
    [Fact]
    public async Task BuiltApplicationIsPublishedBeforeEntryPointContinuesPastBuild()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var buildProbe = TestingAppHostEntryPointProbe.CreateBuildProbe();
        await using var factory = new BuildObservingFactory(buildProbe, ["--crash-after-build", $"--entry-point-build-probe={buildProbe.Id}"]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => factory.StartAsync().WaitAsync(cts.Token));
        Assert.Contains("after-build", exception.Message);

        var builtDuringBuild = await factory.Built.WaitAsync(cts.Token);
        Assert.True(builtDuringBuild, "OnBuilt was not invoked synchronously from Build() on the entry-point thread.");

        var app = await factory.ResolveApplicationAsync(cts.Token);
        Assert.NotNull(app);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TestingBuilderBuildAsyncSucceedsWhenAppHostCrashesImmediatelyAfterBuild(bool genericEntryPoint)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        using var buildProbe = TestingAppHostEntryPointProbe.CreateBuildProbe();
        string[] args = ["--crash-after-build", $"--entry-point-build-probe={buildProbe.Id}"];

        await using var builder = genericEntryPoint
            ? await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(args, cts.Token).WaitAsync(cts.Token)
            : await DistributedApplicationTestingBuilder.CreateAsync(typeof(Projects.TestingAppHost1_AppHost), args, cts.Token).WaitAsync(cts.Token);

        // The crash happens after Build(), so it must surface from StartAsync rather than from BuildAsync.
        await using var app = await builder.BuildAsync(cts.Token).WaitAsync(cts.Token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync(cts.Token).WaitAsync(cts.Token));
        Assert.Contains("after-build", exception.Message);
    }

    private sealed class BuildObservingFactory(TestingAppHostEntryPointProbe.BuildProbe buildProbe, string[] args)
        : DistributedApplicationFactory(typeof(Projects.TestingAppHost1_AppHost), args)
    {
        private readonly TaskCompletionSource<bool> _built = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> Built => _built.Task;

        protected override void OnBuilt(DistributedApplication application)
        {
            base.OnBuilt(application);
            _built.TrySetResult(buildProbe.IsBuildingOnCurrentThread);
        }
    }
}
