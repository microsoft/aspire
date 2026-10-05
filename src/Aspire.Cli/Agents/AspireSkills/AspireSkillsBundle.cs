// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// A validated Aspire skills bundle.
/// </summary>
internal sealed class AspireSkillsBundle
{
    private readonly string _version;
    private readonly IReadOnlyList<ValidatedAspireSkill> _skills;

    internal AspireSkillsBundle(string version, IReadOnlyList<ValidatedAspireSkill> skills)
    {
        _version = version;
        _skills = skills;
    }

    /// <summary>
    /// Gets the bundle version.
    /// </summary>
    public string Version => _version;

    /// <summary>
    /// Gets installable files for the specified skill.
    /// </summary>
    public Task<IReadOnlyList<SkillAssetFile>> GetSkillFilesAsync(SkillDefinition skill, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(skill);
        cancellationToken.ThrowIfCancellationRequested();

        var bundledSkill = _skills.FirstOrDefault(s => string.Equals(s.Definition.Name, skill.Name, StringComparison.Ordinal));
        if (bundledSkill is null)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle does not contain skill '{0}'.", skill.Name));
        }

        List<SkillAssetFile> files = [];
        foreach (var bundledFile in bundledSkill.Files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            var relativePath = bundledFile.RelativePath;
            if (!skill.ShouldInstallFile(relativePath) ||
                !bundledSkill.Definition.ShouldInstallFile(relativePath))
            {
                continue;
            }

            files.Add(bundledFile);
        }

        return Task.FromResult<IReadOnlyList<SkillAssetFile>>(files);
    }

    /// <summary>
    /// Gets the installable skill definitions declared by the bundle manifest.
    /// </summary>
    public IReadOnlyList<SkillDefinition> GetSkillDefinitions()
    {
        return _skills
            .Select(static skill => skill.Definition)
            .ToList();
    }
}

internal sealed record ValidatedAspireSkill(
    SkillDefinition Definition,
    IReadOnlyList<SkillAssetFile> Files);
