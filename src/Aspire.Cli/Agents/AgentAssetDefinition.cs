// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;

namespace Aspire.Cli.Agents;

/// <summary>
/// Describes an agent asset that can be selected and installed.
/// </summary>
internal abstract class AgentAssetDefinition
{
    protected AgentAssetDefinition(
        string name,
        string description,
        IReadOnlyList<string> installExcludedRelativePaths,
        bool isDefault,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        Name = name;
        Description = description;
        InstallExcludedRelativePaths = installExcludedRelativePaths;
        IsDefault = isDefault;
        ApplicableLanguages = applicableLanguages ?? [];
    }

    /// <summary>
    /// Gets the asset name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the description shown in selection prompts.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets relative paths that should be excluded when the asset is installed.
    /// </summary>
    public IReadOnlyList<string> InstallExcludedRelativePaths { get; }

    /// <summary>
    /// Gets whether the asset should be selected by default.
    /// </summary>
    public bool IsDefault { get; }

    /// <summary>
    /// Gets the language identifiers to which this asset applies.
    /// </summary>
    public IReadOnlyList<string> ApplicableLanguages { get; }

    /// <summary>
    /// Gets whether a bundled file should be installed.
    /// </summary>
    public bool ShouldInstallFile(string relativePath)
    {
        foreach (var excludedPath in InstallExcludedRelativePaths)
        {
            if (PathMatchesOrIsUnder(relativePath, excludedPath))
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
        if (ApplicableLanguages.Count == 0)
        {
            return true;
        }

        if (detectedLanguage is null)
        {
            return false;
        }

        return ApplicableLanguages.Any(language =>
            string.Equals(language, detectedLanguage.Value.Value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets whether this asset has the specified name.
    /// </summary>
    public bool HasName(string name, StringComparison comparison = StringComparison.Ordinal)
        => string.Equals(Name, name, comparison);

    /// <inheritdoc />
    public override string ToString() => Name;

    private static bool PathMatchesOrIsUnder(string relativePath, string excludedPath)
    {
        if (string.Equals(relativePath, excludedPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!relativePath.StartsWith(excludedPath, StringComparison.Ordinal))
        {
            return false;
        }

        return relativePath.Length > excludedPath.Length &&
            relativePath[excludedPath.Length] == Path.DirectorySeparatorChar;
    }
}
