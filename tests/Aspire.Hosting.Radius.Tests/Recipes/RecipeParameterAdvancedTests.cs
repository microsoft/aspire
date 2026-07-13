// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Recipes;

public class RecipeParameterAdvancedTests
{
    private static string Publish(DistributedApplication app)
    {
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        return context.GenerateBicep(model);
    }

    // T008 — ParameterResource binding: emit a param reference, never a secret value.
    [Fact]
    public void ParameterBoundValue_EmitsSecureParamReference_NoSecretValue()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("recipeSecret", "TopSecretValue", secret: true);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p => p["apiKey"] = secret);
        builder.AddRedis("cache");

        using var app = builder.Build();
        var bicep = Publish(app);

        Assert.DoesNotContain("TopSecretValue", bicep);
        Assert.Contains("param recipeSecret string", bicep);
        Assert.Contains("@secure()", bicep);
        // Recipe parameter references the param identifier, not a literal value.
        Assert.Contains("apiKey: recipeSecret", bicep);
    }

    // T014 — resource-type scoping: scoped params only on that type; env-wide on all; type wins.
    [Fact]
    public void ResourceTypeScoped_OnlyOnThatType_UnionWithEnvWide_TypeWins()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p => { p["shared"] = "all"; p["tier"] = "base"; })
            .WithRecipeParameters("Radius.Compute/containers", p => { p["sku"] = "Premium"; p["tier"] = "compute"; });
        builder.AddContainer("api", "myapp/api", "latest");

        using var app = builder.Build();
        var bicep = Publish(app);

        Assert.Contains("shared: 'all'", bicep);
        Assert.Contains("sku: 'Premium'", bicep);
        // Resource-type-scoped value wins on key collision with env-wide.
        Assert.Contains("tier: 'compute'", bicep);
        Assert.DoesNotContain("tier: 'base'", bicep);
    }

    // T015 — unmatched resource type: publish still succeeds.
    [Fact]
    public void UnmatchedResourceTypeScope_PublishSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters("Radius.Data/doesNotExist", p => p["x"] = "y");
        builder.AddRedis("cache");

        using var app = builder.Build();
        var bicep = Publish(app);

        Assert.NotNull(bicep);
        Assert.DoesNotContain("x: 'y'", bicep);
    }

    // T026 — provider-scope reference resolves at publish.
    [Fact]
    public void ProviderReference_ResolvesConfiguredRegion()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .WithAwsProvider("123456789012", "us-west-2", aws => aws.WithIrsa("arn:aws:iam::123456789012:role/radius"))
            .WithRecipeParameters(p => p["region"] = RadiusProviderReference.AwsRegion);
        builder.AddRedis("cache");

        using var app = builder.Build();
        var bicep = Publish(app);

        Assert.Contains("region: 'us-west-2'", bicep);
    }

    // T026 — provider reference to an unconfigured provider fails at publish.
    [Fact]
    public void ProviderReference_Unconfigured_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p => p["region"] = RadiusProviderReference.AwsRegion);
        builder.AddRedis("cache");

        using var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => Publish(app));
        Assert.Contains("AWS", ex.Message);
    }

    // T031 — additive: no recipe parameters => no parameters key emitted.
    [Fact]
    public void NoRecipeParameters_ProducesNoParametersKey()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache");

        using var app = builder.Build();
        var bicep = Publish(app);

        Assert.DoesNotContain("parameters: {", bicep);
    }

    // Two recipe parameters bound to distinct Aspire parameters whose names sanitize to the same
    // Bicep identifier ("Radius" and "radiusenv" both map to "radiusenv") fail with ASPIRERADIUS028
    // instead of emitting duplicate `param` declarations.
    [Fact]
    public void CollidingSanitizedParameterIdentifiers_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var first = builder.AddParameter("Radius", "v1");
        var second = builder.AddParameter("radiusenv", "v2");
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p =>
            {
                p["a"] = first;
                p["b"] = second;
            });
        builder.AddRedis("cache");

        using var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => Publish(app));
        Assert.Contains("ASPIRERADIUS028", ex.Message);
    }

    // Regression: a recipe parameter named "radius" (which SanitizeIdentifier remaps to the Bicep
    // identifier "radiusenv") and a *distinct* container env-var parameter named "radiusenv" (which
    // NormalizeBicepIdentifier leaves as "radiusenv") both target the same Bicep identifier. They
    // must not be silently merged into one param (which would bind the container to the recipe
    // parameter's value); they fall through to a genuine collision surfaced as ASPIRERADIUS056.
    [Fact]
    public void RecipeAndContainerParams_CollidingIdentifiers_DistinctResources_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var recipeParam = builder.AddParameter("radius", "recipe-value");
        var envParam = builder.AddParameter("radiusenv", "env-value");
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p => p["apiKey"] = recipeParam);
        builder.AddContainer("api", "myapp/api", "latest")
            .WithEnvironment("API_KEY", envParam);

        using var app = builder.Build();

        var ex = Assert.Throws<InvalidOperationException>(() => Publish(app));
        Assert.Contains("ASPIRERADIUS056", ex.Message);
    }

    // The same Aspire parameter used as both a recipe parameter and a container env var produces a
    // single Bicep `param` (and one deploy binding), not a duplicate declaration.
    [Fact]
    public void RecipeAndContainerParams_SameResource_EmitSingleParam()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var shared = builder.AddParameter("shared", "v", secret: true);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p => p["apiKey"] = shared);
        builder.AddContainer("api", "myapp/api", "latest")
            .WithEnvironment("API_KEY", shared);

        using var app = builder.Build();
        var bicep = Publish(app);

        var occurrences = bicep.Split("param shared string").Length - 1;
        Assert.Equal(1, occurrences);
    }
}
