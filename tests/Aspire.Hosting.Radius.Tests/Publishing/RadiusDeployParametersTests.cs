// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.ResourceMapping;
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

    /// <summary>
    /// One secret parameter used by <em>both</em> a container env var and a type-scoped recipe
    /// parameter is a valid model, but each allocator used to declare its own Bicep <c>param</c>
    /// for it — the two spellings normalize to the same identifier, so the second allocation tripped
    /// the identifier-collision guard (ASPIRERADIUS056) on a graph that should publish. The
    /// allocators now reuse each other's declaration in both directions, so ordering cannot matter.
    /// </summary>
    [Fact]
    public void ParameterSharedByContainerEnvironmentAndRecipeParameter_PublishesWithOneDeclaration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var shared = builder.AddParameter("sharedSecret", "TopSecretValue", secret: true);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(RadiusResourceTypes.SecuritySecrets, p => p["apiKey"] = shared);
        builder.AddContainer("api", "myapp/api", "latest")
            .WithEnvironment("API_KEY", shared);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);

        var bicep = new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);

        // Exactly one `param sharedSecret` declaration, and it stays secure so the value is never
        // written into the artifact or printed in deploy logs.
        var declarations = Regex.Matches(bicep, @"^@secure\(\)\r?\n\s*param sharedSecret ", RegexOptions.Multiline);
        Assert.Single(declarations);
    }

    /// <summary>
    /// `radius` is reserved: a bare `param radius` collides with the `extension radius` alias, so
    /// the identifier has to be `radiusenv`. Because the env-var and recipe-parameter allocators
    /// reuse each other's declarations, a parameter reached through either path must produce the
    /// same reserved-aware identifier — using two different normalizers would emit invalid Bicep
    /// that no collision check catches.
    /// </summary>
    [Fact]
    public void ParameterNamedRadius_UsesTheReservedAwareIdentifier()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var shared = builder.AddParameter("radius", "TopSecretValue", secret: true);
        builder.AddRadiusEnvironment("myenv")
            .WithRecipeParameters(RadiusResourceTypes.SecuritySecrets, p => p["apiKey"] = shared);
        builder.AddContainer("api", "myapp/api", "latest")
            .WithEnvironment("API_KEY", shared);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);

        var bicep = new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);

        var declarations = Regex.Matches(bicep, @"^@secure\(\)\r?\n\s*param radiusenv ", RegexOptions.Multiline);
        Assert.Single(declarations);
    }

    /// <summary>
    /// Reaches the env allocator without a recipe parameter, so it must apply the reserved-aware
    /// normalization itself rather than inheriting it by reusing a recipe-allocated declaration —
    /// the env-plus-recipe test above would still pass if this branch regressed, because recipe
    /// parameters are allocated first and the env allocator would simply reuse that result.
    /// </summary>
    [Fact]
    public void ParameterNamedRadius_UsedOnlyByAContainerEnvironmentVariable_UsesTheReservedAwareIdentifier()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var reserved = builder.AddParameter("radius", "TopSecretValue", secret: true);
        builder.AddRadiusEnvironment("myenv");
        builder.AddContainer("api", "myapp/api", "latest")
            .WithEnvironment("API_KEY", reserved);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var radiusEnv = model.Resources.OfType<RadiusEnvironmentResource>().First();
        RadiusTestHelper.AttachDeploymentTargets(radiusEnv, model);

        var bicep = new RadiusBicepPublishingContext(radiusEnv).GenerateBicep(model);

        // A bare `param radius` would collide with the `extension radius` alias, so assert on the
        // complete set of emitted parameter names rather than only on the expected one.
        var declared = Regex.Matches(bicep, @"^\s*param (?<name>\w+) ", RegexOptions.Multiline)
            .Select(match => match.Groups["name"].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["radiusenv"], declared);

        // The deploy step keys the generated parameter file by the emitted Bicep identifier, so the
        // recorded key has to be that identifier, not the Aspire parameter name.
        var annotation = Assert.Single(radiusEnv.Annotations.OfType<RadiusDeployParametersAnnotation>());
        var binding = Assert.Single(annotation.Parameters);
        Assert.Equal("radiusenv", binding.Key);
        Assert.Same(reserved.Resource, binding.Value);
    }
}
