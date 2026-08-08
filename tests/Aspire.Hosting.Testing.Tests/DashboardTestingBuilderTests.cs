// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001

using System.Runtime.CompilerServices;
using Aspire.Hosting.Dashboard;
using Aspire.Hosting.Dcp;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;
using TestingResources = Aspire.Hosting.Testing.Properties.Resources;

namespace Aspire.Hosting.Testing.Tests;

public class DashboardTestingBuilderTests
{
    private const string AspNetCoreUrls = "ASPNETCORE_URLS";
    private const string DashboardOtlpGrpcEndpointUrl = "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL";
    private const string DashboardOtlpHttpEndpointUrl = "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL";
    private const string DashboardUnsecuredAllowAnonymous = "ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS";
    private const string InteractivityEnabled = "ASPIRE_INTERACTIVITY_ENABLED";
    private const string ResourceServiceEndpointUrl = "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL";
    private const string DashboardFrontendBrowserToken = "ASPIRE_DASHBOARD_FRONTEND_BROWSERTOKEN";
    private const string AppHostBrowserToken = "AppHost:BrowserToken";

    [Fact]
    public void DashboardIsDisabledByDefault()
    {
        var options = new DistributedApplicationTestingBuilderOptions();

        Assert.False(options.EnableDashboard);

        using var builder = DistributedApplicationTestingBuilder.Create();
        Assert.Null(builder.Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DashboardServiceHost)));
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    [InlineData(CreationSurface.AdHoc)]
    public async Task DashboardCanBeEnabledAtBuilderCreation(CreationSurface creationSurface)
    {
        var builder = await CreateDashboardBuilderAsync(creationSurface, []);

        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(DashboardServiceHost));
        AssertDashboardTestingDefaults(builder);

        await using var app = await builder.BuildAsync();
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    [InlineData(CreationSurface.AdHoc)]
    public async Task DashboardTestingDefaultsOverrideConfigurationPresentDuringCreation(CreationSurface creationSurface)
    {
        string[] args =
        [
            "--DcpPublisher:RandomizePorts=false",
            $"--{AspNetCoreUrls}=http://127.0.0.1:12345",
            $"--{DashboardOtlpGrpcEndpointUrl}=http://127.0.0.1:12346",
            $"--{DashboardOtlpHttpEndpointUrl}=http://127.0.0.1:12347",
            $"--{ResourceServiceEndpointUrl}=http://127.0.0.1:12348",
            $"--{DashboardUnsecuredAllowAnonymous}=true",
            $"--{InteractivityEnabled}=true"
        ];

        await using var builder = await CreateDashboardBuilderAsync(creationSurface, args);

        AssertDashboardTestingDefaults(builder);
        Assert.Equal(nameof(ResourceServiceAuthMode.ApiKey), builder.Configuration["AppHost:ResourceService:AuthMode"]);
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    public async Task DashboardTestingDefaultsOverrideAppHostConfiguration(CreationSurface creationSurface)
    {
        await using var builder = await CreateDashboardBuilderAsync(
            creationSurface,
            ["--override-dashboard-testing-defaults"]);

        AssertDashboardTestingDefaults(builder);
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    [InlineData(CreationSurface.AdHoc)]
    public async Task DashboardTestingGeneratesAFreshBrowserTokenPerApplication(CreationSurface creationSurface)
    {
        // A token shared by every application under test is barely better than anonymous access, and this is
        // exactly the shape an ambient ASPIRE_DASHBOARD_FRONTEND_BROWSERTOKEN on a CI agent would take.
        const string SharedToken = "shared-browser-token";
        string[] args = [$"--{DashboardFrontendBrowserToken}={SharedToken}"];

        await using var first = await CreateDashboardBuilderAsync(creationSurface, args);
        await using var second = await CreateDashboardBuilderAsync(creationSurface, args);

        var firstToken = first.Configuration[AppHostBrowserToken];
        var secondToken = second.Configuration[AppHostBrowserToken];

        Assert.NotEmpty(firstToken!);
        Assert.NotEmpty(secondToken!);
        Assert.NotEqual(SharedToken, firstToken);
        Assert.NotEqual(SharedToken, secondToken);
        Assert.NotEqual(firstToken, secondToken);
    }

    [Fact]
    public async Task DashboardTestingDefaultsAreNonInteractiveAndFailFast()
    {
        var builder = DistributedApplicationTestingBuilder.Create(CreateDashboardOptions(), []);

        await using var app = await builder.BuildAsync();

        Assert.False(app.Services.GetRequiredService<IInteractionService>().IsAvailable);
        Assert.Equal(
            WaitBehavior.StopOnResourceUnavailable,
            app.Services.GetRequiredService<IOptions<ResourceNotificationServiceOptions>>().Value.DefaultWaitBehavior);
    }

    [Fact]
    public async Task DashboardTestingDefaultWaitBehaviorCanBeOverriddenThroughOptions()
    {
        // Fail-fast is right for an unattended run, but when the dashboard is up to be looked at, waiting keeps the
        // stuck resource alive long enough to inspect instead of tearing the application down.
        var options = CreateDashboardOptions();
        options.DefaultWaitBehavior = WaitBehavior.WaitOnResourceUnavailable;

        var builder = DistributedApplicationTestingBuilder.Create(options, []);

        await using var app = await builder.BuildAsync();

        Assert.Equal(
            WaitBehavior.WaitOnResourceUnavailable,
            app.Services.GetRequiredService<IOptions<ResourceNotificationServiceOptions>>().Value.DefaultWaitBehavior);
    }

    [Fact]
    public async Task DashboardTestingDefaultsCanBeOverriddenAfterCreation()
    {
        var builder = DistributedApplicationTestingBuilder.Create(CreateDashboardOptions(), []);
        builder.Configuration[InteractivityEnabled] = "true";
        builder.Configuration["DcpPublisher:RandomizePorts"] = "false";
        builder.Configuration[AspNetCoreUrls] = "http://127.0.0.1:12345";
        builder.Services.Configure<ResourceNotificationServiceOptions>(
            options => options.DefaultWaitBehavior = WaitBehavior.WaitOnResourceUnavailable);

        await using var app = await builder.BuildAsync();

        Assert.True(app.Services.GetRequiredService<IInteractionService>().IsAvailable);
        Assert.False(app.Services.GetRequiredService<IOptions<DcpOptions>>().Value.RandomizePorts);
        Assert.Equal(
            "http://127.0.0.1:12345",
            app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value.DashboardUrl);
        Assert.Equal(
            WaitBehavior.WaitOnResourceUnavailable,
            app.Services.GetRequiredService<IOptions<ResourceNotificationServiceOptions>>().Value.DefaultWaitBehavior);
    }

    [Fact]
    public async Task DashboardTestingOptionsCannotBeNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(null!, []));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DistributedApplicationTestingBuilder.CreateAsync(typeof(Projects.TestingAppHost1_AppHost), null!, []));
        Assert.Throws<ArgumentNullException>(() => DistributedApplicationTestingBuilder.Create(null!, []));
    }

    [Fact]
    public async Task ExistingDefaultCallsRemainUnambiguous()
    {
        // `default` has to keep binding to the pre-existing params string[] and CancellationToken overloads
        // rather than to the new options overloads, otherwise adding those overloads is a source-breaking change.
        // The compiler proves the binding; these assertions prove the bound overloads still behave as before.
        Assert.Throws<ArgumentNullException>(() => DistributedApplicationTestingBuilder.Create(default!));

        await using var genericBuilder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(default);
        await using var typeBuilder =
            await DistributedApplicationTestingBuilder.CreateAsync(typeof(Projects.TestingAppHost1_AppHost), default);

        Assert.Null(genericBuilder.Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DashboardServiceHost)));
        Assert.Null(typeBuilder.Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DashboardServiceHost)));

        await using var genericOptionsBuilder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                CreateDashboardOptions(),
                [],
                default);
        await using var typeOptionsBuilder =
            await DistributedApplicationTestingBuilder.CreateAsync(
                typeof(Projects.TestingAppHost1_AppHost),
                CreateDashboardOptions(),
                [],
                default);

        Assert.Single(genericOptionsBuilder.Services, descriptor => descriptor.ServiceType == typeof(DashboardServiceHost));
        Assert.Single(typeOptionsBuilder.Services, descriptor => descriptor.ServiceType == typeof(DashboardServiceHost));
    }

    [Fact]
    public async Task BuildAsyncWithPreCanceledTokenDoesNotReleaseAppHost()
    {
        var probe = TestingAppHostBuildProbe.Create();
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                [$"--block-apphost-build={probe.Id}"]);
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            cancellationTokenSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => builder.BuildAsync(cancellationTokenSource.Token));
            await Assert.ThrowsAsync<TimeoutException>(
                () => probe.BuildEntered.WaitAsync(TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            probe.ContinueBuilding();
            await builder.DisposeAsync();
            probe.Dispose();
        }
    }

    [Fact]
    public async Task BuildAsyncCancellationAfterReleaseReturnsPromptlyAndDisposesBuiltApplication()
    {
        var probe = TestingAppHostBuildProbe.Create();
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                [$"--block-apphost-build={probe.Id}"]);
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var buildTask = builder.BuildAsync(cancellationTokenSource.Token);
            await probe.BuildEntered.DefaultTimeout();
            Assert.False(buildTask.IsCompleted);

            cancellationTokenSource.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buildTask.DefaultTimeout());

            // The AppHost is still blocked inside Build(), so observing cancellation here proves BuildAsync
            // returned without waiting for the application it already released.
            Assert.False(probe.ApplicationDisposed.IsCompleted);

            probe.ContinueBuilding();

            // Promptness is already proven by the assertion above. Reclaiming the late application goes through
            // DistributedApplicationFactory.DisposeAsync, which first waits for the released AppHost entry point to
            // exit under the host's shutdown timeout, so this leg needs a budget larger than DefaultTimeout's 5s.
            await probe.ApplicationDisposed.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            probe.ContinueBuilding();
            await builder.DisposeAsync();
            probe.Dispose();
        }
    }

    [Fact]
    public async Task BuildAsyncCancellationFollowedByDisposeStillDisposesLateApplication()
    {
        var probe = TestingAppHostBuildProbe.Create();
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                [$"--block-apphost-build={probe.Id}"]);
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var buildTask = builder.BuildAsync(cancellationTokenSource.Token);
            await probe.BuildEntered.DefaultTimeout();

            cancellationTokenSource.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buildTask.DefaultTimeout());

            // Disposing before the AppHost finishes building is the ordinary `await using` sequence. The
            // application arrives after disposal has already claimed the factory, so nothing the caller holds
            // can tear it down; the factory has to reclaim it.
            await builder.DisposeAsync().DefaultTimeout();

            probe.ContinueBuilding();

            await probe.ApplicationDisposed.DefaultTimeout();
        }
        finally
        {
            probe.ContinueBuilding();
            await builder.DisposeAsync();
            probe.Dispose();
        }
    }

    [Fact]
    public async Task BuildAsyncCancellationPreservesOperationCanceledExceptionWhenAppHostLaterFails()
    {
        var probe = TestingAppHostBuildProbe.Create();
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                [$"--block-apphost-build={probe.Id}", "--crash-after-build"]);
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var buildTask = builder.BuildAsync(cancellationTokenSource.Token);
            await probe.BuildEntered.DefaultTimeout();

            cancellationTokenSource.Cancel();
            probe.ContinueBuilding();
            await probe.EntryPointFailure.DefaultTimeout();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buildTask);
        }
        finally
        {
            probe.ContinueBuilding();
            await builder.DisposeAsync();
            probe.Dispose();
        }
    }

    [Fact]
    public async Task BuildAsyncAfterBuilderDisposedThrowsObjectDisposedException()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>();
        await builder.DisposeAsync();

        var exception = await Assert.ThrowsAsync<ObjectDisposedException>(() => builder.BuildAsync());

        Assert.Equal(nameof(IDistributedApplicationTestingBuilder), exception.ObjectName);
    }

    [Fact]
    public async Task BuildAsyncAfterCancellationAbandonedTheApplicationIsRejected()
    {
        var probe = TestingAppHostBuildProbe.Create();
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                [$"--block-apphost-build={probe.Id}"]);
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var buildTask = builder.BuildAsync(cancellationTokenSource.Token);
            await probe.BuildEntered.DefaultTimeout();

            cancellationTokenSource.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => buildTask.DefaultTimeout());

            // No caller was left waiting, so the application the AppHost is still building belongs to the
            // background reclaim. Handing it to a retry would give that caller an application being disposed.
            // The budget only bounds a regression: rejection happens before this token can be observed.
            using var retryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => builder.BuildAsync(retryTimeout.Token));

            Assert.Equal(
                "The application was abandoned because the BuildAsync call that released the AppHost was canceled, " +
                "and it is being disposed. An AppHost entry point builds a single application, so building again " +
                "requires a new testing builder.",
                exception.Message);
        }
        finally
        {
            probe.ContinueBuilding();
            await builder.DisposeAsync();
            probe.Dispose();
        }
    }

    [Fact]
    public async Task BuildAsyncCancellationLeavesTheApplicationToAConcurrentBuild()
    {
        var probe = TestingAppHostBuildProbe.Create();
        var builder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                [$"--block-apphost-build={probe.Id}"]);
        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var canceledBuildTask = builder.BuildAsync(cancellationTokenSource.Token);
            await probe.BuildEntered.DefaultTimeout();

            // BuildAsync registers the caller as waiting before it reaches its first await, so by the time this
            // returns a task the surviving build is already accounted for by the cancellation below.
            var survivingBuildTask = builder.BuildAsync(CancellationToken.None);

            cancellationTokenSource.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledBuildTask.DefaultTimeout());

            probe.ContinueBuilding();
            var application = await survivingBuildTask.WaitAsync(TimeSpan.FromSeconds(60));

            // A background reclaim would dispose the factory as soon as the application arrived, which flips the
            // builder into its disposed state. Owning the application means the builder still hands it back.
            await Task.Delay(TimeSpan.FromSeconds(2));
            var rebuiltApplication = await builder.BuildAsync().DefaultTimeout();
            Assert.Same(application.Services, rebuiltApplication.Services);
            Assert.False(probe.ApplicationDisposed.IsCompleted);
        }
        finally
        {
            probe.ContinueBuilding();
            await builder.DisposeAsync();
            probe.Dispose();
        }
    }

    [Theory]
    [InlineData(CreationSurface.Generic, "--operation", "publish")]
    [InlineData(CreationSurface.Generic, "--publisher", "manifest")]
    [InlineData(CreationSurface.Type, "--operation", "publish")]
    [InlineData(CreationSurface.Type, "--publisher", "manifest")]
    [InlineData(CreationSurface.AdHoc, "--operation", "publish")]
    [InlineData(CreationSurface.AdHoc, "--publisher", "manifest")]
    public async Task DashboardTestingIsRejectedInPublishMode(
        CreationSurface creationSurface,
        string argumentName,
        string argumentValue)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreatePublishBuilderAsync(creationSurface, [argumentName, argumentValue]));

        Assert.Equal(TestingResources.DashboardTestingPublishModeExceptionMessage, exception.Message);
    }

    [Fact]
    public async Task DashboardEnabledThroughConfigureBuilderKeepsCallerConfiguration()
    {
        // DistributedApplicationOptions.DisableDashboard = false is the older spelling of "run the dashboard" and it
        // predates DistributedApplicationTestingBuilderOptions. It is not the hardened dashboard testing mode, so
        // configuration the caller supplied at creation has to survive rather than being rewritten underneath them.
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
            [$"--{DashboardUnsecuredAllowAnonymous}=true", $"--{AspNetCoreUrls}=http://127.0.0.1:12345"],
            (options, _) => options.DisableDashboard = false);

        Assert.Equal("true", builder.Configuration[DashboardUnsecuredAllowAnonymous]);
        Assert.Equal("http://127.0.0.1:12345", builder.Configuration[AspNetCoreUrls]);
    }

    [Fact]
    public async Task DashboardEnabledThroughConfigureBuilderIsNotRejectedInPublishMode()
    {
        // Publish mode never adds a dashboard resource, so the older spelling has always been ignored there.
        // Rejecting it would break existing callers that publish with DisableDashboard = false, and dropping their
        // arguments while resolving dashboard settings would silently turn a publish run into a run-mode run.
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
            ["--publisher", "manifest"],
            (options, _) => options.DisableDashboard = false);

        Assert.True(builder.ExecutionContext.IsPublishMode);
    }

    [Theory]
    [InlineData(CreationSurface.Generic, "--clear-apphost-browser-token")]
    [InlineData(CreationSurface.Generic, "--null-apphost-browser-token")]
    [InlineData(CreationSurface.Type, "--clear-apphost-browser-token")]
    [InlineData(CreationSurface.Type, "--null-apphost-browser-token")]
    public async Task DashboardTestingRestoresTheBrowserTokenAnAppHostCleared(CreationSurface creationSurface, string appHostFlag)
    {
        // DistributedApplicationBuilder freezes the token into AppHost:BrowserToken during construction, but
        // DashboardOptions does not read that key until the application starts. AppHost code runs between those
        // points, so without the restore it can blank the key and DashboardEventHandlers launches the dashboard
        // with Unsecured frontend authentication, defeating the authenticated default this opt-in promises.
        await using var builder = await CreateDashboardBuilderAsync(creationSurface, [appHostFlag]);
        await using var app = await builder.BuildAsync();

        var dashboardOptions = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.False(string.IsNullOrEmpty(dashboardOptions.DashboardToken));
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    public async Task DashboardTestingLeavesTheBrowserTokenToTheCallersBuilder(CreationSurface creationSurface)
    {
        // The restore above runs before the caller sees the builder, so a test that deliberately wants the
        // anonymous dashboard keeps the documented escape hatch through the returned builder.
        await using var builder = await CreateDashboardBuilderAsync(creationSurface, []);
        builder.Configuration["AppHost:BrowserToken"] = "";
        await using var app = await builder.BuildAsync();

        var dashboardOptions = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.True(string.IsNullOrEmpty(dashboardOptions.DashboardToken));
    }

    [Fact]
    public async Task ConcurrentDisposeAsyncRunsTeardownOnce()
    {
        // DistributedApplicationFactory.DisposeAsync claims disposal with an interlocked exchange. A guard that only
        // reads the disposal cancellation token lets concurrent disposers all pass, because that token is cancelled
        // after the guard, and each of them then stops and disposes the same application. Racing several disposers
        // is the only way to exercise the claim; a sequential second call is filtered by either implementation.
        var hostStopCount = new StrongBox<int>();
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>();
        CountHostStops(builder, hostStopCount);

        await builder.BuildAsync().DefaultTimeout();

        // The window between reading the guard and cancelling the disposal token is only a few instructions wide,
        // so the disposers run on dedicated threads released by a barrier. Thread pool continuations arrive too far
        // apart to land in that window reliably.
        const int DisposerCount = 8;
        var disposals = new Task[DisposerCount];
        var threads = new Thread[DisposerCount];
        using var start = new Barrier(DisposerCount);
        for (var i = 0; i < DisposerCount; i++)
        {
            var index = i;
            threads[index] = new Thread(() =>
            {
                start.SignalAndWait();

                // DisposeAsync runs synchronously on this thread up to its first suspension point, which is past
                // the guard, so the returned task is only awaited once every disposer has been through it.
                disposals[index] = builder.DisposeAsync().AsTask();
            })
            {
                IsBackground = true,
                Name = $"Disposer-{index}"
            };
            threads[index].Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        }

        // A disposer that ran teardown against an application another disposer had already disposed surfaces its
        // exception here. Disposal waits for the released AppHost entry point to exit under the host's shutdown
        // timeout, so this needs a budget larger than DefaultTimeout's 5s.
        await Task.WhenAll(disposals).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(1, hostStopCount.Value);
    }

    private static void CountHostStops(IDistributedApplicationTestingBuilder builder, StrongBox<int> stopCount)
    {
        // The testing factory registers the IHost that HostApplicationBuilder.Build() resolves and that
        // DistributedApplication.StopAsync() delegates to. Wrapping the last registration counts every teardown pass
        // through DistributedApplicationFactory.DisposeAsync, including passes an unclaimed guard would allow.
        var hostDescriptor = builder.Services.Last(
            descriptor => descriptor.ServiceType == typeof(IHost) && descriptor.ServiceKey is null);
        builder.Services.Remove(hostDescriptor);
        builder.Services.AddSingleton<IHost>(
            sp => new StopCountingHost((IHost)hostDescriptor.ImplementationFactory!(sp), stopCount));
    }

    private static DistributedApplicationTestingBuilderOptions CreateDashboardOptions()
    {
        return new()
        {
            EnableDashboard = true
        };
    }

    private static void AssertDashboardTestingDefaults(IDistributedApplicationTestingBuilder builder)
    {
        Assert.Equal("true", builder.Configuration["DcpPublisher:RandomizePorts"]);

        // Blank is how the product spells "assign me a free port", so these must stay empty rather than
        // carrying an explicit :0, which would be a literal fixed port.
        Assert.Equal(string.Empty, builder.Configuration[AspNetCoreUrls]);
        Assert.Equal(string.Empty, builder.Configuration[DashboardOtlpGrpcEndpointUrl]);
        Assert.Equal(string.Empty, builder.Configuration[DashboardOtlpHttpEndpointUrl]);

        Assert.Equal("http://127.0.0.1:0", builder.Configuration[ResourceServiceEndpointUrl]);
        Assert.Equal("false", builder.Configuration[DashboardUnsecuredAllowAnonymous]);
        Assert.Equal("false", builder.Configuration[InteractivityEnabled]);

        // The token has to survive all the way into AppHost:BrowserToken, which is the key the dashboard
        // actually validates against and the one GetDashboardLoginUrlAsync hands back.
        var browserToken = builder.Configuration[DashboardFrontendBrowserToken];
        Assert.NotEmpty(browserToken!);
        Assert.Equal(browserToken, builder.Configuration[AppHostBrowserToken]);
    }

    private static async Task<IDistributedApplicationTestingBuilder> CreateDashboardBuilderAsync(
        CreationSurface creationSurface,
        string[] args)
    {
        var options = CreateDashboardOptions();

        return creationSurface switch
        {
            CreationSurface.Generic => await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(options, args),
            CreationSurface.Type => await DistributedApplicationTestingBuilder.CreateAsync(typeof(Projects.TestingAppHost1_AppHost), options, args),
            CreationSurface.AdHoc => DistributedApplicationTestingBuilder.Create(options, args),
            _ => throw new ArgumentOutOfRangeException(nameof(creationSurface))
        };
    }

    private static async Task CreatePublishBuilderAsync(CreationSurface creationSurface, string[] args)
    {
        var options = CreateDashboardOptions();

        var builder = creationSurface switch
        {
            CreationSurface.Generic => await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(options, args),
            CreationSurface.Type => await DistributedApplicationTestingBuilder.CreateAsync(typeof(Projects.TestingAppHost1_AppHost), options, args),
            CreationSurface.AdHoc => DistributedApplicationTestingBuilder.Create(options, args),
            _ => throw new ArgumentOutOfRangeException(nameof(creationSurface))
        };

        await builder.DisposeAsync();
    }

    public enum CreationSurface
    {
        Generic,
        Type,
        AdHoc
    }

    private sealed class StopCountingHost(IHost innerHost, StrongBox<int> stopCount) : IHost, IAsyncDisposable
    {
        public IServiceProvider Services => innerHost.Services;

        public void Dispose() => innerHost.Dispose();

        public async ValueTask DisposeAsync()
        {
            if (innerHost is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
                return;
            }

            innerHost.Dispose();
        }

        public Task StartAsync(CancellationToken cancellationToken = default) => innerHost.StartAsync(cancellationToken);

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref stopCount.Value);
            return innerHost.StopAsync(cancellationToken);
        }
    }
}
