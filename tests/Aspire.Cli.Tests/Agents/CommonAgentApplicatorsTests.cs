// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;

namespace Aspire.Cli.Tests.Agents;

public class CommonAgentApplicatorsTests
{
    private const int MaxSkillDescriptionLength = 1024;

    [Fact]
    public void DotnetInspectSkillDescriptionFitsAgentHostLimits()
    {
        var description = GetFrontmatterValue(CommonAgentApplicators.DotnetInspectSkillFileContent, "description");

        Assert.NotNull(description);
        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.True(
            description.Length <= MaxSkillDescriptionLength,
            $"Skill '{CommonAgentApplicators.DotnetInspectSkillName}' description is {description.Length} characters; agent hosts such as Codex and Copilot CLI accept at most {MaxSkillDescriptionLength}.");
        Assert.Contains("# dotnet-inspect", CommonAgentApplicators.DotnetInspectSkillFileContent);
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

        var frontmatter = normalizedContent[4..frontmatterEndIndex];
        var keyPrefix = $"{key}:";

        foreach (var line in frontmatter.Split('\n'))
        {
            if (!line.StartsWith(keyPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var value = line[keyPrefix.Length..].Trim();
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
                ? value[1..^1]
                : value;
        }

        return null;
    }
}
