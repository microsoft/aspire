// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Tests for L2: legacy parent emission.
///
/// When a resource falls back to a legacy <c>Applications.*</c> type, it must
/// be parented to <c>Applications.Core/environments</c> + <c>Applications.Core/applications</c>
/// rather than the <c>Radius.Core/*</c> UDT parents. The UDT pair and the legacy
/// pair share the same resource <c>name:</c> values; only the Bicep identifiers differ.
/// </summary>
public class LegacyFallbackEmissionTests
{
    private static string GenerateBicep(Action<IDistributedApplicationBuilder> configure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        configure(builder);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        return context.GenerateBicep(model);
    }

    [Fact]
    public void RedisOnly_EmitsLegacyParentsAndNoUdtResources()
    {
        var bicep = GenerateBicep(b => b.AddRedis("cache"));

        // Legacy parents emitted
        Assert.Contains("Applications.Core/environments@", bicep);
        Assert.Contains("Applications.Core/applications@", bicep);

        // Legacy env carries inline recipes with legacy schema keys
        Assert.Contains("templateKind:", bicep);
        Assert.Contains("templatePath:", bicep);

        // Pure-legacy publish: UDT parents + recipe pack must NOT appear.
        Assert.DoesNotContain("Radius.Core/environments@", bicep);
        Assert.DoesNotContain("Radius.Core/applications@", bicep);
        Assert.DoesNotContain("Radius.Core/recipePacks@", bicep);
        Assert.DoesNotContain("resource recipepack ", bicep);

        // With no UDT chain, legacy env/app take the unsuffixed identifiers.
        Assert.Contains("myenv.id", bicep);
        Assert.Contains("app.id", bicep);
        Assert.DoesNotContain("myenv_legacy", bicep);
        Assert.DoesNotContain("app_legacy", bicep);
    }

    [Fact]
    public void LegacyEnvironment_SharesResourceNameWithUdt()
    {
        var bicep = GenerateBicep(b =>
        {
            b.AddRedis("cache");
            b.AddPostgres("db");
        });

        // Both envs emit `name: 'myenv'` (the UDT one and the legacy one).
        var udtEnvIdx = bicep.AsSpan().IndexOf("resource myenv ".AsSpan());
        var legacyEnvIdx = bicep.AsSpan().IndexOf("resource myenv_legacy ".AsSpan());

        Assert.True(udtEnvIdx >= 0, "Expected UDT env identifier `myenv`");
        Assert.True(legacyEnvIdx >= 0, "Expected legacy env identifier `myenv_legacy`");

        // Both declarations should contain `name: 'myenv'`
        var nameOccurrences = System.Text.RegularExpressions.Regex.Matches(
            bicep, @"name:\s*'myenv'").Count;
        Assert.True(nameOccurrences >= 2,
            $"Expected at least 2 occurrences of `name: 'myenv'`, saw {nameOccurrences}");
    }

    [Fact]
    public void MixedLegacyAndUdt_EmitsBothParentPairs()
    {
        var bicep = GenerateBicep(b =>
        {
            b.AddRedis("cache");     // legacy fallback → Applications.Datastores/redisCaches
            b.AddPostgres("db");     // UDT → Radius.Data/postgreSQL (or similar)
            b.AddContainer("api", "myapp/api", "latest");
        });

        // Both UDT and legacy parent pairs present.
        Assert.Contains("Radius.Core/environments@", bicep);
        Assert.Contains("Radius.Core/applications@", bicep);
        Assert.Contains("Applications.Core/environments@", bicep);
        Assert.Contains("Applications.Core/applications@", bicep);

        // UDT recipe pack uses new schema; legacy env uses legacy schema.
        Assert.Contains("recipeKind:", bicep);
        Assert.Contains("recipeLocation:", bicep);
        Assert.Contains("templateKind:", bicep);
        Assert.Contains("templatePath:", bicep);
    }

