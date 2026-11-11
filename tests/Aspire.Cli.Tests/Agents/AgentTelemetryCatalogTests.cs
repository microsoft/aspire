// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.Agents;

public class AgentTelemetryCatalogTests
{
    private const string Allowlist = """
        {
          "skills": [ "another-skill", "new-skill" ],
          "references": [ "another-skill/references/guide.yml", "new-skill/references/new.md" ],
          "mcpTools": [ "another-tool", "new_tool" ]
        }
        """;

    [Fact]
    public void ReadsAllowlistsFromJson()
    {
        var catalog = AgentTelemetryCatalog.Parse(Allowlist);
        Assert.Equal(["another-skill", "new-skill"], catalog.Skills.Order());
        Assert.Equal(["another-tool", "new_tool"], catalog.Tools.Order());
        Assert.Equal(["another-skill/references/guide.yml", "new-skill/references/new.md"], catalog.References.Order());
        Assert.Contains("NEW-SKILL", catalog.Skills);
        Assert.False(catalog.Skills.Contains("third-party"));
    }

    [Theory]
    [InlineData("skills", "valid $(command)")]
    [InlineData("skills", "third/party")]
    [InlineData("mcpTools", "valid name;command")]
    [InlineData("mcpTools", "")]
    public void SkillAndToolIdentifiersMustBeValid(string propertyName, string value)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues(propertyName, value)));
    }

    [Theory]
    [InlineData("unknown-skill/references/new.md")]
    [InlineData("new-skill/private.md")]
    [InlineData("new-skill/references/../private.md")]
    [InlineData("new-skill\\references\\private.md")]
    public void ReferencesMustBelongToKnownSkillReferenceDirectories(string reference)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues("references", reference)));
    }

    [Theory]
    [InlineData("skills")]
    [InlineData("references")]
    [InlineData("mcpTools")]
    public void MissingEmptyOrNonArrayPropertiesAreRejected(string propertyName)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Mutate(root => root.Remove(propertyName))));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Mutate(root => root[propertyName] = new JsonArray())));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Mutate(root => root[propertyName] = "new-skill")));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Mutate(root => root[propertyName] = new JsonArray(1))));
    }

    [Theory]
    [InlineData("skills", "NEW-SKILL", "new-skill")]
    [InlineData("mcpTools", "NEW_TOOL", "new_tool")]
    [InlineData("references", "NEW-SKILL/references/new.md", "new-skill/references/new.md")]
    public void DuplicateValuesAreRejectedCaseInsensitively(string propertyName, string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues(propertyName, first, second)));
    }

    [Theory]
    [InlineData("skills", "new-skill", "another-skill")]
    [InlineData("mcpTools", "new_tool", "another-tool")]
    [InlineData("references", "new-skill/references/new.md", "another-skill/references/guide.yml")]
    public void UnsortedValuesAreRejected(string propertyName, string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues(propertyName, first, second)));
    }

    [Theory]
    [InlineData("""{ "skills": [ "new-skill" ], "references": [ "new-skill/references/new.md" ], "mcpTools": [ "new_tool" ], "extra": [] }""")]
    [InlineData("""[]""")]
    [InlineData("""{ "skills": [ "new-skill" ],""")]
    public void UnexpectedDocumentsAreRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(json));
    }

    private static string ReplaceValues(string propertyName, params string[] values)
        => Mutate(root => root[propertyName] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()));

    private static string Mutate(Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(Allowlist)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }
}