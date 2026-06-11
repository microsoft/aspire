// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureSqlDatabaseConnectionPropertiesTests
{
    [Fact]
    public void AzureSqlServerResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sqlServer = builder.AddAzureSqlServer("sql");

        var resource = Assert.Single(builder.Resources.OfType<AzureSqlServerResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties.OrderBy(p => p.Key),
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{sql.outputs.sqlServerFqdn}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:sqlserver://{sql.outputs.sqlServerFqdn}:1433;encrypt=true;trustServerCertificate=false", property.Value.ValueExpression);
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
            });
    }

    [Fact]
    public void AzureSqlDatabaseResourceGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sqlServer = builder.AddAzureSqlServer("sql");
        var database = sqlServer.AddDatabase("database", "mydb");

        var resource = Assert.Single(builder.Resources.OfType<AzureSqlDatabaseResource>());
        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties.OrderBy(p => p.Key),
            property =>
            {
                Assert.Equal("DatabaseName", property.Key);
                Assert.Equal("mydb", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{sql.outputs.sqlServerFqdn}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:sqlserver://{sql.outputs.sqlServerFqdn}:1433;database=mydb;encrypt=true;trustServerCertificate=false", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("1433", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("mssql://{sql.outputs.sqlServerFqdn}:1433/mydb", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureSqlServerResourceWithRunAsContainerGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sqlServer = builder.AddAzureSqlServer("sql");
        sqlServer.RunAsContainer(c =>
        {
            c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));
        });

        // After RunAsContainer, the resource is still an AzureSqlServerResource but with InnerResource set
        var resource = sqlServer.Resource;
        Assert.True(resource.IsContainer);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties.OrderBy(p => p.Key),
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{sql.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:sqlserver://{sql.bindings.tcp.host}:{sql.bindings.tcp.port};trustServerCertificate=true", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.NotEmpty(property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{sql.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("mssql://sa:{sql-password.value}@{sql.bindings.tcp.host}:{sql.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("sa", property.Value.ValueExpression);
            });
    }

    [Fact]
    public void AzureSqlDatabaseResourceWithRunAsContainerGetConnectionPropertiesReturnsExpectedValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sqlServer = builder.AddAzureSqlServer("sql");
        var database = sqlServer.AddDatabase("database", "mydb");
        sqlServer.RunAsContainer(c =>
        {
            c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 1433));
        });

        // After RunAsContainer, the database resource still exists but its parent has InnerResource set
        var resource = database.Resource;
        Assert.True(resource.IsContainer);

        var properties = ((IResourceWithConnectionString)resource).GetConnectionProperties().ToDictionary(x => x.Key, x => x.Value);

        Assert.Collection(
            properties.OrderBy(p => p.Key),
            property =>
            {
                Assert.Equal("DatabaseName", property.Key);
                Assert.Equal("mydb", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Host", property.Key);
                Assert.Equal("{sql.bindings.tcp.host}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("JdbcConnectionString", property.Key);
                Assert.Equal("jdbc:sqlserver://{sql.bindings.tcp.host}:{sql.bindings.tcp.port};databaseName=mydb;trustServerCertificate=true", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Password", property.Key);
                Assert.NotEmpty(property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Port", property.Key);
                Assert.Equal("{sql.bindings.tcp.port}", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Uri", property.Key);
                Assert.Equal("mssql://sa:{sql-password.value}@{sql.bindings.tcp.host}:{sql.bindings.tcp.port}/mydb", property.Value.ValueExpression);
            },
            property =>
            {
                Assert.Equal("Username", property.Key);
                Assert.Equal("sa", property.Value.ValueExpression);
            });
    }
}
