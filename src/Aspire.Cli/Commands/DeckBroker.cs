// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Describes a running Aspire Deck instance, read from its discovery file. Aspire
/// Deck is a persistent app that multiple AppHosts attach to; this is how the CLI
/// finds an already-running Deck so additional <c>aspire run --deck</c> invocations
/// register with it instead of launching a new one.
/// </summary>
internal sealed record DeckInstanceInfo(string ControlUrl, string OtlpGrpcUrl, string OtlpHttpUrl, string Token, int Pid);

/// <summary>
/// Discovers a running Aspire Deck (via its instance file) and attaches/detaches
/// AppHosts over its loopback control endpoint. The CLI uses this so multiple runs
/// share a single persistent Deck and can be switched between in the UI.
/// </summary>
internal sealed class DeckBroker(ILogger<DeckBroker> logger)
{
    // AOT-safe JSON: read with JsonDocument, write with Utf8JsonWriter. No reflection.
    private readonly ILogger<DeckBroker> _logger = logger;

    /// <summary>
    /// <c>&lt;user-profile&gt;/.aspire/deck/instance.json</c>. Both Deck (writer) and the CLI
    /// (reader) compute this path the same way.
    /// </summary>
    public static string InstanceFilePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".aspire", "deck", "instance.json");
    }

    /// <summary>
    /// Returns the running Deck instance if one is discoverable and its process is
    /// still alive, otherwise <see langword="null"/>.
    /// </summary>
    public DeckInstanceInfo? FindRunningInstance()
    {
        var path = InstanceFilePath();
        if (!File.Exists(path))
        {
            return null;
        }

        DeckInstanceInfo info;
        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            info = new DeckInstanceInfo(
                ControlUrl: root.GetProperty("controlUrl").GetString() ?? "",
                OtlpGrpcUrl: root.GetProperty("otlpGrpcUrl").GetString() ?? "",
                OtlpHttpUrl: root.GetProperty("otlpHttpUrl").GetString() ?? "",
                Token: root.GetProperty("token").GetString() ?? "",
                Pid: root.GetProperty("pid").GetInt32());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Aspire Deck instance file at {Path}", path);
            return null;
        }

        if (string.IsNullOrEmpty(info.ControlUrl))
        {
            return null;
        }

        // Verify the process is still alive; a stale file is left behind if Deck was
        // killed without cleanup.
        try
        {
            using var process = Process.GetProcessById(info.Pid);
            if (process.HasExited)
            {
                return null;
            }
        }
        catch (ArgumentException)
        {
            // No process with that id.
            return null;
        }

        return info;
    }

    /// <summary>
    /// Polls for a Deck instance to appear and respond to a health check, used after
    /// launching Deck. Returns the instance, or <see langword="null"/> on timeout.
    /// </summary>
    public async Task<DeckInstanceInfo?> WaitForInstanceAsync(int expectedPid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var info = FindRunningInstance();
            if (info is not null && info.Pid == expectedPid && await PingAsync(client, info, cancellationToken).ConfigureAwait(false))
            {
                return info;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    private static async Task<bool> PingAsync(HttpClient client, DeckInstanceInfo info, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync($"{info.ControlUrl}/health", cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Attaches an AppHost to the running Deck so it appears in the switcher.
    /// </summary>
    public async Task<bool> RegisterAsync(DeckInstanceInfo info, string id, string name, string resourceServiceUrl, CancellationToken cancellationToken)
    {
        var body = BuildJson(writer =>
        {
            writer.WriteString("id", id);
            writer.WriteString("name", name);
            writer.WriteString("resourceServiceUrl", resourceServiceUrl);
        });
        return await PostAsync(info, "register", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detaches an AppHost from the running Deck (e.g. when the run ends).
    /// </summary>
    public async Task<bool> UnregisterAsync(DeckInstanceInfo info, string id, CancellationToken cancellationToken)
    {
        var body = BuildJson(writer => writer.WriteString("id", id));
        return await PostAsync(info, "unregister", body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings the running Deck window to the front. Used by <c>aspire deck</c> when a hub is already
    /// running so it focuses the existing window instead of opening a second.
    /// </summary>
    public async Task<bool> FocusAsync(DeckInstanceInfo info, CancellationToken cancellationToken)
    {
        return await PostAsync(info, "focus", "{}", cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> PostAsync(DeckInstanceInfo info, string path, string jsonBody, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{info.ControlUrl}/{path}")
            {
                Content = content,
            };
            request.Headers.Add("x-deck-token", info.Token);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Aspire Deck control '{Path}' returned {StatusCode}", path, (int)response.StatusCode);
            }
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Aspire Deck control '{Path}' request failed", path);
            return false;
        }
    }

    private static string BuildJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
