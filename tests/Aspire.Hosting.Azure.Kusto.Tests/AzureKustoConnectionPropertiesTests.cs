// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Kusto.Tests;

public class AzureKustoConnectionPropertiesTests
{
    [Fact]
    public void AzureKustoClusterResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var kusto = builder.AddAzureKustoCluster("kusto");

        var properties = ((IResourceWithConnectionString)kusto.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{kusto.outputs.clusterUri}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureKustoClusterResourceWithEmulatorGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();

        var properties = ((IResourceWithConnectionString)kusto.Resource).GetConnectionProperties().ToArray();

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{kusto.bindings.http.url}", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureKustoDatabaseResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var kusto = builder.AddAzureKustoCluster("kusto");
        var database = kusto.AddReadWriteDatabase("testdb");

        var resource = Assert.Single(builder.Resources.OfType<AzureKustoReadWriteDatabaseResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{kusto.outputs.clusterUri}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("DatabaseName", property.Key);
                Assert.Equal("testdb", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureKustoDatabaseResourceWithEmulatorGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var kusto = builder.AddAzureKustoCluster("kusto").RunAsEmulator();
        var database = kusto.AddReadWriteDatabase("testdb");

        var resource = Assert.Single(builder.Resources.OfType<AzureKustoReadWriteDatabaseResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("{kusto.bindings.http.url}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("DatabaseName", property.Key);
                Assert.Equal("testdb", property.Value.ValueExpression);
            });
    }
}
