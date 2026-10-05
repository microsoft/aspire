// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Annotations;
using Aspire.Hosting.Radius.CloudProviders;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.CloudProviders;

public class MultiEnvironmentProvidersTests
{
    private const string SubA = "11111111-1111-1111-1111-111111111111";
    private const string SubB = "22222222-2222-2222-2222-222222222222";
    private const string TenantId = "33333333-3333-3333-3333-333333333333";
    private const string ClientId = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public void ConfiguringEnvA_DoesNotAffectEnvB()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("s", "v", secret: true);

        var envA = builder.AddRadiusEnvironment("dev")
            .WithAzureProvider(SubA, "rg-dev",
                azure => azure.WithServicePrincipal(TenantId, ClientId, secret));
        var envB = builder.AddRadiusEnvironment("prod");

        var annA = envA.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.NotNull(annA.Azure);
        Assert.Equal(SubA, annA.Azure!.SubscriptionId);

        var annB = envB.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().FirstOrDefault();
        Assert.Null(annB);
    }

    [Fact]
    public void HybridEnv_HasBothAzureAndAws()
    {
        var builder = DistributedApplication.CreateBuilder();
        var secret = builder.AddParameter("s", "v", secret: true);

        var env = builder.AddRadiusEnvironment("prod")
            .WithAzureProvider(SubA, "rg",
                azure => azure.WithServicePrincipal(TenantId, ClientId, secret))
            .WithAwsProvider("123456789012", "us-east-1",
                aws => aws.WithIrsa("arn:aws:iam::123456789012:role/r"));

        var ann = env.Resource.Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        Assert.NotNull(ann.Azure);
        Assert.NotNull(ann.Aws);
    }

    [Fact]
    public void MultiEnvBicep_DifferentSubscriptions_EachGetsOwnScope()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("s", "v", secret: true);

        builder.AddRadiusEnvironment("dev")
            .WithAzureProvider(SubA, "rg-dev",
                azure => azure.WithServicePrincipal(TenantId, ClientId, secret));
        builder.AddRadiusEnvironment("prod")
            .WithAzureProvider(SubB, "rg-prod",
                azure => azure.WithWorkloadIdentity(TenantId, ClientId));

        builder.AddContainer("api", "nginx");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var envs = model.Resources.OfType<RadiusEnvironmentResource>().ToList();
        var devAnn = envs.Single(e => e.Name == "dev").Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();
        var prodAnn = envs.Single(e => e.Name == "prod").Annotations.OfType<RadiusCloudProvidersAnnotation>().Single();

        Assert.Equal(SubA, devAnn.Azure!.SubscriptionId);
        Assert.Equal(SubB, prodAnn.Azure!.SubscriptionId);
        Assert.IsType<AzureRadiusCredential.ServicePrincipal>(devAnn.Azure.Credential);
        Assert.IsType<AzureRadiusCredential.WorkloadIdentity>(prodAnn.Azure.Credential);
    }
}
