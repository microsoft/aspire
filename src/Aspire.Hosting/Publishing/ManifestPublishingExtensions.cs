// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.Text.Json;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Publishing;

/// <summary>
/// Provides extension methods for adding manifest publishing to the pipeline.
/// </summary>
internal static class ManifestPublishingExtensions
{
    /// <summary>
    /// Adds a step to the pipeline that publishes an Aspire manifest file.
    /// </summary>
    /// <param name="pipeline">The pipeline to add the manifest publishing step to.</param>
    /// <returns>The pipeline for chaining.</returns>
    [AspireExportIgnore(Reason = "Manifest publishing is an internal pipeline step and not part of the polyglot AppHost surface.")]
    public static IDistributedApplicationPipeline AddManifestPublishing(this IDistributedApplicationPipeline pipeline)
    {
        var step = new PipelineStep
        {
            Name = WellKnownPipelineSteps.PublishManifest,
            Description = "Publishes the Aspire application model as a JSON manifest file.",
            // A legacy file target can emit Dockerfiles and Bicep modules beside the manifest.
            // Those siblings are not represented by the single-file primary output, so only the
            // directory form can safely participate in relocated publishing.
            OutputPathRelocationSupportEvaluator = primaryOutput => primaryOutput.Kind == PipelineOutputKind.Directory,
            Action = async context =>
            {
                var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("Aspire.Hosting.Publishing.ManifestPublisher");
                var pipelineOptions = context.Services.GetRequiredService<IOptions<PipelineOptions>>();
                var executionContext = context.Services.GetRequiredService<DistributedApplicationExecutionContext>();

                if (pipelineOptions.Value.OutputPath == null)
                {
                    throw new DistributedApplicationException(
                        "The '--output-path [path]' option was not specified even though manifest publishing was requested.");
                }

                var primaryOutput = context.Outputs.PrimaryOutput;
                var outputPath = GetManifestPath(primaryOutput.OutputPath, primaryOutput.Kind);
                var logicalManifestPath = GetManifestPath(primaryOutput.LogicalTargetPath, primaryOutput.Kind);

                var parentDirectory = Directory.GetParent(outputPath);
                if (!Directory.Exists(parentDirectory!.FullName))
                {
                    // Create the directory if it does not exist
                    Directory.CreateDirectory(parentDirectory.FullName);
                }

                using var stream = new FileStream(outputPath, FileMode.Create);
                using var jsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                var publishingContext = new ManifestPublishingContext(
                    executionContext,
                    outputPath,
                    logicalManifestPath,
                    jsonWriter,
                    context.CancellationToken);

                await publishingContext.WriteModel(context.Model, context.CancellationToken).ConfigureAwait(false);

                var fullyQualifiedPath = Path.GetFullPath(outputPath);
                logger.LogInformation("Published manifest to: {ManifestPath}", fullyQualifiedPath);
            }
        };
        pipeline.AddStep(step);

        return pipeline;
    }

    private static string GetManifestPath(string outputPath, PipelineOutputKind kind)
    {
        if (kind == PipelineOutputKind.File)
        {
            return outputPath;
        }

        // The registry preserves the legacy direct-manifest file-path contract by assigning
        // File kind. Shared primary outputs are directories and use the standard manifest name.
        return Path.Combine(outputPath, "aspire-manifest.json");
    }
}
