// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureCosmosDBConnectionPropertiesTests
{
    [Fact]
    public void AzureCosmosDBResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        var cosmosDBResource = new AzureCosmosDBResource("cosmos", _ => { });

        var properties = ((IResourceWithConnectionString)cosmosDBResource).GetConnectionProperties().ToArray();
        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{cosmos.outputs.connectionString}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureCosmosDBResourceWithAccessKeyAuthenticationGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos").WithAccessKeyAuthentication();

        var resource = Assert.Single(builder.Resources.OfType<AzureCosmosDBResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();
        
        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{cosmos.outputs.connectionString}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("AccountKey", property.Key);
                Assert.Equal("{cosmos-kv.secrets.primaryaccesskey--cosmos}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ConnectionString", property.Key);
                Assert.Equal("{cosmos-kv.secrets.connectionstrings--cosmos}", property.Value.ValueExpression);
            });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AzureCosmosDBResourceEmulatorGetConnectionPropertiesReturnsExpectedValues(bool useClassic)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos");
        cosmos = useClassic ? cosmos.RunAsClassicEmulator() : cosmos.RunAsEmulator();

        var resource = Assert.Single(builder.Resources.OfType<AzureCosmosDBResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();
        var expectedConnectionString = useClassic
            ? "AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;AccountEndpoint=https://{cosmos.bindings.emulator.host}:{cosmos.bindings.emulator.port};DisableServerCertificateValidation=True;"
            : "AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;AccountEndpoint={cosmos.bindings.emulator.url}";

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{cosmos.bindings.emulator.url}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("AccountKey", property.Key);
                Assert.Equal("C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ConnectionString", property.Key);
                Assert.Equal(expectedConnectionString, property.Value.ValueExpression);
            });
    }
}
