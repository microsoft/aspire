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

    public GitAuthenticationServer(bool acceptCredentials = false)
    {
        _listener.Start();
        Repository = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/repository";
        _run = RunAsync(acceptCredentials);
    }

    public string Repository { get; }
    public int Requests => Volatile.Read(ref _requests);

    private async Task RunAsync(bool acceptCredentials)
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
                var authorized = false;
                while (await reader.ReadLineAsync(_shutdown.Token) is { Length: > 0 } header)
                {
                    authorized |= header == "Authorization: Basic cHJvYmU6cHJvYmUtb25seQ==";
                }
                Interlocked.Increment(ref _requests);
                if (acceptCredentials && authorized)
                {
                    // Smart HTTP advertises an empty repository: service header,
                    // packet flush, then an empty ref advertisement. No credential storage.
                    var body = "001e# service=git-upload-pack\n00000000";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/x-git-upload-pack-advertisement\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), _shutdown.Token);
                }
                else
                {
                    await stream.WriteAsync(
                        "HTTP/1.1 401 Unauthorized\r\nWWW-Authenticate: Basic realm=\"cli-test\"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(),
                        _shutdown.Token);
                }
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