    [Fact]
    public void UdtOnly_DoesNotEmitLegacyParents()
    {
        var bicep = GenerateBicep(b =>
        {
            b.AddPostgres("db");
            b.AddContainer("api", "myapp/api", "latest");
        });

        // Legacy parents must NOT appear when no legacy resource is present.
        Assert.DoesNotContain("Applications.Core/environments@", bicep);
        Assert.DoesNotContain("Applications.Core/applications@", bicep);
        Assert.DoesNotContain("myenv_legacy", bicep);
        Assert.DoesNotContain("app_legacy", bicep);

        // UDT pair still emitted.
        Assert.Contains("Radius.Core/environments@", bicep);
        Assert.Contains("Radius.Core/applications@", bicep);
    }

    [Fact]
    public void LegacyResource_DoesNotReferenceUdtParents()
    {
        var bicep = GenerateBicep(b =>
        {
            b.AddRedis("cache");
            b.AddPostgres("db");
        });

        var cacheBlock = ExtractResourceBlock(bicep, "cache");

        // The cache block must reference legacy parents, not UDT parents.
        Assert.Contains("myenv_legacy.id", cacheBlock);
        Assert.Contains("app_legacy.id", cacheBlock);

        // Use leading-space prefix so we don't match `myenv_legacy.id`.
        Assert.DoesNotContain(" myenv.id", cacheBlock);
        Assert.DoesNotContain(": myenv.id", cacheBlock);
    }

    [Fact]
    public void UdtResource_DoesNotReferenceLegacyParents()
    {
        var bicep = GenerateBicep(b =>
        {
            b.AddRedis("cache");
            b.AddPostgres("db");
        });

        var dbBlock = ExtractResourceBlock(bicep, "db");

        Assert.DoesNotContain("myenv_legacy.id", dbBlock);
        Assert.DoesNotContain("app_legacy.id", dbBlock);
    }

    private static string ExtractResourceBlock(string bicep, string identifier)
    {
        var prefix = $"resource {identifier} ";
        var start = bicep.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Expected `{prefix}` in generated Bicep");

        // Find the matching closing brace of this resource declaration.
        var braceStart = bicep.IndexOf('{', start);
        Assert.True(braceStart >= 0);

        var depth = 0;
        for (var i = braceStart; i < bicep.Length; i++)
        {
            if (bicep[i] == '{')
            {
                depth++;
            }
            else if (bicep[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return bicep.Substring(start, i - start + 1);
                }
            }
        }

        return bicep.Substring(start);
    }

    [Fact]
    public void LegacyEnvironment_EmitsComputeKindKubernetes()
    {
        var bicep = GenerateBicep(b => b.AddRedis("cache"));

        // Legacy environment needs properties.compute.kind/namespace.
        Assert.Contains("compute:", bicep);
        Assert.Contains("kind: 'kubernetes'", bicep);
    }

    [Fact]
    public void LegacyRedis_HasDefaultRecipeTemplate()
    {
        var bicep = GenerateBicep(b => b.AddRedis("cache"));

        Assert.Contains("ghcr.io/radius-project/recipes/local-dev/rediscaches", bicep);
    }

    [Fact]
    public void LegacyRecipes_MultipleNamedRecipesPerType_AllEmitted()
    {
        // Two legacy resources of the same type but different custom recipe
        // names must both register in the legacy env's
        // `recipes[type][recipeName]` map — neither should overwrite the other.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");

        builder.AddRedis("cacheA")
            .PublishAsRadiusResource(c =>
            {
                c.Recipe = new Aspire.Hosting.Radius.Models.RadiusRecipe
                {
                    Name = "recipeA",
                    RecipeLocation = "ghcr.io/myorg/recipes/redis-a:latest",
                };
            });

        builder.AddRedis("cacheB")
            .PublishAsRadiusResource(c =>
            {
                c.Recipe = new Aspire.Hosting.Radius.Models.RadiusRecipe
                {
                    Name = "recipeB",
                    RecipeLocation = "ghcr.io/myorg/recipes/redis-b:latest",
                };
            });

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // Both named recipes must appear in the legacy env's recipes block.
        Assert.Contains("ghcr.io/myorg/recipes/redis-a:latest", bicep);
        Assert.Contains("ghcr.io/myorg/recipes/redis-b:latest", bicep);
        Assert.Contains("recipeA:", bicep);
        Assert.Contains("recipeB:", bicep);
    }
}
