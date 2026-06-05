// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureCosmosDBContainerConnectionPropertiesTests
{
    [Fact]
    public void AzureCosmosDBContainerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmosdb = builder.AddAzureCosmosDB("cosmosdb");
        var database = cosmosdb.AddCosmosDatabase("database", "mydb");
        var container = database.AddContainer("container", "/id", "mycontainer");

        var resource = Assert.Single(builder.Resources.OfType<AzureCosmosDBContainerResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{cosmosdb.outputs.connectionString}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("DatabaseName", property.Key);
                Assert.Equal("mydb", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ContainerName", property.Key);
                Assert.Equal("mycontainer", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureCosmosDBContainerResourceWithAccessKeyAuthenticationGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmosdb = builder.AddAzureCosmosDB("cosmosdb").WithAccessKeyAuthentication();
        var database = cosmosdb.AddCosmosDatabase("database", "mydb");
        var container = database.AddContainer("container", "/id", "mycontainer");

        var resource = Assert.Single(builder.Resources.OfType<AzureCosmosDBContainerResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{cosmosdb.outputs.connectionString}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("AccountKey", property.Key);
                Assert.Equal("{cosmosdb-kv.secrets.primaryaccesskey--cosmosdb}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ConnectionString", property.Key);
                Assert.Equal("{cosmosdb-kv.secrets.connectionstrings--cosmosdb}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("DatabaseName", property.Key);
                Assert.Equal("mydb", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ContainerName", property.Key);
                Assert.Equal("mycontainer", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureCosmosDBContainerResourceEmulatorGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmosdb = builder.AddAzureCosmosDB("cosmosdb").RunAsEmulator();
        var database = cosmosdb.AddCosmosDatabase("database", "mydb");
        var container = database.AddContainer("container", "/id", "mycontainer");

        var resource = Assert.Single(builder.Resources.OfType<AzureCosmosDBContainerResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("https://{cosmosdb.bindings.emulator.host}:{cosmosdb.bindings.emulator.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("AccountKey", property.Key);
                Assert.Equal("C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ConnectionString", property.Key);
                Assert.Equal("AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;AccountEndpoint=https://{cosmosdb.bindings.emulator.host}:{cosmosdb.bindings.emulator.port};DisableServerCertificateValidation=True;", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("DatabaseName", property.Key);
                Assert.Equal("mydb", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("ContainerName", property.Key);
                Assert.Equal("mycontainer", property.Value.ValueExpression);
            });
    }
}
