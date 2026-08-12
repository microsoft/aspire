// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using Azure.Provisioning;
using Azure.Provisioning.Primitives;

namespace Aspire.Hosting.Azure.Provisioning;

/// <summary>
/// Round-trips Azure provisioning infrastructure through the language-neutral provisioning JSON model.
/// </summary>
internal static class AzureInfrastructureJsonBridge
{
    public static void Transform(
        AzureResourceInfrastructure infrastructure,
        Func<AzureInfrastructureCustomizationContext, AzureInfrastructureCustomizationResult> transform)
    {
        var resourceName = infrastructure.AspireResource.Name;
        string infrastructureJson;

        try
        {
            infrastructureJson = Serialize(infrastructure);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to serialize Azure infrastructure for Aspire resource '{resourceName}'.",
                ex);
        }

        AzureInfrastructureCustomizationResult result;
        try
        {
            result = transform(new()
            {
                ResourceName = resourceName,
                InfrastructureJson = infrastructureJson
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The Azure infrastructure customization callback failed for Aspire resource '{resourceName}'.",
                ex);
        }

        if (result is null || string.IsNullOrWhiteSpace(result.InfrastructureJson))
        {
            throw new InvalidOperationException(
                $"The Azure infrastructure customization callback for Aspire resource '{resourceName}' must return a non-empty infrastructure JSON document.");
        }

        Infrastructure replacement;
        try
        {
            replacement = Deserialize(result.InfrastructureJson);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to deserialize the Azure infrastructure customization result for Aspire resource '{resourceName}'.",
                ex);
        }

        if (!string.Equals(infrastructure.BicepName, replacement.BicepName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Azure infrastructure customization callback for Aspire resource '{resourceName}' changed the Bicep name from '{infrastructure.BicepName}' to '{replacement.BicepName}'. Infrastructure renaming is not supported.");
        }

        try
        {
            ReplaceContents(infrastructure, replacement);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to apply the Azure infrastructure customization result for Aspire resource '{resourceName}'.",
                ex);
        }
    }

    private static string Serialize(AzureResourceInfrastructure infrastructure)
    {
        // Azure.Provisioning applies typed-resource defaults such as generated names and locations
        // during Build. Materialize them before serialization because deserialization returns generic
        // resources that cannot re-run the original resource type's configuration.
        _ = infrastructure.Build(infrastructure.AspireResource.ProvisioningBuildOptions);

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        // The TypeScript provisioning serializer uses this envelope:
        //   { "infras": [ { "fileName": "storage.bicep", ... } ] }
        writer.WriteStartObject();
        writer.WritePropertyName("infras");
        writer.WriteStartArray();
        ((IJsonModel<Infrastructure>)infrastructure).Write(writer, ModelReaderWriterOptions.Json);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static Infrastructure Deserialize(string infrastructureJson)
    {
        using var document = JsonDocument.Parse(infrastructureJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("infras", out var infrastructures) ||
            infrastructures.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Expected a JSON object containing an 'infras' array.");
        }

        if (infrastructures.GetArrayLength() != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one infrastructure entry, but received {infrastructures.GetArrayLength()}.");
        }

        var infrastructure = ModelReaderWriter.Read<Infrastructure>(
            BinaryData.FromString(infrastructures[0].GetRawText()),
            ModelReaderWriterOptions.Json,
            AzureProvisioningContext.Default);

        return infrastructure ?? throw new InvalidOperationException("Azure Provisioning returned no infrastructure.");
    }

    private static void ReplaceContents(AzureResourceInfrastructure target, Infrastructure replacement)
    {
        var originalResources = GetDistinctResources(target);
        var replacementResources = GetDistinctResources(replacement);
        var originalTargetScope = target.TargetScope;

        // Provisionable instances retain their owning infrastructure. Detach the replacement
        // graph before attaching it to the Aspire-owned infrastructure that carries AspireResource.
        foreach (var resource in replacementResources)
        {
            replacement.Remove(resource);
        }

        var removedOriginalResources = new List<Provisionable>(originalResources.Count);
        var addedReplacementResources = new List<Provisionable>(replacementResources.Count);

        try
        {
            foreach (var resource in originalResources)
            {
                target.Remove(resource);
                removedOriginalResources.Add(resource);
            }

            foreach (var resource in replacementResources)
            {
                target.Add(resource);
                addedReplacementResources.Add(resource);
            }

            target.TargetScope = replacement.TargetScope;
        }
        catch (Exception applyException)
        {
            try
            {
                for (var i = addedReplacementResources.Count - 1; i >= 0; i--)
                {
                    target.Remove(addedReplacementResources[i]);
                }

                foreach (var resource in removedOriginalResources)
                {
                    target.Add(resource);
                }

                target.TargetScope = originalTargetScope;
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "Applying the replacement infrastructure failed and the original infrastructure could not be restored.",
                    applyException,
                    rollbackException);
            }

            throw;
        }
    }

    private static List<Provisionable> GetDistinctResources(Infrastructure infrastructure)
    {
        var resources = new List<Provisionable>();
        var seen = new HashSet<Provisionable>(ReferenceEqualityComparer.Instance);

        foreach (var resource in infrastructure.GetProvisionableResources())
        {
            if (seen.Add(resource))
            {
                resources.Add(resource);
            }
        }

        return resources;
    }
}
