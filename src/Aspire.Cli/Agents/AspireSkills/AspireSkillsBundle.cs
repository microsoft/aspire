// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// A validated Aspire Skills bundle.
/// </summary>
internal sealed class AspireSkillsBundle
{
    private readonly string _version;
    private readonly IReadOnlyList<ValidatedAspireSkill> _skills;

    internal AspireSkillsBundle(
        string version,
        IReadOnlyList<ValidatedAspireSkill> skills)
    {
        _version = version;
        _skills = skills
            .Select(static skill => new ValidatedAspireSkill(skill.Definition, [.. skill.Files]))
            .ToList();
    }

    /// <summary>
    /// Gets the bundle version.
    /// </summary>
    public string Version => _version;

    /// <summary>
    /// Gets installable files for a skill supplied by this bundle.
    /// </summary>
    public Task<IReadOnlyList<AgentAssetFile>> GetSkillFilesAsync(
        AgentFileAssetDefinition skill,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skill);
        cancellationToken.ThrowIfCancellationRequested();

        // Bundle definitions are payload handles. Requiring the exact instance prevents a same-name
        // asset from another bundle or source from resolving to this bundle's files.
        var bundledSkill = _skills.FirstOrDefault(candidate => ReferenceEquals(candidate.Definition, skill));
        if (bundledSkill is null)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire Skills bundle does not own skill definition '{0}'.",
                skill.Name));
        }

        return Task.FromResult<IReadOnlyList<AgentAssetFile>>(
            bundledSkill.Files
                .Where(file => skill.ShouldInstallFile(file.RelativePath))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToList());
    }

    /// <summary>
    /// Gets the installable skill definitions declared by the bundle manifest.
    /// </summary>
    public IReadOnlyList<AgentFileAssetDefinition> GetSkillDefinitions()
    {
        return _skills
            .Select(static skill => skill.Definition)
            .ToList();
    }
}

internal sealed record ValidatedAspireSkill(
    AgentFileAssetDefinition Definition,
    IReadOnlyList<AgentAssetFile> Files);
