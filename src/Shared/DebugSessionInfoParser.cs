// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Utils;

internal static class DebugSessionInfoParser
{
    public static bool TryGetSupportedLaunchConfigurations(
        string? debugSessionInfoJson,
        out string[]? supportedLaunchConfigurations)
    {
        if (debugSessionInfoJson is null)
        {
            supportedLaunchConfigurations = null;
            return false;
        }

        try
        {
            var debugSessionInfo = JsonSerializer.Deserialize<DebugSessionInfo>(debugSessionInfoJson);
            supportedLaunchConfigurations = debugSessionInfo?.SupportedLaunchConfigurations;
            return debugSessionInfo is not null;
        }
        catch (JsonException)
        {
            supportedLaunchConfigurations = null;
            return false;
        }
    }

    private sealed class DebugSessionInfo
    {
        [JsonPropertyName("protocols_supported")]
        public required string[] ProtocolsSupported { get; set; }

        [JsonPropertyName("supported_launch_configurations")]
        public string[]? SupportedLaunchConfigurations { get; set; }
    }
}
