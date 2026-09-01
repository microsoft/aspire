// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Provides the Aspire Skills bundle containing agent skills.
/// </summary>
internal class SkillBundleProvider : AspireSkillsBundleProvider
{
    private const int MaxSkillDescriptionLength = 1024;

    public SkillBundleProvider(
        CliExecutionContext executionContext,
        ILogger<SkillBundleProvider> logger)
        : base(executionContext, logger)
    {
    }

    internal SkillBundleProvider(CliExecutionContext executionContext)
        : base(executionContext, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
    }

    internal SkillBundleProvider()
    {
    }

    internal SkillBundleProvider(string currentCliVersion, string currentSdkVersion)
        : base(currentCliVersion, currentSdkVersion)
    {
    }

    public override AgentAssetKind AssetKind => AgentAssetKind.Skill;

    public override string AssetKindName => "skills";

    public override string AssetPrefix => "aspire-skills";

    public override string CacheDirectoryName => "aspire-skills";

    public override string DisplayName => "Aspire skills";

    public override string ManifestFileName => "skill-manifest.json";

    public override string ManifestAssetsPropertyName => "skills";

    public override string ContentRootDirectoryName => "skills";

    public override string RequiredFileName => "SKILL.md";

    public override string EmbeddedArchiveResourceName => "aspire-skills.bundle.tgz";

    public override string EmbeddedMetadataResourceName => "aspire-skills.metadata.json";

    protected override void ValidateRequiredFile(string assetName, ReadOnlySpan<byte> content)
    {
        try
        {
            ValidateSkillFileFrontmatter(assetName, AgentAssetFile.DecodeText(content));
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                $"Aspire skills bundle skill '{assetName}' must contain valid UTF-8 in SKILL.md.",
                ex);
        }
    }

    internal static void ValidateSkillFileFrontmatter(string skillName, string content)
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
