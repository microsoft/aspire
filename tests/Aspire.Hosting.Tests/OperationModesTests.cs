// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREWATCH001

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class OperationModesTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task VerifyBackwardsCompatibleRunModeInvocation()
    {
        // The purpose of this test is to verify that the apphost executable will continue
        // to enter run mode if executed without any arguments.

        using var builder = TestDistributedApplicationBuilder.Create().WithTestAndResourceLogging(outputHelper);
        
        var tcs = new TaskCompletionSource<DistributedApplicationExecutionContext>();
        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((e, ct) => {
            var context = e.Services.GetRequiredService<DistributedApplicationExecutionContext>();
            tcs.SetResult(context);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        
        await app.StartAsync().WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        var context = await tcs.Task.WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        await app.StopAsync().WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        Assert.Equal(DistributedApplicationOperation.Run, context.Operation);
        Assert.True(context.IsRunMode);
    }

    [Fact]
    public async Task VerifyExplicitRunModeInvocation()
    {
        // The purpose of this test is to verify that the apphost executable will enter
        // run mode if executed with the "--operation run" argument.

        using var builder = TestDistributedApplicationBuilder
            .Create(["--operation", "run"])
            .WithTestAndResourceLogging(outputHelper);
        
        var tcs = new TaskCompletionSource<DistributedApplicationExecutionContext>();
        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((e, ct) => {
            var context = e.Services.GetRequiredService<DistributedApplicationExecutionContext>();
            tcs.SetResult(context);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        
        await app.StartAsync().WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        var context = await tcs.Task.WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        await app.StopAsync().WaitAsync(TestConstants.LongTimeoutTimeSpan);

        Assert.Equal(DistributedApplicationOperation.Run, context.Operation);
        Assert.True(context.IsRunMode);
    }

    [Fact]
    public async Task VerifyExplicitRunModeWithPublisherInvocation()
    {
        // The purpose of this test is to verify that the apphost executable will enter
        // run mode if executed with the "--operation run" argument.

        using var builder = TestDistributedApplicationBuilder
            .Create(["--operation", "run", "--publisher", "manifest"])
            .WithTestAndResourceLogging(outputHelper);
        
        var tcs = new TaskCompletionSource<DistributedApplicationExecutionContext>();
        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((e, ct) => {
            var context = e.Services.GetRequiredService<DistributedApplicationExecutionContext>();
            tcs.SetResult(context);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        
        await app.StartAsync().WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        var context = await tcs.Task.WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        await app.StopAsync().WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        Assert.Equal(DistributedApplicationOperation.Run, context.Operation);
        Assert.True(context.IsRunMode);
    }

    [Fact]
    public async Task VerifyBackwardsCompatiblePublishModeInvocation()
    {
        // The purpose of this test is to verify that the apphost executable will continue
        // to enter publish mode if the --publisher argument is specified.

        using var builder = TestDistributedApplicationBuilder
            .Create(["--publisher", "manifest", "--output-path", "test-output-path"])
            .WithTestAndResourceLogging(outputHelper);

        // TOOD: This won't work because this event does not fire in publish mode. We need
        //       another way to get at this internal state.
        var tcs = new TaskCompletionSource<DistributedApplicationExecutionContext>();
        builder.Eventing.Subscribe<BeforeStartEvent>((e, ct) => {
            var context = e.Services.GetRequiredService<DistributedApplicationExecutionContext>();
            tcs.SetResult(context);
            return Task.CompletedTask;
        });

        using var app = builder.Build();
        
        await app.StartAsync().WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        var context = await tcs.Task.WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        await app.StopAsync().WaitAsync(TestConstants.DefaultTimeoutTimeSpan);

        Assert.Equal(DistributedApplicationOperation.Publish, context.Operation);
        Assert.True(context.IsPublishMode);
    }

    [Fact]
    public void VerifyExplicitPublishModeInvocation()
    {
        // The purpose of this test is to verify that the apphost executable will continue
        // to enter publish mode if the --publisher argument is specified.

        using var builder = TestDistributedApplicationBuilder
            .Create(["--operation", "publish", "--publisher", "manifest", "--output-path", "test-output-path"])
            .WithTestAndResourceLogging(outputHelper);
        Assert.Equal(DistributedApplicationOperation.Publish, builder.ExecutionContext.Operation);
    }

    [Fact]
    public void WatchIsDisabledByDefaultInRunMode()
    {
        // Without any watch configuration the AppHost runs without watch.

        using var builder = TestDistributedApplicationBuilder
            .Create()
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.False(builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Fact]
    public void WatchIsEnabledWhenConfigured()
    {
        // The "AppHost:Run:WatchEnabled" configuration key enables watch.

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:Run:WatchEnabled=true"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.True(builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Fact]
    public void WatchConfigurationIsCaseInsensitive()
    {
        // The value is parsed case-insensitively so callers do not have to match a particular casing.

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:Run:WatchEnabled=TRUE"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Fact]
    public void WatchIsDisabledForUnparseableValue()
    {
        // An unrecognized value must never fail the run; watch stays disabled.

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:Run:WatchEnabled=bogus"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.False(builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Fact]
    public void WatchIsDisabledForNumericValue()
    {
        // Some configuration sources emit "1" for booleans. bool.TryParse rejects it, so watch stays
        // disabled rather than being silently enabled by a value the AppHost does not accept.

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:Run:WatchEnabled=1"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.False(builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Fact]
    public void WatchIsDisabledInPublishModeEvenWhenConfigured()
    {
        // The run configuration is only meaningful in run mode; publish mode always reports defaults.

        using var builder = TestDistributedApplicationBuilder
            .Create(["--operation", "publish", "--publisher", "manifest", "--output-path", "test-output-path", "AppHost:Run:WatchEnabled=true"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsPublishMode);
        Assert.False(builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Fact]
    public void RunConfigurationIsDefaultWhenExecutionContextConstructedForPublish()
    {
        // The run configuration only applies to run mode. Even if a caller constructs options with watch
        // enabled and a Publish operation, the execution context must report defaults (publish never watches).

        var options = new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
        {
            RunConfiguration = new RunConfiguration { WatchEnabled = true }
        };

        var context = new DistributedApplicationExecutionContext(options);

        Assert.True(context.IsPublishMode);
        Assert.False(context.RunConfiguration.WatchEnabled);
    }

    [Fact]
    public void RunConfigurationIsNeverNull()
    {
        // Every constructor must produce a usable run configuration so integrations never have to null-check it.

        Assert.NotNull(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run).RunConfiguration);
        Assert.NotNull(new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish, "manifest").RunConfiguration);
        Assert.NotNull(new DistributedApplicationExecutionContext(new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)).RunConfiguration);
    }
}
