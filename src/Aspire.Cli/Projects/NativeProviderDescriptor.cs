// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Projects;

/// <summary>Parses package-relative provider assets without resolving files or starting tooling.</summary>
internal sealed record NativeProviderDescriptor(string PackageName, string Metadata,
    IReadOnlyDictionary<string, string> SdkFiles, IReadOnlyList<string> Exports)
{
    public static NativeProviderDescriptor Read(string json)
    {
        // aspire.apphost-server.json contains {"protocolVersion":2,"packageName":"example.provider",
        // "metadata":"contract.json","sdkFiles":{"provider.mts":"src/provider.mts"},"exports":["provider.mts"]}.
        // Asset paths remain package-relative until preparation resolves and checks them.
        try
        {
            if (JsonNode.Parse(json) is not JsonObject descriptor ||
                descriptor["protocolVersion"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != 2 ||
                descriptor["sdkFiles"] is not JsonObject { Count: <= 128 } files ||
                descriptor["exports"] is not JsonArray { Count: <= 128 } exports)
            {
                throw new InvalidDataException("Provider packages require a bounded protocol-v2 descriptor.");
            }
            var sdkFiles = files.ToDictionary(file => file.Key, file => ReadString(file.Value), StringComparer.Ordinal);

            return new(ReadString(descriptor["packageName"]), ReadString(descriptor["metadata"]),
                sdkFiles, exports.Select(ReadString).ToArray());
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The provider package descriptor is malformed.", exception);
        }
    }

    private static string ReadString(JsonNode? value)
    {
        if (value is not JsonValue text || !text.TryGetValue<string>(out var result) || string.IsNullOrWhiteSpace(result))
        {
            throw new InvalidDataException("Provider package identities and asset names must be nonempty strings.");
        }

        return result;
    }
}
