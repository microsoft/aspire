// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Annotations;

namespace Aspire.Hosting.Radius.Tests.CloudProviders;

public class AdditiveCompatibilityTests
{
    [Fact]
    public void EnvironmentWithoutProvider_HasNoCloudProvidersAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        Assert.Empty(env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>());
    }

    [Fact]
    public void EnvironmentWithAzureProvider_HasAnnotationButOnlyAzureSlotPopulated()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("s", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius")
            .WithAzureProvider(
                "11111111-1111-1111-1111-111111111111", "rg",
                azure => azure.WithServicePrincipal(
                    "22222222-2222-2222-2222-222222222222",
                    "33333333-3333-3333-3333-333333333333",
                    secret));

        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.NotNull(ann.Azure);
        Assert.Null(ann.Aws);
    }

    [Fact]
    public void GetOrAdd_ReturnsSameInstance_OnRepeatedCalls()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var first = RadiusCloudProvidersAnnotation.GetOrAdd(env.Resource);
        var second = RadiusCloudProvidersAnnotation.GetOrAdd(env.Resource);

        Assert.Same(first, second);
        Assert.Single(env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>());
    }
}
