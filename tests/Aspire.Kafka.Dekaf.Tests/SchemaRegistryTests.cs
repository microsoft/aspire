// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using Dekaf.SchemaRegistry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Kafka.Dekaf.Tests;

public class SchemaRegistryTests
{
    private const string Section = "Aspire:Kafka:Dekaf:SchemaRegistry";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConfigurationPrecedencePreservesNativeOptions(bool keyed, bool connectionString)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration[$"{Section}:Config:Url"] = "https://default.example.com";
        builder.Configuration[$"{Section}:Config:RequestTimeoutMs"] = "1234";
        builder.Configuration[$"{Section}:Config:MaxCachedSchemas"] = "250";
        builder.Configuration[$"{Section}:Config:BasicAuthUserInfo"] = "test-user:test-password";
        builder.Configuration[$"{Section}:registry:Config:Url"] = "https://named.example.com";
        builder.Configuration[$"{Section}:registry:Config:LatestCacheTtlSecs"] = "15";
        builder.Configuration[$"{Section}:registry:HealthCheckTimeout"] = "00:00:02";
        if (connectionString)
        {
            builder.Configuration[$"{Section}:Config:Urls:0"] = "https://failover.example.com";
            builder.Configuration["ConnectionStrings:registry"] = "https://connection.example.com";
        }

        using var handler = new SchemaRegistryTestHandler();
        builder.Services.AddSingleton(handler);
        var calls = 0;
        ISchemaRegistryClient CreateClient(IServiceProvider services, SchemaRegistryConfig config)
        {
            calls++;
            Assert.Equal(connectionString ? "https://connection.example.com" : "https://named.example.com", config.Url);
            Assert.Equal(1234, config.RequestTimeoutMs);
            Assert.Equal(250, config.MaxCachedSchemas);
            Assert.Equal(15, config.LatestCacheTtlSecs);
            Assert.Equal("test-user:test-password", config.BasicAuthUserInfo);
            return new SchemaRegistryClient(config, services.GetRequiredService<SchemaRegistryTestHandler>());
        }
        if (keyed)
        {
            builder.AddKeyedDekafSchemaRegistryClient("registry", clientFactory: CreateClient);
        }
        else
        {
            builder.AddDekafSchemaRegistryClient("registry", clientFactory: CreateClient);
        }

