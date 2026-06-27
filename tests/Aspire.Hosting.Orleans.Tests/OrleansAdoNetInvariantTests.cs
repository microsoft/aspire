// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Orleans.Tests;

public class OrleansAdoNetInvariantTests
{
    [Fact]
    public void WithOrleansAdoNetInvariantAddsAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        }).WithOrleansAdoNetInvariant("Npgsql");

        var annotation = Assert.Single(provider.Resource.Annotations.OfType<OrleansAdoNetInvariantAnnotation>());
        Assert.Equal("Npgsql", annotation.Invariant);
    }

    [Fact]
    public void WithOrleansAdoNetInvariantReplacesPreviousAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        })
            .WithOrleansAdoNetInvariant("Npgsql")
            .WithOrleansAdoNetInvariant("Microsoft.Data.SqlClient");

        var annotation = Assert.Single(provider.Resource.Annotations.OfType<OrleansAdoNetInvariantAnnotation>());
        Assert.Equal("Microsoft.Data.SqlClient", annotation.Invariant);
    }

    [Fact]
    public void WithOrleansAdoNetInvariantShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<TestProviderResource> builder = null!;

        var action = () => builder.WithOrleansAdoNetInvariant("Npgsql");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void WithOrleansAdoNetInvariantShouldThrowWhenInvariantIsNullOrWhiteSpace(string? invariant)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        });

        var action = () => provider.WithOrleansAdoNetInvariant(invariant!);

        var exception = invariant is null
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);

        Assert.Equal(nameof(invariant), exception.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void CtorOrleansAdoNetInvariantAnnotationShouldThrowWhenInvariantIsNullOrWhiteSpace(string? invariant)
    {
        var action = () => new OrleansAdoNetInvariantAnnotation(invariant!);

        var exception = invariant is null
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);

        Assert.Equal(nameof(invariant), exception.ParamName);
    }

    [Fact]
    public async Task AdoNetProviderUsesConnectionNameInsteadOfServiceKey()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        })
            .WithOrleansProviderType("AdoNet")
            .WithOrleansAdoNetInvariant("Npgsql");

        var orleans = builder.AddOrleans("orleans")
            .WithClustering(provider);

        var silo = builder.AddContainer("silo", "image")
            .WithReference(orleans);

        var config = await TestUtils.GetEnvironmentVariablesAsync(silo.Resource, builder);

        Assert.Equal("AdoNet", config["Orleans__Clustering__ProviderType"]);
        Assert.Equal("provider", config["Orleans__Clustering__ConnectionName"]);
        Assert.False(config.ContainsKey("Orleans__Clustering__ServiceKey"));
    }

    [Fact]
    public async Task AdoNetProviderWritesInvariant()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        })
            .WithOrleansProviderType("AdoNet")
            .WithOrleansAdoNetInvariant("Npgsql");

        var orleans = builder.AddOrleans("orleans")
            .WithClustering(provider);

        var silo = builder.AddContainer("silo", "image")
            .WithReference(orleans);

        var config = await TestUtils.GetEnvironmentVariablesAsync(silo.Resource, builder);

        Assert.Equal("Npgsql", config["Orleans__Clustering__Invariant"]);
    }

    [Fact]
    public async Task NonAdoNetProviderDoesNotWriteInvariant()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        })
            .WithOrleansProviderType("Redis");

        var orleans = builder.AddOrleans("orleans")
            .WithClustering(provider);

        var silo = builder.AddContainer("silo", "image")
            .WithReference(orleans);

        var config = await TestUtils.GetEnvironmentVariablesAsync(silo.Resource, builder);

        Assert.False(config.ContainsKey("Orleans__Clustering__Invariant"));
    }

    [Fact]
    public void AdoNetProviderWithoutInvariantThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var provider = builder.AddResource(new TestProviderResource("provider")
        {
            ConnectionString = "connectionString"
        })
            .WithOrleansProviderType("AdoNet");

        var action = () =>
        {
            var orleans = builder.AddOrleans("orleans")
                .WithClustering(provider);

            var silo = builder.AddContainer("silo", "image")
                .WithReference(orleans);
        };

        Assert.Throws<ArgumentNullException>(action);
    }
}
