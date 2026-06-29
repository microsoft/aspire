// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Microsoft.Azure.Cosmos.Tests;

public class AspireMicrosoftAzureCosmosExtensionsTests
{
    [Theory]
    [InlineData("AccountEndpoint=https://localhost:8081;AccountKey=fake;", "https://localhost:8081/")]
    [InlineData("AccountEndpoint=https://localhost:8081;Database=db;", "https://localhost:8081/")]
    [InlineData("AccountEndpoint=https://localhost:8081;Database=db;Container=mycontainer;", "https://localhost:8081/")]
    [InlineData("AccountEndpoint=https://localhost:8081;AccountKey=fake;Database=db", "https://localhost:8081/")]
    [InlineData("AccountEndpoint=https://localhost:8081;AccountKey=fake;Database=db;DisableServerCertificateValidation=True", "https://localhost:8081/")]
    [InlineData("AccountEndpoint=https://localhost:8081;AccountKey=fake;Database=db;Container=mycontainer", "https://localhost:8081/")]
    [InlineData("https://example1.documents.azure.com:443", "https://example1.documents.azure.com/")]
    public void AddAzureCosmosClient_EnsuresConnectionStringIsCorrect(string connectionString, string expectedEndpoint)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        PopulateConfiguration(builder.Configuration, connectionString);

        builder.AddAzureCosmosClient("cosmos");

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<CosmosClient>();

