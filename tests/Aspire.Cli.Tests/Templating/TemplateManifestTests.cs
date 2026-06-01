// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.Templating;

namespace Aspire.Cli.Tests.Templating;

/// <summary>
/// Tests for the declarative <c>template.json</c> manifest parser and its
/// validation rules. The manifest is the contract between a template tree on
/// disk and the CLI code that supplies symbol values, so malformed or
/// drifted manifests must fail loudly at parse time rather than emit a
/// half-rendered project.
/// </summary>
public class TemplateManifestTests
{
    private static TemplateManifest Parse(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return TemplateManifest.Parse(stream);
    }

    [Fact]
    public void Parse_ValidManifest_PopulatesReplacementsRenamesAndConditions()
    {
        var manifest = Parse("""
            {
              "replacements": [
                { "from": "Aspire.AppHost1", "to": "projectName", "target": "both" },
                { "from": "{{aspireVersion}}", "to": "aspireVersion" },
                { "from": "{{port}}", "to": "port", "target": "content" }
              ],
              "fileRenames": [
                { "fromSuffix": "._csproj", "toSuffix": ".csproj" }
              ],
              "conditions": [ "useRedisCache", "localhostTld" ]
            }
            """);

        Assert.Equal(3, manifest.Replacements.Count);

        var projectName = manifest.Replacements[0];
        Assert.Equal("Aspire.AppHost1", projectName.From);
        Assert.Equal("projectName", projectName.To);
        Assert.True(projectName.AppliesToContent);
        Assert.True(projectName.AppliesToPath);

        // Default target is content-only when omitted.
        var version = manifest.Replacements[1];
        Assert.True(version.AppliesToContent);
        Assert.False(version.AppliesToPath);

        Assert.Equal(TemplateReplacementTarget.Content, manifest.Replacements[2].Target);

        var rename = Assert.Single(manifest.FileRenames);
        Assert.Equal("._csproj", rename.FromSuffix);
        Assert.Equal(".csproj", rename.ToSuffix);

        Assert.Equal(new[] { "useRedisCache", "localhostTld" }, manifest.Conditions);
    }

    [Fact]
    public void Parse_PreservesDeclaredReplacementOrder()
    {
        // Replacements are applied in declared order, so the parser must preserve it.
        var manifest = Parse("""
            { "replacements": [
                { "from": "A", "to": "a" },
                { "from": "B", "to": "b" },
                { "from": "C", "to": "c" } ] }
            """);

        Assert.Equal(new[] { "A", "B", "C" }, manifest.Replacements.Select(r => r.From).ToArray());
    }

    [Fact]
    public void Parse_EmptyDocument_ReturnsEmptyManifest()
    {
        var manifest = Parse("{}");

        Assert.Empty(manifest.Replacements);
        Assert.Empty(manifest.FileRenames);
        Assert.Empty(manifest.Conditions);
    }

    [Fact]
    public void Parse_MalformedJson_Throws()
    {
        var ex = Assert.Throws<TemplateManifestException>(() => Parse("{ not json"));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void Parse_MissingFrom_Throws()
    {
        Assert.Throws<TemplateManifestException>(() => Parse("""
            { "replacements": [ { "to": "projectName" } ] }
            """));
    }

    [Fact]
    public void Parse_MissingTo_Throws()
    {
        var ex = Assert.Throws<TemplateManifestException>(() => Parse("""
            { "replacements": [ { "from": "X" } ] }
            """));
        Assert.Contains("non-empty 'to'", ex.Message);
    }

    [Fact]
    public void Parse_UnknownTarget_Throws()
    {
        var ex = Assert.Throws<TemplateManifestException>(() => Parse("""
            { "replacements": [ { "from": "X", "to": "x", "target": "filename" } ] }
            """));
        Assert.Contains("unknown target", ex.Message);
    }

    [Fact]
    public void Parse_DuplicateFromForContent_Throws()
    {
        var ex = Assert.Throws<TemplateManifestException>(() => Parse("""
            { "replacements": [
                { "from": "X", "to": "a" },
                { "from": "X", "to": "b" } ] }
            """));
        Assert.Contains("more than once for content", ex.Message);
    }

    [Fact]
    public void Parse_SameFromForContentAndPath_IsAllowed()
    {
        // The same literal may map to one symbol in content and another in paths
        // because the two are applied in independent passes.
        var manifest = Parse("""
            { "replacements": [
                { "from": "X", "to": "a", "target": "content" },
                { "from": "X", "to": "b", "target": "path" } ] }
            """);

        Assert.Equal(2, manifest.Replacements.Count);
    }

    [Fact]
    public void Parse_DuplicateCondition_Throws()
    {
        var ex = Assert.Throws<TemplateManifestException>(() => Parse("""
            { "conditions": [ "redis", "redis" ] }
            """));
        Assert.Contains("more than once", ex.Message);
    }

    [Theory]
    [InlineData("1redis")]
    [InlineData("_redis")]
    [InlineData("redis.cache")]
    [InlineData("redis cache")]
    public void Parse_InvalidConditionName_Throws(string conditionName)
    {
        // Condition names must stay within the grammar the leftover-marker detector
        // understands, otherwise an undeclared marker could ship to the user.
        var ex = Assert.Throws<TemplateManifestException>(() => Parse(
            $$"""
            { "conditions": [ "{{conditionName}}" ] }
            """));
        Assert.Contains("not a valid name", ex.Message);
    }

    [Fact]
    public void Parse_StripSuffixRename_AllowsEmptyToSuffix()
    {
        var manifest = Parse("""
            { "fileRenames": [ { "fromSuffix": ".txt-template" } ] }
            """);

        var rename = Assert.Single(manifest.FileRenames);
        Assert.Equal(".txt-template", rename.FromSuffix);
        Assert.Equal(string.Empty, rename.ToSuffix);
    }

    [Fact]
    public void EnsureSatisfiedBy_MissingSymbol_Throws()
    {
        var manifest = Parse("""
            { "replacements": [ { "from": "X", "to": "projectName" } ] }
            """);

        var ex = Assert.Throws<TemplateManifestException>(() => manifest.EnsureSatisfiedBy(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal)));
        Assert.Contains("projectName", ex.Message);
    }

    [Fact]
    public void EnsureSatisfiedBy_MissingCondition_Throws()
    {
        var manifest = Parse("""
            { "conditions": [ "useRedisCache" ] }
            """);

        var ex = Assert.Throws<TemplateManifestException>(() => manifest.EnsureSatisfiedBy(
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal)));
        Assert.Contains("useRedisCache", ex.Message);
    }

    [Fact]
    public void EnsureSatisfiedBy_AllSupplied_DoesNotThrow()
    {
        var manifest = Parse("""
            {
              "replacements": [ { "from": "X", "to": "projectName" } ],
              "conditions": [ "useRedisCache" ]
            }
            """);

        manifest.EnsureSatisfiedBy(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["projectName"] = "MyApp" },
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["useRedisCache"] = true });
    }
}
