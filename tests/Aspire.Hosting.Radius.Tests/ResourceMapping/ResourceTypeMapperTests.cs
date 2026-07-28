// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.ResourceMapping;
using Microsoft.Extensions.Logging;

#pragma warning disable CS0618 // Tests intentionally verify legacy fallback behavior

namespace Aspire.Hosting.Radius.Tests.ResourceMapping;

public class ResourceTypeMapperTests
{
    private readonly ResourceTypeMapper _mapper;
    private readonly FakeLogger _logger;

    public ResourceTypeMapperTests()
    {
        _logger = new FakeLogger();
        _mapper = new ResourceTypeMapper(_logger);
    }

    [Fact]
    public void RedisResource_MapsToLegacyFallback_LogsLegacyMapping()
    {
        var resource = new RedisResource("cache");

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.LegacyRedisCaches, type);
        Assert.Equal(RadiusResourceTypes.LegacyApiVersion, apiVersion);
        Assert.Contains("legacy", _logger.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServerResource_MapsToRadiusDataSqlDatabases()
    {
        var password = new ParameterResource("password", _ => "p@ss", secret: true);
        var resource = new SqlServerServerResource("sqldb", password);

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.SqlDatabases, type);
        Assert.Equal(RadiusResourceTypes.RadiusApiVersion, apiVersion);
    }

    [Fact]
    public void MongoDBResource_MapsToLegacyFallback_LogsLegacyMapping()
    {
        var resource = new MongoDBServerResource("mongo");

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.LegacyMongoDatabases, type);
        Assert.Equal(RadiusResourceTypes.LegacyApiVersion, apiVersion);
        Assert.Contains("legacy", _logger.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RabbitMQResource_MapsToLegacyFallback_LogsLegacyMapping()
    {
        var password = new ParameterResource("password", _ => "guest", secret: true);
        var resource = new RabbitMQServerResource("rabbit", null, password);

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.LegacyRabbitMQQueues, type);
        Assert.Equal(RadiusResourceTypes.LegacyApiVersion, apiVersion);
        Assert.Contains("legacy", _logger.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ContainerResource_MapsToRadiusComputeContainers()
    {
        var resource = new ContainerResource("api");

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.Containers, type);
        Assert.Equal(RadiusResourceTypes.RadiusApiVersion, apiVersion);
    }

    [Fact]
    public void ProjectResource_MapsToRadiusComputeContainers()
    {
        var resource = new ProjectResource("webapp");

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.Containers, type);
        Assert.Equal(RadiusResourceTypes.RadiusApiVersion, apiVersion);
    }

    [Fact]
    public void PostgresResource_MapsToRadiusDataPostgreSqlDatabases()
    {
        var password = new ParameterResource("password", _ => "secret", secret: true);
        var resource = new PostgresServerResource("pgdb", null, password);

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.PostgreSqlDatabases, type);
        Assert.Equal(RadiusResourceTypes.RadiusApiVersion, apiVersion);
    }

    [Fact]
    public void UnmappedResource_FallsBackToContainers_WithWarning()
    {
        var resource = new CustomUnmappedResource("custom-svc");

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.Containers, type);
        Assert.Equal(RadiusResourceTypes.RadiusApiVersion, apiVersion);
        Assert.Contains("no Radius mapping", _logger.LastMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("falling back", _logger.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SqlServerResource_NoLegacyFallback_NoWarning()
    {
        var password = new ParameterResource("password", _ => "p@ss", secret: true);
        var resource = new SqlServerServerResource("sqldb", password);

        _mapper.MapResource(resource);

        Assert.Null(_logger.LastMessage);
    }

    [Fact]
    public void ContainerResource_NoWarning()
    {
        var resource = new ContainerResource("api");

        _mapper.MapResource(resource);

        Assert.Null(_logger.LastMessage);
    }

    [Fact]
    public void DaprStateStoreResource_MapsToLegacyFallback_LogsLegacyMapping()
    {
        var resource = new DaprStateStoreResource("statestore");

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.LegacyDaprStateStores, type);
        Assert.Equal(RadiusResourceTypes.LegacyApiVersion, apiVersion);
        Assert.Contains("legacy", _logger.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DaprPubSubResource_MapsToLegacyFallback_LogsLegacyMapping()
    {
        var resource = new DaprPubSubResource("pubsub");

        var (type, apiVersion) = _mapper.MapResource(resource);

        Assert.Equal(RadiusResourceTypes.LegacyDaprPubSubBrokers, type);
        Assert.Equal(RadiusResourceTypes.LegacyApiVersion, apiVersion);
        Assert.Contains("legacy", _logger.LastMessage, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A custom resource type that does not have a Radius mapping, used to test fallback behavior.
    /// </summary>
    private sealed class CustomUnmappedResource(string name) : Resource(name)
    {
    }

    /// <summary>
    /// Simulates DaprStateStoreResource for testing type mapping (Dapr hosting package not referenced).
    /// The mapper matches on type name, so this class name must match exactly.
    /// </summary>
    private sealed class DaprStateStoreResource(string name) : Resource(name)
    {
    }

    /// <summary>
    /// Simulates DaprPubSubResource for testing type mapping.
    /// </summary>
    private sealed class DaprPubSubResource(string name) : Resource(name)
    {
    }

    /// <summary>
    /// Simple fake logger that captures the last log message at any level for assertion.
    /// </summary>
    private sealed class FakeLogger : ILogger<ResourceTypeMapper>
    {
        public string? LastMessage { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Capture every log entry: ResourceTypeMapper deliberately emits the legacy-fallback
            // message at Information rather than Warning (legacy is still a supported v1 mapping,
            // not a problem), so a Warning-only filter would miss the message under test.
            LastMessage = formatter(state, exception);
        }
    }
}
