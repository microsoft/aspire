// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Bridges owned and adopted hosts by persisting and validating the browser-level CDP endpoint for a shared
// user-data directory. The registry treats this metadata as a hint only; this type proves the endpoint is live before
// an existing browser can be adopted.
internal sealed class BrowserEndpointDiscovery(ILogger<BrowserLogsSessionManager> logger)
{
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(2);
    private readonly ILogger<BrowserLogsSessionManager> _logger = logger;

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

            using var stream = File.OpenRead(metadataPath);
            metadata = await JsonSerializer.DeserializeAsync(stream, BrowserEndpointJsonContext.Default.BrowserDebugEndpointMetadata, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogDebug(ex, "Unable to read tracked browser endpoint metadata '{MetadataPath}'. Treating it as stale.", metadataPath);
            TryDelete(metadataPath);
            return null;
        }

        var metadataExecutablePath = TryNormalizePath(metadata?.ExecutablePath);
        var metadataUserDataRootPath = TryNormalizePath(metadata?.UserDataRootPath);

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

        if (!string.Equals(metadata.ProfileDirectoryName, profileDirectoryName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"A tracked browser is already running for user data directory '{identity.UserDataRootPath}' with profile '{metadata.ProfileDirectoryName ?? "(default)"}'. " +
                $"The requested profile is '{profileDirectoryName ?? "(default)"}'. Close the existing tracked browser session or use isolated user data mode.");
        }

        return metadata with { Endpoint = endpoint.ToString() };
    }

    public static async Task WriteAsync(BrowserHostIdentity identity, string? profileDirectoryName, Uri endpoint, int processId, CancellationToken cancellationToken)
    {
        var metadataPath = GetEndpointMetadataFilePath(identity.UserDataRootPath);
        var tempPath = $"{metadataPath}.{Guid.NewGuid():N}.tmp";
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
            using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, BrowserEndpointJsonContext.Default.BrowserDebugEndpointMetadata, cancellationToken).ConfigureAwait(false);
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

    public static void DeleteDevToolsActivePort(string userDataDirectory) =>
        TryDelete(Path.Combine(userDataDirectory, "DevToolsActivePort"));

    public static bool IsNonDebuggableBrowserRunning(string userDataDirectory)
    {
        var singletonLockPath = Path.Combine(userDataDirectory, "SingletonLock");
        FileInfo singletonLock;
        try
        {
            singletonLock = new FileInfo(singletonLockPath);
            // Broken Unix symlinks can report Exists=false while still exposing the host-pid LinkTarget we need.
            if (!singletonLock.Exists && string.IsNullOrWhiteSpace(singletonLock.LinkTarget))
            {
                return false;
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (TryGetSingletonLockProcessId(singletonLock) is { } pid)
        {
            return IsProcessAlive(pid);
        }

        return OperatingSystem.IsWindows();
    }

    private static async Task<bool> ProbeBrowserEndpointAsync(Uri browserEndpoint, CancellationToken cancellationToken)
    {
        var versionEndpoint = new UriBuilder(browserEndpoint)
        {
            Scheme = browserEndpoint.Scheme == Uri.UriSchemeWss ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            Path = "/json/version",
            Query = null
        }.Uri;

        using var httpClient = new HttpClient { Timeout = s_probeTimeout };
        using var response = await httpClient.GetAsync(versionEndpoint, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return document.RootElement.TryGetProperty("webSocketDebuggerUrl", out var endpointElement) &&
            endpointElement.ValueKind == JsonValueKind.String &&
            Uri.TryCreate(endpointElement.GetString(), UriKind.Absolute, out _);
    }

    private static int? TryGetSingletonLockProcessId(FileInfo singletonLock)
    {
        try
        {
            var linkTarget = singletonLock.LinkTarget;
            if (string.IsNullOrWhiteSpace(linkTarget))
            {
                return null;
            }

            var separatorIndex = linkTarget.LastIndexOf('-');
            if (separatorIndex < 0 || separatorIndex == linkTarget.Length - 1)
            {
                return null;
            }

            return int.TryParse(linkTarget.AsSpan(separatorIndex + 1), out var pid) ? pid : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
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

// On-disk adoption hint written by an owned host. A matching file never proves adoption is safe by itself; it must be
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

// Source-generated JSON context for the small metadata file exchanged between owned and adopted host paths.
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BrowserDebugEndpointMetadata))]
internal sealed partial class BrowserEndpointJsonContext : JsonSerializerContext;
