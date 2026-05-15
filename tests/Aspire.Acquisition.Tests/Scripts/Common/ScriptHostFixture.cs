// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Aspire.Templates.Tests;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// xUnit class fixture that hosts the CLI acquisition scripts over HTTP on a random localhost port.
/// This enables testing the documented <c>curl | bash -s</c> and <c>irm | iex</c> piped install
/// patterns against a real HTTP server, closely matching production behavior.
/// </summary>
public sealed class ScriptHostFixture : IAsyncLifetime
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _serverTask;
    private string? _scriptsDirectory;

    /// <summary>
    /// Gets the TCP port the HTTP server is listening on.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Gets the base URL for the HTTP server (e.g., <c>http://localhost:12345</c>).
    /// </summary>
    public string BaseUrl => $"http://localhost:{Port}";

    public async ValueTask InitializeAsync()
    {
        // Resolve the scripts directory from the repo root
        var repoRoot = TestUtils.RepoRoot;
        _scriptsDirectory = Path.Combine(repoRoot, "eng", "scripts");

        if (!Directory.Exists(_scriptsDirectory))
        {
            throw new DirectoryNotFoundException($"Scripts directory not found: {_scriptsDirectory}");
        }

        // Retry binding to avoid TOCTOU port races: another process can claim the
        // probed port between TcpListener.Stop() and HttpListener.Start().
        const int maxRetries = 5;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            // Find a free port by binding to port 0
            using (var portFinder = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
            {
                portFinder.Start();
                Port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
                portFinder.Stop();
            }

            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");

            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < maxRetries - 1)
            {
                _listener.Close();
                _cts.Dispose();
                _listener = null;
                _cts = null;
            }
        }

        _serverTask = Task.Run(() => ServeAsync(_cts!.Token));

        // Verify the server is reachable
        using var client = new HttpClient();
        using var response = await client.GetAsync($"{BaseUrl}/get-aspire-cli.sh");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Script host failed to start: HTTP {response.StatusCode}");
        }

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();

        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        catch (HttpListenerException)
        {
            // The listener may already be torn down while another process has reused the probed port.
        }

        if (_serverTask is not null)
        {
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                // Server didn't stop in time — ignore
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        try
        {
            _listener?.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        catch (HttpListenerException)
        {
            // Closing only releases cleanup state; the server loop has already been canceled.
        }

        _cts?.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener?.IsListening == true)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            try
            {
                await HandleRequestAsync(ctx);
            }
            catch
            {
                // Don't let a single request crash the server
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        var requestPath = ctx.Request.Url?.AbsolutePath.TrimStart('/');

        if (string.IsNullOrEmpty(requestPath) || _scriptsDirectory is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        // Prevent directory traversal
        var safeName = Path.GetFileName(requestPath);
        var filePath = Path.Combine(_scriptsDirectory, safeName);

        if (!File.Exists(filePath))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var content = await File.ReadAllBytesAsync(filePath);

        // Serve as text/plain — this matches what raw.githubusercontent.com does
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength64 = content.Length;
        ctx.Response.StatusCode = 200;
        await ctx.Response.OutputStream.WriteAsync(content);
        ctx.Response.Close();
    }
}
