// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

}
