// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.CloudProviders;

namespace Aspire.Hosting.Radius.Tests.CloudProviders;

public class WithAzureProviderTests
{
    private const string ValidSubscriptionId = "11111111-1111-1111-1111-111111111111";
    private const string ValidResourceGroup = "rg-test";
    private const string ValidTenantId = "22222222-2222-2222-2222-222222222222";
    private const string ValidClientId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void WithAzureProvider_HappyPath_ReturnsSameBuilder_AndPopulatesAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("azureClientSecret", "secret-XYZ", secret: true);

        var env = builder.AddRadiusEnvironment("radius");
        var returned = env.WithAzureProvider(
            ValidSubscriptionId,
            ValidResourceGroup,
            azure => azure.WithServicePrincipal(ValidTenantId, ValidClientId, secret));

        Assert.Same(env, returned);

        var annotation = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.NotNull(annotation.Azure);
        Assert.Equal(ValidSubscriptionId, annotation.Azure!.SubscriptionId);
        Assert.Equal(ValidResourceGroup, annotation.Azure.ResourceGroup);

        var sp = Assert.IsType<AzureRadiusCredential.ServicePrincipal>(annotation.Azure.Credential);
        Assert.Equal(ValidTenantId, sp.TenantId);
        Assert.Equal(ValidClientId, sp.ClientId);
        Assert.Same(secret, sp.ClientSecret);
    }

    [Fact]
    public void WithAzureProvider_NonGuidSubscription_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("s", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAzureProvider(
            "not-a-guid", ValidResourceGroup,
            azure => azure.WithServicePrincipal(ValidTenantId, ValidClientId, secret)));
        Assert.Equal("subscriptionId", ex.ParamName);
    }

    [Fact]
    public void WithAzureProvider_EmptyResourceGroup_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("s", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAzureProvider(
            ValidSubscriptionId, "",
            azure => azure.WithServicePrincipal(ValidTenantId, ValidClientId, secret)));
        Assert.Equal("resourceGroup", ex.ParamName);
    }

    [Fact]
    public void WithAzureProvider_NullConfigure_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentNullException>(() => env.WithAzureProvider(
            ValidSubscriptionId, ValidResourceGroup, null!));
    }

    [Fact]
    public void WithAzureProvider_CallbackReturnsWithoutCredential_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<InvalidOperationException>(() => env.WithAzureProvider(
            ValidSubscriptionId, ValidResourceGroup, _ => { }));
        Assert.Contains("ASPIRERADIUS010", ex.Message);
    }

    [Fact]
    public void WithServicePrincipal_NonGuidTenantId_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("s", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAzureProvider(
            ValidSubscriptionId, ValidResourceGroup,
            azure => azure.WithServicePrincipal("bad", ValidClientId, secret)));
        Assert.Equal("tenantId", ex.ParamName);
    }

    [Fact]
    public void WithServicePrincipal_EmptyClientId_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("s", "v", secret: true);
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAzureProvider(
            ValidSubscriptionId, ValidResourceGroup,
            azure => azure.WithServicePrincipal(ValidTenantId, "", secret)));
        Assert.Equal("clientId", ex.ParamName);
    }

    [Fact]
    public void WithWorkloadIdentity_HappyPath_PopulatesAnnotation()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        env.WithAzureProvider(ValidSubscriptionId, ValidResourceGroup,
            azure => azure.WithWorkloadIdentity(ValidClientId, ValidTenantId));

        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        var wi = Assert.IsType<AzureRadiusCredential.WorkloadIdentity>(ann.Azure!.Credential);
        Assert.Equal(ValidClientId, wi.ClientId);
        Assert.Equal(ValidTenantId, wi.TenantId);
    }

    [Fact]
    public void WithWorkloadIdentity_NonGuidTenantId_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAzureProvider(
            ValidSubscriptionId, ValidResourceGroup,
            azure => azure.WithWorkloadIdentity(ValidClientId, "bad")));
        Assert.Equal("tenantId", ex.ParamName);
    }

    [Fact]
    public void WithWorkloadIdentity_EmptyClientId_Throws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var ex = Assert.Throws<ArgumentException>(() => env.WithAzureProvider(
            ValidSubscriptionId, ValidResourceGroup,
            azure => azure.WithWorkloadIdentity("", ValidTenantId)));
        Assert.Equal("clientId", ex.ParamName);
    }
}
