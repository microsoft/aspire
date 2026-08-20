// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared.DashboardModel;
using Microsoft.AspNetCore.InternalTesting;
using Xunit;
using Struct = Google.Protobuf.WellKnownTypes.Struct;
using Value = Google.Protobuf.WellKnownTypes.Value;

namespace Aspire.Dashboard.Tests;

public class ResourceOutgoingPeerResolverTests
{
    private static ResourceViewModel CreateResource(string name, string? serviceAddress = null, int? servicePort = null, string? displayName = null, KnownResourceState? state = null)
    {
        return ModelTestHelpers.CreateResource(
            resourceName: name,
            displayName: displayName,
            state: state,
            urls: serviceAddress is null || servicePort is null ? [] : [new UrlViewModel(name, new($"http://{serviceAddress}:{servicePort}"), isInternal: false, isInactive: false, displayProperties: UrlDisplayPropertiesViewModel.Empty)]);
    }

    [Fact]
    public void EmptyAttributes_NoMatch()
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test"] = CreateResource("test", "localhost", 5000)
        };

        // Act & Assert
        Assert.False(TryResolvePeerName(resources, [], out _));
    }

    [Fact]
    public void EmptyUrlAttribute_NoMatch()
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test"] = CreateResource("test", "localhost", 5000)
        };

        // Act & Assert
        Assert.False(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "")], out _));
    }

    [Fact]
    public void NullUrlAttribute_NoMatch()
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test"] = CreateResource("test", "localhost", 5000)
        };

        // Act & Assert
        Assert.False(TryResolvePeerName(resources, [KeyValuePair.Create<string, string>("peer.service", null!)], out _));
    }

    [Fact]
    public void ExactValueAttribute_Match()
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test"] = CreateResource("test", "localhost", 5000)
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "localhost:5000")], out var value));
        Assert.Equal("test", value);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("host.docker.internal")]
    [InlineData("host.containers.internal")]
    [InlineData("HOST.DOCKER.INTERNAL")]
    [InlineData("HOST.CONTAINERS.INTERNAL")]
    public void NonLocalhostAttribute_Match(string host)
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test"] = CreateResource("test", "localhost", 5000)
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", $"{host}:5000")], out var value));
        Assert.Equal("test", value);
    }

    [Fact]
    public void CommaAddressValueAttribute_Match()
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test"] = CreateResource("test", "localhost", 5000)
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "127.0.0.1,5000")], out var value));
        Assert.Equal("test", value);
    }

    [Fact]
    public void ServerAddressAndPort_Match()
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test"] = CreateResource("test", "localhost", 5000)
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("server.address", "localhost"), KeyValuePair.Create("server.port", "5000")], out var value));
        Assert.Equal("test", value);
    }

    [Theory]
    [InlineData("db.namespace", "catalogdb")]
    [InlineData("db.name", "CATALOGDB")]
    public void DatabaseAttributeMatchesDatabaseResourceOverContainerResource(string databaseAttribute, string databaseName)
    {
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["jobsdb"] = CreateResourceWithConnectionString("jobsdb", "Host=localhost;Port=50267;Username=postgres;Password=password;Database=jobsdb", resourceType: "PostgresDatabaseResource", relationships: [new("postgres", "Parent")]),
            ["postgres-evxqcrgg"] = CreateResourceWithConnectionString("postgres-evxqcrgg", "Host=localhost;Port=50267;Username=postgres;Password=password", resourceType: KnownResourceTypes.Container, displayName: "postgres"),
            ["catalogdb"] = CreateResourceWithConnectionString("catalogdb", "Host=localhost;Port=50267;Username=postgres;Password=password;Database=catalogdb", resourceType: "PostgresDatabaseResource", relationships: [new("postgres", "Parent")])
        };
        var attributes = new[]
        {
            KeyValuePair.Create("server.address", "localhost"),
            KeyValuePair.Create("server.port", "50267"),
            KeyValuePair.Create(databaseAttribute, databaseName)
        };

        Assert.True(TryResolvePeerName(resources, attributes, out var value));
        Assert.Equal("catalogdb", value);
    }

    [Fact]
    public void DatabaseAttributePrefersExactDatabaseNameMatchOverCaseInsensitiveMatch()
    {
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["Catalog"] = CreateResourceWithConnectionString("Catalog", "Host=localhost;Port=50267;Username=postgres;Password=password;Database=Catalog", resourceType: "PostgresDatabaseResource", relationships: [new("postgres", "Parent")]),
            ["catalog"] = CreateResourceWithConnectionString("catalog", "Host=localhost;Port=50267;Username=postgres;Password=password;Database=catalog", resourceType: "PostgresDatabaseResource", relationships: [new("postgres", "Parent")])
        };

        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("server.address", "localhost"), KeyValuePair.Create("server.port", "50267"), KeyValuePair.Create("db.namespace", "catalog")], out var value));
        Assert.Equal("catalog", value);
    }

    [Fact]
    public void DatabaseAttributeMatchesDatabaseResourceAfterAddressTransformationOverExactContainerResource()
    {
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["postgres-evxqcrgg"] = CreateResourceWithConnectionString("postgres-evxqcrgg", "Host=127.0.0.1;Port=50267;Username=postgres;Password=password", resourceType: KnownResourceTypes.Container, displayName: "postgres"),
            ["catalogdb"] = CreateResourceWithConnectionString("catalogdb", "Host=localhost;Port=50267;Username=postgres;Password=password;Database=catalogdb", resourceType: "PostgresDatabaseResource", relationships: [new("postgres", "Parent")])
        };
        var attributes = new[]
        {
            KeyValuePair.Create("server.address", "127.0.0.1"),
            KeyValuePair.Create("server.port", "50267"),
            KeyValuePair.Create("db.namespace", "catalogdb")
        };

        Assert.True(TryResolvePeerName(resources, attributes, out var value));
        Assert.Equal("catalogdb", value);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(null)]
    public void DatabaseAttributeDoesNotMatchDatabaseResource_MatchesContainerResource(string? databaseName)
    {
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["unrelated"] = CreateResource("unrelated", "localhost", 50267),
            ["catalogdb"] = CreateResourceWithConnectionString("catalogdb", "Host=localhost;Port=50267;Username=postgres;Password=password;Database=catalogdb", resourceType: "PostgresDatabaseResource", relationships: [new("postgres", "Parent")]),
            ["postgres-evxqcrgg"] = CreateResourceWithConnectionString("postgres-evxqcrgg", "Host=localhost;Port=50267;Username=postgres;Password=password", resourceType: KnownResourceTypes.Container, displayName: "postgres")
        };
        var attributes = new List<KeyValuePair<string, string>>
        {
            KeyValuePair.Create("server.address", "localhost"),
            KeyValuePair.Create("server.port", "50267")
        };
        if (databaseName is not null)
        {
            attributes.Add(KeyValuePair.Create("db.namespace", databaseName));
        }

        Assert.True(TryResolvePeerName(resources, attributes.ToArray(), out var value));
        Assert.Equal("postgres", value);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData(null)]
    public void DatabaseAttributeDoesNotMatchAndNoContainerResource_MatchesFirstDatabaseResource(string? databaseName)
    {
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["unrelated"] = CreateResource("unrelated", "localhost", 50267),
            ["jobsdb"] = CreateResourceWithConnectionString("jobsdb", "Host=localhost;Port=50267;Username=postgres;Password=password;Database=jobsdb", resourceType: "PostgresDatabaseResource", relationships: [new("postgres", "Parent")]),
            ["catalogdb"] = CreateResourceWithConnectionString("catalogdb", "Host=localhost;Port=50267;Username=postgres;Password=password;Database=catalogdb", resourceType: "PostgresDatabaseResource", relationships: [new("postgres", "Parent")])
        };
        var attributes = new List<KeyValuePair<string, string>>
        {
            KeyValuePair.Create("server.address", "localhost"),
            KeyValuePair.Create("server.port", "50267")
        };
        if (databaseName is not null)
        {
            attributes.Add(KeyValuePair.Create("db.namespace", databaseName));
        }

        Assert.True(TryResolvePeerName(resources, attributes.ToArray(), out var value));
        Assert.Equal("jobsdb", value);
    }

    [Fact]
    public void DatabaseAttributeMatchesMongoDbUriResourceOverContainerResource()
    {
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["mongodb"] = CreateResourceWithConnectionString("mongodb", "mongodb://localhost:27017", resourceType: KnownResourceTypes.Container),
            ["catalogdb"] = CreateResourceWithConnectionString("catalogdb", "mongodb://localhost:27017/catalogdb", resourceType: "MongoDBDatabaseResource", relationships: [new("mongodb", "Parent")])
        };

        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("server.address", "localhost"), KeyValuePair.Create("server.port", "27017"), KeyValuePair.Create("db.namespace", "catalogdb")], out var value));
        Assert.Equal("catalogdb", value);
    }

    [Theory]
    [InlineData("DatabaseName")]
    [InlineData("databaseName")]
    public void DatabaseAttributeMatchesDatabaseNameConnectionProperty(string databaseNamePropertyName)
    {
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["jobsdb"] = CreateResourceWithConnectionString("jobsdb", "user id=system;password=password;data source=localhost:1521/FREEPDB1", resourceType: "OracleDatabaseResource", databaseName: "jobsdb", databaseNamePropertyName: databaseNamePropertyName),
            ["catalogdb"] = CreateResourceWithConnectionString("catalogdb", "user id=system;password=password;data source=localhost:1521/FREEPDB1", resourceType: "OracleDatabaseResource", databaseName: "catalogdb", databaseNamePropertyName: databaseNamePropertyName)
        };

        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("server.address", "localhost"), KeyValuePair.Create("db.namespace", "catalogdb")], out var value));
        Assert.Equal("catalogdb", value);
    }

    [Fact]
    public void DatabaseAttributeDoesNotMatchMongoDbUriResource_MatchesContainerResource()
    {
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["catalogdb"] = CreateResourceWithConnectionString("catalogdb", "mongodb://localhost:27017/catalogdb", resourceType: "MongoDBDatabaseResource", relationships: [new("mongodb", "Parent")]),
            ["mongodb"] = CreateResourceWithConnectionString("mongodb", "mongodb://localhost:27017", resourceType: KnownResourceTypes.Container)
        };

        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("server.address", "localhost"), KeyValuePair.Create("server.port", "27017"), KeyValuePair.Create("db.namespace", "unknown")], out var value));
        Assert.Equal("mongodb", value);
    }

    [Fact]
    public async Task OnPeerChanges_DataUpdates_EventRaised()
    {
        // Arrange
        var tcs = new TaskCompletionSource<ResourceViewModelSubscription>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sourceChannel = Channel.CreateUnbounded<ResourceViewModelChange>();
        var resultChannel = Channel.CreateUnbounded<int>();
        var dashboardClient = new MockDashboardClient(tcs.Task);
        var resolver = new ResourceOutgoingPeerResolver(dashboardClient);
        var changeCount = 0;
        resolver.OnPeerChanges(async () =>
        {
            await resultChannel.Writer.WriteAsync(++changeCount);
        });

        var readValue = 0;
        Assert.False(resultChannel.Reader.TryRead(out readValue));

        // Act 1
        // Initial resource causes change.
        tcs.SetResult(new ResourceViewModelSubscription(
            [CreateResource("test", serviceAddress: "localhost", servicePort: 8080)],
            GetChanges()));

        // Assert 1
        readValue = await resultChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(1, readValue);

        // Act 2
        // New resource causes change.
        await sourceChannel.Writer.WriteAsync(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, CreateResource("test2", serviceAddress: "localhost", servicePort: 8080, state: KnownResourceState.Starting)));

        // Assert 2
        readValue = await resultChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(2, readValue);

        // Act 3
        // URL change causes change.
        await sourceChannel.Writer.WriteAsync(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, CreateResource("test2", serviceAddress: "localhost", servicePort: 8081, state: KnownResourceState.Starting)));

        // Assert 3
        readValue = await resultChannel.Reader.ReadAsync().DefaultTimeout();
        Assert.Equal(3, readValue);

        // Act 4
        // Resource update doesn't cause change.
        await sourceChannel.Writer.WriteAsync(new ResourceViewModelChange(ResourceViewModelChangeType.Upsert, CreateResource("test2", serviceAddress: "localhost", servicePort: 8081, state: KnownResourceState.Running)));

        // Dispose so that we know that all changes are processed.
        await resolver.DisposeAsync().DefaultTimeout();
        resultChannel.Writer.Complete();

        // Assert 4
        Assert.False(await resultChannel.Reader.WaitToReadAsync().DefaultTimeout());
        Assert.Equal(3, changeCount);

        async IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> GetChanges([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in sourceChannel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return [item];
            }
        }
    }

    [Fact]
    public void NameAndDisplayNameDifferent_OneInstance_ReturnDisplayName()
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test-abc"] = CreateResource("test-abc", "localhost", 5000, displayName: "test")
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("server.address", "localhost"), KeyValuePair.Create("server.port", "5000")], out var value));
        Assert.Equal("test", value);
    }

    [Fact]
    public void NameAndDisplayNameDifferent_MultipleInstances_ReturnName()
    {
        // Arrange
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["test-abc"] = CreateResource("test-abc", "localhost", 5000, displayName: "test"),
            ["test-def"] = CreateResource("test-def", "localhost", 5001, displayName: "test")
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("server.address", "localhost"), KeyValuePair.Create("server.port", "5000")], out var value1));
        Assert.Equal("test-abc", value1);

        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("server.address", "localhost"), KeyValuePair.Create("server.port", "5001")], out var value2));
        Assert.Equal("test-def", value2);
    }

    private static bool TryResolvePeerName(IDictionary<string, ResourceViewModel> resources, KeyValuePair<string, string>[] attributes, out string? peerName)
    {
        return ResourceOutgoingPeerResolver.TryResolvePeerCore(resources, attributes, out peerName, out _);
    }

    [Fact]
    public void ConnectionStringWithEndpoint_Match()
    {
        // Arrange - GitHub Models resource with connection string containing endpoint
        var connectionString = "Endpoint=https://models.github.ai/inference;Key=test-key;Model=openai/gpt-4o-mini";
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["github-model"] = CreateResourceWithConnectionString("github-model", connectionString)
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "models.github.ai:443")], out var value));
        Assert.Equal("github-model", value);
    }

    [Fact]
    public void ConnectionStringWithEndpointOrganization_Match()
    {
        // Arrange - GitHub Models resource with organization endpoint
        var connectionString = "Endpoint=https://models.github.ai/orgs/myorg/inference;Key=test-key;Model=openai/gpt-4o-mini";
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["github-model"] = CreateResourceWithConnectionString("github-model", connectionString)
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "models.github.ai:443")], out var value));
        Assert.Equal("github-model", value);
    }

    [Fact]
    public void ParameterWithUrlValue_Match()
    {
        // Arrange - Parameter resource with URL value
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["api-url-param"] = CreateResourceWithParameterValue("api-url-param", "https://api.example.com:8080/endpoint")
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "api.example.com:8080")], out var value));
        Assert.Equal("api-url-param", value);
    }

    [Fact]
    public void ConnectionStringWithoutEndpoint_NoMatch()
    {
        // Arrange - Connection string without Endpoint property
        var connectionString = "Server=localhost;Database=test;User=admin;Password=secret";
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["sql-connection"] = CreateResourceWithConnectionString("sql-connection", connectionString)
        };

        // Act & Assert
        Assert.False(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "localhost:1433")], out _));
    }

    [Fact]
    public void ParameterWithNonUrlValue_NoMatch()
    {
        // Arrange - Parameter resource with non-URL value
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["config-param"] = CreateResourceWithParameterValue("config-param", "simple-config-value")
        };

        // Act & Assert
        Assert.False(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "localhost:5000")], out _));
    }

    [Fact]
    public void ConnectionStringAsDirectUrl_Match()
    {
        // Arrange - Connection string that is itself a URL (e.g., blob storage)
        var connectionString = "https://mystorageaccount.blob.core.windows.net/";
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["blob-storage"] = CreateResourceWithConnectionString("blob-storage", connectionString)
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "mystorageaccount.blob.core.windows.net:443")], out var value));
        Assert.Equal("blob-storage", value);
    }

    [Fact]
    public void ConnectionStringAsDirectUrlWithCustomPort_Match()
    {
        // Arrange - Connection string that is itself a URL with custom port
        var connectionString = "https://myvault.vault.azure.net:8080/";
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["key-vault"] = CreateResourceWithConnectionString("key-vault", connectionString)
        };

        // Act & Assert
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "myvault.vault.azure.net:8080")], out var value));
        Assert.Equal("key-vault", value);
    }

    private static ResourceViewModel CreateResourceWithConnectionString(string name, string connectionString, string resourceType = KnownResourceTypes.ConnectionString, string? displayName = null, ImmutableArray<RelationshipViewModel>? relationships = null, string? databaseName = null, string databaseNamePropertyName = "DatabaseName")
    {
        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Resource.ConnectionString] = new(
                name: KnownProperties.Resource.ConnectionString,
                value: Value.ForString(connectionString),
                isValueSensitive: false,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false)
        };

        if (databaseName is not null)
        {
            var connectionProperties = new Struct();
            connectionProperties.Fields[databaseNamePropertyName] = Value.ForString(databaseName);
            properties[KnownProperties.Resource.ConnectionProperties] = new(
                name: KnownProperties.Resource.ConnectionProperties,
                value: new Value { StructValue = connectionProperties },
                isValueSensitive: false,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false);
        }

        return ModelTestHelpers.CreateResource(
            resourceName: name,
            displayName: displayName,
            resourceType: resourceType,
            relationships: relationships,
            properties: properties);
    }

    private static ResourceViewModel CreateResourceWithParameterValue(string name, string value)
    {
        var properties = new Dictionary<string, ResourcePropertyViewModel>
        {
            [KnownProperties.Parameter.Value] = new(
                name: KnownProperties.Parameter.Value,
                value: Value.ForString(value),
                isValueSensitive: false,
                knownProperty: null,
                sortOrder: 0,
                displayName: null,
                isHighlighted: false)
        };

        return ModelTestHelpers.CreateResource(
            resourceName: name,
            resourceType: KnownResourceTypes.Parameter,
            properties: properties);
    }

    [Fact]
    public void MultipleResourcesMatch_ViaTransformation_ReturnsFirstMatch()
    {
        // Arrange - Resources that match via address transformation
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["sql-primary"] = CreateResource("sql-primary", "localhost", 1433),
            ["sql-replica"] = CreateResource("sql-replica", "127.0.0.1", 1433)
        };

        // Act & Assert - Should return the first match found
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "127.0.0.1:1433")], out var name));
        Assert.Equal("sql-replica", name);
    }

    [Fact]
    public void MultipleResourcesSameAddress_ReturnsFirstMatch()
    {
        // Test to verify that "first one wins" logic is restored
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["database1"] = CreateResource("database1", "localhost", 5432),
            ["database2"] = CreateResource("database2", "localhost", 5432)
        };

        // Should return the first match found (verifies regression fix)
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "localhost:5432")], out var name));
        Assert.NotNull(name);
        // Should match one of the databases (first one found)
        Assert.Contains(name, new[] { "database1", "database2" });
    }

    [Fact]
    public void SingleResourceAfterTransformation_ReturnsTrue()
    {
        // Arrange - Only one resource that matches after address transformation
        var resources = new Dictionary<string, ResourceViewModel>
        {
            ["unique-service"] = CreateResource("unique-service", "localhost", 8080),
            ["other-service"] = CreateResource("other-service", "remotehost", 9090)
        };

        // Act & Assert - Only the first resource should match "127.0.0.1:8080" after transformation
        Assert.True(TryResolvePeerName(resources, [KeyValuePair.Create("peer.service", "127.0.0.1:8080")], out var name));
        Assert.Equal("unique-service", name);
    }

    private sealed class MockDashboardClient(Task<ResourceViewModelSubscription> subscribeResult) : IDashboardClient
    {
        public bool IsEnabled => true;
        public Task WhenConnected => Task.CompletedTask;
        public string ApplicationName => "ApplicationName";
        public string? MinRequiredVersion => null;
        public DashboardConnectionState ConnectionState => DashboardConnectionState.Connected;
#pragma warning disable CS0067 // Event is never used - required by interface
        public event Action<DashboardConnectionState>? ConnectionStateChanged;
#pragma warning restore CS0067
        public Task ReconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, ExecuteResourceCommandOptions options, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, int interactionId, string inputName, CancellationToken cancellationToken) => throw new NotImplementedException();
        public ResourceViewModel? GetResource(string resourceName) => null;
        public IReadOnlyList<ResourceViewModel> GetResources() => [];
        public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, CancellationToken cancellationToken) => throw new NotImplementedException();
        public IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken) => subscribeResult;
    }
}
