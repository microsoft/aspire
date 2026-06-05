// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSqlConnectionPropertiesTests
{
    [Fact]
    public void AzureSqlServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sql = builder.AddAzureSqlServer("sql");

        var resource = Assert.Single(builder.Resources.OfType<AzureSqlServerResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties,
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{sql.outputs.sqlServerFqdn}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("1433", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("mssql://{sql.outputs.sqlServerFqdn}:1433", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:sqlserver://{sql.outputs.sqlServerFqdn}:1433;encrypt=true;trustServerCertificate=false", property.Value.ValueExpression);
            });
    }
}
