// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// Minimal loopback HTTP file server for tests that need a real TCP endpoint —
/// e.g. piping a script into <c>bash</c>/<c>iex</c> via <c>curl</c>/<c>irm</c>,
/// or pointing <c>winget install</c> at an <c>InstallerUrl</c> that WinINet's
/// <c>InternetOpenUrl</c> can fetch (it supports http/https/ftp/gopher only —
/// not <c>file://</c>). The ASP.NET Core <c>TestServer</c>
/// (<c>Microsoft.AspNetCore.TestHost</c>) is in-memory only and would not
/// satisfy either scenario.
/// </summary>
/// <remarks>
/// Loopback HTTP prefixes (<c>http://127.0.0.1:port/</c> and
/// <c>http://localhost:port/</c>) do not require admin / <c>netsh urlacl</c>
/// reservation on Windows — that restriction only applies to non-loopback
/// prefixes. See <c>eng/winget/dogfood.ps1</c>
/// <c>Start-LocalArchiveServer</c> for the same rationale used in dogfood.
/// </remarks>
internal sealed class LoopbackHttpFileServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;

    /// <summary>
    /// Base URL with no trailing slash, e.g. <c>http://127.0.0.1:50123</c>.
    /// Callers append <c>/&lt;filename&gt;</c> to request a mapped file.
    /// </summary>
    public string BaseUrl { get; }

    private LoopbackHttpFileServer(
        HttpListener listener,
        string baseUrl,
        IReadOnlyDictionary<string, string> fileMap,
        string contentType)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ServeAsync(fileMap, contentType, _cts.Token));
    }

    /// <summary>
    /// Starts a loopback HTTP server that serves the values of
    /// <paramref name="fileMap"/> by file name. Lookup is case-insensitive
    /// regardless of the dictionary's own comparer because Windows file paths
    /// are case-insensitive and Linux/macOS scripts requesting these test
    /// URLs do not vary case at the test sites.
    /// </summary>
    /// <param name="fileMap">
    /// Map from request file name (the last path segment) to absolute file
    /// path on disk. Files are read fresh on each request, so callers can
    /// rewrite a backing file between requests in the same test.
    /// </param>
    /// <param name="contentType">
    /// The <c>Content-Type</c> header to send on every 200 response.
    /// </param>
    /// <param name="host">
    /// Host part of the URL prefix. Use <c>127.0.0.1</c> when the consumer
    /// is sensitive to IPv4 vs IPv6 resolution of <c>localhost</c> (e.g.
    /// Windows' WinINet); use <c>localhost</c> when the URL string itself
    /// is asserted by tests.
    /// </param>
    public static LoopbackHttpFileServer Start(
        IReadOnlyDictionary<string, string> fileMap,
        string contentType,
        string host = "127.0.0.1")
    {
        ArgumentNullException.ThrowIfNull(fileMap);
        ArgumentException.ThrowIfNullOrEmpty(contentType);
        ArgumentException.ThrowIfNullOrEmpty(host);

        // Wrap the caller's map in a case-insensitive view so we don't depend
        // on the caller's comparer choice.
        var caseInsensitiveMap = new Dictionary<string, string>(fileMap, StringComparer.OrdinalIgnoreCase);

        // Retry binding to avoid TOCTOU: another process can grab the probed
        // port between TcpListener releasing it and HttpListener.Start binding
        // to it. Both existing call sites in this project hit this and need
        // the retry.
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var port = GetFreeLoopbackPort();
            var prefix = $"http://{host}:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                // BaseUrl without trailing slash; callers append "/<file>".
                var baseUrl = $"http://{host}:{port}";
                return new LoopbackHttpFileServer(listener, baseUrl, caseInsensitiveMap, contentType);
            }
            catch (HttpListenerException) when (attempt < maxAttempts)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException($"Could not bind a loopback HttpListener after {maxAttempts} attempts.");
    }

    private async Task ServeAsync(
        IReadOnlyDictionary<string, string> fileMap,
        string contentType,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                // Strip the leading '/' and any subdirectories; only the file
                // name is significant for lookup. URL-decode so spaces/escapes
                // in archive names round-trip.
                var name = Path.GetFileName(Uri.UnescapeDataString(context.Request.Url!.AbsolutePath));
                if (fileMap.TryGetValue(name, out var filePath) && File.Exists(filePath))
                {
                    var bytes = await File.ReadAllBytesAsync(filePath, ct);
                    context.Response.ContentType = contentType;
                    context.Response.ContentLength64 = bytes.LongLength;
                    await context.Response.OutputStream.WriteAsync(bytes, ct);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
            }
            catch
            {
                // Never let a single bad request kill the listen loop.
                try
                {
                    context.Response.StatusCode = 500;
                }
                catch
                {
                    // Response may already be in an unwritable state.
                }
            }
            finally
            {
                try
                {
                    context.Response.Close();
                }
                catch
                {
                    // Best-effort close; nothing actionable here.
                }
            }
        }
    }

    private static int GetFreeLoopbackPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Already canceled / disposed.
        }
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Listener may already be torn down; the loop's catch blocks handle that.
        }
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Loop exited via exception path or didn't observe cancel in time; nothing to recover.
        }
        try
        {
            _listener.Close();
        }
        catch
        {
            // Already closed.
        }
        _cts.Dispose();
    }
}
