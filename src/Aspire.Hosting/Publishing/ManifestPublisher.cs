// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable CS0618 // Type or member is obsolete

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Publishing;

[Obsolete("ManifestPublisher is obsolete. Use the publish-manifest pipeline step.")]
internal class ManifestPublisher(ILogger<ManifestPublisher> logger,
                               IOptions<PublishingOptions> options,
                               DistributedApplicationExecutionContext executionContext) : IDistributedApplicationPublisher
{
    private readonly ILogger<ManifestPublisher> _logger = logger;
    private readonly IOptions<PublishingOptions> _options = options;
    private readonly DistributedApplicationExecutionContext _executionContext = executionContext;

    public Utf8JsonWriter? JsonWriter { get; set; }

    public async Task PublishAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        await PublishInternalAsync(model, cancellationToken).ConfigureAwait(false);
    }

    protected virtual async Task PublishInternalAsync(DistributedApplicationModel model, CancellationToken cancellationToken)
    {
        if (_options.Value.OutputPath == null)
        {
            throw new DistributedApplicationException(
                "The '--output-path [path]' option was not specified even though '--publisher manifest' argument was used."
                );
        }

        if (!_options.Value.OutputPath.EndsWith(".json", StringComparison.Ordinal))
        {
            // If the manifest path ends with .json we assume that the output path was specified
            // as a filename. If not, we assume that the output path was specified as a directory
            // and append aspire-manifest.json to the path. This is so that we retain backwards
            // compatibility with AZD, but also support manifest publishing via the Aspire CLI
            // where the output path is a directory (since not all publishers use a manifest).
            _options.Value.OutputPath = Path.Combine(_options.Value.OutputPath, "aspire-manifest.json");
        }

        var parentDirectory = Directory.GetParent(_options.Value.OutputPath);
        if (!Directory.Exists(parentDirectory!.FullName))
        {
            // Create the directory if it does not exist
            Directory.CreateDirectory(parentDirectory.FullName);
        }

        using var stream = new FileStream(_options.Value.OutputPath, FileMode.Create);
        using var jsonWriter = JsonWriter ?? new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        await WriteManifestAsync(model, jsonWriter, cancellationToken).ConfigureAwait(false);

        var fullyQualifiedPath = Path.GetFullPath(_options.Value.OutputPath);
        _logger.LogInformation("Published manifest to: {ManifestPath}", fullyQualifiedPath);
    }

    protected async Task WriteManifestAsync(DistributedApplicationModel model, Utf8JsonWriter jsonWriter, CancellationToken cancellationToken)
    {
        var manifestPath = _options.Value.OutputPath ?? throw new DistributedApplicationException("The '--output-path [path]' option was not specified even though '--publisher manifest' argument was used.");

        var context = new ManifestPublishingContext(_executionContext, manifestPath, jsonWriter, cancellationToken);

        await context.WriteModel(model, cancellationToken).ConfigureAwait(false);
    }
}
