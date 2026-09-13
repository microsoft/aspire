// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Validates the frontmatter required by agent hosts in SKILL.md files.
/// </summary>
internal static class SkillFileValidator
{
    private const int MaxSkillDescriptionLength = 1024;

    public static void Validate(string assetName, ReadOnlySpan<byte> content)
    {
        ValidateFrontmatter(assetName, AgentAssetFile.DecodeText(content));
    }

    private static void ValidateFrontmatter(string skillName, string content)
    {
        var frontmatterName = GetFrontmatterValue(content, "name");
        if (string.IsNullOrWhiteSpace(frontmatterName))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire Skills bundle skill '{0}' must define a frontmatter name in SKILL.md.", skillName));
        }

        if (!string.Equals(frontmatterName, skillName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire Skills bundle skill '{0}' SKILL.md frontmatter name '{1}' must match its manifest and directory name.",
                skillName,
                frontmatterName));
        }

        var description = GetFrontmatterValue(content, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire Skills bundle skill '{0}' must define a frontmatter description in SKILL.md.", skillName));
        }

        if (description.Length > MaxSkillDescriptionLength)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "Aspire Skills bundle skill '{0}' SKILL.md description is {1} characters; agent hosts accept at most {2}.",
                skillName,
                description.Length,
                MaxSkillDescriptionLength));
        }
    }

    private static string? GetFrontmatterValue(string content, string key)
    {
        var normalizedContent = content.ReplaceLineEndings("\n");
        if (!normalizedContent.StartsWith("---\n", StringComparison.Ordinal))
        {
            return null;
        }

        var frontmatterEndIndex = normalizedContent.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (frontmatterEndIndex < 0)
        {
            return null;
        }

        // Skill files use simple YAML frontmatter:
        //   ---
        //   name: aspire
        //   description: "Use when working with an Aspire distributed application"
        //   ---
        // Agent hosts read these fields directly, so validate the bundled SKILL.md
        // before caching content that they would reject or ignore.
        var frontmatter = normalizedContent[4..frontmatterEndIndex];
        var keyPrefix = $"{key}:";
        foreach (var line in frontmatter.Split('\n'))
        {
            if (!line.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[keyPrefix.Length..].Trim();
            return value.Length >= 2 &&
                   ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
                ? value[1..^1]
                : value;
        }

        return null;
    }
}
