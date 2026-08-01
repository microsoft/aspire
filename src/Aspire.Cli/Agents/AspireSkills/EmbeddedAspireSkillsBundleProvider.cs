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
    EmbeddedBundleMetadata? GetMetadata(AgentAssetKind assetKind);

    /// <summary>
    /// Opens the embedded Aspire skills bundle archive.
    /// </summary>
    Stream? OpenArchive(AgentAssetKind assetKind);
}

internal sealed class EmbeddedAspireSkillsBundleProvider : IEmbeddedAspireSkillsBundleProvider
{
    private readonly ILogger<EmbeddedAspireSkillsBundleProvider> _logger;

    public EmbeddedAspireSkillsBundleProvider(ILogger<EmbeddedAspireSkillsBundleProvider> logger)
    {
        _logger = logger;
    }

    public EmbeddedBundleMetadata? GetMetadata(AgentAssetKind assetKind)
    {
        var resourceName = BundleDescriptor.GetDescriptor(assetKind).EmbeddedMetadataResourceName;
        if (resourceName is null)
        {
            return null;
        }

        return LoadMetadata(resourceName);
    }

    public Stream? OpenArchive(AgentAssetKind assetKind)
    {
        var resourceName = BundleDescriptor.GetDescriptor(assetKind).EmbeddedArchiveResourceName;
        if (resourceName is null)
        {
            return null;
        }

        var stream = typeof(EmbeddedAspireSkillsBundleProvider).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogDebug("Embedded Aspire skills archive resource {ResourceName} was not found.", resourceName);
        }

        return stream;
    }

    private EmbeddedBundleMetadata? LoadMetadata(string resourceName)
    {
        using var stream = typeof(EmbeddedAspireSkillsBundleProvider).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogDebug("Embedded Aspire skills metadata resource {ResourceName} was not found.", resourceName);
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize(
                stream,
                AspireSkillsJsonSerializerContext.Default.EmbeddedBundleMetadata);

            if (metadata is null)
            {
                _logger.LogDebug("Embedded Aspire skills metadata resource {ResourceName} was empty.", resourceName);
            }

            return metadata;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Embedded Aspire skills metadata resource {ResourceName} could not be parsed.", resourceName);
            return null;
        }
    }
}
