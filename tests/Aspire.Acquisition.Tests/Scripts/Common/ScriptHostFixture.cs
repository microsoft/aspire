// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
    private LoopbackHttpFileServer? _server;

    /// <summary>
    /// Gets the base URL for the HTTP server (e.g., <c>http://localhost:12345</c>).
    /// </summary>
    public string BaseUrl => _server?.BaseUrl ?? throw new InvalidOperationException("Server not started.");

    public async ValueTask InitializeAsync()
    {
        // Resolve the scripts directory from the repo root.
        var repoRoot = TestUtils.FindRepoRoot()?.FullName
            ?? throw new InvalidOperationException("Could not find repository root");
        var scriptsDirectory = Path.Combine(repoRoot, "eng", "scripts");

        if (!Directory.Exists(scriptsDirectory))
        {
            throw new DirectoryNotFoundException($"Scripts directory not found: {scriptsDirectory}");
        }

        // Snapshot the script files at startup. Files are re-read on each
        // request by the loopback server, so edits during a test still take
        // effect; only file *additions* would require a fixture restart, and
        // these tests don't add scripts at runtime.
        var fileMap = Directory.EnumerateFiles(scriptsDirectory)
            .ToDictionary(p => Path.GetFileName(p)!, p => p, StringComparer.OrdinalIgnoreCase);

        // The piped-install tests embed BaseUrl into shell snippets executed by
        // bash/pwsh; keep the URL host as "localhost" to preserve existing
        // call-site URLs verbatim. Content type matches raw.githubusercontent.com
        // (text/plain) so curl/irm don't apply any transformation a real install
        // wouldn't see.
        _server = LoopbackHttpFileServer.Start(
            fileMap,
            contentType: "text/plain; charset=utf-8",
            host: "localhost");

        // Verify the server is reachable on the documented entry-point script
        // before any test runs; surfaces port-bind races and the rare case
        // where the scripts directory is unreadable.
        using var client = new HttpClient();
        using var response = await client.GetAsync($"{BaseUrl}/get-aspire-cli.sh");
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Script host failed to start: HTTP {response.StatusCode}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _server?.Dispose();
        _server = null;
        return ValueTask.CompletedTask;
    }
}
