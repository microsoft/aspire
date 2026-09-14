// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Agents;

/// <summary>
/// Represents an agent skill or extension and its installable files.
/// </summary>
[DebuggerDisplay("Name = {Name}, IsDefault = {IsDefault}")]
internal sealed class AgentAssetDefinition
{
    internal AgentAssetDefinition(
        string name,
        string description,
        IReadOnlyList<AgentAssetFile> files,
        IReadOnlyList<string> installExcludedRelativePaths,
        bool isDefault,
        IReadOnlyList<string>? applicableLanguages = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Name = name;
        Description = description;
        var excludedPaths = installExcludedRelativePaths.ToArray();
        Files = files
            .Where(file => !excludedPaths.Any(excludedPath =>
                string.Equals(file.RelativePath, excludedPath, StringComparison.Ordinal) ||
                (file.RelativePath.StartsWith(excludedPath, StringComparison.Ordinal) &&
                 file.RelativePath.Length > excludedPath.Length &&
                 file.RelativePath[excludedPath.Length] == Path.DirectorySeparatorChar)))
            .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        IsDefault = isDefault;
        ApplicableLanguages = applicableLanguages is null ? [] : [.. applicableLanguages];
    }

    public string Name { get; }

    public string Description { get; }

    /// <summary>
    /// Gets the resolved installable payload, with exclusions applied and paths ordered.
    /// </summary>
    public IReadOnlyList<AgentAssetFile> Files { get; }

    public bool HasInstallableFiles => Files.Count > 0;

    public bool IsDefault { get; }

    public IReadOnlyList<string> ApplicableLanguages { get; }

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
