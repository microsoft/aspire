// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet.Tests;

public class ProjectReferenceCompatibilityTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task ProjectReferencesPreserveDiscoveryAndRelationshipsAcrossFactories(
        bool v2Source, bool v2Consumer, bool explicitEndpoint)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        IResourceBuilder<IResourceWithServiceDiscovery> source;
        IResourceBuilder<IResourceWithEnvironment> consumer;

        if (v2Source)
        {
            source = builder.AddDotnetProject("source", "Source.csproj", options => options.ExcludeLaunchProfile = true);
        }
        else
        {
#pragma warning disable CS0618 // Verify legacy-to-legacy and both mixed-model reference directions.
            source = builder.AddProject("source", "Source.csproj", options => options.ExcludeLaunchProfile = true);
#pragma warning restore CS0618
        }

        if (v2Consumer)
        {
            consumer = builder.AddDotnetProject("consumer", "Consumer.csproj", options => options.ExcludeLaunchProfile = true);
        }
        else
        {
#pragma warning disable CS0618 // Verify legacy-to-legacy and both mixed-model reference directions.
            consumer = builder.AddProject("consumer", "Consumer.csproj", options => options.ExcludeLaunchProfile = true);
#pragma warning restore CS0618
        }

        source.WithHttpEndpoint(name: "http")
            .WithEndpoint("http", endpoint => endpoint.AllocatedEndpoint = new(endpoint, "localhost", 1080))
            .WithHttpsEndpoint(name: "admin")
            .WithEndpoint("admin", endpoint =>
            {
                endpoint.AllocatedEndpoint = new(endpoint, "localhost", 1443);
                endpoint.ExcludeReferenceEndpoint = true;
            });

        var originalRelationships = consumer.Resource.Annotations.OfType<ResourceRelationshipAnnotation>().ToArray();

        if (explicitEndpoint)
        {
            consumer.WithReference(source.GetEndpoint("admin"));
        }
        else
        {
            consumer.WithReference(source);
        }

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            consumer.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var referenceEnvironment = environment
            .Where(pair => pair.Key.StartsWith("services__", StringComparison.Ordinal)
                || pair.Key.StartsWith("SOURCE_", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        var endpointName = explicitEndpoint ? "admin" : "http";
        var expectedUrl = explicitEndpoint ? "https://localhost:1443" : "http://localhost:1080";

        Assert.Equal(
            new KeyValuePair<string, string>[]
            {
                new($"SOURCE_{endpointName.ToUpperInvariant()}", expectedUrl),
                new($"services__source__{endpointName}__0", expectedUrl)
            },
            referenceEnvironment);
        var relationships = consumer.Resource.Annotations.OfType<ResourceRelationshipAnnotation>().ToArray();
        Assert.Equal(originalRelationships, relationships.Take(originalRelationships.Length));
        var relationship = Assert.Single(relationships.Skip(originalRelationships.Length));
        Assert.Equal("Reference", relationship.Type);
        Assert.Same(source.Resource, relationship.Resource);
    }
}
