// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Shared;

/// <summary>
/// Handles Windows-only WSLc support and its session-manager metadata protocol.
/// </summary>
internal sealed class WslcContainerRuntimeProbe() : ContainerRuntimeProbe(KnownContainerRuntimes.Wslc, "wslc", "WSLc", 2)
{
    public override bool IsSupportedHost(bool isWindows)
    {
        return isWindows;
    }

    protected override async Task<RuntimeVersionInfo> ReadVersionInfoAsync(bool isRunning, ILogger? logger, CancellationToken cancellationToken)
    {
        if (!isRunning)
        {
            // Doctor does not consume version metadata for a stopped WSLc session.
            return default;
        }

        var result = await ReadVersionOutputAsync("info --format json", async (stream, token) =>
        {
            var info = await JsonSerializer.DeserializeAsync(stream, WslcRuntimeJsonContext.Default.WslcRuntimeInfoJson, token).ConfigureAwait(false);
            return NormalizeInfo(info);
        }, logger, cancellationToken).ConfigureAwait(false);
        return result.ClientVersion is null || result.ServerVersion is null
            ? result with { Error = result.Error ?? "WSLc did not return valid client and session manager versions." }
            : result;
    }

    /// <summary>
    /// Normalizes WSLc info metadata into the common client/server version result.
    /// </summary>
    internal static RuntimeVersionInfo ParseInfoOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return new RuntimeVersionInfo { Error = "WSLc returned empty version metadata." };
        }

        try
        {
            // `wslc info --format json` reports:
            // {"Client":{"Version":"3.0.3.0"},"Server":{"SessionManagerVersion":"3.0.3"}}
            var info = JsonSerializer.Deserialize(output, WslcRuntimeJsonContext.Default.WslcRuntimeInfoJson);
            return NormalizeInfo(info);
        }
        catch (JsonException ex)
        {
            return new RuntimeVersionInfo { Error = $"WSLc returned invalid version metadata: {ex.Message}" };
        }
    }

    private static RuntimeVersionInfo NormalizeInfo(WslcRuntimeInfoJson? info)
    {
        var clientVersion = ParseRuntimeVersion(info?.Client?.Version, out var clientPrerelease);
        var serverVersion = ParseRuntimeVersion(info?.Server?.SessionManagerVersion, out var serverPrerelease);

        return new RuntimeVersionInfo(clientVersion, serverVersion, false, null)
        {
            IsClientPrerelease = clientPrerelease,
            IsServerPrerelease = serverPrerelease,
            ClientVersionText = info?.Client?.Version,
            ServerVersionText = info?.Server?.SessionManagerVersion,
            Error = clientVersion is null || serverVersion is null
                ? "WSLc did not return valid client and session manager versions."
                : null
        };
    }
}

internal sealed class WslcRuntimeInfoJson
{
    public WslcClientVersionJson? Client { get; set; }
    public WslcServerVersionJson? Server { get; set; }
}

internal sealed class WslcClientVersionJson
{
    public string? Version { get; set; }
}

internal sealed class WslcServerVersionJson
{
    public string? SessionManagerVersion { get; set; }
}

[JsonSerializable(typeof(WslcRuntimeInfoJson))]
internal sealed partial class WslcRuntimeJsonContext : JsonSerializerContext
{
}
