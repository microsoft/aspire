// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

[Obsolete]
public class AzureRedisExtensionsTests
{
    /// <summary>
    /// Test both with and without ACA infrastructure because the role assignments
    /// are handled differently between the two. This ensures that the bicep is generated
    /// consistently regardless of the infrastructure used in RunMode.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAzureRedis(bool useAcaInfrastructure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        if (useAcaInfrastructure)
        {
            builder.AddAzureContainerAppEnvironment("env");
        }

        var redis = builder.AddAzureRedis("redis-cache");

        builder.AddContainer("api", "myimage")
            .WithReference(redis);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (manifest, bicep) = await GetManifestWithBicep(model, redis.Resource);
        var redisRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "redis-cache-roles");
        var (redisRolesManifest, redisRolesBicep) = await AzureManifestUtils.GetManifestWithBicep(redisRoles, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
              .AppendContentAsFile(bicep, "bicep")
              .AppendContentAsFile(redisRolesManifest.ToString(), "json")
              .AppendContentAsFile(redisRolesBicep, "bicep");

    }

    [Fact]
    public async Task AddAzureRedis_WithAccessKeyAuthentication_NoKeyVaultWithContainer()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        builder.AddAzureRedis("redis").WithAccessKeyAuthentication().RunAsContainer();

        var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        Assert.Empty(model.Resources.OfType<AzureKeyVaultResource>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mykeyvault")]
    public async Task AddAzureRedisWithAccessKeyAuthentication(string? kvName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddAzureRedis("redis-cache");

        if (kvName is null)
        {
            redis.WithAccessKeyAuthentication();
        }
        else
        {
            redis.WithAccessKeyAuthentication(builder.AddAzureKeyVault(kvName));
        }

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(redis.Resource);

        await Verify(bicep, extension: "bicep")
                  .AppendContentAsFile(manifest.ToString(), "json");

    }

    [Fact]
    public async Task AddAzureRedisRunAsContainerProducesCorrectConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        RedisResource? redisResource = null;
        var redis = builder.AddAzureRedis("cache")
            .RunAsContainer(c =>
            {
                redisResource = c.Resource;

                c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455));
            });

        Assert.True(redis.Resource.IsContainer(), "The resource should now be a container resource.");

        Assert.NotNull(redisResource?.PasswordParameter);
#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"localhost:12455,password={redisResource.PasswordParameter.Value}", await redis.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task AddAzureRedisRunAsContainerProducesCorrectHostAndPassword()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pass = builder.AddParameter("pass", "p@ssw0rd1");

        RedisResource? redisResource = null;
        var redis = builder.AddAzureRedis("cache")
            .RunAsContainer(c =>
            {
                redisResource = c.Resource;

                c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455))
                    .WithHostPort(12455)
                    .WithPassword(pass);
            });

        Assert.NotNull(redisResource);

        var endpoint = Assert.Single(redisResource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(6379, endpoint.TargetPort);
        Assert.False(endpoint.IsExternal);
        Assert.Equal("tcp", endpoint.Name);
        Assert.Equal(12455, endpoint.Port);
        Assert.Equal(ProtocolType.Tcp, endpoint.Protocol);
        Assert.Equal("tcp", endpoint.Transport);
        Assert.Equal("redis", endpoint.UriScheme);

        Assert.True(redis.Resource.IsContainer(), "The resource should now be a container resource.");

        Assert.NotNull(redisResource?.PasswordParameter);
        Assert.Equal($"localhost:12455,password=p@ssw0rd1", await redis.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));

        // Test the new reference properties
        Assert.NotNull(redis.Resource.HostName);
        Assert.Equal("localhost:12455", await redis.Resource.HostName.GetValueAsync(CancellationToken.None));

        Assert.NotNull(redis.Resource.Password);
        Assert.Equal("p@ssw0rd1", await redis.Resource.Password.GetValueAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunAsContainerAppliesAnnotationsCorrectly(bool before)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cache = builder.AddAzureRedis("cache");

        if (before)
        {
            cache.WithAnnotation(new Dummy1Annotation());
        }

        cache.RunAsContainer(c =>
        {
            c.WithAnnotation(new Dummy2Annotation());
        });

        if (!before)
        {
            cache.WithAnnotation(new Dummy1Annotation());
        }

        var cacheInModel = builder.Resources.Single(r => r.Name == "cache");

        Assert.True(cacheInModel.TryGetAnnotationsOfType<Dummy1Annotation>(out var cacheAnnotations1));
        Assert.Single(cacheAnnotations1);

        Assert.True(cacheInModel.TryGetAnnotationsOfType<Dummy2Annotation>(out var cacheAnnotations2));
        Assert.Single(cacheAnnotations2);
    }

    [Fact]
    public async Task AddAzureRedisWithAutoGeneratedPasswordProducesCorrectValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddAzureRedis("redis-test")
                           .RunAsContainer(c =>
                           {
                               c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 6379));
                           });

        // Even with auto-generated password, Password and HostName should be available
        Assert.NotNull(redis.Resource.HostName);
        Assert.NotNull(redis.Resource.Password);
        
        // Validate the values can be resolved
        var hostValue = await redis.Resource.HostName.GetValueAsync(CancellationToken.None);
        Assert.Equal("localhost:6379", hostValue);
        
        var passwordValue = await redis.Resource.Password.GetValueAsync(CancellationToken.None);
        Assert.NotNull(passwordValue);
        Assert.NotEmpty(passwordValue);
    }

    [Fact]
    public void AddAzureRedisEntraIdModePasswordIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var redis = builder.AddAzureRedis("redis-data");

        // In Azure mode (both Entra ID and access key), Password should be null since Redis uses connection strings
        Assert.Null(redis.Resource.Password);

        // HostName should still be available and resolve to bicep output
        Assert.NotNull(redis.Resource.HostName);
        Assert.Equal("{redis-data.outputs.hostName}", redis.Resource.HostName.ValueExpression);
    }

    private sealed class Dummy1Annotation : IResourceAnnotation
    {
    }

    private sealed class Dummy2Annotation : IResourceAnnotation
    {
    }

    [Fact]
    public async Task PublishAsRedisPublishesRedisAsAzureRedisInfrastructure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

