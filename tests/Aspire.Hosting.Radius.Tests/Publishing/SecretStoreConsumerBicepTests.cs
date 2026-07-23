// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS006 // Experimental: the secret-store APIs are under test.
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class SecretStoreConsumerBicepTests
{
    [Fact]
    public void Consumers_EmitRecipeConfig_ReferencingStoreById()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var user = builder.AddParameter("u", secret: true);
        var pass = builder.AddParameter("p", secret: true);
        var env = builder.AddRadiusEnvironment("radius");
        var store = builder.AddRadiusSecretStore("registry-creds", RadiusSecretStoreType.BasicAuthentication)
            .WithData(d => { d.Add("username", user); d.Add("password", pass); });

        env.WithBicepRegistryAuthentication("myregistry.azurecr.io", store)
           .WithTerraformGitAuthentication("github.com", store)
           .WithRecipeEnvironmentSecret("DB_PASSWORD", store, "password");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var bicep = new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);

        // recipeConfig block referencing the store by its in-group .id (identifier 'registry_creds').
        Assert.Contains("recipeConfig:", bicep);
        Assert.Contains("authentication:", bicep);
        Assert.Contains("'myregistry.azurecr.io'", bicep);
        Assert.Contains("pat:", bicep);
        Assert.Contains("'github.com'", bicep);
        Assert.Contains("envSecrets:", bicep);
        Assert.Contains("registry_creds.id", bicep);
        Assert.Contains("key: 'password'", bicep);
    }

    [Fact]
    public void NoConsumers_EmitsNoRecipeConfig()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var pass = builder.AddParameter("p", secret: true);
        var env = builder.AddRadiusEnvironment("radius");
        env.WithSecretStore("db-creds", RadiusSecretStoreType.Generic, s => s.WithData(d => d.Add("k", pass)));

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var bicep = new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);

        Assert.DoesNotContain("recipeConfig:", bicep);
    }
}
