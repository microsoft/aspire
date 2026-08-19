// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Provides Aspire-skills bundles embedded in the CLI assembly.
/// </summary>
internal interface IEmbeddedAspireSkillsBundleProvider
{
    /// <summary>
    /// Gets the metadata embedded alongside the specified bundle.
    /// </summary>
    EmbeddedAspireSkillsBundleMetadata? GetMetadata(AgentAssetKind assetKind);

    /// <summary>
    /// Creates the embedded bundle in the specified directory.
    /// </summary>
    Task<AspireSkillsBundle?> CreateBundleAsync(
        AgentAssetKind assetKind,
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provides validated Aspire-skills bundles embedded in the CLI assembly.
/// </summary>
internal sealed class EmbeddedAspireSkillsBundleProvider : IEmbeddedAspireSkillsBundleProvider
{
    private readonly IAspireSkillsBundleProvider _bundleProvider;
    private readonly ILogger<EmbeddedAspireSkillsBundleProvider> _logger;
    private readonly ConcurrentDictionary<AgentAssetKind, Lazy<EmbeddedAspireSkillsBundleMetadata?>> _metadata = [];

    public EmbeddedAspireSkillsBundleProvider(
        IAspireSkillsBundleProvider bundleProvider,
        ILogger<EmbeddedAspireSkillsBundleProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(bundleProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _bundleProvider = bundleProvider;
        _logger = logger;
    }

    /// <summary>
    /// Gets the parsed metadata embedded alongside the specified Aspire-skills bundle archive.
    /// </summary>
    public EmbeddedAspireSkillsBundleMetadata? GetMetadata(AgentAssetKind assetKind)
    {
        var descriptor = AspireSkillsBundleDescriptor.Find(assetKind);
        if (descriptor is null)
        {
            return null;
        }

        return _metadata.GetOrAdd(
            assetKind,
            _ => new Lazy<EmbeddedAspireSkillsBundleMetadata?>(
                () => LoadMetadata(descriptor))).Value;
    }

    public async Task<AspireSkillsBundle?> CreateBundleAsync(
        AgentAssetKind assetKind,
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);

        var descriptor = AspireSkillsBundleDescriptor.Find(assetKind);
        if (descriptor is null)
        {
            return null;
        }

        var metadata = GetMetadata(assetKind);
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Sha512))
        {
            return null;
        }

        await using var archiveStream = OpenArchive(descriptor);
        if (archiveStream is null)
        {
            return null;
        }

        Directory.CreateDirectory(bundleDirectory.FullName);
        var temporaryDirectoryRoot = bundleDirectory.Parent
            ?? throw new InvalidOperationException($"The {descriptor.DisplayName} bundle staging directory must have a parent directory.");
        // Keep the archive beside the staging directory so a transient Windows file lock during
        // best-effort cleanup cannot prevent the validated staging directory from being published.
        using var temporaryDirectory = TemporaryCacheDirectory.Create(
            temporaryDirectoryRoot.FullName,
            "embedded",
            path => FileDeleteHelper.TryDeleteDirectory(path),
            path => FileDeleteHelper.TryDeleteFile(path));
        var archivePath = Path.Combine(temporaryDirectory.FullName, "bundle.tgz");

        await using (var fileStream = File.Create(archivePath))
        {
            await archiveStream.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
        }

        return await _bundleProvider.CreateAsync(
            assetKind,
            new FileInfo(archivePath),
            bundleDirectory,
            metadata.Sha512,
            cancellationToken,
            skipCompatibilityCheck: true).ConfigureAwait(false);
    }

    private Stream? OpenArchive(AspireSkillsBundleDescriptor descriptor)
    {
        var stream = typeof(EmbeddedAspireSkillsBundleProvider).Assembly.GetManifestResourceStream(descriptor.EmbeddedArchiveResourceName);
        if (stream is null)
        {
            _logger.LogDebug("Embedded {BundleDisplayName} archive resource {ResourceName} was not found.", descriptor.DisplayName, descriptor.EmbeddedArchiveResourceName);
        }

        return stream;
    }

    private EmbeddedAspireSkillsBundleMetadata? LoadMetadata(AspireSkillsBundleDescriptor descriptor)
    {
        using var stream = typeof(EmbeddedAspireSkillsBundleProvider).Assembly.GetManifestResourceStream(descriptor.EmbeddedMetadataResourceName);
        if (stream is null)
        {
            _logger.LogDebug("Embedded {BundleDisplayName} metadata resource {ResourceName} was not found.", descriptor.DisplayName, descriptor.EmbeddedMetadataResourceName);
            return null;
        }

        try
        {
            var metadata = JsonSerializer.Deserialize(
                stream,
                AspireSkillsJsonSerializerContext.Default.EmbeddedAspireSkillsBundleMetadata);

            if (metadata is null)
            {
                _logger.LogDebug("Embedded {BundleDisplayName} metadata resource {ResourceName} was empty.", descriptor.DisplayName, descriptor.EmbeddedMetadataResourceName);
            }

            return metadata;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Embedded {BundleDisplayName} metadata resource {ResourceName} could not be parsed.", descriptor.DisplayName, descriptor.EmbeddedMetadataResourceName);
            return null;
        }
    }
}
