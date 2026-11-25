// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// Plain-HTTP loopback stand-in for the DCP API server used by the doctor connection probe.
/// It answers <c>GET /version</c> successfully. By default it rejects the graceful stop request
/// (<c>PATCH /admin/execution</c>) so the checker falls back to its tree kill. When stop requests are
/// accepted it behaves like DCP: the Content-Type must be exactly the merge-patch media type, otherwise
/// it answers 415.
/// </summary>
internal sealed class FakeDcpApiServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private const string AcceptedStopMediaType = "application/merge-patch+json";

    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<CancellationToken, Task>? _onStopRequest;
    private readonly bool _acceptStopRequests;
    private readonly TaskCompletionSource _stopAccepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _stopRequestContentTypes = new();
    private readonly Task _run;

    /// <param name="onStopRequest">Awaited before the stop request is answered.</param>
    /// <param name="acceptStopRequests">Whether stop requests with the expected media type are accepted.</param>
    public FakeDcpApiServer(Func<CancellationToken, Task>? onStopRequest = null, bool acceptStopRequests = false)
    {
        _onStopRequest = onStopRequest;
        _acceptStopRequests = acceptStopRequests;
        _listener.Start();
        Server = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _run = RunAsync();
    }

    public Uri Server { get; }

    /// <summary>Completes when a stop request is accepted.</summary>
    public Task StopAccepted => _stopAccepted.Task;

    public string[] StopRequestContentTypes => _stopRequestContentTypes.ToArray();

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
                string? contentType = null;
                while (await reader.ReadLineAsync(_shutdown.Token) is { Length: > 0 } header)
                {
                    const string contentLengthHeader = "Content-Length:";
                    const string contentTypeHeader = "Content-Type:";
                    if (header.StartsWith(contentLengthHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(header[contentLengthHeader.Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (header.StartsWith(contentTypeHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = header[contentTypeHeader.Length..].Trim();
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
                    ["PATCH", "/admin/execution", ..] => await HandleStopRequestAsync(contentType),
                    _ => "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                };

                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task<string> HandleStopRequestAsync(string? contentType)
    {
        _stopRequestContentTypes.Enqueue(contentType ?? string.Empty);
        if (_onStopRequest is not null)
        {
            await _onStopRequest(_shutdown.Token);
        }

        if (!_acceptStopRequests)
        {
            return "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        }

        if (!string.Equals(contentType, AcceptedStopMediaType, StringComparison.Ordinal))
        {
            return "HTTP/1.1 415 Unsupported Media Type\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        }

        _stopAccepted.TrySetResult();
        return "HTTP/1.1 202 Accepted\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
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
