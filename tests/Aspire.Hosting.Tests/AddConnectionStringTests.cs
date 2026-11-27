// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AddConnectionStringTests
{
    [Theory]
    [InlineData("expression")]
    [InlineData("parameter")]
    [InlineData("database")]
    [InlineData("postgres-redirect")]
    [InlineData("redis-redirect")]
    [InlineData("third-party-value-provider")]
    [InlineData("third-party-connection-string")]
    public async Task ContextualConnectionStringResolutionTracksTheRequestingResource(string source)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parameter = source == "parameter"
            ? Assert.IsAssignableFrom<ParameterResource>(builder.AddConnectionString("password").Resource)
            : builder.AddParameter("password", secret: true).Resource;
        parameter.EnsureValueTask();
        IResourceWithConnectionString resource;
        if (source == "database")
        {
            var server = builder.AddPostgres("postgres", password: builder.CreateResourceBuilder(parameter))
                .WithEndpoint("tcp", e => e.AllocatedEndpoint = new(e, "localhost", 2000));
            resource = server.AddDatabase("database").Resource;
        }
        else if (source == "parameter")
        {
            resource = (IResourceWithConnectionString)parameter;
        }
        else if (source is "third-party-value-provider" or "third-party-connection-string")
        {
            resource = builder.AddResource(new ResourceWithoutLifetime("third-party")
            {
                ConnectionStringExpression = ReferenceExpression.Create($"Password={parameter}")
            }).Resource;
        }
        else
        {
            var connection = builder.AddConnectionString("connection", ReferenceExpression.Create($"Password={parameter}"));
            resource = source switch
            {
                "postgres-redirect" => builder.AddPostgres("postgres")
                    .WithAnnotation(new ConnectionStringRedirectAnnotation(connection.Resource)).Resource,
                "redis-redirect" => builder.AddRedis("redis")
                    .WithAnnotation(new ConnectionStringRedirectAnnotation(connection.Resource)).Resource,
                _ => connection.Resource
            };
        }
        var caller = builder.AddExecutable("caller", "unused", ".").Resource;
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var context = new ValueProviderContext
        {
            ExecutionContext = builder.ExecutionContext,
            Caller = caller,
            Network = KnownNetworkIdentifiers.LocalhostNetwork
        };
        var read = source == "third-party-value-provider"
            ? resource.GetValueAsync(context, cts.Token).AsTask()
            : resource.GetConnectionStringAsync(context, cts.Token).AsTask();

        Assert.False(read.IsCompleted);
        Assert.True(app.ResourceNotifications.TryGetCurrentState(caller.Name, out var waiting));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, waiting.Snapshot.State?.Text);
        var unresolved = Assert.Single(waiting.Snapshot.Properties);
        Assert.Equal(KnownProperties.Resource.UnresolvedParameters, unresolved.Name);
        Assert.Equal(new[] { parameter.Name }, Assert.IsType<string[]>(unresolved.Value));
        Assert.False(app.ResourceNotifications.TryGetCurrentState(resource.Name, out _));

        await parameter.SetValueAsync("password-value");
        Assert.Equal(await resource.GetConnectionStringAsync(cts.Token), await read.DefaultTimeout());
        Assert.True(app.ResourceNotifications.TryGetCurrentState(caller.Name, out var completed));
        Assert.Null(completed.Snapshot.State);
        Assert.Empty(completed.Snapshot.Properties);
    }

    [Theory]
    [InlineData("connection-string")]
    [InlineData("value-provider")]
    [InlineData("reference")]
    public async Task ContextualResolutionPreservesThirdPartyExpressionNetwork(string source)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var server = builder.AddContainer("server", "test-image")
            .WithHttpEndpoint(targetPort: 8080);
        var endpoint = server.GetEndpoint("http");
        var annotation = endpoint.EndpointAnnotation;
        annotation.AllocatedEndpoint = new(annotation, "localhost", 18080);
        annotation.AllAllocatedEndpoints.AddOrUpdateAllocatedEndpoint(
            KnownNetworkIdentifiers.DefaultAspireContainerNetwork,
            new(annotation, "server", 8080, EndpointBindingMode.SingleAddress, networkId: KnownNetworkIdentifiers.DefaultAspireContainerNetwork));
        IResourceWithConnectionString resource = builder.AddResource(new ResourceWithoutLifetime("third-party")
        {
            ConnectionStringExpression = ReferenceExpression.Create($"Endpoint={endpoint}")
        }).Resource;
        var caller = builder.AddContainer("caller", "test-image").Resource;
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var context = new ValueProviderContext
        {
            ExecutionContext = builder.ExecutionContext,
            Caller = caller,
            Network = KnownNetworkIdentifiers.DefaultAspireContainerNetwork
        };
        IValueProvider provider = source == "reference" ? new ConnectionStringReference(resource, optional: false) : resource;
        var value = source == "connection-string"
            ? await resource.GetConnectionStringAsync(context, cts.Token)
            : await provider.GetValueAsync(context, cts.Token);

        Assert.Equal("Endpoint=http://server:8080", value);
        Assert.Equal("Endpoint=http://localhost:18080", await resource.GetValueAsync(cts.Token));
    }

    [Theory]
    [InlineData("public")]
    [InlineData("explicit")]
    [InlineData("inherited")]
    [InlineData("derived-interface")]
    public async Task ContextualValueProviderUsesImplementedConnectionStringResolver(string implementation)
    {
        IResourceWithConnectionString resource = implementation switch
        {
            "public" => new ContextualResource("connection"),
            "explicit" => new ExplicitContextualResource("connection"),
            "inherited" => new InheritedContextualResource("connection"),
            _ => new DerivedInterfaceContextualResource("connection")
        };
        var caller = new ContainerResource("caller");

        var value = await resource.GetValueAsync(new ValueProviderContext { Caller = caller }, CancellationToken.None);

        Assert.Equal(caller.Name, value);
    }

    [Theory]
    [InlineData("public")]
    [InlineData("explicit")]
    [InlineData("inherited")]
    [InlineData("derived-interface")]
    public async Task ContextualConnectionStringDefaultPreservesLegacyResolver(string implementation)
    {
        IResourceWithConnectionString resource = implementation switch
        {
            "public" => new LegacyResource("connection"),
            "explicit" => new ExplicitLegacyResource("connection"),
            "inherited" => new InheritedLegacyResource("connection"),
            _ => new DerivedInterfaceLegacyResource("connection")
        };
        var context = new ValueProviderContext { Caller = new ContainerResource("caller") };

        Assert.Equal("legacy", await resource.GetConnectionStringAsync(default));
        Assert.Equal("legacy", await resource.GetConnectionStringAsync(context, TestContext.Current.CancellationToken));
        Assert.Equal("ResourceWithoutLifetime", await resource.GetValueAsync(context, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionConfigurationUsesConnectionStringExpression(bool secret)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var value = new ParameterResource("parameter", _ => "expression-value", secret);
        var resource = new ContextualResource("connection")
        {
            ConnectionStringExpression = ReferenceExpression.Create($"{value}"),
            Resolver = (_, _) => new("custom-value")
        };
        var source = builder.AddResource(resource);
        var caller = builder.AddContainer("caller", "test-image")
            .WithReference(source)
            .WithEnvironment("CUSTOM_CONNECTION", source)
            .WithEnvironment(context => context.EnvironmentVariables["RAW_RESOURCE"] = resource)
            .WithArgs(resource, ReferenceExpression.Create($"wrapped:{resource}"));
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();

        var configuration = await ExecutionConfigurationBuilder.Create(caller.Resource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, cts.Token);

        Assert.Null(configuration.Exception);
        Assert.Collection(configuration.EnvironmentVariables.OrderBy(entry => entry.Key, StringComparer.Ordinal),
            entry => Assert.Equal(new("CUSTOM_CONNECTION", "expression-value"), entry),
            entry => Assert.Equal(new("ConnectionStrings__connection", "expression-value"), entry),
            entry => Assert.Equal(new("RAW_RESOURCE", "expression-value"), entry));
        Assert.Equal([("expression-value", secret), ("wrapped:expression-value", secret)], configuration.Arguments);
        var redactedValues = app.Services.GetRequiredService<SecretRedactionHistory>().AddValuesAndSnapshot([]);
        Assert.Equal(secret, redactedValues.Contains("expression-value"));
        Assert.Equal("custom-value", await resource.GetConnectionStringAsync(new ValueProviderContext { Caller = caller.Resource }, cts.Token));
    }

    [Fact]
    public async Task ExecutionConfigurationTracksConnectionStringExpressionParameterWaits()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var password = builder.AddParameter("password", secret: true).Resource;
        password.EnsureValueTask();
        var caller = builder.AddContainer("caller", "test-image");
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();
        var source = builder.AddResource(new ContextualResource("connection")
        {
            ConnectionStringExpression = ReferenceExpression.Create($"Password={password}"),
            Resolver = (_, _) => new("custom-value")
        });
        caller.WithEnvironment("CONNECTION", source);
        using var app = builder.Build();

        var configurationTask = ExecutionConfigurationBuilder.Create(caller.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, cts.Token);

        Assert.False(configurationTask.IsCompleted);
        Assert.True(app.ResourceNotifications.TryGetCurrentState(caller.Resource.Name, out var waiting));
        Assert.Equal(KnownResourceStates.UnresolvedParameters, waiting.Snapshot.State?.Text);
        var unresolved = Assert.Single(waiting.Snapshot.Properties, property => property.Name == KnownProperties.Resource.UnresolvedParameters);
        Assert.Equal(["password"], Assert.IsType<string[]>(unresolved.Value));

        await password.SetValueAsync("runtime-value");
        var configuration = await configurationTask.DefaultTimeout();

        Assert.Null(configuration.Exception);
        Assert.Equal(new("CONNECTION", "Password=runtime-value"), Assert.Single(configuration.EnvironmentVariables));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public async Task ExecutionConfigurationChecksConnectionStringOptionality(string? value, bool optional)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var expression = value is null
            ? ReferenceExpression.Create($"")
            : ReferenceExpression.Create($"{new ParameterResource("value", _ => value, secret: false)}");
        var source = builder.AddConnectionString("connection", expression);
        var caller = builder.AddContainer("caller", "test-image").WithReference(source, optional: optional);
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();

        var configuration = await ExecutionConfigurationBuilder.Create(caller.Resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, cts.Token);

        if (optional)
        {
            Assert.Null(configuration.Exception);
            if (value is null)
            {
                Assert.Empty(configuration.EnvironmentVariables);
            }
            else
            {
                Assert.Equal(new("ConnectionStrings__connection", value), Assert.Single(configuration.EnvironmentVariables));
            }
        }
        else
        {
            var aggregate = Assert.IsType<AggregateException>(configuration.Exception);
            Assert.IsType<DistributedApplicationException>(Assert.Single(aggregate.InnerExceptions));
            Assert.Empty(configuration.EnvironmentVariables);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecutionConfigurationPreservesSelectedConnectionStringExpression(bool secret)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = new ContextualResource("connection")
        {
            Resolver = (_, _) => throw new InvalidOperationException("The default connection string was not selected.")
        };
        var parameter = new ParameterResource("selected", _ => "selected-value", secret);
#pragma warning disable ASPIRECONNECTIONSTRINGS001
        var reference = new ConnectionStringReference(resource, optional: false,
            ConnectionStringEnvironmentVariableNames.Create(resource, "selected"),
            "SelectedConnectionString", ReferenceExpression.Create($"{parameter}"));
#pragma warning restore ASPIRECONNECTIONSTRINGS001
        var caller = builder.AddContainer("caller", "test-image")
            .WithEnvironment("SELECTED", reference)
            .WithArgs(reference);
        using var app = builder.Build();
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource();

        var configuration = await ExecutionConfigurationBuilder.Create(caller.Resource)
            .WithArgumentsConfig()
            .WithEnvironmentVariablesConfig()
            .BuildAsync(builder.ExecutionContext, NullLogger.Instance, cts.Token);

        Assert.Null(configuration.Exception);
        Assert.Equal(new("SELECTED", "selected-value"), Assert.Single(configuration.EnvironmentVariables));
        Assert.Equal(("selected-value", secret), Assert.Single(configuration.Arguments));
    }

    [Fact]
    public async Task AddConnectionStringExpressionIsAValueInTheManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var endpoint = appBuilder.AddParameter("endpoint", "http://localhost:3452");
        var key = appBuilder.AddParameter("key", "secretKey", secret: true);

        // Get the service provider.
        appBuilder.AddConnectionString("mycs", ReferenceExpression.Create($"Endpoint={endpoint};Key={key}"));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var connectionStringResource = Assert.Single(appModel.Resources.OfType<ConnectionStringResource>());

        Assert.Equal("mycs", connectionStringResource.Name);
        var connectionStringManifest = await ManifestUtils.GetManifest(connectionStringResource).DefaultTimeout();

        var expectedManifest = $$"""
            {
              "type": "value.v0",
              "connectionString": "Endpoint={endpoint.value};Key={key.value}"
            }
            """;

        var s = connectionStringManifest.ToString();

        Assert.Equal(expectedManifest, s);
    }

    [Fact]
    public void ConnectionStringsAreVisibleByDefault()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var endpoint = appBuilder.AddParameter("endpoint", "http://localhost:3452");
        var key = appBuilder.AddParameter("key", "secretKey", secret: true);

        appBuilder.AddConnectionString("testcs", ReferenceExpression.Create($"Endpoint={endpoint};Key={key}"));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<ConnectionStringResource>());
        var annotation = connectionStringResource.Annotations.OfType<ResourceSnapshotAnnotation>().SingleOrDefault();

        Assert.NotNull(annotation);

        var state = annotation.InitialSnapshot;

        Assert.False(state.IsHidden);
        Assert.Equal(KnownResourceTypes.ConnectionString, state.ResourceType);
        Assert.Equal(KnownResourceStates.NotStarted, state.State?.Text);
    }

    [Fact]
    public void ConnectionStringResourceAddsWaitAnnotationsForReferencedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddContainer("c", "redis").WithEndpoint(name: "tcp");
        var key = builder.AddParameter("key", "secretKey", secret: true);
        var rwl = builder.AddResource(new ResourceWithoutLifetime("rwl"));

        var cs = builder.AddConnectionString("mycs",
            ReferenceExpression.Create($"Endpoint={redis.GetEndpoint("tcp")};key={key};{rwl}"));

        cs.Resource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations);

        Assert.NotNull(waitAnnotations);

        Assert.Collection(waitAnnotations,
            wa =>
            {
                Assert.Same(redis.Resource, wa.Resource);
                Assert.Equal(WaitType.WaitUntilStarted, wa.WaitType);
            },
            wa =>
            {
                Assert.Same(key.Resource, wa.Resource);
                Assert.Equal(WaitType.WaitUntilStarted, wa.WaitType);
            });
    }

    private class ResourceWithoutLifetime(string name) : Resource(name), IResourceWithConnectionString, IResourceWithoutLifetime
    {
        public ReferenceExpression ConnectionStringExpression { get; init; } =
            ReferenceExpression.Create($"ResourceWithoutLifetime");
    }

    private class ContextualResource(string name) : ResourceWithoutLifetime(name), IResourceWithConnectionString
    {
        public Func<ValueProviderContext, CancellationToken, ValueTask<string?>>? Resolver { get; init; }

        public ValueTask<string?> GetConnectionStringAsync(ValueProviderContext context, CancellationToken cancellationToken) =>
            Resolver is { } resolver ? resolver(context, cancellationToken) : new(context.Caller?.Name);
    }

    private sealed class ExplicitContextualResource(string name) : ResourceWithoutLifetime(name), IResourceWithConnectionString
    {
        ValueTask<string?> IResourceWithConnectionString.GetConnectionStringAsync(ValueProviderContext context, CancellationToken cancellationToken) =>
            new(context.Caller?.Name);
    }

    private sealed class InheritedContextualResource(string name) : ContextualResource(name);

    private interface IContextualConnectionStringResource : IResourceWithConnectionString
    {
        ValueTask<string?> IResourceWithConnectionString.GetConnectionStringAsync(ValueProviderContext context, CancellationToken cancellationToken) =>
            new(context.Caller?.Name);
    }

    private sealed class DerivedInterfaceContextualResource(string name) : ResourceWithoutLifetime(name), IContextualConnectionStringResource;

    private class LegacyResource(string name) : ResourceWithoutLifetime(name), IResourceWithConnectionString
    {
        public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken) => new("legacy");
    }

    private sealed class ExplicitLegacyResource(string name) : ResourceWithoutLifetime(name), IResourceWithConnectionString
    {
        ValueTask<string?> IResourceWithConnectionString.GetConnectionStringAsync(CancellationToken cancellationToken) => new("legacy");
    }

    private sealed class InheritedLegacyResource(string name) : LegacyResource(name);

    private interface ILegacyConnectionStringResource : IResourceWithConnectionString
    {
        ValueTask<string?> IResourceWithConnectionString.GetConnectionStringAsync(CancellationToken cancellationToken) => new("legacy");
    }

    private sealed class DerivedInterfaceLegacyResource(string name) : ResourceWithoutLifetime(name), ILegacyConnectionStringResource;
}