// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Aspire.MongoDB.EntityFrameworkCore.Tests;

public class MongoDBEntityFrameworkCorePublicApiTests
{
    [Fact]
    public void AddMongoDbContextShouldThrowWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;
        const string connectionName = "mongodb";
        const string databaseName = "testdb";

        var action = () => builder.AddMongoDbContext<DbContext>(connectionName, databaseName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddMongoDbContextShouldThrowWhenConnectionNameIsNullOrEmpty(bool isNull)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var connectionName = isNull ? null! : string.Empty;
        const string databaseName = "testdb";

        var action = () => builder.AddMongoDbContext<DbContext>(connectionName, databaseName);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(connectionName), exception.ParamName);
    }

    [Fact]
    public void AddMongoDbContextDatabaseNameIsOptional()
    {
        // databaseName parameter is optional - no exception should be thrown at registration time
        // Exception will be thrown at resolution time if no database name is available
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:mongodb", "mongodb://localhost:27017"),
        ]);

        // Should not throw - databaseName is optional
        var exception = Record.Exception(() => builder.AddMongoDbContext<TestDbContext>("mongodb"));
        Assert.Null(exception);
    }

    [Fact]
    public void EnrichMongoDbContextShouldThrowWhenBuilderIsNull()
    {
        IHostApplicationBuilder builder = null!;

        var action = () => builder.EnrichMongoDbContext<DbContext>();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    public class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
    {
    }
}
