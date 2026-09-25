// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // Type or member is obsolete
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES004

using System.Text.Json;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Helpers;

internal sealed class JsonDocumentManifestPublisher(
    ILogger<ManifestPublisher> logger,
    IOptions<PublishingOptions> options,
    DistributedApplicationExecutionContext executionContext
    ) : ManifestPublisher(logger, options, executionContext)
{
    protected override async Task PublishInternalAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new() { Indented = true });

        await WriteManifestAsync(model, writer, cancellationToken).ConfigureAwait(false);

        stream.Seek(0, SeekOrigin.Begin);
        _manifestDocument = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private JsonDocument? _manifestDocument;

    public JsonDocument ManifestDocument
    {
        get
        {
            return _manifestDocument ?? throw new InvalidOperationException("JsonDocument not available.");
        }
    }
}

/// <summary>
/// Service that stores the manifest document in memory for test purposes.
/// </summary>
internal sealed class JsonDocumentManifestStore
{
    private JsonDocument? _manifestDocument;

    public JsonDocument ManifestDocument
    {
        get => _manifestDocument ?? throw new InvalidOperationException("JsonDocument not available.");
        set => _manifestDocument = value;
    }
}

/// <summary>
/// Provides extension methods for adding JSON manifest publishing to the pipeline.
/// </summary>
internal static class JsonDocumentManifestPublishingExtensions
{
    /// <summary>
    /// Adds a step to the pipeline that publishes an Aspire manifest as a JsonDocument to memory.
    /// </summary>
    /// <param name="pipeline">The pipeline to add the JSON manifest publishing step to.</param>
    /// <returns>The pipeline for chaining.</returns>
    public static IDistributedApplicationPipeline AddJsonDocumentManifestPublishing(this IDistributedApplicationPipeline pipeline)
    {
        var step = new PipelineStep
        {
            Name = "publish-json-manifest",
            Action = async context =>
            {
                var executionContext = context.Services.GetRequiredService<DistributedApplicationExecutionContext>();
                var manifestStore = context.Services.GetRequiredService<JsonDocumentManifestStore>();

                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream, new() { Indented = true });

                var outputService = context.Services.GetRequiredService<IPipelineOutputService>();
                var manifestPath = outputService.GetOutputDirectory();
                var publishingContext = new ManifestPublishingContext(executionContext, manifestPath, writer, context.CancellationToken);

                await publishingContext.WriteModel(context.Model, context.CancellationToken).ConfigureAwait(false);

                stream.Seek(0, SeekOrigin.Begin);
                manifestStore.ManifestDocument = await JsonDocument.ParseAsync(stream, cancellationToken: context.CancellationToken).ConfigureAwait(false);
            }
        };
        pipeline.AddStep(step);

        return pipeline;
    }
}
