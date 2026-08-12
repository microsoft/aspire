// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Recipes;

public class RecipePackParametersBicepTests
{
    private static string Publish(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        return context.GenerateBicep(model);
    }

    [Fact]
    public void EnvironmentWideParameters_EmittedOnRecipeEntry_WithBicepTypeFidelity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p =>
            {
                p["vpcId"] = "vpc-123";
                p["subnetIds"] = new[] { "subnet-a", "subnet-b" };
                p["port"] = 6379;
                p["tlsEnabled"] = true;
                p["settings"] = new Dictionary<string, object> { ["tier"] = "standard", ["replicas"] = 3 };
            });
        builder.AddRedis("cache");

        using var app = builder.Build();
        var bicep = Publish(app);

        Assert.Contains("parameters: {", bicep);
        Assert.Contains("vpcId: 'vpc-123'", bicep);
        // Array preserved as a Bicep array (not a quoted string).
        Assert.Contains("'subnet-a'", bicep);
        Assert.Contains("'subnet-b'", bicep);
        // Number and boolean literals (not quoted).
        Assert.Contains("port: 6379", bicep);
        Assert.Contains("tlsEnabled: true", bicep);
        // Nested object preserved.
        Assert.Contains("tier: 'standard'", bicep);
        Assert.Contains("replicas: 3", bicep);
        Assert.DoesNotContain("port: '6379'", bicep);
        Assert.DoesNotContain("tlsEnabled: 'true'", bicep);
    }

    [Fact]
    public void NoRecipeParameters_OmitsParametersKey()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache");

        using var app = builder.Build();
        var bicep = Publish(app);

        Assert.DoesNotContain("parameters: {", bicep);
    }

    // T021 — legacy inline-recipes shape carries the parameters block.
    [Fact]
    public void EnvironmentWideParameters_EmittedOnLegacyRecipeEntry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p => { p["region"] = "us-west-2"; p["replicas"] = 3; });
        // Redis falls back to the legacy Applications.* recipe shape.
        builder.AddRedis("cache");

        using var app = builder.Build();
        var bicep = Publish(app);

        Assert.Contains("Applications.Core/environments", bicep);
        Assert.Contains("parameters: {", bicep);
        Assert.Contains("region: 'us-west-2'", bicep);
        Assert.Contains("replicas: 3", bicep);
    }
}
