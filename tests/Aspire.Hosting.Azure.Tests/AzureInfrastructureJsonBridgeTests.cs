// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Utils;
using Azure.Provisioning;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting.Azure.Tests;

public class AzureInfrastructureJsonBridgeTests
{
    [Fact]
    public async Task ConfigureInfrastructureForPolyglotAppliesSerializedMutationsInOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var storage = builder.AddAzureStorage("storage");

        AzureProvisioningResourceExtensions.ConfigureInfrastructureForPolyglot(
            storage,
            context =>
            {
                var document = JsonNode.Parse(context.InfrastructureJson)!.AsObject();
                var storageValue = GetResourceValue(document, "storage");
                storageValue["sku"]!["value"]!["name"]!["value"] = "Standard_LRS";

                return new() { InfrastructureJson = document.ToJsonString() };
            });

        AzureProvisioningResourceExtensions.ConfigureInfrastructureForPolyglot(
            storage,
            context =>
            {
                var document = JsonNode.Parse(context.InfrastructureJson)!.AsObject();
                var storageValue = GetResourceValue(document, "storage");
                Assert.Equal("Standard_LRS", storageValue["sku"]!["value"]!["name"]!["value"]!.GetValue<string>());

                storageValue["properties"]!["value"]!["allowSharedKeyAccess"]!["value"] = true;

                return new() { InfrastructureJson = document.ToJsonString() };
            });

        var storageManifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);

        await Verify(storageManifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task AddAzureInfrastructureForPolyglotPreservesNestedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var infrastructure = AzureProvisioningResourceExtensions.AddAzureInfrastructureForPolyglot(
            builder,
            "infrastructure",
            _ =>
            {
                var replacement = new Infrastructure("infrastructure");
                var storage = new StorageAccount("storage")
                {
                    Kind = StorageKind.StorageV2,
                    Sku = new StorageSku { Name = StorageSkuName.StandardLrs },
                    AllowSharedKeyAccess = true
                };
                var blobs = new BlobService("blobs")
                {
                    Parent = storage
                };

                replacement.Add(storage);
                replacement.Add(blobs);

                return new() { InfrastructureJson = Serialize(replacement) };
            });

        var infrastructureManifest = await AzureManifestUtils.GetManifestWithBicep(infrastructure.Resource);

        await Verify(infrastructureManifest.BicepText, extension: "bicep");
    }

    [Fact]
    public async Task ConfigureInfrastructureForPolyglotUsesProvisioningBuildOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var storage = builder.AddAzureStorage("storage");
        storage.Resource.ProvisioningBuildOptions = new();
        storage.Resource.ProvisioningBuildOptions.InfrastructureResolvers.Insert(0, new AspireV8ResourceNamePropertyResolver());

        AzureProvisioningResourceExtensions.ConfigureInfrastructureForPolyglot(
            storage,
            context => new() { InfrastructureJson = context.InfrastructureJson });

        var bicep = storage.Resource.GetBicepTemplateString();

        await Verify(bicep, extension: "bicep");
    }

    [Theory]
    [InlineData("", "The Azure infrastructure customization callback for Aspire resource 'infrastructure' must return a non-empty infrastructure JSON document.")]
    [InlineData("not-json", "Failed to deserialize the Azure infrastructure customization result for Aspire resource 'infrastructure'.")]
    [InlineData("""{"infras":[]}""", "Failed to deserialize the Azure infrastructure customization result for Aspire resource 'infrastructure'.")]
    [InlineData("""{"infras":[{},{}]}""", "Failed to deserialize the Azure infrastructure customization result for Aspire resource 'infrastructure'.")]
    public void AddAzureInfrastructureForPolyglotRejectsInvalidResults(string infrastructureJson, string expectedMessage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var infrastructure = AzureProvisioningResourceExtensions.AddAzureInfrastructureForPolyglot(
            builder,
            "infrastructure",
            _ => new() { InfrastructureJson = infrastructureJson });

        var exception = Assert.Throws<InvalidOperationException>(infrastructure.Resource.GetBicepTemplateString);

        Assert.Equal(expectedMessage, exception.Message);
    }

    [Fact]
    public void ConfigureInfrastructureForPolyglotRejectsRenaming()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var infrastructure = builder.AddAzureInfrastructure("infrastructure", _ => { });
        AzureProvisioningResourceExtensions.ConfigureInfrastructureForPolyglot(
            infrastructure,
            context =>
            {
                var document = JsonNode.Parse(context.InfrastructureJson)!.AsObject();
                document["infras"]!.AsArray()[0]!["fileName"] = "renamed.bicep";

                return new() { InfrastructureJson = document.ToJsonString() };
            });

        var exception = Assert.Throws<InvalidOperationException>(infrastructure.Resource.GetBicepTemplateString);

        Assert.Equal(
            "The Azure infrastructure customization callback for Aspire resource 'infrastructure' changed the Bicep name from 'infrastructure' to 'renamed'. Infrastructure renaming is not supported.",
            exception.Message);
    }

    private static JsonObject GetResourceValue(JsonObject document, string resourceName)
        => document["infras"]!.AsArray()[0]!["resources"]![resourceName]!["value"]!["value"]!.AsObject();

    private static string Serialize(Infrastructure infrastructure)
    {
        _ = infrastructure.Build();

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        writer.WriteStartObject();
        writer.WritePropertyName("infras");
        writer.WriteStartArray();
        ((IJsonModel<Infrastructure>)infrastructure).Write(writer, ModelReaderWriterOptions.Json);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
