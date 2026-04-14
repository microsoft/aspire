// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

public class BicepIdentifierTests
{
    [Theory]
    [InlineData("cache", "cache")]
    [InlineData("my-cache", "my_cache")]
    [InlineData("my.cache", "my_cache")]
    [InlineData("my cache", "my_cache")]
    [InlineData("123start", "r123start")]
    [InlineData("", "resource")]
    [InlineData("valid_name", "valid_name")]
    [InlineData("CamelCase", "CamelCase")]
    public void SanitizeIdentifier_ProducesValidBicepIdentifiers(string input, string expected)
    {
        var result = BicepPostProcessor.SanitizeIdentifier(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeIdentifier_RadiusName_SanitizedToRadiusenv()
    {
        // "radius" collides with `extension radius` directive
        var result = BicepPostProcessor.SanitizeIdentifier("radius");
        Assert.Equal("radiusenv", result);
    }

    [Fact]
    public void SanitizeIdentifier_RadiusCaseInsensitive()
    {
        Assert.Equal("radiusenv", BicepPostProcessor.SanitizeIdentifier("Radius"));
        Assert.Equal("radiusenv", BicepPostProcessor.SanitizeIdentifier("RADIUS"));
    }

    [Fact]
    public void HyphenatedResourceNames_HandledInBicep()
    {
        // When a resource name contains hyphens, the SDK handles quoting automatically
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        var cache = builder.AddRedis("my-cache");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithReference(cache);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        var context = new RadiusBicepPublishingContext(radiusEnv);
        var bicep = context.GenerateBicep(model);

        // The connection key for a hyphenated name should be present in the output
        Assert.Contains("my-cache", bicep);
    }
}
