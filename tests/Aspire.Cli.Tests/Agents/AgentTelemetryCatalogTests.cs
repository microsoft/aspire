// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Hooks;

namespace Aspire.Cli.Tests.Agents;

public class AgentTelemetryCatalogTests
{
    private const string Script = """ASPIRE_MCP_TOOLS="new_tool" """;
    private const string Manifest = """
        {
          "version": "test",
          "skills": [
            {
              "name": "new-skill",
              "files": [
                { "relativePath": "SKILL.md" },
                { "relativePath": "references/new.md" },
                { "relativePath": "evals/private.json" },
                { "relativePath": "scripts/helper.sh" }
              ]
            },
            {
              "name": "another-skill",
              "files": [
                { "relativePath": "SKILL.md" },
                { "relativePath": "references/guide.yml" }
              ]
            }
          ]
        }
        """;

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ReadsSkillsAndReferencesFromManifestWithoutScriptDeclarations(string newline)
    {
        var catalog = AgentTelemetryCatalog.Parse(ParseManifest(Manifest.ReplaceLineEndings(newline)), Script);
        Assert.Equal(["another-skill", "new-skill"], catalog.Skills.Order());
        Assert.Equal(["new_tool"], catalog.Tools);
        Assert.Equal(["another-skill/references/guide.yml", "new-skill/references/new.md"], catalog.References.Order());
        Assert.Contains("NEW-SKILL", catalog.Skills);
        Assert.False(catalog.Skills.Contains("third-party"));
    }

    [Fact]
    public void ManifestControlsSkillsEvenIfScriptHasStaleSkillDeclarations()
    {
        var script = Script + "\n" + """
            ASPIRE_SKILLS="stale-skill"
            ASPIRE_REFERENCE_FILES="stale-skill/references/old.md"
            """;
        var catalog = AgentTelemetryCatalog.Parse(ParseManifest(Manifest), script);
        Assert.Equal(["another-skill", "new-skill"], catalog.Skills.Order());
        Assert.Equal(["another-skill/references/guide.yml", "new-skill/references/new.md"], catalog.References.Order());
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"skills":null}""")]
    [InlineData("""{"skills":[null]}""")]
    [InlineData("""{"skills":[{"name":""}]}""")]
    [InlineData("""{"skills":[{"name":"missing-files"}]}""")]
    [InlineData("""{"skills":[{"name":"null-files","files":null}]}""")]
    [InlineData("""{"skills":[{"name":"duplicate","files":[]},{"name":"duplicate","files":[]}]}""")]
    public void InvalidSkillInventoryFailsExplicitly(string json)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ParseManifest(json), Script));
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("third/party")]
    [InlineData("C:\\private")]
    public void SkillNamesUseExistingBundleValidation(string name)
    {
        var manifest = new SkillBundleManifest { Skills = [new() { Name = name }] };
        Assert.Throws<InvalidOperationException>(() => AgentTelemetryCatalog.Parse(manifest, Script));
    }

    [Theory]
    [InlineData("../private.md")]
    [InlineData("/private.md")]
    [InlineData("references/../../private.md")]
    public void ReferencePathsUseExistingBundleValidation(string path)
    {
        var manifest = new SkillBundleManifest
        {
            Skills = [new() { Name = "test-skill", Files = [new() { RelativePath = path }] }]
        };
        Assert.Throws<InvalidOperationException>(() => AgentTelemetryCatalog.Parse(manifest, Script));
    }

    [Theory]
    [InlineData("")]
    [InlineData("$(command)")]
    [InlineData("name;command")]
    public void InvalidToolDeclarationIsRejected(string values)
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ParseManifest(Manifest), Script.Replace("new_tool", values)));
    }

    [Fact]
    public void MissingOrDuplicateToolDeclarationsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ParseManifest(Manifest), ""));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.Parse(ParseManifest(Manifest), Script + "\n" + Script));
    }

    [Theory]
    [InlineData("skill-manifest.json")]
    [InlineData("package/skill-manifest.json")]
    [InlineData("aspire-skills-v1.2.3/skill-manifest.json")]
    public void ReadsStructuredManifestInMemoryWithoutVersionSpecificPaths(string path)
    {
        using var archive = CreateArchive((path, Manifest), ("skills/third-party/SKILL.md", "Not in the manifest."));
        var manifest = AgentTelemetryCatalog.ReadManifest(archive);
        var catalog = AgentTelemetryCatalog.Parse(manifest, Script);
        Assert.Equal(["another-skill", "new-skill"], catalog.Skills.Order());
        Assert.True(archive.CanRead);
    }

    [Fact]
    public void MissingOrDuplicateManifestsAreRejected()
    {
        using var missing = CreateArchive(("skills/not-a-manifest.txt", "{}"));
        using var duplicate = CreateArchive(("first/skill-manifest.json", Manifest), ("second/skill-manifest.json", Manifest));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.ReadManifest(missing));
        Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.ReadManifest(duplicate));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{malformed")]
    public void InvalidManifestJsonIsRejected(string content)
    {
        using var archive = CreateArchive(("skill-manifest.json", content));
        if (content == "null")
        {
            Assert.Throws<InvalidDataException>(() => AgentTelemetryCatalog.ReadManifest(archive));
        }
        else
        {
            Assert.Throws<JsonException>(() => AgentTelemetryCatalog.ReadManifest(archive));
        }
    }

    private static SkillBundleManifest ParseManifest(string json)
        => JsonSerializer.Deserialize(json, AspireSkillsJsonSerializerContext.Default.SkillBundleManifest)!;

    private static MemoryStream CreateArchive(params (string Path, string Content)[] entries)
    {
        var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionMode.Compress, leaveOpen: true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(content));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = data });
            }
        }
        archive.Position = 0;
        return archive;
    }
}
