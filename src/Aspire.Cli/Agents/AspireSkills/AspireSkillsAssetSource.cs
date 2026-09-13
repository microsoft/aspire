// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Adapts Aspire bundle acquisition to the source-neutral agent asset contract.
/// </summary>
internal sealed class AspireSkillsAssetSource : IAgentAssetSource
{
    private static readonly BundleConfiguration[] s_configurations =
    [
        new(new()
        {
            AssetKind = AgentAssetKind.Skill,
            AssetKindName = "skills",
            AssetPrefix = "aspire-skills",
            CacheDirectoryName = "aspire-skills",
            DisplayName = "Aspire skills",
            ManifestFileName = "skill-manifest.json",
            ManifestAssetsPropertyName = "skills",
            ContentRootDirectoryName = "skills",
            RequiredFileName = "SKILL.md",
            EmbeddedArchiveResourceName = "aspire-skills.bundle.tgz",
            EmbeddedMetadataResourceName = "aspire-skills.metadata.json",
        }, SkillFileValidator.Validate),
        new(new()
        {
            AssetKind = AgentAssetKind.Extension,
            AssetKindName = "extensions",
            AssetPrefix = "aspire-extensions",
            CacheDirectoryName = "aspire-extensions",
            DisplayName = "Aspire extensions",
            ManifestFileName = "extension-manifest.json",
            ManifestAssetsPropertyName = "extensions",
            ContentRootDirectoryName = "extensions",
            RequiredFileName = "extension.mjs",
            EmbeddedArchiveResourceName = "aspire-extensions.bundle.tgz",
            EmbeddedMetadataResourceName = "aspire-extensions.metadata.json",
        }, static (_, _) => { }),
    ];

    private readonly IAspireSkillsInstaller _installer;
    private readonly Dictionary<AgentAssetKind, AspireSkillsBundleProvider> _providers;

    public AspireSkillsAssetSource(
        IAspireSkillsInstaller installer,
        CliExecutionContext executionContext,
        ILogger<AspireSkillsBundleProvider> logger)
    {
        _installer = installer;
        // Retain one reader per configuration, including its lazy embedded metadata, across
        // catalog resolutions. The effective CLI identity also supplies the SDK version.
        _providers = s_configurations.ToDictionary(
            configuration => configuration.Descriptor.AssetKind,
            configuration => configuration.CreateProvider(executionContext.IdentitySdkVersion, executionContext.IdentitySdkVersion, logger));
    }

    public async Task<AgentAssetSourceResult> GetAssetsAsync(AgentAssetKind assetKind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_providers.TryGetValue(assetKind, out var provider))
        {
            throw new ArgumentOutOfRangeException(nameof(assetKind), assetKind, "No Aspire bundle is available for this asset kind.");
        }

        var result = await _installer.InstallAsync(provider, cancellationToken);
        if (result.Status is not AspireSkillsInstallStatus.Installed)
        {
            var message = result.Message ?? (assetKind is AgentAssetKind.Extension
                ? AgentCommandStrings.InitCommand_ExtensionBundleUnavailable
                : string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable, provider.Descriptor.DisplayName));
            return AgentAssetSourceResult.Unavailable(message);
        }

        var bundle = result.Bundle
            ?? throw new InvalidOperationException("Aspire bundle acquisition returned an installed result without a bundle.");
        if (bundle.AssetKind != assetKind)
        {
            throw new InvalidOperationException($"Aspire bundle acquisition returned a bundle of another kind for '{assetKind}'.");
        }

        return AgentAssetSourceResult.Available(bundle.Assets);
    }

    internal static AspireSkillsBundleProvider CreateBundleProvider(
        AgentAssetKind assetKind, string currentCliVersion, string currentSdkVersion, ILogger logger)
    {
        var configuration = s_configurations.Single(configuration => configuration.Descriptor.AssetKind == assetKind);
        return configuration.CreateProvider(currentCliVersion, currentSdkVersion, logger);
    }

    private sealed record BundleConfiguration(
        AspireSkillsBundleDescriptor Descriptor,
        Action<string, ReadOnlySpan<byte>> ValidateRequiredFile)
    {
        public AspireSkillsBundleProvider CreateProvider(string currentCliVersion, string currentSdkVersion, ILogger logger)
            => new(Descriptor, ValidateRequiredFile, currentCliVersion, currentSdkVersion, logger);
    }
}
