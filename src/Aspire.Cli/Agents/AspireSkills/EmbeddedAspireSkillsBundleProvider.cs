// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Provides the validated Aspire skills bundle embedded in the CLI assembly.
/// </summary>
internal interface IEmbeddedAspireSkillsBundleProvider
{
    /// <summary>
    /// Gets the parsed metadata embedded alongside the Aspire skills bundle archive.
    /// </summary>
    EmbeddedAspireSkillsBundleMetadata? Metadata { get; }

    /// <summary>
    /// Creates the embedded Aspire skills bundle in the specified directory.
    /// </summary>
    Task<AspireSkillsBundle?> CreateBundleAsync(
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken);
}

internal sealed class EmbeddedAspireSkillsBundleProvider : IEmbeddedAspireSkillsBundleProvider
{
    private const string ArchiveResourceName = "aspire-skills.bundle.tgz";
    private const string MetadataResourceName = "aspire-skills.metadata.json";

    private readonly IAspireSkillsBundleProvider _bundleProvider;
    private readonly ILogger<EmbeddedAspireSkillsBundleProvider> _logger;
    private readonly Lazy<EmbeddedAspireSkillsBundleMetadata?> _metadata;

    public EmbeddedAspireSkillsBundleProvider(
        IAspireSkillsBundleProvider bundleProvider,
        ILogger<EmbeddedAspireSkillsBundleProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(bundleProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _bundleProvider = bundleProvider;
        _logger = logger;
        _metadata = new Lazy<EmbeddedAspireSkillsBundleMetadata?>(LoadMetadata);
    }

    public EmbeddedAspireSkillsBundleMetadata? Metadata => _metadata.Value;

    public async Task<AspireSkillsBundle?> CreateBundleAsync(
        DirectoryInfo bundleDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundleDirectory);

        var metadata = Metadata;
        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Sha512))
        {
            return null;
        }

        await using var archiveStream = OpenArchive();
        if (archiveStream is null)
        {
            return null;
        }

        Directory.CreateDirectory(bundleDirectory.FullName);
        var temporaryDirectoryRoot = bundleDirectory.Parent
            ?? throw new InvalidOperationException("The Aspire skills bundle staging directory must have a parent directory.");
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
            new FileInfo(archivePath),
            bundleDirectory,
            metadata.Sha512,
            cancellationToken,
            skipCompatibilityCheck: true).ConfigureAwait(false);
    }

    private Stream? OpenArchive()
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
