// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.Browsers.Resources;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Supports WebSocket attach/adoption by persisting and validating the browser-level CDP endpoint for a shared user-data
// directory.
//
// Why this exists:
// - Chromium's singleton is keyed by the user data root, not by Aspire. If a WebSocket-based option already launched a
//   debug-enabled browser for that root, a later browser-log session can attach to it instead of starting another
//   process.
// - Chromium's DevToolsActivePort file is only a launch-time hand-off file and isn't enough for cross-session adoption.
//   This sidecar records the exact browser identity and endpoint proved during startup.
// - The sidecar is intentionally treated as a hint. Users can close the browser, edit/delete files, or reuse ports, so
//   every read revalidates schema, identity, PID liveness, endpoint reachability, and profile compatibility.
internal sealed class BrowserEndpointDiscovery(ILogger<BrowserLogsSessionManager> logger)
{
    private static readonly TimeSpan s_probeHttpClientTimeout = Timeout.InfiniteTimeSpan;
    // Endpoint adoption is on the command path, so fail quickly when stale metadata points at a dead or reused port.
    // Two seconds is long enough for a local /json/version response under load while keeping browser launch responsive.
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(2);
    private static readonly HttpClient s_probeHttpClient = new()
    {
        // Keep the singleton client free of a global timeout. Each probe applies a linked CTS below so
        // endpoint-probe timeouts remain local while caller cancellation still propagates.
        Timeout = s_probeHttpClientTimeout
    };
    private static readonly BrowserEndpointJsonContext s_jsonContext = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    });

    private readonly ILogger<BrowserLogsSessionManager> _logger = logger;

    // Aspire sidecar file stored at the Chromium user data root next to browser singleton files such as
    // SingletonLock/lockfile and DevToolsActivePort. Keeping it under the user data root makes the adoption state
    // specific to the same browser singleton boundary that Chromium itself uses.
    public static string GetEndpointMetadataFilePath(string userDataDirectory) =>
        Path.Combine(userDataDirectory, "aspire-debug-endpoint.json");

    public async Task<BrowserDebugEndpointMetadata?> TryReadAndValidateAsync(BrowserHostIdentity identity, string? profileDirectoryName, CancellationToken cancellationToken)
    {
        var metadataPath = GetEndpointMetadataFilePath(identity.UserDataRootPath);
        BrowserDebugEndpointMetadata? metadata;

        try
        {
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            // This file is intentionally durable so adoption can survive an AppHost restart, but real browsers can leave
            // it behind when the process is closed externally. Treat unreadable or invalid metadata as stale and delete it
            // so future starts take the normal owned-browser path.
            // Aspire endpoint metadata shape:
            // {
            //   "schemaVersion": 1,
            //   "endpoint": "ws://127.0.0.1:50981/devtools/browser/<id>",
            //   "processId": 12345,
            //   "executablePath": "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            //   "userDataRootPath": "/Users/me/Library/Application Support/Microsoft Edge",
            //   "profileDirectoryName": "Profile 1",
            //   "createdAt": "2026-04-25T19:37:25Z"
            // }
            using var stream = File.OpenRead(metadataPath);
                metadata = await JsonSerializer.DeserializeAsync(stream, s_jsonContext.BrowserDebugEndpointMetadata, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Unable to read tracked browser endpoint metadata '{MetadataPath}'. Treating it as stale.", metadataPath);
            TryDelete(metadataPath);
            return null;
        }

        var metadataExecutablePath = TryNormalizePath(metadata?.ExecutablePath);
        var metadataUserDataRootPath = TryNormalizePath(metadata?.UserDataRootPath);

        // Cheap structural checks come first so clearly stale files are removed before any process or network probe.
        // Executable and user-data paths are normalized before comparison because the sidecar may have been written by a
        // previous AppHost process with different slash/casing/trailing-separator spelling.
        if (metadata is null ||
            metadata.SchemaVersion != BrowserDebugEndpointMetadata.CurrentSchemaVersion ||
            metadata.ProcessId <= 0 ||
            string.IsNullOrWhiteSpace(metadata.Endpoint) ||
            !Uri.TryCreate(metadata.Endpoint, UriKind.Absolute, out var endpoint) ||
            metadataExecutablePath is null ||
            metadataUserDataRootPath is null ||
            !string.Equals(metadataExecutablePath, identity.ExecutablePath, GetPathComparison()) ||
            !string.Equals(metadataUserDataRootPath, identity.UserDataRootPath, GetPathComparison()))
        {
            TryDelete(metadataPath);
            return null;
        }

        if (!IsProcessAlive(metadata.ProcessId))
        {
            _logger.LogDebug("Tracked browser endpoint metadata '{MetadataPath}' points to process {ProcessId}, but that process is not running.", metadataPath, metadata.ProcessId);
            TryDelete(metadataPath);
            return null;
        }

        // Even a live process id is not enough: the browser may be shutting down, the port may now belong to another
        // process, or the endpoint may no longer be accepting CDP traffic. The /json/version probe is the observable
        // proof that the browser-level websocket is usable.
        bool endpointResponded;
        try
        {
            endpointResponded = await ProbeBrowserEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Tracked browser endpoint metadata '{MetadataPath}' points to endpoint '{Endpoint}', but probing /json/version failed.", metadataPath, endpoint);
            endpointResponded = false;
        }

        if (!endpointResponded)
        {
            _logger.LogDebug("Tracked browser endpoint metadata '{MetadataPath}' points to endpoint '{Endpoint}', but it did not respond to /json/version.", metadataPath, endpoint);
            TryDelete(metadataPath);
            return null;
        }

        // At this point the sidecar points at a live Aspire-launched browser for the same user-data root. A profile
        // mismatch is therefore a real conflict, not stale metadata, and should be reported to the caller.
        if (!string.Equals(metadata.ProfileDirectoryName, profileDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                string.Format(
                    CultureInfo.CurrentCulture,
                    BrowserMessageStrings.BrowserLogsTrackedBrowserProfileConflict,
                    identity.UserDataRootPath,
                    metadata.ProfileDirectoryName ?? BrowserMessageStrings.BrowserLogsDefaultProfileName,
                    profileDirectoryName ?? BrowserMessageStrings.BrowserLogsDefaultProfileName));
        }

        return metadata with { Endpoint = endpoint.ToString() };
    }

    public static async Task WriteAsync(BrowserHostIdentity identity, string? profileDirectoryName, Uri endpoint, int processId, CancellationToken cancellationToken)
    {
        var metadataPath = GetEndpointMetadataFilePath(identity.UserDataRootPath);
        var tempPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";
        // The sidecar captures the identity that was used to launch the owned browser, not just the endpoint URL. That
        // lets a future AppHost reject metadata from a different browser executable or user-data root before connecting.
        //
        // Example:
        // {
        //   "schemaVersion": 1,
        //   "endpoint": "ws://127.0.0.1:50981/devtools/browser/<id>",
        //   "processId": 12345,
        //   "executablePath": "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
        //   "userDataRootPath": "/Users/me/Library/Application Support/Microsoft Edge",
        //   "profileDirectoryName": "Profile 1",
        //   "createdAt": "2026-04-25T19:37:25Z"
        // }
        var metadata = new BrowserDebugEndpointMetadata
        {
            SchemaVersion = BrowserDebugEndpointMetadata.CurrentSchemaVersion,
            Endpoint = endpoint.ToString(),
            ProcessId = processId,
            ExecutablePath = identity.ExecutablePath,
            UserDataRootPath = identity.UserDataRootPath,
            ProfileDirectoryName = profileDirectoryName,
            CreatedAt = DateTimeOffset.UtcNow
        };

        try
        {
            // Write through a temp file and then replace so readers never see a partially-written JSON document. A
            // malformed document is handled as stale, but atomic replacement avoids unnecessary delete/restart cycles.
            using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, s_jsonContext.BrowserDebugEndpointMetadata, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, metadataPath, overwrite: true);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    public static void DeleteEndpointMetadata(string userDataDirectory) =>
        TryDelete(GetEndpointMetadataFilePath(userDataDirectory));

    private static async Task<bool> ProbeBrowserEndpointAsync(Uri browserEndpoint, CancellationToken cancellationToken)
    {
        // BrowserHost.DebugEndpoint is a websocket URL. Chromium exposes the matching HTTP endpoint by swapping
        // ws/wss -> http/https and reading /json/version from the same authority.
        var versionEndpoint = new UriBuilder(browserEndpoint)
        {
            Scheme = browserEndpoint.Scheme == Uri.UriSchemeWss ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            Path = "/json/version",
            Query = null
        }.Uri;

        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(s_probeTimeout);

        using var response = await s_probeHttpClient.GetAsync(versionEndpoint, probeCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        // Chromium /json/version shape includes the browser-level websocket URL used for future CDP connections:
        // {
        //   "Browser": "Chrome/...",
        //   "Protocol-Version": "1.3",
        //   "webSocketDebuggerUrl": "ws://127.0.0.1:50981/devtools/browser/<id>"
        // }
        using var stream = await response.Content.ReadAsStreamAsync(probeCts.Token).ConfigureAwait(false);
        var version = await JsonSerializer.DeserializeAsync(stream, s_jsonContext.BrowserJsonVersionResponse, probeCts.Token).ConfigureAwait(false);
        return Uri.TryCreate(version?.WebSocketDebuggerUrl, UriKind.Absolute, out _);
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return NormalizePath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

// On-disk adoption hint for WebSocket-backed hosts. A matching file never proves adoption is safe by itself; it must be
// validated against the requested identity, profile, process, and /json/version endpoint first.
internal sealed record BrowserDebugEndpointMetadata
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; }

    public required string Endpoint { get; init; }

    public required int ProcessId { get; init; }

    public required string ExecutablePath { get; init; }

    public required string UserDataRootPath { get; init; }

    public string? ProfileDirectoryName { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

// Minimal shape of Chromium's /json/version response. The documented browser-target discovery format includes fields
// such as "Browser", "Protocol-Version", and "webSocketDebuggerUrl"; only the browser WebSocket endpoint is required
// here to prove the probed HTTP endpoint is a DevTools endpoint.
// See https://chromedevtools.github.io/devtools-protocol/#how-do-i-access-the-browser-target
// Example: { "webSocketDebuggerUrl": "ws://127.0.0.1:9222/devtools/browser/<id>" }
internal sealed record BrowserJsonVersionResponse
{
    public string? WebSocketDebuggerUrl { get; init; }
}

// Source-generated JSON context for the small metadata file exchanged between WebSocket attach/adoption paths and the
// Chromium /json/version probe response.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserDebugEndpointMetadata))]
[JsonSerializable(typeof(BrowserJsonVersionResponse))]
internal sealed partial class BrowserEndpointJsonContext : JsonSerializerContext;
