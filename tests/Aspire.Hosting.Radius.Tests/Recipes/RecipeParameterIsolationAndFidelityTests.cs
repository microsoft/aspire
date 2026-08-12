// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Annotations;

namespace Aspire.Hosting.Radius.Tests.Recipes;

public class RecipeParameterIsolationAndFidelityTests
{
    // T029 — per-environment isolation: parameters on one environment do not leak to another.
    [Fact]
    public void RecipeParameters_AreScopedPerEnvironment()
    {
        var builder = DistributedApplication.CreateBuilder();
        var dev = builder.AddRadiusEnvironment("dev").WithRecipeParameters(p => p["region"] = "dev-region");
        var prod = builder.AddRadiusEnvironment("prod").WithRecipeParameters(p => p["region"] = "prod-region");

        var devAnn = dev.Resource.Annotations.OfType<RadiusRecipeParametersAnnotation>().Single();
        var prodAnn = prod.Resource.Annotations.OfType<RadiusRecipeParametersAnnotation>().Single();

        Assert.NotSame(devAnn, prodAnn);
        Assert.Equal("dev-region", devAnn.EnvironmentWide["region"]);
        Assert.Equal("prod-region", prodAnn.EnvironmentWide["region"]);
        Assert.False(devAnn.EnvironmentWide.ContainsValue("prod-region"));
    }
}