#pragma warning disable CS0618 // Type or member is obsolete
        var redis = builder.AddRedis("cache")
            .WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455))
            .PublishAsAzureRedis();
#pragma warning restore CS0618 // Type or member is obsolete

        Assert.True(redis.Resource.IsContainer());
        Assert.NotNull(redis.Resource.PasswordParameter);

#pragma warning disable CS0618 // Type or member is obsolete
        Assert.Equal($"localhost:12455,password={redis.Resource.PasswordParameter.Value}", await redis.Resource.GetConnectionStringAsync());
#pragma warning restore CS0618 // Type or member is obsolete

        var manifest = await AzureManifestUtils.GetManifestWithBicep(redis.Resource);

        var expectedManifest = """
            {
              "type": "azure.bicep.v0",
              "connectionString": "{cache.secretOutputs.connectionString}",
              "path": "cache.module.bicep",
              "params": {
                "keyVaultName": ""
              }
            }
            """;
        Assert.Equal(expectedManifest, manifest.ManifestNode.ToString());

        await Verify(manifest.BicepText, extension: "bicep");
    }

    [Fact]
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureRedisCacheResource()
    {
        // Arrange
        var redisResource = new AzureRedisCacheResource("test-redis", _ => { });
        var infrastructure = new AzureResourceInfrastructure(redisResource, "test-redis");

        // Act - Call AddAsExistingResource twice
        var firstResult = redisResource.AddAsExistingResource(infrastructure);
        var secondResult = redisResource.AddAsExistingResource(infrastructure);

        // Assert - Both calls should return the same resource instance, not duplicates
        Assert.Same(firstResult, secondResult);
    }

    [Fact]
    public async Task AddAsExistingResource_RespectsExistingAzureResourceAnnotation_ForAzureRedisCacheResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var existingName = builder.AddParameter("existing-redis-name");
        var existingResourceGroup = builder.AddParameter("existing-redis-rg");

        var redis = builder.AddAzureRedis("test-redis")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = redis.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await AzureManifestUtils.GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(manifest.ToString(), "json")
             .AppendContentAsFile(bicep, "bicep");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mykeyvault")]
    public void WithAccessKeyAuthentication_SetsSecretOwner(string? kvName)
    {
        // Arrange
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddAzureRedis("redis-cache");

        // Act
        if (kvName is null)
        {
            redis.WithAccessKeyAuthentication();
        }
        else
        {
            redis.WithAccessKeyAuthentication(builder.AddAzureKeyVault(kvName));
        }

        // Assert - Verify that the SecretOwner is set to the Redis resource
        Assert.NotNull(redis.Resource.ConnectionStringSecretOutput);
        Assert.Same(redis.Resource, redis.Resource.ConnectionStringSecretOutput.SecretOwner);
        
        // Also verify that References includes both the KeyVault and the Redis resource
        var references = ((IValueWithReferences)redis.Resource.ConnectionStringSecretOutput).References.ToList();
        Assert.Contains(redis.Resource, references);
        Assert.Contains(redis.Resource.ConnectionStringSecretOutput.Resource, references);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
