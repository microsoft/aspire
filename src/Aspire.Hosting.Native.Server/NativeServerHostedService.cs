// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Dashboard;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Runtime;
using Aspire.Hosting.Native.Auxiliary;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Native.Server;

/// <summary>Runs native transport and controller services under the generic host lifetime.</summary>
internal sealed class NativeServerHostedService(NativeServerConfiguration settings, string[] args,
    ILoggerFactory loggerFactory, IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    private NativeApplicationLifetime? _applicationStop;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_applicationStop is { } lifetime)
            {
                // CLI shutdown can arrive as an OS signal rather than an RPC. Drain the
                // guest's connection-owned lifetime before cancelling its transport.
                await lifetime.RequestStopAsync(settings.Options.CleanupTimeout, settings.Options.TimeProvider,
                    () => loggerFactory.CreateLogger<NativeServerHostedService>().LogWarning(
                        "The application guest did not finish shutdown before the cleanup deadline; stopping the native server."),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await base.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Unix helpers cannot rely on an OS kill-on-parent-exit job. Keep the
        // launcher identity alive through teardown so abrupt CLI exit follows
        // the same bounded controller/workload cleanup as ordinary shutdown.
        var parentMonitor = settings.CliProcess.MonitorParent(() =>
        {
            loggerFactory.CreateLogger<NativeServerHostedService>()
                .LogInformation("The launching CLI has exited; stopping AppHost Server v2.");
            applicationLifetime.StopApplication();
        }, settings.Options.TimeProvider);
        try
        {
            await RunServerAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch
        {
            Environment.ExitCode = 1;
            throw;
        }
        finally
        {
            try
            {
                if (parentMonitor is not null)
                {
                    await parentMonitor.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                applicationLifetime.StopApplication();
            }
        }
    }

    private async Task RunServerAsync(CancellationToken stoppingToken)
    {
        var logger = loggerFactory.CreateLogger("Aspire.Hosting.Native.Server");
        var options = settings.Options;
        var token = settings.RpcToken;
        var writer = new NativeDiagnosticWriter(settings.TraceOperations, loggerFactory.CreateLogger<NativeDiagnosticWriter>());
        await using var diagnostics = new NativeDiagnosticPump(writer.Write, DiagnosticStreamFailed, options).ConfigureAwait(false);
        var endPoint = NativeStartupArguments.Parse(args, settings.SocketPath);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var dcp = new DcpApiClient(options, loggerFactory.CreateLogger<DcpApiClient>());
        await using var dcpLifetime = dcp.ConfigureAwait(false);
        var dcpExecutable = settings.IsPublishing ? null : settings.DcpPath;
        var server = new NativeApplicationServer(UnavailableWorkloadExecutor.Instance, options.Runtime);
        using var startup = new TimeProviderCancellation(options.ControllerStartupTimeout, options.TimeProvider, shutdown.Token);
        var dcpReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (dcpExecutable is not null)
        {
            // Publish the RPC socket independently of DCP startup. The CLI must be able
            // to authenticate and fetch its SDK while the controller host is warming up;
            // workload launches still await successful DCP readiness.
            server = new NativeApplicationServer(new DcpWorkloadExecutor(dcp, dcpReady.Task, options,
                loggerFactory.CreateLogger<DcpWorkloadExecutor>()), options.Runtime);
        }
        using var applicationStopLifetime = server.ApplicationLifetime;
        _applicationStop = applicationStopLifetime;
        using var listener = new NativeRpcListener(endPoint, token, loggerFactory.CreateLogger<NativeRpcListener>(), server,
            NativeLanguageCatalog.LoadEmbedded(settings.GeneratedSdkDirectory), options);
        NativeDashboardHost? dashboard = null;
        Task? dashboardCleanup = null;
        Task? controllerCleanup = null;
        var dashboardPath = settings.IsPublishing ? null : settings.DashboardPath;
        var dashboardRequested = dashboardPath is not null;
        var dashboardState = new DcpDashboard(loggerFactory.CreateLogger<DcpDashboard>());
        var dashboardReady = new TaskCompletionSource<NativeCliDashboardUrls>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dashboardRequested)
        {
            dashboardReady.SetResult(new());
        }
        var logs = new NativeCliLogBuffer(options.RetainedAppHostLogEntries);
        await using var cliLogging = new NativeDiagnosticPump(logs.Append, DiagnosticStreamFailed, options).ConfigureAwait(false);
        INativeBackchannelRpc backchannelRpc = new NativeBackchannelRpc(options,
            loggerFactory.CreateLogger<NativeBackchannelRpc>());
        using var backchannel = settings.BackchannelPath is { } backchannelPath
            ? new NativeCliProtocol(backchannelPath, server, applicationLifetime.StopApplication,
                dashboardReady.Task.WaitAsync, logs, options, backchannelRpc)
            : null;
        var appHostPath = settings.AppHostPath;
        using var auxiliary = new NativeAuxiliaryBackchannel(appHostPath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), server, options,
            exitCode =>
            {
                Environment.ExitCode = exitCode ?? 0;
                applicationLifetime.StopApplication();
            },
            backchannel is not null ? backchannel.WaitForAppHostReadyAsync : WaitForNativeAppHostReadyAsync,
            dashboardReady.Task.WaitAsync, () => dashboardState.ApiToken, logs, settings.CliProcess, backchannelRpc,
            loggerFactory.CreateLogger<NativeAuxiliaryBackchannel>());
        try
        {
            var dashboardPort = settings.IsPublishing ? null : settings.DashboardPort;
            var dashboardApiKey = settings.DashboardCredentials.ResourceServiceApiKey;
            if (dashboardPort is not null || dashboardRequested)
            {
                dashboard = await NativeDashboardHost.StartAsync(server,
                    settings.ApplicationName,
                    dashboardApiKey,
                    dashboardPort ?? 0, options, shutdown.Token).ConfigureAwait(false);
                // Direct-launch clients discover endpoints from these stdout announcements.
                // Component diagnostics go through ILogger and its root-configured stderr provider.
                Console.WriteLine($"Native Dashboard resource service listening on {dashboard.Address}.");
            }
            Console.WriteLine($"Native AppHost server listening on {listener.EndPoint}.");
            var listening = Task.WhenAll(listener.RunAsync(shutdown.Token),
                backchannel?.RunAsync(shutdown.Token) ?? Task.CompletedTask,
                auxiliary.RunAsync(shutdown.Token));
            try
            {
                try
                {
                    if (dcpExecutable is not null)
                    {
                        await dcp.StartAsync(dcpExecutable, startup.Token).ConfigureAwait(false);
                    }
                    if (dashboardPath is not null)
                    {
                        if (dcpExecutable is null || dashboard is null)
                        {
                            throw new InvalidOperationException("Dashboard launch requires DCP and its resource-service adapter.");
                        }
                        await dashboardState.StartAsync(dcp, dashboardPath, dashboard.Address,
                            settings.DashboardCredentials, options, startup.Token).ConfigureAwait(false);
                        dashboardReady.TrySetResult(dashboardState.Urls);
                        dashboardCleanup = StopDashboardOnShutdownAsync();
                    }
                    dcpReady.SetResult();
                    startup.StopTimeout();
                    if (dcpExecutable is not null)
                    {
                        controllerCleanup = StopControllerOnShutdownAsync();
                    }
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    dcpReady.TrySetCanceled(shutdown.Token);
                    dashboardReady.TrySetCanceled(shutdown.Token);
                }
                catch (Exception exception)
                {
                    dcpReady.SetException(exception);
                    dashboardReady.TrySetException(exception);
                    _ = dashboardReady.Task.Exception;
                    // Observe the shared readiness failure even if no integration has
                    // launched a workload yet; the server still surfaces the original.
                    _ = dcpReady.Task.Exception;
                    throw;
                }
                await listening.ConfigureAwait(false);
            }
            finally
            {
                await shutdown.CancelAsync().ConfigureAwait(false);
                await listening.ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (dashboard is not null)
                {
                    dashboard.BeginShutdown();
                    // Watches were quiesced before requesting Dashboard deletion. Remove its DCP
                    // process before disposing the resource-service host so reconnects cannot keep
                    // either side alive through the CLI's graceful shutdown budget.
                    using var cleanup = new CancellationTokenSource(options.CleanupTimeout, options.TimeProvider);
                    if (dashboardCleanup is null)
                    {
                        await dashboardState.StopAsync(dcp, cleanup.Token).ConfigureAwait(false);
                    }
                }
                await Task.WhenAll(controllerCleanup ?? Task.CompletedTask,
                    dashboardCleanup ?? Task.CompletedTask).ConfigureAwait(false);
            }
            finally
            {
                if (dashboard is not null)
                {
                    await dashboard.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        async Task WaitForNativeAppHostReadyAsync(CancellationToken cancellationToken)
        {
            await dcpReady.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (server.GetApplicationObserver() is null)
            {
                await Task.Delay(options.RetryInterval, options.TimeProvider, cancellationToken).ConfigureAwait(false);
            }
        }

        async Task StopDashboardOnShutdownAsync()
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, options.TimeProvider, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            // Dashboard and application workloads are independent DCP resources. Start
            // their cleanup together rather than accumulating graceful-stop budgets.
            dashboard?.BeginShutdown();
            using var cleanup = new CancellationTokenSource(options.CleanupTimeout, options.TimeProvider);
            await dashboardState.StopAsync(dcp, cleanup.Token).ConfigureAwait(false);
        }

        async Task StopControllerOnShutdownAsync()
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, options.TimeProvider, shutdown.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }
            // DCP owns dependency-aware process and container termination. Initiate it with
            // owner retirement, rather than waiting for every individual API deletion before
            // beginning controller cleanup within the CLI's shared graceful-stop window.
            dashboard?.BeginShutdown();
            using var cleanup = new CancellationTokenSource(options.CleanupTimeout, options.TimeProvider);
            await dcp.CleanupResourcesAsync(cleanup.Token).ConfigureAwait(false);
        }

        void DiagnosticStreamFailed(Exception error) =>
            logger.LogError("Native diagnostic stream failed: {ErrorType}.", error.GetType().Name);
    }
}
