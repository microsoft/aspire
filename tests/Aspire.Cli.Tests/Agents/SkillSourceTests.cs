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
    public void GetPluginNames_DefaultsToAspireWithoutSettingPlugins()
    {
        var source = new SkillSource { Marketplace = "team" };

        Assert.Equal(["aspire@team"], source.GetPluginNames());
        Assert.Null(source.Plugins);
    }

    [Fact]
    public void Resolve_MergesNamedSourcesAndFieldsAcrossConfigurationLayers()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"skillSources":{"default":{},"team":{"repository":"example/team","plugins":["team","deployment"],"openCodeV1Catalog":"https://example.com/v1/"}}}
            """)));
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"skillSources":{"TEAM":{"marketplace":"custom-market","openCodeV2Catalog":"https://example.com/v2/"},"other":{"repository":"example/other"}}}
            """)));

        var sources = SkillSource.Resolve(configuration);

        Assert.Collection(sources,
            source =>
            {
                Assert.Equal("microsoft/aspire-skills", source.Repository);
                Assert.Equal(["aspire@aspire-skills"], source.GetPluginNames());
            },
            source => Assert.Equal("example/other", source.Repository),
            source =>
            {
                Assert.Equal("example/team", source.Repository);
                Assert.Equal(["team@custom-market", "deployment@custom-market"], source.GetPluginNames());
                Assert.Equal("https://example.com/v1/", source.OpenCodeV1Catalog);
                Assert.Equal("https://example.com/v2/", source.OpenCodeV2Catalog);
            });
    }

    [Fact]
    public void Resolve_DefaultsMarketplaceToSourceName()
    {
        using var configuration = new ConfigurationManager();
        configuration["skillSources:default:enabled"] = "false";
        configuration["skillSources:team:repository"] = "example/team";

        var source = Assert.Single(SkillSource.Resolve(configuration));

        Assert.Equal("team", source.Marketplace);
        Assert.Equal(["aspire@team"], source.GetPluginNames());
    }

    [Theory]
    [InlineData("shared", "shared")]
    [InlineData("shared", "SHARED")]
    [InlineData("team", null)]
    [InlineData("TEAM", null)]
    public void Resolve_RejectsDuplicateResolvedMarketplaces(string firstMarketplace, string? teamMarketplace)
    {
        using var configuration = new ConfigurationManager();
        configuration["skillSources:default:enabled"] = "false";
        configuration["skillSources:first:repository"] = "example/first";
        configuration["skillSources:first:marketplace"] = firstMarketplace;
        configuration["skillSources:team:repository"] = "example/team";
        if (teamMarketplace is not null)
        {
            configuration["skillSources:team:marketplace"] = teamMarketplace;
        }

        var error = Assert.Throws<InvalidOperationException>(() => SkillSource.Resolve(configuration));

        Assert.Equal(AspireSkillsRemoteConfiguration.ExistingConfiguration("skillSources.team.marketplace").Message, error.Message);
    }

    [Fact]
    public void Resolve_MergesPluginsByIndexAcrossConfigurationLayers()
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"skillSources":{"default":{"plugins":["aspire","deployment"]}}}
            """)));
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes("""
            {"skillSources":{"default":{"plugins":["custom"]}}}
            """)));

        var source = Assert.Single(SkillSource.Resolve(configuration));

        Assert.Equal(["custom@aspire-skills", "deployment@aspire-skills"], source.GetPluginNames());
    }

    [Theory]
    [InlineData("""{"skillSources":"invalid"}""")]
    [InlineData("""{"skillSources":{"team":"invalid"}}""")]
    [InlineData("""{"skillSources":{"team":{}}}""")]
    [InlineData("""{"skillSources":{"team":{"repository":"relative/path/invalid"}}}""")]
    [InlineData("""{"skillSources":{"team":{"repository":"https://user:token@example.com/skills"}}}""")]
    [InlineData("""{"skillSources":{"team":{"repository":"example/team","marketplace":"aspire-skills"}}}""")]
    [InlineData("""{"skillSources":{"default":{"openCodeV1Catalog":"http://example.com/skills"}}}""")]
    [InlineData("""{"skillSources":{"default":{"plugins":"aspire"}}}""")]
    [InlineData("""{"skillSources":{"default":{"plugins":null}}}""")]
    [InlineData("""{"skillSources":{"default":{"plugins":[]}}}""")]
    [InlineData("""{"skillSources":{"default":{"plugins":[""]}}}""")]
    [InlineData("""{"skillSources":{"default":{"plugins":["aspire",null]}}}""")]
    [InlineData("""{"skillSources":{"default":{"plugins":["aspire","invalid@name"]}}}""")]
    [InlineData("""{"skillSources":{"default":{"plugins":{"name":"aspire"}}}}""")]
    [InlineData("""{"skillSources":{"default":{"plugins":[{"name":"aspire"}]}}}""")]
    public void Resolve_RejectsInvalidSources(string json)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Throws<InvalidOperationException>(() => SkillSource.Resolve(configuration));
    }

    [Theory]
    [InlineData("example/team", null)]
    [InlineData("https://git.example.com/team/skills", "main")]
    [InlineData("ssh://git@git.example.com/team/skills", "teams/preview")]
    [InlineData("git@git.example.com:team/skills", "refs/heads/teams/preview")]
    [InlineData("example/team", "0123456789abcdef0123456789abcdef01234567")]
    [InlineData("example/team", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123")]
    public void Resolve_AcceptsGitRepositoryAndRef(string repository, string? reference)
    {
        using var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["skillSources:default:repository"] = repository,
            ["skillSources:default:ref"] = reference
        });

        var source = Assert.Single(SkillSource.Resolve(configuration));

        Assert.Equal(repository, source.Repository);
        Assert.Equal(reference, source.Ref);
    }

    [Theory]
    [InlineData("")]
    [InlineData("--upload-pack=command")]
    [InlineData("HEAD")]
    [InlineData("main~1")]
    [InlineData("main:other")]
    [InlineData("main..other")]
    [InlineData("main@{1}")]
    [InlineData("refs/tags/release")]
    [InlineData("branch.lock")]
    [InlineData("branch//child")]
    [InlineData(".hidden/branch")]
    [InlineData("branch\nother")]
    public void Resolve_RejectsInvalidRefs(string reference)
    {
        using var configuration = new ConfigurationManager();
        configuration["skillSources:default:ref"] = reference;

        var error = Assert.Throws<InvalidOperationException>(() => SkillSource.Resolve(configuration));

        Assert.Equal(AspireSkillsRemoteConfiguration.ExistingConfiguration("skillSources.default.repository/ref").Message, error.Message);
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
            ["skillSources:team:plugins:0"] = "team",
            ["skillSources:team:plugins:1"] = "deployment",
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

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task Configure_ConflictInAdditionalSourceDoesNotPublishAnySource(string clientId)
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration["skillSources:team:repository"] = "example/team";
        context.Configuration["skillSources:team:plugins:0"] = "aspire";
        context.Configuration["skillSources:team:plugins:1"] = "deployment";
        var client = context.Clients.Single(client => client.Id == clientId);
        var path = client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project);
        const string original = """{"enabledPlugins":{"deployment@team":false}}""";
        await AgentConfigurationTestContext.WriteAsync(path, original);

        var result = Assert.Single(await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [client]).DefaultTimeout());

        Assert.Equal(AgentConfigurationStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task HasIntegrationRegistration_RecognizesAdditionalPlugin(string clientId)
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration["skillSources:team:repository"] = "example/team";
        context.Configuration["skillSources:team:plugins:0"] = "aspire";
        context.Configuration["skillSources:team:plugins:1"] = "deployment";
        var client = context.Clients.Single(client => client.Id == clientId);
        await AgentConfigurationTestContext.WriteAsync(
            client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project),
            """{"enabledPlugins":{"deployment@team":true}}""");

        Assert.True(client.HasIntegrationRegistration(context.Project, AgentConfigurationScope.Project, context.Sources));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task Configure_AddsPluginsToExistingPinnedMarketplace(string clientId)
    {
        using var context = new AgentConfigurationTestContext(output);
        context.Configuration["skillSources:default:enabled"] = "false";
        context.Configuration["skillSources:team:repository"] = "example/team";
        context.Configuration["skillSources:team:plugins:0"] = "aspire";
        context.Configuration["skillSources:team:plugins:1"] = "deployment";
        context.Configuration["skillSources:team:ref"] = "main";
        var client = context.Clients.Single(client => client.Id == clientId);
        var path = client.GetIntegrationConfigPath(AgentConfigurationScope.Project, context.Project);
        await AgentConfigurationTestContext.WriteAsync(path, """
            {
              "extraKnownMarketplaces": {
                "team": { "source": { "source": "github", "repo": "example/team", "ref": "release" } }
              },
              "enabledPlugins": { "aspire@team": true }
            }
            """);

        var result = Assert.Single(await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [client]).DefaultTimeout());

        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        await Verify(await File.ReadAllTextAsync(path), "json").UseParameters(clientId);
        var unchanged = Assert.Single(await context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [client]).DefaultTimeout());
        Assert.Equal(AgentConfigurationStatus.Unchanged, unchanged.Status);
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
            ["skillSources:default:plugins:0"] = "custom",
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