        Assert.Equal(expectedEndpoint, client.Endpoint.ToString());
    }

    [Fact]
    public void AddAzureCosmosClient_FailsWithError()
    {
        var e = Assert.Throws<ArgumentException>(() =>
            AddAzureCosmosClient_EnsuresConnectionStringIsCorrect("this=isnt;a=valid;cosmos=connectionstring", string.Empty));

        Assert.Contains("missing", e.Message);
        Assert.Contains("AccountEndpoint", e.Message);
    }

    [Theory]
    [InlineData("AccountEndpoint=https://localhost:8081;AccountKey=fake;Database=testdb;Container=mycontainers")]
    [InlineData("AccountEndpoint=https://localhost:8081;AccountKey=fake;Database=testdb;")]
    public void AddAzureCosmosClient_WorksWithChildConnectionStrings(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var expectedEndpoint = "https://localhost:8081/";

        PopulateConfiguration(builder.Configuration, connectionString);

        builder.AddAzureCosmosClient("cosmos");

        using var host = builder.Build();
        var client = host.Services.GetService<CosmosClient>();

        Assert.NotNull(client);
        Assert.Equal(expectedEndpoint, client.Endpoint.ToString());
    }

    [Fact]
    public void AddAzureCosmosDatabase_RegistersDatabaseService()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var expectedEndpoint = "https://localhost:8081/";

        PopulateConfiguration(builder.Configuration, $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};");

        builder.AddAzureCosmosDatabase("cosmos");

        using var host = builder.Build();
        var database = host.Services.GetRequiredService<Database>();
        var client = host.Services.GetService<CosmosClient>();

        Assert.Null(client);
        Assert.NotNull(database);
        Assert.Equal(databaseName, database.Id);
        Assert.Equal(expectedEndpoint, database.Client.Endpoint.ToString());
    }

    [Fact]
    public void AddAzureCosmosContainer_RegistersContainerService()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var containerName = "testcontainer";
        var expectedEndpoint = "https://localhost:8081/";

        PopulateConfiguration(builder.Configuration, $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};Container={containerName};");

        builder.AddAzureCosmosContainer("cosmos");

        using var host = builder.Build();
        var container = host.Services.GetRequiredService<Container>();
        var database = host.Services.GetService<Database>();
        var client = host.Services.GetService<CosmosClient>();

        Assert.NotNull(container);
        Assert.Null(client);
        Assert.Null(database);
        Assert.Equal(containerName, container.Id);
        Assert.Equal(expectedEndpoint, container.Database.Client.Endpoint.ToString());
    }

    [Fact]
    public void AddKeyedAzureCosmosDatabase_RegistersDatabaseService()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";
        var databaseName = "testdb";
        var expectedEndpoint = "https://localhost:8081/";

        PopulateConfiguration(builder.Configuration, $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};", serviceKey);

        builder.AddKeyedAzureCosmosDatabase(serviceKey);

        using var host = builder.Build();
        var database = host.Services.GetRequiredKeyedService<Database>(serviceKey);
        var client = host.Services.GetKeyedService<CosmosClient>(serviceKey);

        Assert.Null(client);
        Assert.NotNull(database);
        Assert.Equal(databaseName, database.Id);
        Assert.Equal(expectedEndpoint, database.Client.Endpoint.ToString());
    }

    [Fact]
    public void AddKeyedAzureCosmosContainer_RegistersContainerService()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";
        var databaseName = "testdb";
        var containerName = "testcontainer";
        var expectedEndpoint = "https://localhost:8081/";

        PopulateConfiguration(builder.Configuration, $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};Container={containerName}", serviceKey);

        builder.AddKeyedAzureCosmosContainer(serviceKey);

        using var host = builder.Build();
        var container = host.Services.GetRequiredKeyedService<Container>(serviceKey);
        var database = host.Services.GetKeyedService<Database>(serviceKey);
        var client = host.Services.GetKeyedService<CosmosClient>(serviceKey);

        Assert.NotNull(container);
        Assert.Null(client);
        Assert.Null(database);
        Assert.Equal(containerName, container.Id);
        Assert.Equal(expectedEndpoint, container.Database.Client.Endpoint.ToString());
    }

    [Theory]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake;Container=containerName")]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake;Database=databaseName")]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake")]
    public void AddAzureCosmosContainer_ThrowsForInvalidConnectionString(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";

        PopulateConfiguration(builder.Configuration, connectionString);
        builder.AddAzureCosmosContainer(serviceKey);
        using var host = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(host.Services.GetRequiredService<Container>);
        Assert.Equal("The connection string 'cosmos-key' does not exist or is missing the container name or database name.", exception.Message);
    }

    [Theory]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake;Container=containerName")]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake;Database=databaseName")]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake")]
    public void AddKeyedAzureCosmosContainer_ThrowsForInvalidConnectionString(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";

        PopulateConfiguration(builder.Configuration, connectionString, serviceKey);
        builder.AddKeyedAzureCosmosContainer(serviceKey);
        using var host = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => host.Services.GetKeyedService<Container>(serviceKey));
        Assert.Equal("The connection string 'cosmos-key' does not exist or is missing the container name or database name.", exception.Message);
    }

    [Theory]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake;Container=containerName")]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake")]
    public void AddAzureCosmosDatabase_ThrowsForInvalidConnectionString(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";

        PopulateConfiguration(builder.Configuration, connectionString);
        builder.AddAzureCosmosDatabase(serviceKey);
        using var host = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(host.Services.GetRequiredService<Database>);
        Assert.Equal("A Database could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:cosmos-key'.", exception.Message);
    }

    [Theory]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake;Container=containerName")]
    [InlineData("AccountEndpoint=https://localhost:8081/;AccountKey=fake")]
    public void AddKeyedAzureCosmosDatabase_ThrowsForInvalidConnectionString(string connectionString)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";

        PopulateConfiguration(builder.Configuration, connectionString, serviceKey);
        builder.AddKeyedAzureCosmosDatabase(serviceKey);
        using var host = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(() => host.Services.GetRequiredKeyedService<Database>(serviceKey));
        Assert.Equal("A Database could not be configured. Ensure valid connection information was provided in 'ConnectionStrings:cosmos-key'.", exception.Message);
    }

    [Fact]
    public void AddAzureCosmosClient_RespectsLimitToEndpointViaConfigureSettings()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;";

        PopulateConfiguration(builder.Configuration, connectionString);

        builder.AddAzureCosmosClient("cosmos", configureClientOptions: options =>
        {
            options.LimitToEndpoint = false;
        });

        using var host = builder.Build();
        var client = host.Services.GetRequiredService<CosmosClient>();

        Assert.Equal(expectedEndpoint, client.Endpoint.ToString());
        Assert.False(client.ClientOptions.LimitToEndpoint);
    }

    [Fact]
    public void AddAzureCosmosContainer_DoesNoReuseExistingClient()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var containerName = "testcontainer";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};Container={containerName};";

        PopulateConfiguration(builder.Configuration, connectionString);

        builder.AddAzureCosmosClient("cosmos", configureClientOptions: options =>
        {
            options.LimitToEndpoint = false;
        });
        builder.AddAzureCosmosContainer("cosmos");

        using var host = builder.Build();

        var client = host.Services.GetRequiredService<CosmosClient>();
        var container = host.Services.GetRequiredService<Container>();

        Assert.NotSame(client, container.Database.Client);
    }

    [Fact]
    public void AddAzureCosmosDatabase_CreatesNewClient_WhenConfigureClientOptionsProvided()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};";

        PopulateConfiguration(builder.Configuration, connectionString);

        builder.AddAzureCosmosClient("cosmos");
        builder.AddAzureCosmosDatabase("cosmos", configureClientOptions: options =>
        {
            options.LimitToEndpoint = false;
        });

        using var host = builder.Build();

        var client = host.Services.GetRequiredService<CosmosClient>();
        var database = host.Services.GetRequiredService<Database>();

        Assert.NotSame(client, database.Client);
        Assert.Equal(databaseName, database.Id);
        Assert.Equal(expectedEndpoint, database.Client.Endpoint.ToString());
        Assert.False(database.Client.ClientOptions.LimitToEndpoint);
    }

    [Fact]
    public void AddAzureCosmosContainer_CreatesNewClient_WhenConfigureClientOptionsProvided()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var containerName = "testcontainer";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};Container={containerName};";

        PopulateConfiguration(builder.Configuration, connectionString);

        builder.AddAzureCosmosClient("cosmos");
        builder.AddAzureCosmosContainer("cosmos", configureClientOptions: options =>
        {
            options.LimitToEndpoint = false;
        });

        using var host = builder.Build();

        var client = host.Services.GetRequiredService<CosmosClient>();
        var container = host.Services.GetRequiredService<Container>();

        Assert.NotSame(client, container.Database.Client);
        Assert.Equal(containerName, container.Id);
        Assert.Equal(expectedEndpoint, container.Database.Client.Endpoint.ToString());
        Assert.False(container.Database.Client.ClientOptions.LimitToEndpoint);
    }

    [Fact]
    public void AddKeyedAzureCosmosDatabase_CreatesNewClient_WhenConfigureClientOptionsProvided()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";
        var databaseName = "testdb";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};";

        PopulateConfiguration(builder.Configuration, connectionString, serviceKey);

        builder.AddKeyedAzureCosmosClient(serviceKey);
        builder.AddKeyedAzureCosmosDatabase(serviceKey, configureClientOptions: options =>
        {
            options.LimitToEndpoint = false;
        });

        using var host = builder.Build();

        var client = host.Services.GetRequiredKeyedService<CosmosClient>(serviceKey);
        var database = host.Services.GetRequiredKeyedService<Database>(serviceKey);

        Assert.NotSame(client, database.Client);
        Assert.Equal(databaseName, database.Id);
        Assert.Equal(expectedEndpoint, database.Client.Endpoint.ToString());
        Assert.False(database.Client.ClientOptions.LimitToEndpoint);
    }

    [Fact]
    public void AddKeyedAzureCosmosContainer_CreatesNewClient_WhenConfigureClientOptionsProvided()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";
        var databaseName = "testdb";
        var containerName = "testcontainer";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};Container={containerName};";

        PopulateConfiguration(builder.Configuration, connectionString, serviceKey);

        builder.AddKeyedAzureCosmosClient(serviceKey);
        builder.AddKeyedAzureCosmosContainer(serviceKey, configureClientOptions: options =>
        {
            options.LimitToEndpoint = false;
        });

        using var host = builder.Build();

        var client = host.Services.GetRequiredKeyedService<CosmosClient>(serviceKey);
        var container = host.Services.GetRequiredKeyedService<Container>(serviceKey);

        Assert.NotSame(client, container.Database.Client);
        Assert.Equal(containerName, container.Id);
        Assert.Equal(expectedEndpoint, container.Database.Client.Endpoint.ToString());
        Assert.False(container.Database.Client.ClientOptions.LimitToEndpoint);
    }

    [Fact]
    public void AddAzureCosmosDatabase_NoAddKeyedContainer_AddsDatabase()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};";

        PopulateConfiguration(builder.Configuration, connectionString);

        builder.AddAzureCosmosDatabase("cosmos");

        using var host = builder.Build();
        var database = host.Services.GetService<Database>();
        var client = host.Services.GetService<CosmosClient>();
        var container = host.Services.GetService<Container>();

        Assert.NotNull(database);
        Assert.Equal(databaseName, database.Id);
        Assert.Null(client);
        Assert.Null(container);
    }

    [Fact]
    public void AddAzureCosmosDatabase_AddKeyedContainer_RegistersContainerWithDatabaseKey()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var containerName = "testcontainer";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};";

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:cosmos", connectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:container1", $"{connectionString}Container={containerName};")
        ]);

        var databaseBuilder = builder.AddAzureCosmosDatabase("cosmos");
        databaseBuilder.AddKeyedContainer("container1");

        using var host = builder.Build();

        // Database and client should not be registered
        var database = host.Services.GetService<Database>();
        var client = host.Services.GetService<CosmosClient>();
        Assert.Null(client);

        // Verify that database was registered
        Assert.NotNull(database);
        Assert.Equal(databaseName, database.Id);

        // Verify container was registered with the key correct key
        var container = host.Services.GetRequiredKeyedService<Container>("container1");
        Assert.NotNull(container);
        Assert.Equal(containerName, container.Id);
    }

    [Fact]
    public void AddAzureCosmosDatabase_AddMultipleContainers_RegistersAllWithSameClient()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var container1Name = "container1";
        var container2Name = "container2";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};";

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:cosmos", connectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:container1", $"{connectionString}Container={container1Name};"),
            new KeyValuePair<string, string?>("ConnectionStrings:container2", $"{connectionString}Container={container2Name};")
        ]);

        builder.AddAzureCosmosDatabase("cosmos")
            .AddKeyedContainer("container1")
            .AddKeyedContainer("container2");

        using var host = builder.Build();

        var container1 = host.Services.GetRequiredKeyedService<Container>("container1");
        var container2 = host.Services.GetRequiredKeyedService<Container>("container2");

        // Different containers
        Assert.NotNull(container1);
        Assert.NotNull(container2);
        Assert.Equal(container1Name, container1.Id);
        Assert.Equal(container2Name, container2.Id);

        // With the same client
        Assert.Same(container2.Database.Client, container1.Database.Client);
    }

    [Fact]
    public void AddAzureCosmosDatabase_AddKeyedContainer_ThrowsWhenContainerNameMissing()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var connectionString = $"AccountEndpoint=https://localhost:8081/;AccountKey=fake;Database={databaseName};";

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:cosmos", connectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:container1", connectionString)
        ]);

        builder.AddAzureCosmosDatabase("cosmos")
            .AddKeyedContainer("container1");

        using var host = builder.Build();

        var exception = Assert.Throws<InvalidOperationException>(
            () => host.Services.GetRequiredKeyedService<Container>("container1"));
        Assert.Contains("A Container could not be configured", exception.Message);
    }

    [Fact]
    public void AddAzureCosmosDatabase_AddKeyedContainer_WorksWithNoConnectionString()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var connectionString = $"AccountEndpoint=https://localhost:8081/;AccountKey=fake;Database={databaseName};";

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:cosmos", connectionString)
        ]);

        builder.AddAzureCosmosDatabase("cosmos")
            .AddKeyedContainer("container1");

        using var host = builder.Build();

        var container = host.Services.GetKeyedService<Container>("container1");
        var database = host.Services.GetRequiredService<Database>();

        Assert.NotNull(container);
        Assert.Equal("container1", container.Id);
        Assert.Equal(databaseName, container.Database.Id);
        Assert.Equal("https://localhost:8081/", container.Database.Client.Endpoint.ToString());
    }

    [Fact]
    public void AddAzureCosmosDatabase_AddKeyedContainer_CustomizeClientOptions()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var containerName = "testcontainer";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={databaseName};";

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:cosmos", connectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:container1", $"{connectionString}Container={containerName};")
        ]);

        builder.AddAzureCosmosDatabase("cosmos",
            configureClientOptions: options => {
                options.ApplicationName = "TestApp";
                options.LimitToEndpoint = false;
            })
            .AddKeyedContainer("container1");

        using var host = builder.Build();

        // Verify container has the expected client options
        var container = host.Services.GetRequiredKeyedService<Container>("container1");
        Assert.Contains("TestApp", container.Database.Client.ClientOptions.ApplicationName);
        Assert.False(container.Database.Client.ClientOptions.LimitToEndpoint);
    }

    [Fact]
    public void AddAzureCosmosDatabase_ConfigureSettings_AppliesToAllContainers()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var databaseName = "testdb";
        var container1Name = "container1";
        var container2Name = "container2";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;";

        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:cosmos", connectionString),
            new KeyValuePair<string, string?>("ConnectionStrings:container1", $"{connectionString}Container={container1Name};"),
            new KeyValuePair<string, string?>("ConnectionStrings:container2", $"{connectionString}Container={container2Name};")
        ]);

        var databaseBuilder = builder.AddAzureCosmosDatabase("cosmos",
            configureSettings: settings =>
            {
                // Database name comes from settings, not connection string
                settings.DatabaseName = databaseName;
                settings.DisableTracing = true;
            })
            .AddKeyedContainer("container1")
            .AddKeyedContainer("container2");

        using var host = builder.Build();

        var container1 = host.Services.GetRequiredKeyedService<Container>("container1");
        var container2 = host.Services.GetRequiredKeyedService<Container>("container2");

        Assert.Equal(databaseName, container1.Database.Id);
        Assert.Equal(databaseName, container2.Database.Id);
        Assert.Same(container1.Database.Client, container2.Database.Client);
    }

    [Fact]
    public void AddAzureCosmosDatabase_CalledMultipleTimes_CreatesIndependentBuilders()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var database1Name = "db1";
        var database2Name = "db2";
        var container1Name = "users";
        var container2Name = "orders";
        var container3Name = "products";
        var expectedEndpoint = "https://localhost:8081/";
        var connectionString1 = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={database1Name};";
        var connectionString2 = $"AccountEndpoint={expectedEndpoint};AccountKey=fake;Database={database2Name};";

        builder.Configuration.AddInMemoryCollection([
            // First database connection
            new KeyValuePair<string, string?>("ConnectionStrings:cosmos1", connectionString1),
            new KeyValuePair<string, string?>("ConnectionStrings:users", $"{connectionString1}Container={container1Name};"),
            new KeyValuePair<string, string?>("ConnectionStrings:orders", $"{connectionString1}Container={container2Name};"),

            // Second database connection
            new KeyValuePair<string, string?>("ConnectionStrings:cosmos2", connectionString2),
            new KeyValuePair<string, string?>("ConnectionStrings:products", $"{connectionString2}Container={container3Name};")
        ]);

        // Create two separate database builders
        builder.AddAzureCosmosDatabase("cosmos1")
            .AddKeyedContainer("users")
            .AddKeyedContainer("orders");
        builder.AddAzureCosmosDatabase("cosmos2")
            .AddKeyedContainer("products");

        using var host = builder.Build();

        var usersContainer = host.Services.GetRequiredKeyedService<Container>(container1Name);
        var ordersContainer = host.Services.GetRequiredKeyedService<Container>(container2Name);
        var productsContainer = host.Services.GetRequiredKeyedService<Container>(container3Name);

        Assert.Equal(container1Name, usersContainer.Id);
        Assert.Equal(container2Name, ordersContainer.Id);
        Assert.Equal(container3Name, productsContainer.Id);
        Assert.Equal(database1Name, usersContainer.Database.Id);
        Assert.Equal(database1Name, ordersContainer.Database.Id);
        Assert.Equal(database2Name, productsContainer.Database.Id);

        Assert.Same(usersContainer.Database.Client, ordersContainer.Database.Client);
        Assert.NotSame(usersContainer.Database.Client, productsContainer.Database.Client);
    }

    [Fact]
    public void AddAzureCosmosClient_AddsHealthCheckByDefault()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        PopulateConfiguration(builder.Configuration, "AccountEndpoint=https://localhost:8081;AccountKey=fake;");

        builder.AddAzureCosmosClient("cosmos");

        using var host = builder.Build();

        var registrations = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Contains(registrations, r => r.Name == "Microsoft.Azure.Cosmos");
    }

    [Fact]
    public void AddAzureCosmosClient_DoesNotAddHealthCheck_WhenDisabled()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        PopulateConfiguration(builder.Configuration, "AccountEndpoint=https://localhost:8081;AccountKey=fake;");

        builder.AddAzureCosmosClient("cosmos", configureSettings: settings => settings.DisableHealthChecks = true);

        using var host = builder.Build();

        var registrations = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.DoesNotContain(registrations, r => r.Name == "Microsoft.Azure.Cosmos");
    }

    [Fact]
    public void AddKeyedAzureCosmosClient_AddsHealthCheckWithKeyedName()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serviceKey = "cosmos-key";

        PopulateConfiguration(builder.Configuration, "AccountEndpoint=https://localhost:8081;AccountKey=fake;", serviceKey);

        builder.AddKeyedAzureCosmosClient(serviceKey);

        using var host = builder.Build();

        var registrations = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;
        Assert.Contains(registrations, r => r.Name == $"Microsoft.Azure.Cosmos_{serviceKey}");
    }

    private static void PopulateConfiguration(ConfigurationManager configuration, string connectionString, string? key = null) =>
        configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>($"ConnectionStrings:{key ?? "cosmos"}", connectionString)
        ]);
}
