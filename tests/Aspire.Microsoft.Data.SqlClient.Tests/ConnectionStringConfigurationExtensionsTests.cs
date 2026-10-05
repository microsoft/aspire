// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Microsoft.Data.SqlClient.Tests;

public class ConnectionStringConfigurationExtensionsTests
{
    private const string LogicalConnectionName = "9-sql.connection";
    private const string PortableConnectionName = "_9_sql_connection";

    [Fact]
    public void TryGetConnectionStringReadsExactName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new($"ConnectionStrings:{LogicalConnectionName}", "Server=exact;Database=test")
            ])
            .Build();

        var result = configuration.TryGetConnectionString(LogicalConnectionName, out var connectionString);

        Assert.True(result);
        Assert.Equal("Server=exact;Database=test", connectionString);
    }

    [Fact]
    public void TryGetConnectionStringReadsPortableName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new($"ConnectionStrings:{PortableConnectionName}", "Server=portable;Database=test")
            ])
            .Build();

        var result = configuration.TryGetConnectionString(LogicalConnectionName, out var connectionString);

        Assert.True(result);
        Assert.Equal("Server=portable;Database=test", connectionString);
    }

    [Theory]
    [InlineData("db__primary", "db_primary")]
    [InlineData("db--primary", "db_primary")]
    public void TryGetConnectionStringReadsPortableNameWithoutNestedConfigurationSegments(
        string logicalName,
        string portableName)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new($"ConnectionStrings:{portableName}", "Server=portable;Database=test")
            ])
            .Build();

        var result = configuration.TryGetConnectionString(logicalName, out var connectionString);

        Assert.True(result);
        Assert.Equal("Server=portable;Database=test", connectionString);
    }

    [Fact]
    public void TryGetConnectionStringPrefersExactName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new($"ConnectionStrings:{LogicalConnectionName}", "Server=exact;Database=test"),
                new($"ConnectionStrings:{PortableConnectionName}", "Server=portable;Database=test")
            ])
            .Build();

        var result = configuration.TryGetConnectionString(LogicalConnectionName, out var connectionString);

        Assert.True(result);
        Assert.Equal("Server=exact;Database=test", connectionString);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", true)]
    public void TryGetConnectionStringPreservesNullAndEmptySemantics(string? configuredValue, bool expectedSuccess)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>($"ConnectionStrings:{PortableConnectionName}", configuredValue)
            ])
            .Build();

        var result = configuration.TryGetConnectionString(LogicalConnectionName, out var connectionString);

        Assert.Equal(expectedSuccess, result);
        Assert.Equal(configuredValue, connectionString);
    }
}
