// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Configuration;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class OpenCodeConfigurationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task FreshUndetectedClient_UsesStableV1CatalogAndMcp()
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request([AgentClientKind.OpenCode], mcp: true);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(4, results.Count);
        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var project = await File.ReadAllTextAsync(Path.Combine(context.Project.FullName, "opencode.json")).DefaultTimeout();
        Assert.Equal(project, await File.ReadAllTextAsync(Path.Combine(context.Paths.OpenCodeDirectory, "opencode.json")).DefaultTimeout());
        Assert.Empty(context.SkillInstaller.Requests);
        Assert.Equal(0, context.HookInstaller.Calls);
        await Verify(project, "json");
    }

    [Fact]
    public async Task DetectedV2_UsesArrayCatalogAndNestedMcpServers()
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request([AgentClientKind.OpenCode], mcp: true,
            detections: [new(AgentClientKind.OpenCode, "opencode v2.0.0-preview.1", false)]);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var path = Path.Combine(context.Project.FullName, "opencode.json");
        await Verify(await File.ReadAllTextAsync(path).DefaultTimeout(), "json");
    }

    [Fact]
    public async Task ExistingV2Jsonc_EstablishesTheSchemaWithoutVersionDetection()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, ".opencode", "opencode.jsonc");
        await AgentConfigurationTestContext.WriteAsync(path, """{"model":"preserved","skills":["./team-skills"],}""").DefaultTimeout();
        var request = context.Request([AgentClientKind.OpenCode], mcp: true);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path).DefaultTimeout())!;

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.Equal("preserved", (string?)root["model"]);
        Assert.Equal(["./team-skills", OpenCodeConfigurationHandler.V2Catalog], root["skills"]!.AsArray().Select(value => (string)value!));
        Assert.Equal(["aspire"], root["mcp"]!["servers"]!.AsObject().Select(property => property.Key));
        Assert.False(File.Exists(Path.Combine(context.Project.FullName, "opencode.json")));
        await Verify(await File.ReadAllTextAsync(path).DefaultTimeout(), "json");
    }

    [Fact]
    public async Task Registration_PreservesPinnedCatalogsInsteadOfAddingMain()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Paths.OpenCodeDirectory, "opencode.json");
        const string existing = """{"skills":{"paths":["./private-skills"],"urls":["https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/v1/"]},"autoupdate":false}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.OpenCode])).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Unchanged, results.Single(result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        var project = await File.ReadAllTextAsync(Path.Combine(context.Project.FullName, "opencode.json")).DefaultTimeout();
        await Verify(project, "json");
    }

    [Theory]
    [InlineData("""{"skills":{"urls":[]},"mcp":{"servers":{}}}""", null)]
    [InlineData("""{"skills":[]}""", "1.2.0")]
    [InlineData("""{"skills":{"urls":[]}}""", "2.0.0")]
    [InlineData("{}", "3.0.0")]
    [InlineData("""{"skills":["https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/"]}""", null)]
    [InlineData("""{"mcp":{"servers":{},"aspire":{"type":"local","command":["aspire","agent","mcp"]}}}""", null)]
    [InlineData("""{"mcp":{"servers":{"aspire":{"type":"local","enabled":false}}}}""", null)]
    public async Task ConflictingSchemaEvidence_IsBlockedWithoutConversion(string existing, string? version)
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();
        var request = context.Request([AgentClientKind.OpenCode], mcp: true,
            detections: version is null ? [] : [new(AgentClientKind.OpenCode, version, false)]);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.False(Directory.Exists(context.Paths.OpenCodeDirectory));
    }

    [Fact]
    public async Task IncompatibleProjectAndGlobalSchemas_AreBothPreserved()
    {
        using var context = new AgentConfigurationTestContext(output);
        var projectPath = Path.Combine(context.Project.FullName, "opencode.json");
        var userPath = Path.Combine(context.Paths.OpenCodeDirectory, "opencode.json");
        const string project = """{"skills":{"urls":[]}}""";
        const string user = """{"mcp":{"servers":{}}}""";
        await AgentConfigurationTestContext.WriteAsync(projectPath, project).DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(userPath, user).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.OpenCode], mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(project, await File.ReadAllTextAsync(projectPath).DefaultTimeout());
        Assert.Equal(user, await File.ReadAllTextAsync(userPath).DefaultTimeout());
    }

    [Theory]
    [InlineData("""{"skills":null}""")]
    [InlineData("""{"skills":{"urls":[true]}}""")]
    [InlineData("""{"skills":[{}]}""")]
    [InlineData("""{"mcp":[]}""")]
    [InlineData("""{"mcp":{"servers":[]}}""")]
    public async Task InvalidContainerShapes_AreExplicitlyBlocked(string existing)
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.OpenCode], mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
    }

    [Theory]
    [InlineData("""{"mcp":{"aspire":{"enabled":false}}}""")]
    [InlineData("""{"mcp":{"servers":{"aspire":{"disabled":true}}}}""")]
    public async Task Mcp_DisabledChoicesAreNotOverriddenInAnotherScope(string existing)
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Paths.OpenCodeDirectory, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.OpenCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Skipped, result.Status));
        Assert.Equal(existing, await File.ReadAllTextAsync(path).DefaultTimeout());
        Assert.False(File.Exists(Path.Combine(context.Project.FullName, "opencode.json")));
    }

    [Fact]
    public async Task JsonAndJsoncAtOneScope_AreNotArbitrarilyRewritten()
    {
        using var context = new AgentConfigurationTestContext(output);
        var json = Path.Combine(context.Project.FullName, "opencode.json");
        var jsonc = Path.Combine(context.Project.FullName, "opencode.jsonc");
        await AgentConfigurationTestContext.WriteAsync(json, "{}").DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(jsonc, "{/* preserved */}").DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.OpenCode])).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Blocked, results.Single(result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Configured, results.Single(result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.Equal("{}", await File.ReadAllTextAsync(json).DefaultTimeout());
        Assert.Equal("{/* preserved */}", await File.ReadAllTextAsync(jsonc).DefaultTimeout());
    }

    [Fact]
    public async Task OverridesThatAliasProjectAndGlobal_AreDeduplicatedAndIdempotent()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "opencode.json");
        context.SetVariable("OPENCODE_CONFIG", path);
        var request = context.Request([AgentClientKind.OpenCode], mcp: true);

        var first = await context.ConfigureNativeAsync(request).DefaultTimeout();
        File.SetLastWriteTimeUtc(path, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var timestamp = File.GetLastWriteTimeUtc(path);
        var contents = await File.ReadAllBytesAsync(path).DefaultTimeout();
        var second = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(2, first.Count);
        Assert.All(first, result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        Assert.All(second, result => Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status));
        Assert.Equal(contents, await File.ReadAllBytesAsync(path).DefaultTimeout());
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        Assert.False(Directory.Exists(context.Paths.OpenCodeDirectory));
    }

    [Fact]
    public async Task InlineOverride_IsNotOverwrittenOrMistakenForPersistentActivation()
    {
        using var context = new AgentConfigurationTestContext(output);
        context.SetVariable("OPENCODE_CONFIG_CONTENT", """{"skills":[]}""");

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.OpenCode])).DefaultTimeout();

        Assert.All(results, result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.Empty(Directory.EnumerateFiles(context.Project.FullName));
        Assert.False(Directory.Exists(context.Paths.OpenCodeDirectory));
    }

    [Fact]
    public async Task McpMigration_PreservesOpenCodeCommandSuffixAndEnvironment()
    {
        using var context = new AgentConfigurationTestContext(output);
        var path = Path.Combine(context.Project.FullName, "opencode.jsonc");
        await AgentConfigurationTestContext.WriteAsync(path, """
            {
              "mcp": {
                "aspire": {
                  "type": "local",
                  "command": ["aspire", "mcp", "start", "--debug"],
                  "environment": { "PRESERVED": "yes" },
                  "enabled": true
                }
              }
            }
            """).DefaultTimeout();

        var results = await context.ConfigureNativeAsync(context.Request([AgentClientKind.OpenCode], skills: false, mcp: true)).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, results.Single(result => result.Scope is AgentConfigurationScope.Project).Status);
        Assert.Equal(AgentConfigurationStatus.Skipped, results.Single(result => result.Scope is AgentConfigurationScope.User).Status);
        Assert.False(File.Exists(Path.Combine(context.Paths.OpenCodeDirectory, "opencode.json")));
        await Verify(await File.ReadAllTextAsync(path).DefaultTimeout(), "json");
    }

    [Theory]
    [InlineData("0.15.0")]
    [InlineData("1.18.30")]
    [InlineData("opencode v1.18.31-preview.1")]
    [InlineData("1.18.31-preview.1+build")]
    public async Task OlderV1_BlocksCatalogRegistrationWithoutChangingExistingMcp(string version)
    {
        using var context = new AgentConfigurationTestContext(output);
        var project = Path.Combine(context.Project.FullName, "opencode.json");
        var user = Path.Combine(context.Paths.OpenCodeDirectory, "opencode.json");
        const string existing = """{"mcp":{"aspire":{"type":"local","command":["aspire","agent","mcp"]}}}""";
        await AgentConfigurationTestContext.WriteAsync(project, existing).DefaultTimeout();
        await AgentConfigurationTestContext.WriteAsync(user, existing).DefaultTimeout();
        var request = context.Request([AgentClientKind.OpenCode], mcp: true,
            detections: [new(AgentClientKind.OpenCode, version, false)]);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(4, results.Count);
        Assert.All(results.Where(result => result.Asset is AgentAssetKind.AspireSkills), result =>
        {
            Assert.Equal(AgentConfigurationStatus.Blocked, result.Status);
            Assert.NotEmpty(result.Message!);
        });
        Assert.All(results.Where(result => result.Asset is AgentAssetKind.Mcp),
            result => Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status));
        Assert.True(new AgentInitResult(results).HasErrors);
        Assert.Equal(existing, await File.ReadAllTextAsync(project).DefaultTimeout());
        Assert.Equal(existing, await File.ReadAllTextAsync(user).DefaultTimeout());
        Assert.Empty(context.SkillInstaller.Requests);
    }

    [Theory]
    [InlineData("1.18.31")]
    [InlineData("1.18.31+build")]
    [InlineData("1.18.32-preview.1")]
    [InlineData("1.19.0")]
    public async Task SupportedV1_RegistersCatalogAtAndAboveTheCapabilityBoundary(string version)
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request([AgentClientKind.OpenCode],
            detections: [new(AgentClientKind.OpenCode, version, false)]);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(AgentAssetKind.AspireSkills, result.Asset);
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        });
        Assert.Empty(context.SkillInstaller.Requests);
    }

    [Fact]
    public async Task OlderV1_CanConfigureAndRepairMcpWithoutAddingCatalog()
    {
        using var context = new AgentConfigurationTestContext(output);
        var project = Path.Combine(context.Project.FullName, "opencode.json");
        var user = Path.Combine(context.Paths.OpenCodeDirectory, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(project, """
            {
              "model": "preserved",
              "mcp": {
                "other": { "type": "local", "command": ["other-server"] },
                "aspire": { "type": "local", "command": ["aspire", "mcp", "start"], "enabled": true }
              }
            }
            """).DefaultTimeout();
        var request = context.Request([AgentClientKind.OpenCode], mcp: true,
            detections: [new(AgentClientKind.OpenCode, "1.18.30", false)]);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.All(results.Where(result => result.Asset is AgentAssetKind.AspireSkills),
            result => Assert.Equal(AgentConfigurationStatus.Blocked, result.Status));
        Assert.All(results.Where(result => result.Asset is AgentAssetKind.Mcp),
            result => Assert.Equal(AgentConfigurationStatus.Configured, result.Status));
        var configurations = new JsonObject
        {
            ["project"] = JsonNode.Parse(await File.ReadAllTextAsync(project).DefaultTimeout()),
            ["user"] = JsonNode.Parse(await File.ReadAllTextAsync(user).DefaultTimeout())
        };
        await Verify(configurations.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task OlderV1_McpOnlyDoesNotFailTheCatalogCapabilityCheck()
    {
        using var context = new AgentConfigurationTestContext(output);
        var request = context.Request([AgentClientKind.OpenCode], skills: false, mcp: true,
            detections: [new(AgentClientKind.OpenCode, "1.18.30", false)]);

        var results = await context.ConfigureNativeAsync(request).DefaultTimeout();

        Assert.False(new AgentInitResult(results).HasErrors);
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(AgentAssetKind.Mcp, result.Asset);
            Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        });
    }
}
