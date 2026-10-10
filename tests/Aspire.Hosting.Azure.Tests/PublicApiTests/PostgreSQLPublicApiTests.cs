// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class PostgreSQLPublicApiTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [Obsolete($"This method is obsolete and will be removed in a future version. Use AddAzurePostgresFlexibleServer instead to add an Azure PostgreSQL Flexible Server resource.")]

    public void PublishAsAzurePostgresFlexibleServerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<PostgresServerResource> builder = null!;

        var action = () =>
        {
            builder.PublishAsAzurePostgresFlexibleServer();
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    [Obsolete($"This method is obsolete and will be removed in a future version. Use AddAzurePostgresFlexibleServer instead to add an Azure PostgreSQL Flexible Server resource.")]
    public void AsAzurePostgresFlexibleServerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<PostgresServerResource> builder = null!;

        var action = () =>
        {
            builder.AsAzurePostgresFlexibleServer();
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void AddAzurePostgresFlexibleServerShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "postgre-sql";

        var action = () => builder.AddAzurePostgresFlexibleServer(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzurePostgresFlexibleServerShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzurePostgresFlexibleServer(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddDatabaseShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzurePostgresFlexibleServerResource> builder = null!;
        const string name = "postgre-db";

        var action = () => builder.AddDatabase(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddDatabaseShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var testBuilder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var builder = testBuilder.AddAzurePostgresFlexibleServer("postgre-sql");
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddDatabase(name);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void RunAsContainerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzurePostgresFlexibleServerResource> builder = null!;

        var action = () => builder.RunAsContainer();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPasswordAuthenticationShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzurePostgresFlexibleServerResource> builder = null!;

        var action = () => builder.WithPasswordAuthentication();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    [Obsolete($"This class is obsolete and will be removed in a future version. Use AddAzurePostgresFlexibleServer instead to add an Azure Postgres Flexible Server resource.")]
    public void CtorAzurePostgresResourceShouldThrowWhenInnerResourceIsNull()
    {
        PostgresServerResource innerResource = null!;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzurePostgresResource(innerResource, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(innerResource), exception.ParamName);
    }

    [Fact]
    [Obsolete($"This class is obsolete and will be removed in a future version. Use AddAzurePostgresFlexibleServer instead to add an Azure Postgres Flexible Server resource.")]
    public void CtorAzurePostgresResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        ParameterResource? userName = null;
        var resource = new ParameterResource("password", (_) => "password");
        var innerResource = new PostgresServerResource("postgre", userName, resource);
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzurePostgresResource(innerResource, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzurePostgresFlexibleServerDatabaseResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string databaseName = "db";
        var postgresParentResource = new AzurePostgresFlexibleServerResource("postgre-flex", (_) => { });

        var action = () => new AzurePostgresFlexibleServerDatabaseResource(name, databaseName, postgresParentResource);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzurePostgresFlexibleServerDatabaseResourceShouldThrowWhenDatabaseNameIsNullOrEmpty(bool isNull)
    {
        const string name = "postgres";
        var databaseName = isNull ? null! : string.Empty;
        var postgresParentResource = new AzurePostgresFlexibleServerResource("postgre-flex", (_) => { });

        var action = () => new AzurePostgresFlexibleServerDatabaseResource(name, databaseName, postgresParentResource);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(databaseName), exception.ParamName);
    }

    [Fact]
    public void CtorAzurePostgresFlexibleServerDatabaseResourceShouldThrowWhenPostgresParentResourceIsNull()
    {
        const string name = "postgres";
        const string databaseName = "db";
        AzurePostgresFlexibleServerResource postgresParentResource = null!;

        var action = () => new AzurePostgresFlexibleServerDatabaseResource(name, databaseName, postgresParentResource);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(postgresParentResource), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzurePostgresFlexibleServerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        Action<AzureResourceInfrastructure> configureInfrastructure = (_) => { };

        var action = () => new AzurePostgresFlexibleServerResource(name, configureInfrastructure);

        var exception = isNull
           ? Assert.Throws<ArgumentNullException>(action)
           : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzurePostgresFlexibleServerResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        const string name = "postgres";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzurePostgresFlexibleServerResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }
}
