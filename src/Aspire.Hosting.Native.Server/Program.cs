// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Aspire.Hosting.Native.Rpc;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Dashboard;

var token = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_RPC_TOKEN")
    ?? Environment.GetEnvironmentVariable("ASPIRE_REMOTE_APPHOST_TOKEN")
    ?? throw new InvalidOperationException("Set ASPIRE_NATIVE_RPC_TOKEN to authenticate native server connections.");
using var diagnostics = NativeDiagnostics.Events.Subscribe(new NativeDiagnosticWriter(
    Environment.GetEnvironmentVariable("ASPIRE_NATIVE_TRACE_OPERATIONS") == "1"));
EndPoint endPoint = args switch
{
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
var dcp = new DcpApiClient();
await using var dcpLifetime = dcp.ConfigureAwait(false);
var dcpExecutable = Environment.GetEnvironmentVariable("ASPIRE_NATIVE_DCP_PATH");
var server = new NativeApplicationServer();
using var startup = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
startup.CancelAfter(TimeSpan.FromSeconds(45));
var dcpReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
if (dcpExecutable is not null)
{
    // Publish the RPC socket independently of DCP startup. The CLI must be able
    // to authenticate and fetch its SDK while the controller host is warming up;
    // workload launches still await successful DCP readiness.
    server = new NativeApplicationServer(new DcpWorkloadExecutor(dcp, dcpReady.Task));
}
using var listener = new NativeRpcListener(endPoint, token, Console.Error.WriteLine, server,
    new NativeRpcControl(server, shutdown.Cancel));
NativeDashboardHost? dashboard = null;
try
{
    if (Environment.GetEnvironmentVariable("ASPIRE_NATIVE_DASHBOARD_PORT") is { } dashboardPort)
    {
        if (!int.TryParse(dashboardPort, out var number) || number is < 0 or > 65535)
        {
            throw new ArgumentException("ASPIRE_NATIVE_DASHBOARD_PORT must be a local port or zero.");
        }
        dashboard = await NativeDashboardHost.StartAsync(server,
            Environment.GetEnvironmentVariable("ASPIRE_NATIVE_APPLICATION_NAME") ?? "Native AppHost",
            Environment.GetEnvironmentVariable("ASPIRE_NATIVE_DASHBOARD_API_KEY")
                ?? throw new InvalidOperationException("Set ASPIRE_NATIVE_DASHBOARD_API_KEY for the Dashboard adapter."),
            number, shutdown.Token).ConfigureAwait(false);
        Console.WriteLine($"Native Dashboard resource service listening on {dashboard.Address}.");
    }
    Console.WriteLine($"Native AppHost server listening on {listener.EndPoint}.");
    var listening = listener.RunAsync(shutdown.Token);
    try
    {
        try
        {
            if (dcpExecutable is not null)
            {
                await dcp.StartAsync(dcpExecutable, startup.Token).ConfigureAwait(false);
            }
            dcpReady.SetResult();
        }
        catch (Exception exception)
        {
            dcpReady.SetException(exception);
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
        await dashboard.DisposeAsync().ConfigureAwait(false);
    }
}
