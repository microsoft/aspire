// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.Agents;

public class AgentTelemetryCatalogTests
{
    private const string Script = """
        ASPIRE_SKILLS="new-skill another-skill"
        ASPIRE_MCP_TOOLS="new_tool"
        ASPIRE_REFERENCE_FILES="
        new-skill/references/new.md
        another-skill/references/guide.yml
        "
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReadsCatalogWithoutMaintainingAnotherAllowlist(string newline)
    {
        var catalog = AgentTelemetryCatalog.Parse(Script.ReplaceLineEndings(newline));
        Assert.Equal(["another-skill", "new-skill"], catalog.Skills.Order());
        Assert.Equal(["new_tool"], catalog.Tools);
        Assert.Equal(["another-skill/references/guide.yml", "new-skill/references/new.md"], catalog.References.Order());
        Assert.Contains("NEW-SKILL", catalog.Skills);
    }

    [Theory]
    [InlineData("ASPIRE_SKILLS")]
    [InlineData("ASPIRE_MCP_TOOLS")]
    [InlineData("ASPIRE_REFERENCE_FILES")]
    public void MissingOrRenamedDeclarationFailsExplicitly(string declaration)
    {
        var exception = Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Script.Replace(declaration, "RENAMED")));
        Assert.Contains(declaration, exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("$(command)")]
    [InlineData("name;command")]
    public void InvalidDeclarationDoesNotSilentlyDisableOrExpandCollection(string values)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Script.Replace("new_tool", values)));
    }

    [Fact]
    public void DuplicateDeclarationsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Script + "\n" + """ASPIRE_SKILLS="duplicate" """));
    }
}
