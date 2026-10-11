// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Dashboard;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Runtime;

var options = NativeServerOptions.Load(Environment.GetEnvironmentVariable("ASPIRE_NATIVE_SERVER_OPTIONS_PATH"));
var token = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_RPC_TOKEN")
    ?? Environment.GetEnvironmentVariable("ASPIRE_REMOTE_APPHOST_TOKEN")
    ?? throw new InvalidOperationException("Set ASPIRE_NATIVE_RPC_TOKEN to authenticate native server connections.");
using var diagnostics = NativeDiagnostics.Events.Subscribe(new NativeDiagnosticWriter(
    Environment.GetEnvironmentVariable("ASPIRE_NATIVE_TRACE_OPERATIONS") == "1"));
EndPoint endPoint = args switch
{
    ["server", "--contentRoot", var contentRoot] when Directory.Exists(contentRoot) &&
        Environment.GetEnvironmentVariable("REMOTE_APP_HOST_SOCKET_PATH") is { } path &&
        Path.IsPathFullyQualified(path) => new UnixDomainSocketEndPoint(path),
    [] when Environment.GetEnvironmentVariable("REMOTE_APP_HOST_SOCKET_PATH") is { } socketPath &&
        Path.IsPathFullyQualified(socketPath) => new UnixDomainSocketEndPoint(socketPath),
    ["--socket", var path] when Path.IsPathFullyQualified(path) => new UnixDomainSocketEndPoint(path),
    ["--port", var port] when int.TryParse(port, out var number) && number is >= 0 and <= 65535 =>
        new IPEndPoint(IPAddress.Loopback, number),
    _ => throw new ArgumentException("Use --socket <absolute-path> or --port <loopback-port>.")
};
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    shutdown.Cancel();
});
var dcp = new DcpApiClient(options);
await using var dcpLifetime = dcp.ConfigureAwait(false);
var dcpExecutable = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_DCP_PATH");
var server = new NativeApplicationServer(UnavailableWorkloadExecutor.Instance, options.Runtime);
using var startup = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
startup.CancelAfter(options.ControllerStartupTimeout);
var dcpReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
if (dcpExecutable is not null)
{
    // Publish the RPC socket independently of DCP startup. The CLI must be able
    // to authenticate and fetch its SDK while the controller host is warming up;
    // workload launches still await successful DCP readiness.
    server = new NativeApplicationServer(new DcpWorkloadExecutor(dcp, dcpReady.Task, options), options.Runtime);
}
using var listener = new NativeRpcListener(endPoint, token, Console.Error.WriteLine, server,
    NativeLanguageCatalog.LoadEmbedded(), options);
NativeDashboardHost? dashboard = null;
Task? dashboardCleanup = null;
var dashboardPath = Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_PATH");
var dashboardRequested = dashboardPath is not null;
var dashboardState = new DcpDashboard();
var dashboardReady = new TaskCompletionSource<NativeCliDashboardUrls>(TaskCreationOptions.RunContinuationsAsynchronously);
if (!dashboardRequested)
{
    dashboardReady.SetResult(new());
}
var logs = new NativeCliLogBuffer(options.RetainedAppHostLogEntries);
using var cliLogging = NativeDiagnostics.Events.Subscribe(logs);
using var backchannel = Environment.GetEnvironmentVariable("ASPIRE_BACKCHANNEL_PATH") is { } backchannelPath
    ? new NativeCliProtocol(backchannelPath, server, shutdown.Cancel,
        dashboardReady.Task.WaitAsync, logs, options)
    : null;
try
{
    var dashboardPort = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_DASHBOARD_PORT");
    var dashboardApiKey = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_DASHBOARD_API_KEY")
        ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    if (dashboardPort is not null || dashboardRequested)
    {
        var number = 0;
        if (dashboardPort is not null && (!int.TryParse(dashboardPort, out number) || number is < 0 or > 65535))
        {
            throw new ArgumentException("ASPIRE_NATIVE_DASHBOARD_PORT must be a local port or zero.");
        }
        dashboard = await NativeDashboardHost.StartAsync(server,
            Environment.GetEnvironmentVariable("ASPIRE_NATIVE_APPLICATION_NAME") ?? "Native AppHost",
            dashboardApiKey,
            number, options, shutdown.Token).ConfigureAwait(false);
        Console.WriteLine($"Native Dashboard resource service listening on {dashboard.Address}.");
    }
    Console.WriteLine($"Native AppHost server listening on {listener.EndPoint}.");
    var listening = Task.WhenAll(listener.RunAsync(shutdown.Token),
        backchannel?.RunAsync(shutdown.Token) ?? Task.CompletedTask);
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
                    dashboardApiKey, options, startup.Token).ConfigureAwait(false);
                dashboardReady.TrySetResult(dashboardState.Urls);
                dashboardCleanup = StopDashboardOnShutdownAsync();
            }
            dcpReady.SetResult();
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
    await startup.CancelAsync().ConfigureAwait(false);
    if (dashboard is not null)
    {
        try
        {
            // The Dashboard owns long-lived gRPC watches. Stop its DCP process
            // before the resource service, so those requests finish rather than
            // consuming the HTTP server's entire graceful shutdown budget.
            using var cleanup = new CancellationTokenSource(options.CleanupTimeout);
            if (dashboardCleanup is null)
            {
                await dashboardState.StopAsync(dcp, cleanup.Token).ConfigureAwait(false);
            }
            else
            {
                await dashboardCleanup.ConfigureAwait(false);
            }
        }
        finally
        {
            await dashboard.DisposeAsync().ConfigureAwait(false);
        }
    }
}

async Task StopDashboardOnShutdownAsync()
{
    try
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, shutdown.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
    }
    // Dashboard and application workloads are independent DCP resources. Start
    // their cleanup together rather than accumulating graceful-stop budgets.
    using var cleanup = new CancellationTokenSource(options.CleanupTimeout);
    await dashboardState.StopAsync(dcp, cleanup.Token).ConfigureAwait(false);
}
