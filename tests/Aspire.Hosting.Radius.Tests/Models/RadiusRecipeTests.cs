// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Models;

namespace Aspire.Hosting.Radius.Tests.Models;

public class RadiusRecipeTests
{
    [Fact]
    public void RadiusRecipe_PropertyInitialization_SetsNameCorrectly()
    {
        var recipe = new RadiusRecipe { Name = "my-recipe" };

        Assert.Equal("my-recipe", recipe.Name);
    }

    [Fact]
    public void RadiusRecipe_RecipeLocation_DefaultsToNull()
    {
        var recipe = new RadiusRecipe { Name = "test" };

        Assert.Null(recipe.RecipeLocation);
    }

    [Fact]
    public void RadiusRecipe_RecipeLocation_CanBeSet()
    {
        var recipe = new RadiusRecipe
        {
            Name = "azure-redis-premium",
            RecipeLocation = "ghcr.io/myorg/recipes/redis-premium:v1"
        };

        Assert.Equal("ghcr.io/myorg/recipes/redis-premium:v1", recipe.RecipeLocation);
    }

    [Fact]
    public void RadiusRecipe_Parameters_DefaultsToEmptyDictionary()
    {
        var recipe = new RadiusRecipe { Name = "test" };

        Assert.NotNull(recipe.Parameters);
        Assert.Empty(recipe.Parameters);
    }

    [Fact]
    public void RadiusRecipe_Parameters_CanBePopulated()
    {
        var recipe = new RadiusRecipe
        {
            Name = "premium-redis",
            Parameters = { ["sku"] = "Premium", ["capacity"] = 2, ["enableTls"] = true }
        };

        Assert.Equal(3, recipe.Parameters.Count);
        Assert.Equal("Premium", recipe.Parameters["sku"]);
        Assert.Equal(2, recipe.Parameters["capacity"]);
        Assert.Equal(true, recipe.Parameters["enableTls"]);
    }

    [Fact]
    public void RadiusResourceCustomization_Defaults_ProvisioningIsAutomatic()
    {
        var customization = new RadiusResourceCustomization();

        Assert.Equal(ResourceProvisioning.Automatic, customization.Provisioning);
    }

    [Fact]
    public void RadiusResourceCustomization_Defaults_RecipeIsNull()
    {
        var customization = new RadiusResourceCustomization();

        Assert.Null(customization.Recipe);
    }

    [Fact]
    public void RadiusResourceCustomization_Defaults_ConnectionStringOverridesIsEmpty()
    {
        var customization = new RadiusResourceCustomization();

        Assert.NotNull(customization.ConnectionStringOverrides);
        Assert.Empty(customization.ConnectionStringOverrides);
    }

    [Fact]
    public void RadiusResourceCustomization_RecipeAndManual_AreBothSettable()
    {
        // Mutual exclusion is validated at Bicep generation time, not at model level.
        // Both can be set on the model simultaneously.
        var customization = new RadiusResourceCustomization
        {
            Recipe = new RadiusRecipe { Name = "custom" },
            Provisioning = ResourceProvisioning.Manual
        };

        Assert.NotNull(customization.Recipe);
        Assert.Equal(ResourceProvisioning.Manual, customization.Provisioning);
    }

    [Fact]
    public void RadiusResourceCustomization_ManualProvisioning_WithoutRecipe()
    {
        var customization = new RadiusResourceCustomization
        {
            Provisioning = ResourceProvisioning.Manual
        };

        Assert.Equal(ResourceProvisioning.Manual, customization.Provisioning);
        Assert.Null(customization.Recipe);
    }

    [Fact]
    public void RadiusResourceCustomization_AutomaticProvisioning_WithRecipe()
    {
        var customization = new RadiusResourceCustomization
        {
            Recipe = new RadiusRecipe
            {
                Name = "azure-redis-premium",
                RecipeLocation = "ghcr.io/myorg/recipes/redis-premium:v1",
                Parameters = { ["sku"] = "Premium" }
            }
        };

        Assert.Equal(ResourceProvisioning.Automatic, customization.Provisioning);
        Assert.NotNull(customization.Recipe);
        Assert.Equal("azure-redis-premium", customization.Recipe.Name);
    }

    [Fact]
    public void ResourceProvisioning_HasExpectedValues()
    {
        Assert.Equal(0, (int)ResourceProvisioning.Automatic);
        Assert.Equal(1, (int)ResourceProvisioning.Manual);
    }

    [Fact]
    public void RadiusResourceCustomization_RecipeAndManual_MutualExclusionValidatedAtPublish()
    {
        // Model allows both to be set simultaneously — mutual exclusion
        // is enforced at publish time (PublishAsRadiusResource validates this).
        // This test documents that the model layer does not throw, confirming
        // that validation is deferred to the publishing pipeline.
        var customization = new RadiusResourceCustomization
        {
            Recipe = new RadiusRecipe { Name = "custom" },
            Provisioning = ResourceProvisioning.Manual
        };

        // Both values coexist at the model level
        Assert.NotNull(customization.Recipe);
        Assert.Equal(ResourceProvisioning.Manual, customization.Provisioning);

        // The actual InvalidOperationException is thrown by PublishAsRadiusResource(),
        // tested in CustomRecipeTests.cs and ManualProvisioningTests.cs
    }
}
