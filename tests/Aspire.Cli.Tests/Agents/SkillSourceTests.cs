// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Tests.Agents;

public class SkillSourceTests(ITestOutputHelper output)
{
    [Fact]
    public void Resolve_MergesNamedSourcesAndFieldsAcrossConfigurationLayers()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"skillSources":{"default":{},"team":{"repository":"example/team","plugin":"team","openCodeV1Catalog":"https://example.com/v1/"}}}
            """)));
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"skillSources":{"TEAM":{"marketplace":"custom-market","openCodeV2Catalog":"https://example.com/v2/"},"other":{"repository":"example/other"}}}
            """)));

        var sources = SkillSource.Resolve(configuration);

        Assert.Collection(sources,
            source =>
            {
                Assert.Equal("microsoft/aspire-skills", source.Repository);
                Assert.Equal("aspire@aspire-skills", source.PluginName);
            },
            source => Assert.Equal("example/other", source.Repository),
            source =>
            {
                Assert.Equal("example/team", source.Repository);
                Assert.Equal("team@custom-market", source.PluginName);
                Assert.Equal("https://example.com/v1/", source.OpenCodeV1Catalog);
                Assert.Equal("https://example.com/v2/", source.OpenCodeV2Catalog);
            });
    }

    [Theory]
    [InlineData("""{"skillSources":"invalid"}""")]
    [InlineData("""{"skillSources":{"team":"invalid"}}""")]
    [InlineData("""{"skillSources":{"team":{}}}""")]
    [InlineData("""{"skillSources":{"team":{"repository":"relative/path/invalid"}}}""")]
    [InlineData("""{"skillSources":{"team":{"repository":"https://user:token@example.com/skills"}}}""")]
    [InlineData("""{"skillSources":{"team":{"repository":"example/team","marketplace":"aspire-skills"}}}""")]
    [InlineData("""{"skillSources":{"default":{"openCodeV1Catalog":"http://example.com/skills"}}}""")]
    public void Resolve_RejectsInvalidSources(string json)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Throws<InvalidOperationException>(() => SkillSource.Resolve(configuration));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Configure_RegistersAllSourcesAndIsIdempotent(bool global, bool openCodeV2)
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["skillSources:team:repository"] = "https://git.example.com/team/skills.git",
            ["skillSources:team:plugin"] = "team",
            ["skillSources:team:ref"] = "main",
            ["skillSources:team:openCodeV1Catalog"] = "https://example.com/v1/",
            ["skillSources:team:openCodeV2Catalog"] = "https://example.com/v2/"
        });
        var scope = global ? AgentConfigurationScope.Global : AgentConfigurationScope.Project;
        if (openCodeV2)
        {
            await AgentConfigurationTestContext.WriteAsync(
                context.OpenCode.GetIntegrationConfigPath(scope, context.Project),
                """{"skills":["./local-skills"]}""");
        }

        var results = await context.ConfigureIntegrationAsync(scope, context.Clients).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var configurations = new JsonObject();
        foreach (var client in context.Clients)
        {
            Assert.True(client.HasIntegrationRegistration(context.Project, scope, context.Sources));
            configurations[client.Id] = AgentConfiguration.ReadObject(client.GetIntegrationConfigPath(scope, context.Project));
        }

        var unchanged = await context.ConfigureIntegrationAsync(scope, context.Clients).DefaultTimeout();
        Assert.All(unchanged, result => Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status));
        await Verify(configurations.ToJsonString(), "json").UseParameters(global, openCodeV2);
    }

    [Fact]
    public async Task Configure_DisablesDefaultAndPreservesUnrelatedRegistrations()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration["skillSources:default:enabled"] = "false";
        context.Configuration["skillSources:team:repository"] = "example/team";
        var path = context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project);
        await AgentConfigurationTestContext.WriteAsync(path, """
            {"enabledPlugins":{"other@other":false}}
            """);

        var result = Assert.Single(await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [context.Copilot]).DefaultTimeout());

        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        var root = AgentConfiguration.ReadObject(path)!;
        await Verify(root.ToJsonString(), "json");
    }

    [Fact]
    public async Task Configure_ConflictInAdditionalSourceDoesNotPublishAnySource()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration["skillSources:team:repository"] = "example/team";
        var path = context.Copilot.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project);
        const string original = """{"enabledPlugins":{"aspire@team":false}}""";
        await AgentConfigurationTestContext.WriteAsync(path, original);

        var result = Assert.Single(await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [context.Copilot]).DefaultTimeout());

        Assert.Equal(AgentConfigurationStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Resolve_UsesNewlyMaterializedAppConfiguration()
    {
        using var context = new AgentConfigurationTestContext(output);
        var app = context.Project.CreateSubdirectory("app");
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(app.FullName, "aspire.config.json"), """
            {"skillSources":{"team":{"repository":"example/team"},"default":{"enabled":false}}}
            """);

        var source = Assert.Single(SkillSource.Resolve(app, new FileInfo(Path.Combine(context.Home.FullName, "aspire.config.json"))));

        Assert.Equal("example/team", source.Repository);
    }

    [Theory]
    [InlineData("copilot", "microsoft/aspire-skills")]
    [InlineData("copilot", "https://github.com/microsoft/aspire-skills")]
    [InlineData("copilot", "https://github.com/microsoft/aspire-skills.git/")]
    [InlineData("copilot", "https://github.com/Microsoft/Aspire-Skills")]
    [InlineData("claude", "microsoft/aspire-skills")]
    [InlineData("claude", "https://github.com/microsoft/aspire-skills")]
    [InlineData("claude", "https://github.com/microsoft/aspire-skills.git/")]
    [InlineData("claude", "https://github.com/Microsoft/Aspire-Skills")]
    public async Task Configure_PreservesExistingGitHubSourceAndPin(string clientId, string repository)
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration["skillSources:default:repository"] = repository;
        context.Configuration["skillSources:default:ref"] = "main";
        var client = context.Clients.Single(client => client.Id == clientId);
        var path = client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project);
        const string original = """
            {
              "extraKnownMarketplaces": {
                "aspire-skills": { "source": { "source": "github", "repo": "microsoft/aspire-skills", "ref": "release" } }
              },
              "enabledPlugins": { "aspire@aspire-skills": true }
            }
            """;
        await AgentConfigurationTestContext.WriteAsync(path, original);

        var result = Assert.Single(await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [client]).DefaultTimeout());

        Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Configure_AllSourcesDisabledCreatesNoFiles()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration["skillSources:default:enabled"] = "false";
        context.Configuration["skillSources:team:enabled"] = "false";

        var results = await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, context.Clients).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status));
        Assert.Empty(Directory.GetFiles(context.Project.FullName, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("copilot", "https://github.com/Microsoft/Aspire-Skills", "https://github.com/microsoft/aspire-skills.git", true)]
    [InlineData("claude", "https://github.com/Microsoft/Aspire-Skills", "https://github.com/microsoft/aspire-skills.git", true)]
    [InlineData("copilot", "https://GITHUB.COM/Microsoft/Aspire-Skills.git/", "https://github.com/microsoft/aspire-skills.git", true)]
    [InlineData("claude", "https://GITHUB.COM/Microsoft/Aspire-Skills.git/", "https://github.com/microsoft/aspire-skills.git", true)]
    [InlineData("copilot", "https://git.example.com/team/skills", "https://git.example.com/team/skills.git", true)]
    [InlineData("claude", "https://git.example.com/team/skills", "https://git.example.com/team/skills.git", true)]
    [InlineData("copilot", "https://git.example.com/Team/Skills", "https://git.example.com/team/skills.git", false)]
    [InlineData("claude", "https://git.example.com/Team/Skills", "https://git.example.com/team/skills.git", false)]
    public async Task Configure_MatchesExistingGitSourcesUsingHostCaseRules(string clientId, string repository, string existingRepository, bool matches)
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration["skillSources:default:repository"] = repository;
        context.Configuration["skillSources:default:ref"] = "main";
        var client = context.Clients.Single(client => client.Id == clientId);
        var path = client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project);
        var original = $$"""
            {
              "extraKnownMarketplaces": {
                "aspire-skills": { "source": { "source": "git", "url": "{{existingRepository}}", "ref": "release" } }
              },
              "enabledPlugins": { "aspire@aspire-skills": true }
            }
            """;
        await AgentConfigurationTestContext.WriteAsync(path, original);

        var result = Assert.Single(await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [client]).DefaultTimeout());

        Assert.Equal(matches ? AgentConfigurationStatus.Unchanged : AgentConfigurationStatus.Failed, result.Status);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Configure_DefaultOverridesAndCatalogFallback()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["skillSources:default:repository"] = "example/replacement",
            ["skillSources:default:marketplace"] = "replacement",
            ["skillSources:default:plugin"] = "custom",
            ["skillSources:default:openCodeV1Catalog"] = "https://example.com/replacement/v1/",
            ["skillSources:default:openCodeV2Catalog"] = "https://example.com/replacement/v2/",
            ["skillSources:team:repository"] = "example/team",
            ["skillSources:team:openCodeV1Catalog"] = "https://example.com/team/"
        });
        await AgentConfigurationTestContext.WriteAsync(
            context.OpenCode.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project), """{"skills":[]}""");

        var results = await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, context.Clients).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var configurations = new JsonObject();
        foreach (var client in context.Clients)
        {
            configurations[client.Id] = AgentConfiguration.ReadObject(
                client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project));
        }
        await Verify(configurations.ToJsonString(), "json");
    }
}
