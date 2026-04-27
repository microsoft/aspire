// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

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
            Name = "publish-manifest",
            Description = "Publishes the Aspire application model as a JSON manifest file.",
            Action = async context =>
            {
                var loggerFactory = context.Services.GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("Aspire.Hosting.Publishing.ManifestPublisher");
                var pipelineOptions = context.Services.GetRequiredService<IOptions<PipelineOptions>>();
                var executionContext = context.Services.GetRequiredService<DistributedApplicationExecutionContext>();

                if (pipelineOptions.Value.OutputPath == null)
                {
                    throw new DistributedApplicationException(
                        "The '--output-path [path]' option was not specified even though manifest publishing was requested."
                        );
                }

                var outputPath = pipelineOptions.Value.OutputPath;

                if (!outputPath.EndsWith(".json"))
                {
                    // If the manifest path ends with .json we assume that the output path was specified
                    // as a filename. If not, we assume that the output path was specified as a directory
                    // and append aspire-manifest.json to the path. This is so that we retain backwards
                    // compatibility with AZD, but also support manifest publishing via the Aspire CLI
                    // where the output path is a directory (since not all publishers use a manifest).
                    outputPath = Path.Combine(outputPath, "aspire-manifest.json");
                }

                var parentDirectory = Directory.GetParent(outputPath);
                if (!Directory.Exists(parentDirectory!.FullName))
                {
                    // Create the directory if it does not exist
                    Directory.CreateDirectory(parentDirectory.FullName);
                }

                using var stream = new FileStream(outputPath, FileMode.Create);
                using var jsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

                var manifestPath = outputPath;
                var publishingContext = new ManifestPublishingContext(executionContext, manifestPath, jsonWriter, context.CancellationToken);

                await publishingContext.WriteModel(context.Model, context.CancellationToken).ConfigureAwait(false);

                var fullyQualifiedPath = Path.GetFullPath(outputPath);
                logger.LogInformation("Published manifest to: {ManifestPath}", fullyQualifiedPath);
            }
        };
        pipeline.AddStep(step);

        return pipeline;
    }
}
