// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents an agent skill or extension and its installable files.
/// </summary>
[DebuggerDisplay("AssetKind = {AssetKind}, Name = {Name}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetDefinition
{
    internal AgentAssetDefinition(
        AgentAssetKind assetKind,
        string name,
        string description,
        AgentAssetSourceKind sourceKind,
        IReadOnlyList<AgentAssetFile> files,
        IReadOnlyList<string> installExcludedRelativePaths,
        bool isDefault,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        AssetKind = assetKind;
        Name = name;
        Description = description;
        SourceKind = sourceKind;
        InstallExcludedRelativePaths = [.. installExcludedRelativePaths];
        Files = files
            .Where(file => ShouldInstallFile(file.RelativePath))
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        IsDefault = isDefault;
        ApplicableLanguages = applicableLanguages is null ? [] : [.. applicableLanguages];
    }

    /// <summary>
    /// Creates a bundle-sourced asset, selected by default in all installation flows.
    /// </summary>
    internal static AgentAssetDefinition CreateBundled(
        AgentAssetKind assetKind,
        string name,
        string description,
        IReadOnlyList<AgentAssetFile> files,
        IReadOnlyList<string>? installExcludedRelativePaths = null,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        return new(
            assetKind,
            name,
            description,
            AgentAssetSourceKind.Bundled,
            files,
            installExcludedRelativePaths: installExcludedRelativePaths ?? [],
            isDefault: true,
            applicableLanguages);
    }

    public AgentAssetKind AssetKind { get; }

    public string Name { get; }

    public string Description { get; }

    public AgentAssetSourceKind SourceKind { get; }

    /// <summary>
    /// Gets the resolved installable payload, with exclusions applied and paths ordered.
    /// </summary>
    public IReadOnlyList<AgentAssetFile> Files { get; }

    public bool HasInstallableFiles => Files.Count > 0;

    public IReadOnlyList<string> InstallExcludedRelativePaths { get; }

    public bool IsDefault { get; }

    public IReadOnlyList<string> ApplicableLanguages { get; }

    /// <summary>
    /// Gets whether a file should be installed.
    /// </summary>
    private bool ShouldInstallFile(string relativePath)
    {
        foreach (var excludedPath in InstallExcludedRelativePaths)
        {
            if (string.Equals(relativePath, excludedPath, StringComparison.Ordinal) ||
                (relativePath.StartsWith(excludedPath, StringComparison.Ordinal) &&
                 relativePath.Length > excludedPath.Length &&
                 relativePath[excludedPath.Length] == Path.DirectorySeparatorChar))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets whether this asset applies to the detected language.
    /// </summary>
    public bool IsApplicableToLanguage(LanguageId? detectedLanguage)
    {
        return ApplicableLanguages.Count == 0 ||
            (detectedLanguage is not null && ApplicableLanguages.Any(language =>
                string.Equals(language, detectedLanguage.Value.Value, StringComparison.OrdinalIgnoreCase)));
    }

    public bool HasName(string name, StringComparison comparison = StringComparison.Ordinal)
        => string.Equals(Name, name, comparison);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Identifies where an agent asset's files are sourced.
/// </summary>
internal enum AgentAssetSourceKind
{
    Static,
    Bundled,
    ExternalInstaller,
}
