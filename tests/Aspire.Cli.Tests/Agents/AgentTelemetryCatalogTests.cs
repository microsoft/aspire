// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.Agents;

public class AgentTelemetryCatalogTests
{
    private const string CatalogJson = """
        {
          "skills": [ "another-skill", "new-skill" ],
          "references": [ "another-skill/references/guide.yml", "new-skill/references/new.md" ]
        }
        """;

    [Fact]
    public void ReadsCatalogFromJson()
    {
        var catalog = AgentTelemetryCatalog.Parse(CatalogJson);
        Assert.Equal(["another-skill", "new-skill"], catalog.Skills.Order());
        Assert.Equal(["another-skill/references/guide.yml", "new-skill/references/new.md"], catalog.References.Order());
        Assert.Contains("NEW-SKILL", catalog.Skills);
        Assert.False(catalog.Skills.Contains("third-party"));
    }

    [Theory]
    [InlineData("valid $(command)")]
    [InlineData("third/party")]
    [InlineData("")]
    public void SkillIdentifiersMustBeValid(string value)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues("skills", value)));
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
    public void MissingEmptyOrNonArrayPropertiesAreRejected(string propertyName)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Mutate(root => root.Remove(propertyName))));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Mutate(root => root[propertyName] = new JsonArray())));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Mutate(root => root[propertyName] = "new-skill")));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(Mutate(root => root[propertyName] = new JsonArray(1))));
    }

    [Theory]
    [InlineData("skills", "NEW-SKILL", "new-skill")]
    [InlineData("references", "NEW-SKILL/references/new.md", "new-skill/references/new.md")]
    public void DuplicateValuesAreRejectedCaseInsensitively(string propertyName, string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues(propertyName, first, second)));
    }

    [Theory]
    [InlineData("skills", "new-skill", "another-skill")]
    [InlineData("references", "new-skill/references/new.md", "another-skill/references/guide.yml")]
    public void UnsortedValuesAreRejected(string propertyName, string first, string second)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ReplaceValues(propertyName, first, second)));
    }

    [Theory]
    [InlineData("""{ "skills": [ "new-skill" ], "references": [ "new-skill/references/new.md" ], "mcpTools": [ "doctor" ] }""")]
    [InlineData("""{ "skills": [ "new-skill" ], "references": [ "new-skill/references/new.md" ], "extra": [] }""")]
    [InlineData("""{ "skills": [ "unused" ], "skills": [ "new-skill" ], "references": [ "new-skill/references/new.md" ] }""")]
    [InlineData("""{ "skills": [ "new-skill" ], "references": [ "unused/references/a.md" ], "references": [ "new-skill/references/new.md" ] }""")]
    [InlineData("""{ "skills": [ "new-skill" ] }""")]
    [InlineData("""[]""")]
    [InlineData("""{ "skills": [ "new-skill" ],""")]
    public void UnexpectedDocumentsAreRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(json));
    }

    [Theory]
    [InlineData("aspire-list_resources", true)]
    [InlineData("mcp__aspire__list_resources", true)]
    [InlineData("mcp_aspire_list_resources", true)]
    [InlineData("list_resources", false)]
    [InlineData("aspire-unknown_tool", false)]
    [InlineData("aspire-", false)]
    [InlineData(null, false)]
    public void IsKnownMcpToolRequiresPrefixAndCatalogTool(string? toolName, bool expected)
    {
        Assert.Equal(expected, AgentTelemetryCatalog.Canonical.IsKnownMcpTool(toolName));
    }

    private static string ReplaceValues(string propertyName, params string[] values)
        => Mutate(root => root[propertyName] = new JsonArray(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray()));

    private static string Mutate(Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(CatalogJson)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }
}