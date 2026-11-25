// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Plain-HTTP loopback stand-in for the DCP API server used by the doctor connection probe.
/// It answers <c>GET /version</c> successfully and rejects the graceful stop request
/// (<c>PATCH /admin/execution</c>) so the checker falls back to its immediate tree kill.
/// </summary>
internal sealed class FakeDcpApiServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<CancellationToken, Task>? _onStopRequest;
    private readonly Task _run;

    /// <param name="onStopRequest">Awaited before the stop request is answered.</param>
    public FakeDcpApiServer(Func<CancellationToken, Task>? onStopRequest = null)
    {
        _onStopRequest = onStopRequest;
        _listener.Start();
        Server = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _run = RunAsync();
    }

    public Uri Server { get; }

    public string Kubeconfig => $"""
        apiVersion: v1
        kind: Config
        clusters:
        - name: dcp
          cluster:
            server: "{Server}"
        """;

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

                // Requests arrive as, for example:
                //   PATCH /admin/execution HTTP/1.1
                //   Content-Type: application/merge-patch+json
                //   Content-Length: 55
                //   <blank line>
                //   {"status":"Stopping","shutdownResourceCleanup":"None"}
                var requestLine = await reader.ReadLineAsync(_shutdown.Token) ?? string.Empty;
                var contentLength = 0;
                while (await reader.ReadLineAsync(_shutdown.Token) is { Length: > 0 } header)
                {
                    const string contentLengthHeader = "Content-Length:";
                    if (header.StartsWith(contentLengthHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(header[contentLengthHeader.Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                // Drain the body so closing the socket does not reset the connection before the
                // client reads the response.
                if (contentLength > 0)
                {
                    await reader.ReadBlockAsync(new char[contentLength], _shutdown.Token);
                }

                var parts = requestLine.Split(' ');
                var response = parts switch
                {
                    ["GET", "/version", ..] => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}",
                    ["PATCH", "/admin/execution", ..] => await HandleStopRequestAsync(),
                    _ => "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                };

                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task<string> HandleStopRequestAsync()
    {
        if (_onStopRequest is not null)
        {
            await _onStopRequest(_shutdown.Token);
        }

        return "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
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
