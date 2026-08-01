// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Describes an agent asset bundle published by the Aspire skills repository.
/// </summary>
internal sealed record BundleDescriptor
{
    private const string SkillFileName = "SKILL.md";
    private const string ExtensionFileName = "extension.mjs";
    private const int MaxSkillDescriptionLength = 1024;

    private static readonly BundleDescriptor s_skills = new(
        AgentAssetKind.Skill,
        bundleName: "aspire-skills",
        manifestFileName: "skill-manifest.json",
        assetsDirectoryName: "skills",
        displayName: "skill",
        embeddedArchiveResourceName: "aspire-skills.bundle.tgz",
        embeddedMetadataResourceName: "aspire-skills.metadata.json",
        validateFile: ValidateSkillFile,
        validateAsset: ValidateSkillAsset);

    private static readonly BundleDescriptor s_extensions = new(
        AgentAssetKind.Extension,
        bundleName: "aspire-extensions",
        manifestFileName: "extension-manifest.json",
        assetsDirectoryName: "extensions",
        displayName: "extension",
        embeddedArchiveResourceName: "aspire-extensions.bundle.tgz",
        embeddedMetadataResourceName: "aspire-extensions.metadata.json",
        validateFile: null,
        validateAsset: ValidateExtensionAsset);

    private readonly Action<string, string, string>? _validateFile;
    private readonly Action<BundleAsset>? _validateAsset;

    private BundleDescriptor(
        AgentAssetKind assetKind,
        string bundleName,
        string manifestFileName,
        string assetsDirectoryName,
        string displayName,
        string? embeddedArchiveResourceName,
        string? embeddedMetadataResourceName,
        Action<string, string, string>? validateFile,
        Action<BundleAsset>? validateAsset)
    {
        AssetKind = assetKind;
        BundleName = bundleName;
        ManifestFileName = manifestFileName;
        AssetsDirectoryName = assetsDirectoryName;
        DisplayName = displayName;
        EmbeddedArchiveResourceName = embeddedArchiveResourceName;
        EmbeddedMetadataResourceName = embeddedMetadataResourceName;
        _validateFile = validateFile;
        _validateAsset = validateAsset;
    }

    public AgentAssetKind AssetKind { get; }

    public string BundleName { get; }

    public string ManifestFileName { get; }

    public string AssetsDirectoryName { get; }

    public string DisplayName { get; }

    public string? EmbeddedArchiveResourceName { get; }

    public string? EmbeddedMetadataResourceName { get; }

    public void ValidateFile(string assetName, string relativePath, string fullPath)
    {
        _validateFile?.Invoke(assetName, relativePath, fullPath);
    }

    public void ValidateAsset(BundleAsset asset)
    {
        _validateAsset?.Invoke(asset);
    }

    public static BundleDescriptor GetDescriptor(AgentAssetKind assetKind)
    {
        return assetKind switch
        {
            AgentAssetKind.Skill => s_skills,
            AgentAssetKind.Extension => s_extensions,
            _ => throw new NotSupportedException(string.Format(CultureInfo.InvariantCulture, "Agent asset kind '{0}' does not have an Aspire skills repository bundle.", assetKind))
        };
    }

    private static void ValidateSkillFile(string skillName, string relativePath, string fullPath)
    {
        if (!string.Equals(relativePath, SkillFileName, StringComparison.Ordinal))
        {
            return;
        }

        var content = File.ReadAllText(fullPath);
        var description = GetFrontmatterValue(content, "description");
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' must define a frontmatter description in SKILL.md.", skillName));
        }

        if (description.Length > MaxSkillDescriptionLength)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' SKILL.md description is {1} characters; agent hosts accept at most {2}.", skillName, description.Length, MaxSkillDescriptionLength));
        }
    }

    private static void ValidateSkillAsset(BundleAsset skill)
    {
        if (!skill.Files.Any(file => string.Equals(
            AspireSkillsBundle.NormalizeRelativePath(file.RelativePath),
            SkillFileName,
            StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle skill '{0}' must contain SKILL.md.", skill.Name));
        }
    }

    private static void ValidateExtensionAsset(BundleAsset extension)
    {
        if (!extension.Files.Any(file => string.Equals(
            AspireSkillsBundle.NormalizeRelativePath(file.RelativePath),
            ExtensionFileName,
            StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "Aspire skills bundle extension '{0}' must contain extension.mjs.", extension.Name));
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
