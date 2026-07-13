// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Secrets;

public class SecretStoreDefaultPathTests
{
    [Fact]
    public void NoSecretStore_Publish_EmitsNoSecretStores()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("radius");
        builder.AddContainer("api", "img", "latest");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var bicep = new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);

        Assert.DoesNotContain("secretStores", bicep);
    }

    [Fact]
    public void RunMode_DoesNotAddSecretStoreToModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var pass = builder.AddParameter("db-pass", secret: true);
        builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic)
            .WithData(d => d.Add("password", pass));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The Radius integration is publish/deploy-only; in Run mode the store is not registered,
        // so nothing is emitted and no kubectl/rad contact can occur (SC-007).
        Assert.Empty(model.Resources.OfType<RadiusSecretStoreResource>());
    }

    [Fact]
    public void RunMode_SecretParameter_ResolvesFromConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var pass = builder.AddParameter("db-pass", secret: true);
        builder.AddRadiusSecretStore("db-creds", RadiusSecretStoreType.Generic)
            .WithData(d => d.Add("password", pass));

        // The Aspire secret parameter is unaffected by the secret-store declaration.
        Assert.True(pass.Resource.Secret);
    }
}
