// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.TypeSystem;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aspire.Hosting.RemoteHost.Tests;

public class ExternalCapabilityRegistryTests
{
    [Fact]
    public async Task AugmentContext_PreservesManagedMetadataAndExternalNullability()
    {
        using var payload = JsonDocument.Parse("""
            {
              "capabilities": [
                {
                  "id": "test.external/readValue",
                  "method": "readValue",
                  "returnType": { "typeId": "string", "category": "Primitive", "isNullable": true }
                }
              ]
            }
            """);
        using var connection = new IntegrationHostTestConnection(payload.RootElement);
        var registry = new ExternalCapabilityRegistry(NullLogger<ExternalCapabilityRegistry>.Instance);
        registry.AddIntegrationHost(connection.ServerRpc);
        await registry.InitializeAllHostsAsync().WaitAsync(TestContext.Current.CancellationToken);

        var context = new AtsContext
        {
            Capabilities = [],
            HandleTypes = [],
            DtoTypes = [],
            EnumTypes = [],
            ExportedValues =
            [
                new AtsExportedValueInfo
                {
                    OwningAssemblyName = "test.managed",
                    PathSegments = ["Defaults", "Name"],
                    Type = new AtsTypeRef { TypeId = AtsConstants.String, Category = AtsTypeCategory.Primitive },
                    Value = JsonValue.Create("managed-value")
                }
            ]
        };
        context.Methods["test.managed/toString"] = typeof(string).GetMethod(nameof(string.ToString), Type.EmptyTypes)!;
        context.Properties["test.managed/length"] = typeof(string).GetProperty(nameof(string.Length))!;

        var augmented = registry.AugmentContext(context);

        var capability = Assert.Single(augmented.Capabilities);
        Assert.Equal("test.external/readValue", capability.CapabilityId);
        Assert.Equal("readValue", capability.MethodName);
        Assert.True(capability.ReturnType!.IsNullable);
        Assert.Same(context.ExportedValues, augmented.ExportedValues);
        Assert.Same(context.HandleTypes, augmented.HandleTypes);
        Assert.Same(context.DtoTypes, augmented.DtoTypes);
        Assert.Same(context.EnumTypes, augmented.EnumTypes);
        Assert.Same(context.Diagnostics, augmented.Diagnostics);
        Assert.Equal(context.Methods, augmented.Methods);
        Assert.Equal(context.Properties, augmented.Properties);
        Assert.Empty(context.Capabilities);
    }
}
