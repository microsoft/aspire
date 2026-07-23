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
    public void RunSubModeDefaultsToNormalInRunMode()
    {
        // Without any run sub-mode configuration the AppHost runs in the Normal sub-mode.

        using var builder = TestDistributedApplicationBuilder
            .Create()
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.Equal(RunSubMode.Normal, builder.ExecutionContext.RunSubMode);
    }

    [Fact]
    public void RunSubModeIsWatchWhenConfigured()
    {
        // The "AppHost:RunSubMode" configuration key selects the run sub-mode, mirroring "AppHost:Operation".

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:RunSubMode=Watch"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.Equal(RunSubMode.Watch, builder.ExecutionContext.RunSubMode);
    }

    [Fact]
    public void RunSubModeParsingIsCaseInsensitive()
    {
        // The value is parsed case-insensitively so callers do not have to match the enum casing exactly.

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:RunSubMode=watch"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.Equal(RunSubMode.Watch, builder.ExecutionContext.RunSubMode);
    }

    [Fact]
    public void RunSubModeFallsBackToNormalForUnknownValue()
    {
        // An unrecognized value must never fail the run; it falls back to Normal.

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:RunSubMode=bogus"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.Equal(RunSubMode.Normal, builder.ExecutionContext.RunSubMode);
    }

    [Fact]
    public void RunSubModeIsNormalInPublishModeEvenWhenConfigured()
    {
        // The run sub-mode is only meaningful in run mode; publish mode always reports Normal.

        using var builder = TestDistributedApplicationBuilder
            .Create(["--operation", "publish", "--publisher", "manifest", "--output-path", "test-output-path", "AppHost:RunSubMode=Watch"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsPublishMode);
        Assert.Equal(RunSubMode.Normal, builder.ExecutionContext.RunSubMode);
    }

    [Fact]
    public void RunSubModeFallsBackToNormalForNumericValue()
    {
        // A numeric string is not a declared sub-mode name and must fall back to Normal. This guards against
        // Enum.TryParse's behavior of accepting any numeric value (for example "42" => (RunSubMode)42).

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:RunSubMode=42"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.Equal(RunSubMode.Normal, builder.ExecutionContext.RunSubMode);
    }

    [Fact]
    public void RunSubModeFallsBackToNormalForCompoundValue()
    {
        // A comma-separated value is not a declared sub-mode name and must fall back to Normal. This guards
        // against Enum.TryParse accepting "Normal,Watch" as Watch even though RunSubMode is not [Flags].

        using var builder = TestDistributedApplicationBuilder
            .Create(["AppHost:RunSubMode=Normal,Watch"])
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.Equal(RunSubMode.Normal, builder.ExecutionContext.RunSubMode);
    }

    [Fact]
    public void RunSubModeIsNormalWhenExecutionContextConstructedForPublish()
    {
        // The run sub-mode only applies to run mode. Even if a caller constructs options with a Watch
        // sub-mode and a Publish operation, the execution context must report Normal (publish never watches).

        var options = new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Publish)
        {
            RunSubMode = RunSubMode.Watch
        };

        var context = new DistributedApplicationExecutionContext(options);

        Assert.True(context.IsPublishMode);
        Assert.Equal(RunSubMode.Normal, context.RunSubMode);
    }
}
