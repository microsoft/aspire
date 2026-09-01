// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Radius.Annotations;

namespace Aspire.Hosting.Radius.Tests.Recipes;

public class WithRecipeParametersTests
{
    [Fact]
    public void EnvironmentWide_RegistersAnnotation_AndReturnsBuilder()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        var returned = env.WithRecipeParameters(p => p["vpcId"] = "vpc-1");

        Assert.Same(env, returned);
        var ann = env.Resource.Annotations.OfType<RadiusRecipeParametersAnnotation>().Single();
        Assert.Equal("vpc-1", ann.EnvironmentWide["vpcId"]);
        Assert.Empty(ann.ByResourceType);
    }

    [Fact]
    public void RepeatedCalls_SameScope_Merge_LastWriteWinsPerKey()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        env.WithRecipeParameters(p => { p["a"] = 1; p["b"] = 2; });
        env.WithRecipeParameters(p => { p["b"] = 99; p["c"] = 3; });

        var ann = env.Resource.Annotations.OfType<RadiusRecipeParametersAnnotation>().Single();
        Assert.Equal(1, ann.EnvironmentWide["a"]);
        Assert.Equal(99, ann.EnvironmentWide["b"]);
        Assert.Equal(3, ann.EnvironmentWide["c"]);
    }

    [Fact]
    public void ResourceTypeScope_RegistersUnderType()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        env.WithRecipeParameters("Radius.Data/redisCaches", p => p["sku"] = "Standard");

        var ann = env.Resource.Annotations.OfType<RadiusRecipeParametersAnnotation>().Single();
        Assert.Empty(ann.EnvironmentWide);
        Assert.Equal("Standard", ann.ByResourceType["Radius.Data/redisCaches"]["sku"]);
    }

    [Fact]
    public void EmptyKey_ThrowsArgumentException()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentException>(() => env.WithRecipeParameters(p => p["  "] = "x"));
    }

    [Fact]
    public void BlankKeyInCall_DoesNotApplyEarlierKeys_MergeIsTransactional()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        // A blank key later in the same call must abort the whole merge; mutating as we validate
        // would leave "good" applied, so a caller that catches and retries would publish parameters
        // from a failed call.
        Assert.Throws<ArgumentException>(() => env.WithRecipeParameters(p =>
        {
            p["good"] = 1;
            p["  "] = 2;
        }));

        var ann = env.Resource.Annotations.OfType<RadiusRecipeParametersAnnotation>().SingleOrDefault();
        Assert.True(ann is null || !ann.EnvironmentWide.ContainsKey("good"));
    }

    [Fact]
    public void NullArguments_Throw()
    {
        var builder = DistributedApplication.CreateBuilder();
        var env = builder.AddRadiusEnvironment("radius");

        Assert.Throws<ArgumentNullException>(() => env.WithRecipeParameters(configure: null!));
        Assert.Throws<ArgumentException>(() => env.WithRecipeParameters("", p => p["a"] = 1));
    }
}
