// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Templates.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Acquisition.Tests.Scripts;

/// <summary>
/// xUnit class fixture that hosts the CLI acquisition scripts over HTTP on a random localhost port.
/// This enables testing the documented <c>curl | bash -s</c> and <c>irm | iex</c> piped install
/// patterns against a real HTTP server, closely matching production behavior.
/// </summary>
/// <remarks>
/// The fixture uses Kestrel bound to loopback port 0 so the OS atomically allocates a free port.
/// This avoids the TOCTOU window inherent in probing a free port via <c>TcpListener</c> and then
/// re-binding via a different listener, and avoids the process-global prefix teardown behavior of
/// <c>HttpListener</c> that previously caused intermittent <c>Address already in use</c> failures
/// during fixture disposal under CI parallelism.
/// </remarks>
public sealed class ScriptHostFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _scriptsDirectory;

    /// <summary>
    /// Gets the TCP port the HTTP server is listening on.
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// Gets the base URL for the HTTP server (e.g., <c>http://127.0.0.1:12345</c>).
    /// </summary>
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public async ValueTask InitializeAsync()
    {
        // Resolve the scripts directory from the repo root
        var repoRoot = TestUtils.FindRepoRoot()?.FullName
            ?? throw new InvalidOperationException("Could not find repository root");
        _scriptsDirectory = Path.Combine(repoRoot, "eng", "scripts");

        if (!Directory.Exists(_scriptsDirectory))
        {
            throw new DirectoryNotFoundException($"Scripts directory not found: {_scriptsDirectory}");
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        var app = builder.Build();

        // Bind to loopback port 0 — the OS will allocate a free port atomically when Kestrel starts.
        app.Urls.Clear();
        app.Urls.Add("http://127.0.0.1:0");

        app.MapGet("/{fileName}", (string fileName) =>
        {
            // Prevent directory traversal — only serve files directly inside the scripts directory.
            // The route template `{fileName}` already restricts matches to a single path segment
            // (no embedded `/`); Path.GetFileName + a startsWith check on the resolved absolute
            // path act as defense-in-depth against any decoded traversal sequences.
            var safeName = Path.GetFileName(fileName);
            if (string.IsNullOrEmpty(safeName) || _scriptsDirectory is null)
            {
                return Results.NotFound();
            }

            var scriptsRoot = Path.GetFullPath(_scriptsDirectory);
            var filePath = Path.GetFullPath(Path.Combine(scriptsRoot, safeName));
            if (!filePath.StartsWith(scriptsRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(filePath))
            {
                return Results.NotFound();
            }

            // Serve as text/plain — this matches what raw.githubusercontent.com does
            return Results.File(filePath, contentType: "text/plain; charset=utf-8");
        });

        await app.StartAsync();
        _app = app;

        // Read the actually-bound address from the server features rather than guessing it.
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Server addresses feature is not available.");
        var boundAddress = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("Kestrel did not report any bound addresses.");
        Port = new Uri(boundAddress).Port;

        // Verify the server is reachable
        using var client = new HttpClient();
        using var response = await client.GetAsync($"{BaseUrl}/get-aspire-cli.sh");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Script host failed to start: HTTP {response.StatusCode}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // Shutdown timeout — ignore; DisposeAsync below will release the socket.
            }

            await _app.DisposeAsync();
            _app = null;
        }
    }
}
