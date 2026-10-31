// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class GitAuthenticationServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _run;
    private int _requests;

    public GitAuthenticationServer()
    {
        _listener.Start();
        Repository = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/repository";
        _run = RunAsync();
    }

    public string Repository { get; }
    public int Requests => Volatile.Read(ref _requests);

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                // Git sends GET /repository/info/refs?service=git-upload-pack
                // followed by HTTP headers and an empty line. Never record credentials.
                while (await reader.ReadLineAsync(_shutdown.Token) is { Length: > 0 })
                {
                }
                Interlocked.Increment(ref _requests);
                await stream.WriteAsync(
                    "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"cli-test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                    _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        try
        {
            await _run;
        }
        finally
        {
            _listener.Stop();
            _shutdown.Dispose();
        }
    }
}
