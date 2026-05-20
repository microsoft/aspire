// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Radius.Publishing.Constructs;
using Aspire.Hosting.Radius.ResourceMapping;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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

            var options = builder.Build();

            var resourceCount = options.Environments.Count + options.Applications.Count
                + options.RecipePacks.Count + options.ResourceTypeInstances.Count
                + options.Containers.Count + options.LegacyContainers.Count;

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

    /// <summary>
    /// Generates Bicep content from a model without running in the pipeline.
    /// Used for testing.
    /// </summary>
    internal string GenerateBicep(DistributedApplicationModel model, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        // Reuse the caller's logger for the mapper so legacy-fallback / unmapped-type
        // diagnostics (e.g., the FakeLogger pattern in ResourceTypeMapperTests) are
        // observable. Allocating a new LoggerFactory here would leak provider state on
        // every call and would also drop any logger the caller supplied.
        var typeMapper = new ResourceTypeMapper(logger);
        var builder = new RadiusInfrastructureBuilder(_environment, model, typeMapper, logger);

        var options = builder.Build();
        return BicepPostProcessor.CompileBicep(options, _environment.Name, logger);
    }

    /// <summary>
    /// Builds the <see cref="RadiusInfrastructureOptions"/> AST from a model.
    /// Used for testing.
    /// </summary>
    internal RadiusInfrastructureOptions BuildOptions(DistributedApplicationModel model, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        // See GenerateBicep for the rationale on reusing the caller's logger.
        var typeMapper = new ResourceTypeMapper(logger);
        var builder = new RadiusInfrastructureBuilder(_environment, model, typeMapper, logger);

        return builder.Build();
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
}
