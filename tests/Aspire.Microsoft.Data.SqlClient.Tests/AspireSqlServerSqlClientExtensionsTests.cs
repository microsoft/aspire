// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.Microsoft.Data.SqlClient.Tests;

public class AspireSqlServerSqlClientExtensionsTests
{
    private const string ConnectionString = "Data Source=fake;Database=master;Encrypt=True";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsFromConnectionStringsCorrectly(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedSqlServerClient("sqlconnection");
        }
        else
        {
            builder.AddSqlServerClient("sqlconnection");
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<SqlConnection>("sqlconnection") :
            host.Services.GetRequiredService<SqlConnection>();

        Assert.Equal(ConnectionString, connection.ConnectionString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionStringCanBeSetInCode(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", "unused")
        ]);

        static void SetConnectionString(MicrosoftDataSqlClientSettings settings) => settings.ConnectionString = ConnectionString;
        if (useKeyed)
        {
            builder.AddKeyedSqlServerClient("sqlconnection", SetConnectionString);
        }
        else
        {
            builder.AddSqlServerClient("sqlconnection", SetConnectionString);
        }

        using var host = builder.Build();
        var connection = useKeyed ?
            host.Services.GetRequiredKeyedService<SqlConnection>("sqlconnection") :
            host.Services.GetRequiredService<SqlConnection>();

        Assert.Equal(ConnectionString, connection.ConnectionString);
        // the connection string from config should not be used since code set it explicitly
        Assert.DoesNotContain("unused", connection.ConnectionString);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ConnectionNameWinsOverConfigSection(bool useKeyed)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        var key = useKeyed ? "sqlconnection" : null;
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>(ConformanceTests.CreateConfigKey("Aspire:Microsoft:Data:SqlClient", key, "ConnectionString"), "unused"),
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection", ConnectionString)
        ]);

        if (useKeyed)
        {
            builder.AddKeyedSqlServerClient("sqlconnection");
        }
        else
        {
            builder.AddSqlServerClient("sqlconnection");
        }

        using var host = builder.Build();
        var dataSource = useKeyed ?
            host.Services.GetRequiredKeyedService<SqlConnection>("sqlconnection") :
            host.Services.GetRequiredService<SqlConnection>();

        Assert.Equal(ConnectionString, dataSource.ConnectionString);
        // the connection string from config should not be used since it was found in ConnectionStrings
        Assert.DoesNotContain("unused", dataSource.ConnectionString);
    }

    [Fact]
    public void CanAddMultipleKeyedServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection1", "Data Source=fake1;Database=master"),
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection2", "Data Source=fake2;Database=master"),
            new KeyValuePair<string, string?>("ConnectionStrings:sqlconnection3", "Data Source=fake3;Database=master"),
        ]);

        builder.AddSqlServerClient("sqlconnection1");
        builder.AddKeyedSqlServerClient("sqlconnection2");
        builder.AddKeyedSqlServerClient("sqlconnection3");

        using var host = builder.Build();

        var connection1 = host.Services.GetRequiredService<SqlConnection>();
        var connection2 = host.Services.GetRequiredKeyedService<SqlConnection>("sqlconnection2");
        var connection3 = host.Services.GetRequiredKeyedService<SqlConnection>("sqlconnection3");

        Assert.NotSame(connection1, connection2);
        Assert.NotSame(connection1, connection3);
        Assert.NotSame(connection2, connection3);

        Assert.Contains("fake1", connection1.ConnectionString);
        Assert.Contains("fake2", connection2.ConnectionString);
        Assert.Contains("fake3", connection3.ConnectionString);
    }

    // Regression guard: SqlClient 7.0 moved the Entra ID (Active Directory) auth providers into
    // Microsoft.Data.SqlClient.Extensions.Azure. This integration references that package, so they must resolve here;
    // otherwise the Authentication="Active Directory Default" connection strings Aspire emits fail at runtime.
    [Theory]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryDefault)]
    [InlineData(SqlAuthenticationMethod.ActiveDirectoryManagedIdentity)]
    public void EntraIdAuthenticationProviderIsRegistered(SqlAuthenticationMethod method)
    {
        var provider = SqlAuthenticationProvider.GetProvider(method);

        Assert.NotNull(provider);
    }
}
