// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.Agents;

public class AgentTelemetryCatalogTests
{
    private const string Script = """
        ASPIRE_SKILLS="new-skill another-skill"
        ASPIRE_MCP_TOOLS="new_tool another-tool"
        ASPIRE_REFERENCE_FILES="
        new-skill/references/new.md
        another-skill/references/guide.yml
        "
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReadsAllowlistsFromCanonicalScript(string newline)
    {
        var catalog = AgentTelemetryCatalog.Parse(Script.ReplaceLineEndings(newline));
        Assert.Equal(["another-skill", "new-skill"], catalog.Skills.Order());
        Assert.Equal(["another-tool", "new_tool"], catalog.Tools.Order());
        Assert.Equal(["another-skill/references/guide.yml", "new-skill/references/new.md"], catalog.References.Order());
        Assert.Contains("NEW-SKILL", catalog.Skills);
        Assert.False(catalog.Skills.Contains("third-party"));
    }

    [Theory]
    [InlineData("ASPIRE_SKILLS", "valid $(command)")]
    [InlineData("ASPIRE_SKILLS", "third/party")]
    [InlineData("ASPIRE_MCP_TOOLS", "valid name;command")]
    [InlineData("ASPIRE_MCP_TOOLS", "")]
    public void SkillAndToolIdentifiersMustBeValid(string variableName, string values)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues(variableName, values)));
    }

    [Theory]
    [InlineData("unknown-skill/references/new.md")]
    [InlineData("new-skill/private.md")]
    [InlineData("new-skill/references/../private.md")]
    [InlineData("new-skill\\references\\private.md")]
    public void ReferencesMustBelongToKnownSkillReferenceDirectories(string reference)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues("ASPIRE_REFERENCE_FILES", reference)));
    }

    [Theory]
    [InlineData("ASPIRE_SKILLS")]
    [InlineData("ASPIRE_MCP_TOOLS")]
    [InlineData("ASPIRE_REFERENCE_FILES")]
    public void MissingOrDuplicateDeclarationsAreRejected(string variableName)
    {
        var declaration = GetDeclaration(variableName);
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Script.Replace(declaration, "", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse($"{Script}\n{declaration}"));
    }

    [Theory]
    [InlineData("ASPIRE_SKILLS", "new-skill NEW-SKILL")]
    [InlineData("ASPIRE_MCP_TOOLS", "new_tool NEW_TOOL")]
    [InlineData("ASPIRE_REFERENCE_FILES", "new-skill/references/new.md NEW-SKILL/references/new.md")]
    public void DuplicateValuesAreRejectedCaseInsensitively(string variableName, string values)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues(variableName, values)));
    }

    private static string ReplaceValues(string variableName, string values)
    {
        var prefix = $"{variableName}=\"";
        var valueStart = Script.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        Assert.True(valueStart >= prefix.Length);
        var valueEnd = Script.IndexOf('"', valueStart);
        Assert.True(valueEnd >= valueStart);
        return Script[..valueStart] + values + Script[valueEnd..];
    }

    private static string GetDeclaration(string variableName)
    {
        var prefix = $"{variableName}=\"";
        var start = Script.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = Script.IndexOf('"', start + prefix.Length);
        Assert.True(end >= 0);
        return Script[start..(end + 1)];
    }
}
