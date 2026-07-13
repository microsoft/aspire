// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Radius.ResourceMapping;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Orchestrates Bicep generation for a Radius environment: walks the
/// <see cref="DistributedApplicationModel"/>, builds an Azure.Provisioning
/// AST via <see cref="RadiusInfrastructureBuilder"/>, compiles to Bicep via
/// <c>Infrastructure.Build().Compile()</c>, and writes <c>app.bicep</c> +
/// <c>bicepconfig.json</c> to the output directory.
/// </summary>
internal sealed class RadiusBicepPublishingContext
{
    private readonly RadiusEnvironmentResource _environment;

    internal RadiusBicepPublishingContext(RadiusEnvironmentResource environment)
    {
        _environment = environment;
    }

    /// <summary>
    /// Creates a <see cref="PipelineStep"/> that generates Radius Bicep during publish.
    /// </summary>
    internal PipelineStep CreatePipelineStep()
    {
        var step = new PipelineStep
        {
            Name = $"publish-radius-{_environment.Name}",
            Description = $"Publish Radius environment '{_environment.Name}' as Bicep",
            Action = ExecuteAsync
        };
        step.RequiredBy(WellKnownPipelineSteps.Publish);
        return step;
    }

    internal async Task ExecuteAsync(PipelineStepContext context)
    {
        var model = context.Model;
        var cancellationToken = context.CancellationToken;
        var logger = context.Logger;

        logger.LogInformation(
            "Starting Bicep generation for Radius environment '{EnvironmentName}'",
            _environment.Name);

        var task = await context.ReportingStep.CreateTaskAsync(
            $"Generating Bicep for Radius environment '{_environment.Name}'...",
            cancellationToken).ConfigureAwait(false);

        try
        {
            // Build the infrastructure AST
            var typeMapper = new ResourceTypeMapper(
                context.Services.GetRequiredService<ILoggerFactory>().CreateLogger<ResourceTypeMapper>());
            var builder = new RadiusInfrastructureBuilder(_environment, model, typeMapper, logger);

            var options = await builder.BuildAsync(context.ExecutionContext, cancellationToken).ConfigureAwait(false);

            var resourceCount = options.Environments.Count + options.Applications.Count
                + options.RecipePacks.Count + options.ResourceTypeInstances.Count
                + options.Containers.Count;

            logger.LogInformation(
                "Built Radius infrastructure AST with {ResourceCount} resources for environment '{EnvironmentName}'",
                resourceCount,
                _environment.Name);

            // Log recipe pack summary
            LogRecipePackSummary(options, logger);

            // Compile Bicep via Azure.Provisioning SDK pipeline
            var bicepContent = BicepPostProcessor.CompileBicep(options, _environment.Name, logger);
            var bicepConfigContent = BicepPostProcessor.RenderBicepConfig();

            // Write output files
            var outputDir = PublishingContextUtils.GetEnvironmentOutputPath(context, _environment);

            Directory.CreateDirectory(outputDir);

            var bicepPath = Path.Combine(outputDir, "app.bicep");
            var configPath = Path.Combine(outputDir, "bicepconfig.json");

            await File.WriteAllTextAsync(bicepPath, bicepContent, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(configPath, bicepConfigContent, cancellationToken).ConfigureAwait(false);

            // Copy each committed (encrypted) SealedSecret manifest into a per-store subdirectory of
            // the environment output so a cross-machine `deploy` can apply the self-contained
            // artifact when the author's source manifest path is absent. No-op when none declared.
            CopySealedSecretManifests(options, outputDir, logger);

            logger.LogInformation(
                "Bicep generation complete for environment '{EnvironmentName}': {BicepPath}",
                _environment.Name,
                bicepPath);

            await task.CompleteAsync(
                $"Bicep generated: {bicepPath}",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Bicep generation failed for Radius environment '{EnvironmentName}'",
                _environment.Name);

            context.ReportingStep.Log(LogLevel.Error, ex.Message);
            throw;
        }
    }

    internal RadiusInfrastructureOptions BuildOptions(DistributedApplicationModel model, ILogger? logger = null)
    {
        logger ??= Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        var typeMapper = new ResourceTypeMapper(logger);
        var executionContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish);
        var builder = new RadiusInfrastructureBuilder(_environment, model, typeMapper, logger);

        return builder.BuildAsync(executionContext, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal string GenerateBicep(DistributedApplicationModel model, ILogger? logger = null)
    {
        logger ??= Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var options = BuildOptions(model, logger);

        return BicepPostProcessor.CompileBicep(options, _environment.Name, logger);
    }

    private static void LogRecipePackSummary(RadiusInfrastructureOptions options, ILogger logger)
    {
        foreach (var pack in options.RecipePacks.OfType<RadiusRecipePackConstruct>())
        {
            var recipeCount = pack.Recipes.Count;
            var recipeTypes = string.Join(", ", pack.Recipes.Keys);
            logger.LogInformation(
                "Recipe pack '{PackName}' contains {RecipeCount} recipe(s): {RecipeTypes}",
                pack.BicepIdentifier,
                recipeCount,
                recipeTypes);
        }
    }

    // Writes each committed (encrypted) SealedSecret manifest into a per-store subdirectory
    // (sealed-secrets/<storeName>/<file>) next to the emitted app.bicep so the published artifact
    // is self-contained and the deploy step can apply it. Namespacing by the unique store name means
    // two stores whose source manifests share a file name (but live in different source directories)
    // cannot silently overwrite each other. The manifest is already encrypted, and we write the exact
    // bytes validated at build time so a later source-file swap cannot change the published artifact.
    // Missing manifests were already rejected at build time (ASPIRERADIUS044).
    private static void CopySealedSecretManifests(RadiusInfrastructureOptions options, string outputDir, ILogger logger)
    {
        foreach (var (storeName, manifest) in options.SealedSecretManifests)
        {
            var destination = Secrets.SealedSecretArtifact.ResolvePath(outputDir, storeName, manifest.SourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, manifest.Content.ToArray());
            logger.LogInformation("Copied SealedSecret manifest to {Destination}", destination);
        }
    }

}
