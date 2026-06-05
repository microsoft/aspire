// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using static Aspire.Hosting.Utils.AzureManifestUtils;

namespace Aspire.Hosting.Azure.Tests;

public class AzureManagedRedisExtensionsTests
{
    /// <summary>
    /// Test both with and without ACA infrastructure because the role assignments
    /// are handled differently between the two. This ensures that the bicep is generated
    /// consistently regardless of the infrastructure used in RunMode.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAzureManagedRedis(bool useAcaInfrastructure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        if (useAcaInfrastructure)
        {
            builder.AddAzureContainerAppEnvironment("env");
        }

        var redis = builder.AddAzureManagedRedis("redis-cache");

        builder.AddContainer("api", "myimage")
            .WithReference(redis);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var (_, bicep) = await GetManifestWithBicep(model, redis.Resource);
        var redisRoles = Assert.Single(model.Resources.OfType<AzureProvisioningResource>(), r => r.Name == "redis-cache-roles");
        var (_, redisRolesBicep) = await GetManifestWithBicep(redisRoles, skipPreparer: true);

        await Verify(bicep, "bicep")
              .AppendContentAsFile(redisRolesBicep, "bicep");
    }

    [Fact]
    public async Task AddAzureManagedRedisRunAsContainerProducesCorrectConnectionString()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        RedisResource? redisResource = null;
        var redis = builder.AddAzureManagedRedis("cache")
            .RunAsContainer(c =>
            {
                redisResource = c.Resource;

                c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 12455));
            });

        Assert.True(redis.Resource.IsContainer(), "The resource should now be a container resource.");

        var sslArg = redisResource?.TlsEnabled == true ? ",ssl=true" : "";

        Assert.NotNull(redisResource?.PasswordParameter);
        Assert.Equal($"localhost:12455,password={await redisResource.PasswordParameter.GetValueAsync(CancellationToken.None)}{sslArg}", await redis.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AddAzureManagedRedisRunAsContainerProducesCorrectHostAndPassword()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var pass = builder.AddParameter("pass", "p@ssw0rd1");

        RedisResource? redisResource = null;
        var redis = builder.AddAzureManagedRedis("cache")
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
        Assert.Equal("tcp", endpoint.UriScheme);

        Assert.True(redis.Resource.IsContainer(), "The resource should now be a container resource.");

        Assert.NotNull(redisResource?.PasswordParameter);
        Assert.Equal($"localhost:12455,password=p@ssw0rd1", await redis.Resource.ConnectionStringExpression.GetValueAsync(CancellationToken.None));

        // Test the new reference properties
        Assert.NotNull(redis.Resource.HostName);
        Assert.Equal("localhost", await redis.Resource.HostName.GetValueAsync(CancellationToken.None));

        Assert.NotNull(redis.Resource.Password);
        Assert.Equal("p@ssw0rd1", await redis.Resource.Password.GetValueAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RunAsContainerAppliesAnnotationsCorrectly(bool before)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var cache = builder.AddAzureManagedRedis("cache");

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
    public async Task AddAzureManagedRedisWithAutoGeneratedPasswordProducesCorrectValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddAzureManagedRedis("redis-test")
                           .RunAsContainer(c =>
                           {
                               c.WithEndpoint("tcp", e => e.AllocatedEndpoint = new AllocatedEndpoint(e, "localhost", 6379));
                           });

        // Even with auto-generated password, Password and HostName should be available
        Assert.NotNull(redis.Resource.HostName);
        Assert.NotNull(redis.Resource.Password);

        // Validate the values can be resolved
        var hostValue = await redis.Resource.HostName.GetValueAsync(CancellationToken.None);
        Assert.Equal("localhost", hostValue);

        var passwordValue = await redis.Resource.Password.GetValueAsync(CancellationToken.None);
        Assert.NotNull(passwordValue);
        Assert.NotEmpty(passwordValue);
    }

    [Fact]
    public void AddAzureManagedRedisEntraIdModePasswordIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var redis = builder.AddAzureManagedRedis("redis-data");

        // In Azure mode, Password should be null since Redis uses connection strings
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
    public void AddAsExistingResource_ShouldBeIdempotent_ForAzureRedisCacheResource()
    {
        // Arrange
        var redisResource = new AzureManagedRedisResource("test-redis", _ => { });
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

        var redis = builder.AddAzureManagedRedis("test-redis")
            .AsExisting(existingName, existingResourceGroup);

        var module = builder.AddAzureInfrastructure("mymodule", infra =>
        {
            _ = redis.Resource.AddAsExistingResource(infra);
        });

        var (manifest, bicep) = await GetManifestWithBicep(module.Resource, skipPreparer: true);

        await Verify(bicep, "bicep");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("mykeyvault")]
    public async Task AddAzureManagedRedisWithAccessKeyAuthentication(string? kvName)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddAzureManagedRedis("redis-cache");

        if (kvName is null)
        {
            redis.WithAccessKeyAuthentication();
        }
        else
        {
            redis.WithAccessKeyAuthentication(builder.AddAzureKeyVault(kvName));
        }

        var (_, bicep) = await GetManifestWithBicep(redis.Resource);

        await Verify(bicep, extension: "bicep");
    }
}