        Assert.Equal(0, calls);
        using var host = builder.Build();
        var client = GetClient(host.Services, keyed);
        Assert.Same(client, GetClient(host.Services, keyed));
        Assert.Equal(1, calls);
        Assert.Empty(await client.GetAllSubjectsAsync(TestContext.Current.CancellationToken));
        var request = Assert.Single(handler.Requests);
        Assert.Equal(connectionString ? "connection.example.com" : "named.example.com", request.Uri.Host);
        Assert.Equal("Basic dGVzdC11c2VyOnRlc3QtcGFzc3dvcmQ=", request.Authorization);
        var registration = Assert.Single(host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
        Assert.Equal(TimeSpan.FromSeconds(2), registration.Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsCallbackOverridesConnectionAndNativeConfiguration(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["ConnectionStrings:registry"] = "https://connection.example.com";
        using var handler = new SchemaRegistryTestHandler();
        void ConfigureSettings(SchemaRegistrySettings settings)
        {
            Assert.Equal("https://connection.example.com", settings.Config.Url);
            settings.Config = new() { Url = "https://callback.example.com", BearerAuthToken = "test-token", NormalizeSchemas = true };
            settings.DisableHealthChecks = true;
        }
        ISchemaRegistryClient CreateClient(IServiceProvider _, SchemaRegistryConfig config)
        {
            Assert.True(config.NormalizeSchemas);
            return new SchemaRegistryClient(config, handler);
        }
        if (keyed)
        {
            builder.AddKeyedDekafSchemaRegistryClient("registry", ConfigureSettings, CreateClient);
        }
        else
        {
            builder.AddDekafSchemaRegistryClient("registry", ConfigureSettings, CreateClient);
        }

        using var host = builder.Build();
        Assert.Null(host.Services.GetService<HealthCheckService>());
        await GetClient(host.Services, keyed).GetAllSubjectsAsync(TestContext.Current.CancellationToken);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("callback.example.com", request.Uri.Host);
        Assert.Equal("Bearer test-token", request.Authorization);
    }

    [Fact]
    public async Task DefaultAndKeyedClientsHaveIndependentHealthAndDisposal()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration[$"{Section}:Config:Url"] = "https://registry.example.com";
        var healthyHandler = new SchemaRegistryTestHandler();
        var unhealthyHandler = new SchemaRegistryTestHandler { StatusCode = HttpStatusCode.Unauthorized };
        builder.AddDekafSchemaRegistryClient("registry", clientFactory: (_, config) => new SchemaRegistryClient(config, () => healthyHandler));
        builder.AddKeyedDekafSchemaRegistryClient("registry", clientFactory: (_, config) => new SchemaRegistryClient(config, () => unhealthyHandler));

        using (var host = builder.Build())
        {
            Assert.NotSame(GetClient(host.Services, false), GetClient(host.Services, true));
            Assert.Same(GetClient(host.Services, true), GetClient(host.Services, true));
            var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, report.Entries.Count);
            Assert.Equal(HealthStatus.Healthy, report.Entries["Kafka.Dekaf_schema_registry"].Status);
            Assert.Equal(HealthStatus.Unhealthy, report.Entries["Kafka.Dekaf_schema_registry_registry"].Status);
            Assert.Single(healthyHandler.Requests);
            Assert.Single(unhealthyHandler.Requests);
        }

        Assert.True(healthyHandler.Disposed);
        Assert.True(unhealthyHandler.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthCheckCancelsUnresponsiveRegistry(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration[$"{Section}:Config:Url"] = "https://registry.example.com";
        builder.Configuration[$"{Section}:HealthCheckTimeout"] = "00:00:00.100";
        using var handler = new SchemaRegistryTestHandler { WaitForCancellation = true };
        if (keyed)
        {
            builder.AddKeyedDekafSchemaRegistryClient("registry", clientFactory: (_, config) => new SchemaRegistryClient(config, handler));
        }
        else
        {
            builder.AddDekafSchemaRegistryClient("registry", clientFactory: (_, config) => new SchemaRegistryClient(config, handler));
        }
        using var host = builder.Build();
        var report = await host.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, Assert.Single(report.Entries).Value.Status);
        Assert.True(handler.CancellationObserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultFactoryCreatesNativeClientAndDeduplicatesHealthChecks(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration["ConnectionStrings:registry"] = "https://registry.example.com";
        for (var i = 0; i < 2; i++)
        {
            if (keyed)
            {
                builder.AddKeyedDekafSchemaRegistryClient("registry");
            }
            else
            {
                builder.AddDekafSchemaRegistryClient("registry");
            }
        }
        using var host = builder.Build();
        Assert.IsType<SchemaRegistryClient>(GetClient(host.Services, keyed));
        Assert.Single(host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingUrlFailsOnResolution(bool keyed)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        if (keyed)
        {
            builder.AddKeyedDekafSchemaRegistryClient("registry");
        }
        else
        {
            builder.AddDekafSchemaRegistryClient("registry");
        }
        using var host = builder.Build();
        Assert.Throws<ArgumentException>(() => GetClient(host.Services, keyed));
    }

    [Fact]
    public async Task RegisteredClientsCacheSchemasAcrossCalls()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        builder.Configuration[$"{Section}:Config:Url"] = "https://registry.example.com";
        using var handler = new SchemaRegistryTestHandler();
        builder.AddDekafSchemaRegistryClient("writer", clientFactory: (_, config) => new SchemaRegistryClient(config, handler));
        builder.AddKeyedDekafSchemaRegistryClient("reader", clientFactory: (_, config) => new SchemaRegistryClient(config, handler));
        using var host = builder.Build();
        var writer = host.Services.GetRequiredService<ISchemaRegistryClient>();
        var reader = host.Services.GetRequiredKeyedService<ISchemaRegistryClient>("reader");
        var schema = new Schema { SchemaString = "\"string\"" };
        var id = await writer.RegisterSchemaAsync("messages-value", schema, TestContext.Current.CancellationToken);
        Assert.Equal(id, await writer.RegisterSchemaAsync("messages-value", schema, TestContext.Current.CancellationToken));
        var fetched = await reader.GetSchemaAsync(id, TestContext.Current.CancellationToken);
        Assert.Equal(schema.SchemaString, fetched.SchemaString);
        Assert.Equal(schema.SchemaType, fetched.SchemaType);
        Assert.Same(fetched, await reader.GetSchemaAsync(id, TestContext.Current.CancellationToken));
        Assert.Collection(handler.Requests,
            request => Assert.Equal(HttpMethod.Post, request.Method),
            request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryUrlsSupportFailover(bool connectionString)
    {
        var builder = ClientTestHelpers.CreateBuilder();
        if (connectionString)
        {
            builder.Configuration["ConnectionStrings:registry"] = "https://primary.example.com,https://secondary.example.com";
        }
        else
        {
            builder.Configuration[$"{Section}:Config:Url"] = "https://unused.example.com";
            builder.Configuration[$"{Section}:Config:Urls:0"] = "https://primary.example.com";
            builder.Configuration[$"{Section}:Config:Urls:1"] = "https://secondary.example.com";
        }
        using var handler = new SchemaRegistryTestHandler { UnavailableHost = "primary.example.com" };
        builder.AddDekafSchemaRegistryClient("registry", clientFactory: (_, config) => new SchemaRegistryClient(config, handler));
        using var host = builder.Build();
        var client = host.Services.GetRequiredService<ISchemaRegistryClient>();
        Assert.Empty(await client.GetAllSubjectsAsync(TestContext.Current.CancellationToken));
        Assert.Collection(handler.Requests,
            request => Assert.Equal("primary.example.com", request.Uri.Host),
            request => Assert.Equal("secondary.example.com", request.Uri.Host));
    }

    [Fact]
    public void RegistrationValidatesArguments()
    {
        var builder = ClientTestHelpers.CreateBuilder();
        Assert.Throws<ArgumentNullException>("builder", () => AspireDekafSchemaRegistryExtensions.AddDekafSchemaRegistryClient(null!, "registry"));
        Assert.Throws<ArgumentNullException>("builder", () => AspireDekafSchemaRegistryExtensions.AddKeyedDekafSchemaRegistryClient(null!, "registry"));
        Assert.Throws<ArgumentNullException>("connectionName", () => builder.AddDekafSchemaRegistryClient(null!));
        Assert.Throws<ArgumentException>("connectionName", () => builder.AddDekafSchemaRegistryClient(""));
        Assert.Throws<ArgumentNullException>("name", () => builder.AddKeyedDekafSchemaRegistryClient(null!));
        Assert.Throws<ArgumentException>("name", () => builder.AddKeyedDekafSchemaRegistryClient(""));
    }

    private static ISchemaRegistryClient GetClient(IServiceProvider services, bool keyed)
        => keyed ? services.GetRequiredKeyedService<ISchemaRegistryClient>("registry") : services.GetRequiredService<ISchemaRegistryClient>();
}
