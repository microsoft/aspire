// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Azure.Tests.PublicApiTests;

public class CosmosDBPublicApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureCosmosDBContainerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddAzureCosmosDB("cosmos");
        var name = isNull ? null! : string.Empty;
        const string containerName = "db";
        const string partitionKeyPath = "data";
        var parent = new AzureCosmosDBDatabaseResource("database", "cosmos-db", resource.Resource);

        var action = () => new AzureCosmosDBContainerResource(name, containerName, partitionKeyPath, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HierarchicalPartitionCtorAzureCosmosDBContainerResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddAzureCosmosDB("cosmos");
        var name = isNull ? null! : string.Empty;
        const string containerName = "db";
        const string partitionKeyPath = "data";
        var parent = new AzureCosmosDBDatabaseResource("database", "cosmos-db", resource.Resource);

        var action = () => new AzureCosmosDBContainerResource(name, containerName, [partitionKeyPath], parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureCosmosDBContainerResourceShouldThrowWhenContainerNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddAzureCosmosDB("cosmos");
        const string name = "cosmos";
        var containerName = isNull ? null! : string.Empty;
        const string partitionKeyPath = "data";
        var parent = new AzureCosmosDBDatabaseResource("database", "cosmos-db", resource.Resource);

        var action = () => new AzureCosmosDBContainerResource(name, containerName, partitionKeyPath, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(containerName), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HierarchicalPartitionCtorAzureCosmosDBContainerResourceShouldThrowWhenContainerNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddAzureCosmosDB("cosmos");
        const string name = "cosmos";
        var containerName = isNull ? null! : string.Empty;
        const string partitionKeyPath = "data";
        var parent = new AzureCosmosDBDatabaseResource("database", "cosmos-db", resource.Resource);

        var action = () => new AzureCosmosDBContainerResource(name, containerName, [partitionKeyPath], parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(containerName), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureCosmosDBContainerResourceShouldThrowWhenPartitionKeyPathIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddAzureCosmosDB("cosmos");
        const string name = "cosmos";
        const string containerName = "db";
        var partitionKeyPath = isNull ? null! : string.Empty;
        var parent = new AzureCosmosDBDatabaseResource("database", "cosmos-db", resource.Resource);

        var action = () => new AzureCosmosDBContainerResource(name, containerName, partitionKeyPath, parent);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(partitionKeyPath), exception.ParamName);
    }

    [Fact]
    public void HierarchicalPartitionCtorAzureCosmosDBContainerResourceShouldThrowWhenPartitionKeyPathsIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddAzureCosmosDB("cosmos");
        const string name = "cosmos";
        const string containerName = "db";
        IEnumerable<string> partitionKeyPaths = null!;
        var parent = new AzureCosmosDBDatabaseResource("database", "cosmos-db", resource.Resource);

        var action = () => new AzureCosmosDBContainerResource(name, containerName, partitionKeyPaths, parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(partitionKeyPaths), exception.ParamName);
    }

    [Fact]
    public void HierarchicalPartitionCtorAzureCosmosDBContainerResourceShouldThrowWhenPartitionKeyPathsIsEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddAzureCosmosDB("cosmos");
        const string name = "cosmos";
        const string containerName = "db";
        IEnumerable<string> partitionKeyPaths = [];
        var parent = new AzureCosmosDBDatabaseResource("database", "cosmos-db", resource.Resource);

        var action = () => new AzureCosmosDBContainerResource(name, containerName, partitionKeyPaths, parent);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(partitionKeyPaths), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HierarchicalPartitionCtorAzureCosmosDBContainerResourceShouldThrowWhenPartitionKeyPathsContainEmptyOrNull(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddAzureCosmosDB("cosmos");
        const string name = "cosmos";
        const string containerName = "db";
        IEnumerable<string> partitionKeyPaths = [isNull ? null! : string.Empty];
        var parent = new AzureCosmosDBDatabaseResource("database", "cosmos-db", resource.Resource);

        var action = () => new AzureCosmosDBContainerResource(name, containerName, partitionKeyPaths, parent);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(partitionKeyPaths), exception.ParamName);
    }

    [Fact]
    public void CtorAzureCosmosDBContainerResourceShouldThrowWhenParentIsNull()
    {
        const string name = "cosmos";
        const string containerName = "db";
        const string partitionKeyPath = "data";
        AzureCosmosDBDatabaseResource parent = null!;

        var action = () => new AzureCosmosDBContainerResource(name, containerName, partitionKeyPath, parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(parent), exception.ParamName);
    }

    [Fact]
    public void HierarchicalPartitionCtorAzureCosmosDBContainerResourceShouldThrowWhenParentIsNull()
    {
        const string name = "cosmos";
        const string containerName = "db";
        const string partitionKeyPath = "data";
        AzureCosmosDBDatabaseResource parent = null!;

        var action = () => new AzureCosmosDBContainerResource(name, containerName, [partitionKeyPath], parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(parent), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureCosmosDBDatabaseResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddAzureCosmosDB("cosmos");
        var name = isNull ? null! : string.Empty;
        const string databaseName = "database";

        var action = () => new AzureCosmosDBDatabaseResource(name, databaseName, parent.Resource);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureCosmosDBDatabaseResourceShouldThrowWhenDatabaseNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddAzureCosmosDB("cosmos");
        const string name = "cosmos";
        var databaseName = isNull ? null! : string.Empty;

        var action = () => new AzureCosmosDBDatabaseResource(name, databaseName, parent.Resource);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(databaseName), exception.ParamName);
    }

    [Fact]
    public void CtorAzureCosmosDBDatabaseResourceShouldThrowWhenParentIsNull()
    {
        const string name = "cosmos";
        const string databaseName = "database";
        AzureCosmosDBResource parent = null!;

        var action = () => new AzureCosmosDBDatabaseResource(name, databaseName, parent);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(parent), exception.ParamName);
    }

    [Fact]
    public void CtorAzureCosmosDBEmulatorResourceShouldThrowWhenInnerResourceIsNull()
    {
        AzureCosmosDBResource innerResource = null!;

        var action = () => new AzureCosmosDBEmulatorResource(innerResource);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(innerResource), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CtorAzureCosmosDBResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        var configureInfrastructure = (AzureResourceInfrastructure _) => { };

        var action = () => new AzureCosmosDBResource(name, configureInfrastructure);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorAzureCosmosDBResourceShouldThrowWhenConfigureInfrastructureIsNull()
    {
        const string name = "cosmos";
        Action<AzureResourceInfrastructure> configureInfrastructure = null!;

        var action = () => new AzureCosmosDBResource(name, configureInfrastructure);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(configureInfrastructure), exception.ParamName);
    }

    [Fact]
    public void AddAzureCosmosDBShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;
        const string name = "cosmos";

        var action = () => builder.AddAzureCosmosDB(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddAzureCosmosDBShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddAzureCosmosDB(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void RunAsEmulatorShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBResource> builder = null!;
        Action<IResourceBuilder<AzureCosmosDBEmulatorResource>>? configureContainer = null;

        var action = () => builder.RunAsEmulator(configureContainer);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void RunAsClassicEmulatorShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBResource> builder = null!;
        Action<IResourceBuilder<AzureCosmosDBEmulatorResource>>? configureContainer = null;

        var action = () => builder.RunAsClassicEmulator(configureContainer);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDataVolumeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBEmulatorResource> builder = null!;

        var action = () => builder.WithDataVolume();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithGatewayPortShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBEmulatorResource> builder = null!;
        int? port = null;

        var action = () => builder.WithGatewayPort(port);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithPartitionCountShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBEmulatorResource> builder = null!;
        const int count = 1;

        var action = () => builder.WithPartitionCount(count);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use AddCosmosDatabase instead to add a Cosmos DB database.")]
    public void AddDatabaseShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBResource> builder = null!;
        const string databaseName = "cosmos-db";

        var action = () => builder.AddDatabase(databaseName);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Obsolete($"This method is obsolete because it has the wrong return type and will be removed in a future version. Use AddCosmosDatabase instead to add a Cosmos DB database.")]
    public void AddDatabaseShouldThrowWhenDatabaseNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos");
        var databaseName = isNull ? null! : string.Empty;

        var action = () => cosmos.AddDatabase(databaseName);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(databaseName), exception.ParamName);
    }

    [Fact]
    public void AddCosmosDatabaseShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBResource> builder = null!;
        const string name = "cosmos";

        var action = () => builder.AddCosmosDatabase(name);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddCosmosDatabaseShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos");
        var name = isNull ? null! : string.Empty;

        var action = () => cosmos.AddCosmosDatabase(name);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void AddContainerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBDatabaseResource> builder = null!;
        const string name = "cosmos";
        const string partitionKeyPath = "data";

        var action = () => builder.AddContainer(name, partitionKeyPath);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddContainerShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .AddCosmosDatabase("cosmos-db");
        var name = isNull ? null! : string.Empty;
        const string partitionKeyPath = "data";

        var action = () => cosmos.AddContainer(name, partitionKeyPath);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddContainerShouldThrowWhenPartitionKeyPathIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos")
            .AddCosmosDatabase("cosmos-db");
        const string name = "cosmos";
        var partitionKeyPath = isNull ? null! : string.Empty;

        var action = () => cosmos.AddContainer(name, partitionKeyPath);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(partitionKeyPath), exception.ParamName);
    }

    [Fact]
    public void AddContainerShouldThrowWhenHierarchicalPartitionKeyIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos").AddCosmosDatabase("cosmos-db");
        const string name = "cosmos";
        IEnumerable<string>? partitionKeyPaths = null;
        var action = () => cosmos.AddContainer(name, partitionKeyPaths!);
        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(partitionKeyPaths), exception.ParamName);
    }

    [Fact]
    public void AddContainerShouldThrowWhenHierarchicalPartitionKeyIsEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos").AddCosmosDatabase("cosmos-db");
        const string name = "cosmos";
        string[] partitionKeyPaths = [];
        var action = () => cosmos.AddContainer(name, partitionKeyPaths);
        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(partitionKeyPaths), exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddContainerShouldThrowWhenHierarchicalPartitionKeyContainsEmptyOrNull(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var cosmos = builder.AddAzureCosmosDB("cosmos").AddCosmosDatabase("cosmos-db");
        const string name = "cosmos";
        string[] partitionKeyPaths = [isNull ? null! : string.Empty];
        var action = () => cosmos.AddContainer(name, partitionKeyPaths);
        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(partitionKeyPaths), exception.ParamName);
    }

    [Fact]
    public void WithDataExplorerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBEmulatorResource> builder = null!;

        var action = () => builder.WithDataExplorer();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithAccessKeyAuthenticationShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<AzureCosmosDBResource> builder = null!;

        var action = () =>
        {
            builder.WithAccessKeyAuthentication();
        };

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }
}
