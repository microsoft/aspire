// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001

using Aspire.Hosting.Utils;
using Aspire.Hosting.Ats;
using Aspire.Hosting.Backchannel;
using Aspire.Shared;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;

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
    public void WatchIsEnabledByDefaultInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder
            .Create()
            .WithTestAndResourceLogging(outputHelper);

        Assert.True(builder.ExecutionContext.IsRunMode);
        Assert.True(builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Theory]
    [InlineData(false, "true")]
    [InlineData(true, "false")]
    [InlineData(true, "bogus")]
    public void ExplicitCodeWatchDefaultOverridesConfiguration(bool enabled, string configured)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DefaultResourceWatchEnabled = enabled, outputHelper,
            $"AppHost:Run:WatchEnabled={configured}");

        Assert.Equal(enabled, builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DirectAndPolyglotFactoriesPreserveTheExplicitCodeDefault(bool enabled)
    {
        var direct = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            DefaultResourceWatchEnabled = enabled,
            Args = [$"AppHost:Run:WatchEnabled={!enabled}"]
        });
        using var directApp = direct.Build();
        Assert.Equal(enabled, direct.ExecutionContext.RunConfiguration.WatchEnabled);

        var guest = DistributedApplication.CreateBuilder(new CreateBuilderOptions
        {
            DisableDashboard = true,
            DefaultResourceWatchEnabled = enabled,
            Args = [$"AppHost:Run:WatchEnabled={!enabled}"]
        });
        using var guestApp = guest.Build();
        Assert.Equal(enabled, guest.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    public async Task InvalidWatchConfigurationWarnsOnceUnlessCodeOverridesIt(bool? codeDefault, int warnings)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            options => options.DefaultResourceWatchEnabled = codeDefault, outputHelper,
            "AppHost:Run:WatchEnabled=bogus");
        using var logs = new BackchannelLoggerProvider();
        builder.Services.AddLogging(logging => logging.AddProvider(logs));
        using var app = builder.Build();

        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken).DefaultTimeout();
        var entries = logs.Subscribe();
        try
        {
            var notices = entries.Snapshot.Where(entry =>
                entry.CategoryName == ResourceWatch.NoticeCategory &&
                entry.EventId.Id == ResourceWatch.InvalidConfigurationEventId).ToArray();
            Assert.Equal(warnings, notices.Length);
            Assert.All(notices, notice =>
                Assert.Equal(Resources.MessageStrings.InvalidResourceWatchConfiguration, notice.Message));
        }
        finally
        {
            logs.Unsubscribe(entries.SubscriberId);
        }
        Assert.Equal(codeDefault ?? false, builder.ExecutionContext.RunConfiguration.WatchEnabled);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, true)]
    [InlineData(DistributedApplicationOperation.Publish, false)]
    public void ExecutionContextConstructorsUseModeSpecificDefaults(DistributedApplicationOperation operation, bool enabled)
    {
        var options = new DistributedApplicationExecutionContextOptions(operation);
        Assert.Equal(enabled, options.RunConfiguration.WatchEnabled);
        Assert.Equal(enabled, new DistributedApplicationExecutionContext(operation).RunConfiguration.WatchEnabled);
        Assert.Equal(enabled, new DistributedApplicationExecutionContext(options).RunConfiguration.WatchEnabled);
    }

    [Fact]
    public void WatchDefaultDoesNotChangeAfterBuilderCreation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["AppHost:Run:WatchEnabled"] = "false";

        Assert.True(builder.ExecutionContext.RunConfiguration.WatchEnabled);
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

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
