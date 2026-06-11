// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzurePostgresFlexibleServerConnectionPropertiesTests
{
    [Fact]
    public void AzurePostgresFlexibleServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddAzurePostgresFlexibleServer("postgres");

        var resource = Assert.Single(builder.Resources.OfType<AzurePostgresFlexibleServerResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{postgres.outputs.hostName}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("5432", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("postgresql://{postgres.outputs.hostName}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:postgresql://{postgres.outputs.hostName}?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzurePostgresFlexibleServerResourceWithPasswordAuthenticationGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var postgres = builder.AddAzurePostgresFlexibleServer("postgres").WithPasswordAuthentication();

        var resource = Assert.Single(builder.Resources.OfType<AzurePostgresFlexibleServerResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties.OrderBy(p => p.Key),
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{postgres.outputs.hostName}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:postgresql://{postgres.outputs.hostName}?sslmode=require&authenticationPluginClassName=com.azure.identity.extensions.jdbc.postgresql.AzurePostgresqlAuthenticationPlugin", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.Equal("{postgres-password.value}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("5432", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("postgresql://{postgres-username.value}:{postgres-password.value}@{postgres.outputs.hostName}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("{postgres-username.value}", property.Value.ValueExpression);
            });
    }
}
