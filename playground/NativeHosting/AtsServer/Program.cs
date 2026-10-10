// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Diagnostics;
using NativeHosting;

var authToken = Environment.GetEnvironmentVariable("ASPIRE_REMOTE_APPHOST_TOKEN")
    ?? throw new InvalidOperationException("ASPIRE_REMOTE_APPHOST_TOKEN is required.");
await using var registry = new AtsRegistry();
if (args is ["--stdio"])
{
    var session = new AtsSession(registry, authToken);
    using var peer = new RpcPeer(Console.OpenStandardInput(), Console.OpenStandardOutput(), session.InvokeAsync);
    session.Peer = peer;
    await peer.RunAsync();
    registry.UnregisterHost(peer);
    return;
}

if (OperatingSystem.IsWindows())
{
    throw new PlatformNotSupportedException("This exploration currently validates Unix AppHost sockets only.");
}

var path = Environment.GetEnvironmentVariable("REMOTE_APP_HOST_SOCKET_PATH")
    ?? throw new InvalidOperationException("REMOTE_APP_HOST_SOCKET_PATH is required.");
if (!Path.IsPathFullyQualified(path) || !Directory.Exists(Path.GetDirectoryName(path)) || File.Exists(path))
{
    throw new ArgumentException("Use a fresh socket inside an existing private directory.");
}

var directoryPermissions = File.GetUnixFileMode(Path.GetDirectoryName(path)!);
if ((directoryPermissions & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
{
    throw new UnauthorizedAccessException("The native AppHost socket directory must be private to its owner.");
}

using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
listener.Bind(new UnixDomainSocketEndPoint(path));
File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
listener.Listen(8);
using var shutdown = new CancellationTokenSource();
using var connections = new CancellationTokenSource();
registry.StopRequested = shutdown.Cancel;
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
using var signal = System.Runtime.InteropServices.PosixSignalRegistration.Create(
    System.Runtime.InteropServices.PosixSignal.SIGTERM, context => { context.Cancel = true; shutdown.Cancel(); });
var clients = new List<Task>();
using var host = Environment.GetEnvironmentVariable("NATIVE_HOSTING_CLI") == "1"
    ? NativeCliBootstrap.StartIntegrationHost(path)
    : null;
var hostOutput = host is null ? Task.CompletedTask : DrainAsync(host.StandardOutput);
var hostError = host is null ? Task.CompletedTask : DrainAsync(host.StandardError);
var hostLifetime = host is null ? Task.CompletedTask : ObserveHostAsync(host);
try
{
    Console.Error.WriteLine("Native ATS AppHost server listening.");
    while (!shutdown.IsCancellationRequested)
    {
        var socket = await listener.AcceptAsync(shutdown.Token);
        clients.Add(ServeAsync(socket));
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    Console.Error.WriteLine("Native ATS AppHost server stopping.");
}
finally
{
    listener.Close();
    // Controllers must remain connected while graph disposal stops their custom
    // resources. Only close owned sockets after that cleanup finishes.
    await registry.ResetAsync();
    connections.Cancel();
    await Task.WhenAll(clients);
    if (host is not null && !host.HasExited)
    {
        host.Kill(entireProcessTree: true);
    }

    await Task.WhenAll(hostOutput, hostError, hostLifetime);
    File.Delete(path);
}

async Task DrainAsync(StreamReader reader)
{
    while (await reader.ReadLineAsync() is { } line)
    {
        Console.Error.WriteLine($"ATS integration host: {line}");
    }
}

async Task ObserveHostAsync(Process process)
{
    await process.WaitForExitAsync();
    if (!shutdown.IsCancellationRequested)
    {
        Console.Error.WriteLine($"ATS integration host exited unexpectedly with code {process.ExitCode}.");
        Environment.ExitCode = 1;
        shutdown.Cancel();
    }
}

async Task ServeAsync(Socket socket)
{
    using var stream = new NetworkStream(socket, ownsSocket: true);
    var session = new AtsSession(registry, authToken);
    using var peer = new RpcPeer(stream, stream, session.InvokeAsync);
    session.Peer = peer;
    using var registration = connections.Token.Register(socket.Dispose);
    try
    {
        await peer.RunAsync();
    }
    catch (Exception error) when (shutdown.IsCancellationRequested &&
        error is IOException or ObjectDisposedException or SocketException)
    {
        Console.Error.WriteLine("Closed native ATS client during server shutdown.");
    }
    finally
    {
        if (session.IntegrationHost)
        {
            registry.UnregisterHost(peer);
        }

        if (session.Guest || session.IntegrationHost)
        {
            // First reload policy is an isolated graph replacement, not resource
            // reconciliation. Loss of a controller cannot leave a healthy facade.
            await registry.ResetAsync();
        }
    }
}
