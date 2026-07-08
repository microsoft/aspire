// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class ProviderScopeBicepEmissionTests
{
    private const string SubId = "11111111-1111-1111-1111-111111111111";
    private const string Rg = "rg-test";
    private const string TenantId = "22222222-2222-2222-2222-222222222222";
    private const string ClientId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void Azure_Provider_EmitsSubscriptionAndResourceGroup_AndNoSecretLiterals()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("azureClientSecret", "secret-XYZ", secret: true);
        var env = builder.AddRadiusEnvironment("radius")
            .WithAzureProvider(SubId, Rg,
                azure => azure.WithServicePrincipal(TenantId, ClientId, secret));
        builder.AddContainer("api", "nginx");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var envResource = model.Resources.OfType<RadiusEnvironmentResource>().Single();
        RadiusTestHelper.AttachDeploymentTargets(envResource, model);

        var ctx = new RadiusBicepPublishingContext(envResource);
        var bicep = ctx.GenerateBicep(model, NullLogger.Instance);

        // The native Radius.Core/environments schema models the Azure provider with
        // discrete subscriptionId/resourceGroupName fields (not a single legacy scope path).
        Assert.Contains($"subscriptionId: '{SubId}'", bicep);
        Assert.Contains($"resourceGroupName: '{Rg}'", bicep);
        Assert.DoesNotContain("secret-XYZ", bicep);
        Assert.DoesNotContain(TenantId, bicep);
        Assert.DoesNotContain(ClientId, bicep);
    }

    [Fact]
    public void Without_Provider_DoesNotEmit_ProvidersAzure()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");
        builder.AddContainer("api", "nginx");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var envResource = model.Resources.OfType<RadiusEnvironmentResource>().Single();
        RadiusTestHelper.AttachDeploymentTargets(envResource, model);

        var ctx = new RadiusBicepPublishingContext(envResource);
        var bicep = ctx.GenerateBicep(model, NullLogger.Instance);

        Assert.DoesNotContain("providers.azure", bicep, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/subscriptions/", bicep);
    }

    [Fact]
    public void Aws_Provider_EmitsAccountAndRegion_AndNoSecretLiterals()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var keyId = builder.AddParameter("awsKeyId", "AKIAEXAMPLE", secret: true);
        var keySecret = builder.AddParameter("awsKeySecret", "AKIA-XYZ-secret", secret: true);

        builder.AddRadiusEnvironment("radius")
            .WithAwsProvider("123456789012", "us-west-2",
                aws => aws.WithAccessKey(keyId, keySecret));
        builder.AddContainer("api", "nginx");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var envResource = model.Resources.OfType<RadiusEnvironmentResource>().Single();
        RadiusTestHelper.AttachDeploymentTargets(envResource, model);

        var ctx = new RadiusBicepPublishingContext(envResource);
        var bicep = ctx.GenerateBicep(model, NullLogger.Instance);

        // The native Radius.Core/environments schema models the AWS provider with
        // discrete accountId/region fields (not a single legacy scope path).
        Assert.Contains("accountId: '123456789012'", bicep);
        Assert.Contains("region: 'us-west-2'", bicep);
        Assert.DoesNotContain("AKIA-XYZ-secret", bicep);
        Assert.DoesNotContain("AKIAEXAMPLE", bicep);
    }

    [Fact]
    public void Hybrid_Provider_EmitsBothProviders()
    {
        using var builder = Aspire.Hosting.Utils.TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("s", "v", secret: true);
        builder.AddRadiusEnvironment("radius")
            .WithAzureProvider(SubId, Rg,
                azure => azure.WithServicePrincipal(TenantId, ClientId, secret))
            .WithAwsProvider("123456789012", "us-east-1",
                aws => aws.WithIrsa("arn:aws:iam::123456789012:role/r"));
        builder.AddContainer("api", "nginx");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var envResource = model.Resources.OfType<RadiusEnvironmentResource>().Single();
        RadiusTestHelper.AttachDeploymentTargets(envResource, model);

        var ctx = new RadiusBicepPublishingContext(envResource);
        var bicep = ctx.GenerateBicep(model, NullLogger.Instance);

        Assert.Contains($"subscriptionId: '{SubId}'", bicep);
        Assert.Contains($"resourceGroupName: '{Rg}'", bicep);
        Assert.Contains("accountId: '123456789012'", bicep);
        Assert.Contains("region: 'us-east-1'", bicep);
    }
}
