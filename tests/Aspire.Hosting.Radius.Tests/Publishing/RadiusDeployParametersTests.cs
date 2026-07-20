// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

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
    public async Task WriteDeployParametersFile_WritesOwnerOnlyArmParameterFile()
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

        // BuildOptions attaches the RadiusDeployParametersAnnotation that the deploy step reads.
        _ = new RadiusBicepPublishingContext(radiusEnv).BuildOptions(model);

        // Exercise the *production* helper: the deploy step no longer passes `--parameters id=value`
        // on the command line (which would expose secrets); it writes an owner-only ARM JSON file and
        // passes `--parameters @<file>`. Assert that file contract rather than the obsolete flow.
        var step = new RadiusDeploymentPipelineStep(radiusEnv);
        var path = await step.WriteDeployParametersFileAsync(NullLogger.Instance, default);
        Assert.NotNull(path);

        try
        {
            var json = await File.ReadAllTextAsync(path);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            Assert.Equal(
                "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
                root.GetProperty("$schema").GetString());
            Assert.Equal("1.0.0.0", root.GetProperty("contentVersion").GetString());
            Assert.Equal(
                "TopSecretValue",
                root.GetProperty("parameters").GetProperty("recipeSecret").GetProperty("value").GetString());

            // The file holds resolved secret material, so on Unix it must be owner read/write only.
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(path);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
            }

            // Cleanup removes the file (the deploy step deletes it in a finally block).
            RadiusDeploymentPipelineStep.DeleteDeployParametersFile(path, NullLogger.Instance);
            Assert.False(File.Exists(path));
        }
        finally
        {
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task WriteDeployParametersFile_ReturnsNull_WhenNoRecipeParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddRadiusEnvironment("myenv");
        builder.AddRedis("cache");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);
        _ = new RadiusBicepPublishingContext(radiusEnv).BuildOptions(model);

        var step = new RadiusDeploymentPipelineStep(radiusEnv);
        var path = await step.WriteDeployParametersFileAsync(NullLogger.Instance, default);

        Assert.Null(path);
    }
}
