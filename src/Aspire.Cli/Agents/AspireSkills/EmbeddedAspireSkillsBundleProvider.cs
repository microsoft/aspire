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
    EmbeddedAspireSkillsBundleMetadata? GetMetadata(AgentAssetKind assetType);

    /// <summary>
    /// Opens the embedded Aspire skills bundle archive.
    /// </summary>
    Stream? OpenArchive(AgentAssetKind assetType);
}

internal sealed class EmbeddedAspireSkillsBundleProvider : IEmbeddedAspireSkillsBundleProvider
{
    private const string SkillsArchiveResourceName = "aspire-skills.bundle.tgz";
    private const string SkillsMetadataResourceName = "aspire-skills.metadata.json";
    private const string ExtensionsArchiveResourceName = "aspire-extensions.bundle.tgz";
    private const string ExtensionsMetadataResourceName = "aspire-extensions.metadata.json";

    private readonly ILogger<EmbeddedAspireSkillsBundleProvider> _logger;
    private readonly Lazy<EmbeddedAspireSkillsBundleMetadata?> _skillsMetadata;
    private readonly Lazy<EmbeddedAspireSkillsBundleMetadata?> _extensionsMetadata;

    public EmbeddedAspireSkillsBundleProvider(ILogger<EmbeddedAspireSkillsBundleProvider> logger)
    {
        _logger = logger;
        _skillsMetadata = new Lazy<EmbeddedAspireSkillsBundleMetadata?>(() => LoadMetadata(SkillsMetadataResourceName));
        _extensionsMetadata = new Lazy<EmbeddedAspireSkillsBundleMetadata?>(() => LoadMetadata(ExtensionsMetadataResourceName));
    }

    public EmbeddedAspireSkillsBundleMetadata? GetMetadata(AgentAssetKind assetType)
    {
        if (assetType == AgentAssetKind.Skill)
        {
            return _skillsMetadata.Value;
        }

        if (assetType == AgentAssetKind.Extension)
        {
            return _extensionsMetadata.Value;
        }

        throw new InvalidOperationException($"Unsupported agent asset type '{assetType}'.");
    }

    public Stream? OpenArchive(AgentAssetKind assetType)
    {
        string resourceName = assetType switch
        {
            AgentAssetKind.Skill => SkillsArchiveResourceName,
            AgentAssetKind.Extension => ExtensionsArchiveResourceName,
            _ => throw new InvalidOperationException($"Unsupported agent asset type '{assetType}'.")
        };

        var stream = typeof(EmbeddedAspireSkillsBundleProvider).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            _logger.LogDebug("Embedded Aspire skills archive resource {ResourceName} was not found.", resourceName);
        }

        return stream;
    }

    private EmbeddedAspireSkillsBundleMetadata? LoadMetadata(string resourceName)
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
                AspireSkillsJsonSerializerContext.Default.EmbeddedAspireSkillsBundleMetadata);

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
