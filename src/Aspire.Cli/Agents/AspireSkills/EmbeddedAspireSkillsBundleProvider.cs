// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Provides access to the Aspire skills bundle snapshot embedded in the CLI assembly.
/// </summary>
internal interface IEmbeddedAspireSkillsBundleProvider
{
    /// <summary>
    /// Gets metadata for the embedded Aspire skills bundle snapshot.
    /// </summary>
    EmbeddedAspireSkillsBundleMetadata? Metadata { get; }

    /// <summary>
    /// Opens the embedded Aspire skills bundle archive.
    /// </summary>
    Stream? OpenArchive();
}

internal sealed class EmbeddedAspireSkillsBundleProvider : IEmbeddedAspireSkillsBundleProvider
{
    private const string ArchiveResourceName = "aspire-skills.bundle.tgz";
    private const string MetadataResourceName = "aspire-skills.metadata.json";

    private readonly ILogger<EmbeddedAspireSkillsBundleProvider> _logger;
    private readonly Lazy<EmbeddedAspireSkillsBundleMetadata?> _metadata;

    public EmbeddedAspireSkillsBundleProvider(ILogger<EmbeddedAspireSkillsBundleProvider> logger)
    {
        _logger = logger;
        _metadata = new Lazy<EmbeddedAspireSkillsBundleMetadata?>(LoadMetadata);
    }

    public EmbeddedAspireSkillsBundleMetadata? Metadata => _metadata.Value;

    public Stream? OpenArchive()
    {
        var stream = typeof(EmbeddedAspireSkillsBundleProvider).Assembly.GetManifestResourceStream(ArchiveResourceName);
        if (stream is null)
        {
            _logger.LogDebug("Embedded Aspire skills archive resource {ResourceName} was not found.", ArchiveResourceName);
        }

        return stream;
    }

    private EmbeddedAspireSkillsBundleMetadata? LoadMetadata()
    {
        using var stream = typeof(EmbeddedAspireSkillsBundleProvider).Assembly.GetManifestResourceStream(MetadataResourceName);
        if (stream is null)
        {
            _logger.LogDebug("Embedded Aspire skills metadata resource {ResourceName} was not found.", MetadataResourceName);
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize(
                stream,
                AspireSkillsJsonSerializerContext.Default.EmbeddedAspireSkillsBundleMetadata);

            if (metadata is null)
            {
                _logger.LogDebug("Embedded Aspire skills metadata resource {ResourceName} was empty.", MetadataResourceName);
            }

            return metadata;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Embedded Aspire skills metadata resource {ResourceName} could not be parsed.", MetadataResourceName);
            return null;
        }
    }
}
