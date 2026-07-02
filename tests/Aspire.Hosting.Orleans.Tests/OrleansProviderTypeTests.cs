// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Orleans.Tests;

public class OrleansProviderTypeTests
{
    [Fact]
    public async Task ProviderTypeDefaultsToResourceTypeNameWithoutResourceSuffix()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        });

        var orleans = builder.AddOrleans("orleans")
            .WithClustering(provider);

        var silo = builder.AddContainer("silo", "image")
            .WithReference(orleans);

        var config = await TestUtils.GetEnvironmentVariablesAsync(silo.Resource, builder);

        Assert.Equal("TestProvider", config["Orleans__Clustering__ProviderType"]);
        Assert.Equal("provider", config["Orleans__Clustering__ServiceKey"]);
    }

    [Fact]
    public async Task WithOrleansProviderTypeOverridesProviderType()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        }).WithOrleansProviderType("Redis");

        var orleans = builder.AddOrleans("orleans")
            .WithClustering(provider);

        var silo = builder.AddContainer("silo", "image")
            .WithReference(orleans);

        var config = await TestUtils.GetEnvironmentVariablesAsync(silo.Resource, builder);

        Assert.Equal("Redis", config["Orleans__Clustering__ProviderType"]);
        Assert.Equal("provider", config["Orleans__Clustering__ServiceKey"]);
    }

    [Fact]
    public async Task WithOrleansProviderTypeReplacesPreviousProviderType()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        })
            .WithOrleansProviderType("AdoNet")
            .WithOrleansProviderType("Redis");

        var annotation = Assert.Single(provider.Resource.Annotations.OfType<OrleansProviderTypeAnnotation>());
        Assert.Equal("Redis", annotation.ProviderType);

        var orleans = builder.AddOrleans("orleans")
            .WithClustering(provider);

        var silo = builder.AddContainer("silo", "image")
            .WithReference(orleans);

        var config = await TestUtils.GetEnvironmentVariablesAsync(silo.Resource, builder);

        Assert.Equal("Redis", config["Orleans__Clustering__ProviderType"]);
    }

    [Fact]
    public void WithOrleansProviderTypeShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<TestProviderResource> builder = null!;

        var action = () => builder.WithOrleansProviderType("Redis");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void WithOrleansProviderTypeShouldThrowWhenProviderTypeIsNullOrWhiteSpace(string? providerType)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        });

        var action = () => provider.WithOrleansProviderType(providerType!);

        var exception = providerType is null
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(providerType), exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CtorOrleansProviderTypeAnnotationShouldThrowWhenProviderTypeIsNullOrWhiteSpace(string? providerType)
    {
        var action = () => new OrleansProviderTypeAnnotation(providerType!);

        var exception = providerType is null
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(providerType), exception.ParamName);
    }
}
