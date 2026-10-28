// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Aspire.TypeSystem;

namespace Aspire.Hosting.Native.Tooling;

internal sealed record NativeProviderExport(AtsCapabilityInfo Capability, string? LifecyclePhase);

/// <summary>Reads portable provider metadata into ATS inputs for offline tooling.</summary>
internal static class NativeProviderContractReader
{
    private static readonly JsonSerializerOptions s_options = CreateOptions();

    public static IReadOnlyList<NativeProviderExport> Read(string json)
    {
        // Providers use {"protocolVersion":2,"capabilities":[{"id":"package/greet",
        // "method":"greet","parameters":[],"returnType":{"typeId":"void","category":"Primitive"}}]}.
        // Only the capability identity names differ from the ATS serialization schema.
        // Authoring-only SDK packages use an empty capabilities array; runtime
        // provider registration validates its nonempty contract separately.
        try
        {
            using var document = JsonDocument.Parse(json);
            var envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object ||
                !envelope.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var number) || number != 2 ||
                !envelope.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array ||
                capabilities.GetArrayLength() > 128)
            {
                throw new InvalidDataException("Provider metadata requires protocol version 2 and bounded capabilities.");
            }
            var exports = new List<NativeProviderExport>();
            foreach (var metadata in capabilities.EnumerateArray())
            {
                var capability = metadata.Deserialize<AtsCapabilityInfo>(s_options)
                    ?? throw new InvalidDataException("Invalid portable ATS capability.");
                if (string.IsNullOrWhiteSpace(capability.CapabilityId) || string.IsNullOrWhiteSpace(capability.MethodName) ||
                    capability.Parameters is null || capability.ReturnType is null || string.IsNullOrWhiteSpace(capability.ReturnType.TypeId))
                {
                    throw new InvalidDataException("Provider capabilities require an identity, method, parameters and return type.");
                }
                foreach (var parameter in capability.Parameters)
                {
                    if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name) || parameter.Type is null ||
                        string.IsNullOrWhiteSpace(parameter.Type.TypeId))
                    {
                        throw new InvalidDataException("Provider parameters require names and types.");
                    }
                    if (parameter.IsCallback && (parameter.CallbackParameters is null || parameter.CallbackReturnType is null ||
                        string.IsNullOrWhiteSpace(parameter.CallbackReturnType.TypeId) ||
                        parameter.CallbackParameters.Any(value => value is null || value.Type is null || string.IsNullOrWhiteSpace(value.Type.TypeId))))
                    {
                        throw new InvalidDataException("Provider callbacks require a parameter list and return type.");
                    }
                }
                string? phase = null;
                if (metadata.TryGetProperty("lifecyclePhase", out var lifecycle) && lifecycle.ValueKind != JsonValueKind.Null)
                {
                    if (lifecycle.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidDataException("Provider lifecycle phases must be strings.");
                    }
                    phase = lifecycle.GetString();
                }
                exports.Add(new(capability, phase));
            }

            return exports;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid portable provider metadata.", exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type == typeof(AtsCapabilityInfo))
            {
                foreach (var property in info.Properties)
                {
                    property.Name = property.Name switch
                    {
                        nameof(AtsCapabilityInfo.CapabilityId) => "id",
                        nameof(AtsCapabilityInfo.MethodName) => "method",
                        _ => property.Name
                    };
                }
            }
        });

        return new()
        {
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = resolver,
            Converters = { new JsonStringEnumConverter() }
        };
    }
}
