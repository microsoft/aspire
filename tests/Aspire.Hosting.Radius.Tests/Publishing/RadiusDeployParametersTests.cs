// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Covers surfacing recipe-parameter bindings (name -&gt; <see cref="ParameterResource"/>) from the
/// build step so the deploy step can forward each valueless Bicep <c>param</c> to
/// <c>rad deploy --parameters</c>, including secret redaction of the resolved values.
/// </summary>
public class RadiusDeployParametersTests
{
    [Fact]
    public void BuildOptions_SurfacesBindings_ForParameterBoundRecipeParameter()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("recipeSecret", "TopSecretValue", secret: true);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p => p["apiKey"] = secret);
        builder.AddRedis("cache");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);

        var options = new RadiusBicepPublishingContext(radiusEnv).BuildOptions(model);

        // The deploy step needs the param-identifier -> ParameterResource mapping to resolve a
        // value for the otherwise-valueless secure `param recipeSecret`.
        var binding = Assert.Single(options.RecipeParameterBindings);
        Assert.Equal("recipeSecret", binding.Key);
        Assert.Same(secret.Resource, binding.Value);
        Assert.True(binding.Value.Secret);
    }

    [Fact]
    public async Task ResolvedDeployParameters_RedactSecretValuesInLoggedCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var secret = builder.AddParameter("recipeSecret", "TopSecretValue", secret: true);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(p => p["apiKey"] = secret);
        builder.AddRedis("cache");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);

        var options = new RadiusBicepPublishingContext(radiusEnv).BuildOptions(model);

        // Simulate the deploy step: build `--parameters id=value` tokens and collect secret values.
        var args = new List<string> { "deploy", "app.bicep" };
        var secretValues = new List<string>();
        foreach (var (identifier, parameter) in options.RecipeParameterBindings)
        {
            var value = await parameter.GetValueAsync(default) ?? string.Empty;
            args.Add("--parameters");
            args.Add($"{identifier}={value}");
            if (parameter.Secret)
            {
                secretValues.Add(value);
            }
        }

        var command = string.Join(' ', args);
        Assert.Contains("recipeSecret=TopSecretValue", command);

        var redacted = RadCredentialRegisterStep.RedactSecretValues(command, secretValues);
        Assert.DoesNotContain("TopSecretValue", redacted);
        Assert.Contains("recipeSecret=***", redacted);
    }
}
