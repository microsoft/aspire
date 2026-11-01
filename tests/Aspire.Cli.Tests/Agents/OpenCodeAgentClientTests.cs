// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class OpenCodeAgentClientTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Fact]
    public async Task CatalogDiscovery_UsesActiveDirectoriesWithoutChangingTheSelectedWriteTarget()
    {
        var workingDirectory = _context.Project.CreateSubdirectory(Path.Combine("apps", "api"));
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workingDirectory, homeDirectory: _context.Home);
        var client = new OpenCodeAgentClient(executionContext, _context.Environment);
        var nestedConfig = Path.Combine(workingDirectory.FullName, "opencode.json");
        const string existing = """{"skills":{"urls":["https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/v1/"]}}""";
        await AgentConfigurationTestContext.WriteAsync(nestedConfig, existing);
        var target = client.GetIntegrationConfigPath(AgentConfigurationScope.Project, _context.Project);

        Assert.True(client.HasIntegrationRegistration(_context.Project, AgentConfigurationScope.Project, TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(_context.Project.FullName, "opencode.jsonc"), target);
        Assert.False(File.Exists(target));
        Assert.Equal(existing, await File.ReadAllTextAsync(nestedConfig));
        var configured = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [client]);
        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(configured).Status);
        Assert.Equal(target, configured[0].TargetPath);
        var config = JsonNode.Parse(await File.ReadAllTextAsync(target))!;
        Assert.Equal(["https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/v1/"],
            config["skills"]!["urls"]!.AsArray().Select(value => (string)value!));
        Assert.Equal(existing, await File.ReadAllTextAsync(nestedConfig));
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpEdit_ProjectConfigDirectoryRepairsTheEffectiveOwner(bool nativeHigher)
    {
        var lowerPath = Path.Combine(_context.Project.FullName, "opencode.json");
        var ownerPath = Path.Combine(_context.Project.FullName, ".opencode", "opencode.json");
        var higherPath = Path.ChangeExtension(ownerPath, ".jsonc");
        const string legacy = """{"mcp":{"aspire":{"type":"local","command":["aspire","mcp","start"],"enabled":false}}}""";
        const string native = """{"mcp":{"servers":{"aspire":{"type":"local","command":["aspire","mcp","start"],"disabled":true}}}}""";
        var lower = nativeHigher ? legacy : native;
        const string higher = """{"model":"preserved"}""";
        await AgentConfigurationTestContext.WriteAsync(lowerPath, lower);
        await AgentConfigurationTestContext.WriteAsync(ownerPath, nativeHigher ? native : legacy);
        await AgentConfigurationTestContext.WriteAsync(higherPath, higher);

        var result = await _context.ConfigureMcpAsync(_context.OpenCode, AgentConfigurationScope.Project);

        Assert.Equal(ownerPath, result.Path);
        Assert.True(result.Repaired);
        Assert.False(result.Added);
        Assert.Equal(lower, await File.ReadAllTextAsync(lowerPath));
        Assert.Equal(higher, await File.ReadAllTextAsync(higherPath));
        await Verify(await File.ReadAllTextAsync(ownerPath), "json")
            .UseFileName($"{nameof(OpenCodeAgentClientTests)}.{nameof(McpEdit_DocumentPrecedenceOutranksServerRepresentation)}_nativeHigher={nativeHigher}")
            .DisableRequireUniquePrefix();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpEdit_RemoteServer_DoesNotReadAnUnusedMalformedSibling(bool nativeServer)
    {
        var json = Path.Combine(_context.Project.FullName, "opencode.json");
        var jsonc = Path.ChangeExtension(json, ".jsonc");
        const string original = "{broken";
        var remote = nativeServer
            ? """{"mcp":{"servers":{"aspire":{"type":"remote","url":"https://example.test/mcp"}}}}"""
            : """{"mcp":{"aspire":{"type":"remote","url":"https://example.test/mcp"}}}""";
        await File.WriteAllTextAsync(json, original);
        await File.WriteAllTextAsync(jsonc, remote);
        await using (var unusedSibling = File.Open(json, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await _context.ConfigureMcpAsync(_context.OpenCode, AgentConfigurationScope.Project);

            Assert.False(result.Added);
            Assert.False(result.Repaired);
            Assert.Equal(jsonc, result.Path);
        }

        Assert.Equal(original, await File.ReadAllTextAsync(json));
        Assert.Equal(remote, await File.ReadAllTextAsync(jsonc));
    }

    [Theory]
    [InlineData("Project", """{"type":"local","command":["pinned-aspire"],"disabled":true}""")]
    [InlineData("Global", """{"type":"local","command":["pinned-aspire"],"disabled":false}""")]
    [InlineData("Project", """{"type":"remote","url":"https://example.test/mcp","disabled":false}""")]
    [InlineData("Global", """{"type":"remote","url":"https://example.test/mcp","disabled":true}""")]
    public async Task McpEdit_NativeAspireEntry_PreservesPinsDisabledStateAndOwningDocument(string scopeName, string server)
    {
        var scope = Enum.Parse<AgentConfigurationScope>(scopeName);
        var directory = scope is AgentConfigurationScope.Project ? _context.Project.FullName : _context.OpenCodeDirectory;
        var json = Path.Combine(directory, "opencode.json");
        var jsonc = Path.ChangeExtension(json, ".jsonc");
        var original = $$"""{ "mcp": { "aspire": { "command": ["aspire", "mcp", "start"] }, "servers": { "aspire": {{server}} } } }""";
        const string unrelated = """{"model":"preserved"}""";
        await AgentConfigurationTestContext.WriteAsync(json, original);
        await AgentConfigurationTestContext.WriteAsync(jsonc, unrelated);
        var result = await _context.ConfigureMcpAsync(_context.OpenCode, scope);

        Assert.Equal(json, result.Path);
        Assert.False(result.Added);
        Assert.False(result.Repaired);
        Assert.Equal(original, await File.ReadAllTextAsync(json));
        Assert.Equal(unrelated, await File.ReadAllTextAsync(jsonc));
        Assert.Empty((scope is AgentConfigurationScope.Project ? _context.Home : _context.Project).EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpEdit_DocumentPrecedenceOutranksServerRepresentation(bool nativeHigher)
    {
        var json = Path.Combine(_context.Project.FullName, "opencode.json");
        var jsonc = Path.ChangeExtension(json, ".jsonc");
        const string legacy = """{"mcp":{"aspire":{"type":"local","command":["aspire","mcp","start"],"enabled":false}}}""";
        const string native = """{"mcp":{"servers":{"aspire":{"type":"local","command":["aspire","mcp","start"],"disabled":true}}}}""";
        var original = nativeHigher ? legacy : native;
        await File.WriteAllTextAsync(json, original);
        await File.WriteAllTextAsync(jsonc, nativeHigher ? native : legacy);
        var result = await _context.ConfigureMcpAsync(_context.OpenCode, AgentConfigurationScope.Project);

        Assert.Equal(jsonc, result.Path);
        Assert.True(result.Repaired);
        Assert.False(result.Added);
        Assert.Equal(original, await File.ReadAllTextAsync(json));
        await Verify(await File.ReadAllTextAsync(jsonc), "json").UseParameters(nativeHigher);
    }

    [Fact]
    public async Task McpEdit_NewNativeServer_UsesExistingContainerWithoutInterpretingSkills()
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        await File.WriteAllTextAsync(path,
            """{"skills":[true],"mcp":{"servers":{"custom":{"type":"remote","url":"https://example.test/mcp"}},"timeout":{"catalog":5000}}}""");
        var result = await _context.ConfigureMcpAsync(_context.OpenCode, AgentConfigurationScope.Project);

        Assert.True(result.Added);
        await Verify(await File.ReadAllTextAsync(path), "json");
    }

    [Fact]
    public async Task McpEdit_LegacyServerNamedServers_IsNotTreatedAsANativeContainer()
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        await File.WriteAllTextAsync(path, """{"mcp":{"servers":{"type":"local","command":["custom-mcp"]}}}""");
        var result = await _context.ConfigureMcpAsync(_context.OpenCode, AgentConfigurationScope.Project);

        Assert.True(result.Added);
        await Verify(await File.ReadAllTextAsync(path), "json");
    }

    [Theory]
    [InlineData("Project")]
    [InlineData("Global")]
    public async Task FreshUndetectedClient_RegistersOnlyTheStableV1Catalog(string scopeName)
    {
        var scope = Enum.Parse<AgentConfigurationScope>(scopeName);
        if (scope is AgentConfigurationScope.Global)
        {
            _context.SetVariable("OPENCODE_CONFIG_DIR", _context.OpenCodeDirectory);
        }
        var results = await _context.ConfigureIntegrationAsync(scope, [_context.OpenCode]).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        var content = await File.ReadAllTextAsync(results[0].TargetPath);
        var repeated = await _context.ConfigureIntegrationAsync(scope, [_context.OpenCode]);
        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(repeated).Status);
        Assert.Equal(content, await File.ReadAllTextAsync(results[0].TargetPath));
        await Verify(content, "json")
            .UseFileName($"{nameof(OpenCodeAgentClientTests)}.{nameof(FreshUndetectedClient_RegistersOnlyTheStableV1Catalog)}")
            .DisableRequireUniquePrefix();
        Assert.Empty((scope is AgentConfigurationScope.Project ? _context.Home : _context.Project).EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task ExistingV2Jsonc_EstablishesTheSchemaWithoutVersionDetection()
    {
        var root = Path.Combine(_context.Project.FullName, "opencode.jsonc");
        const string inherited = """{"model":"root-preserved"}""";
        await AgentConfigurationTestContext.WriteAsync(root, inherited);
        var path = Path.Combine(_context.Project.FullName, ".opencode", "opencode.jsonc");
        await AgentConfigurationTestContext.WriteAsync(path, """{"model":"preserved","skills":["./team-skills"],}""");

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.Equal(path, results[0].TargetPath);
        Assert.Equal(inherited, await File.ReadAllTextAsync(root));
        await Verify(await File.ReadAllTextAsync(path), "json");
    }

    [Theory]
    [InlineData(false, """{"servers":{"type":"local","command":["custom-mcp"]}}""")]
    [InlineData(true, """{"servers":{}}""")]
    [InlineData(true, """{"servers":{},"aspire":{"type":"local","command":["custom-mcp"]}}""")]
    [InlineData(false, "[]")]
    [InlineData(true, """{"servers":[]}""")]
    [InlineData(false, """{"servers":{},"timeout":{"startup":0}}""")]
    [InlineData(true, """{"servers":{"custom":{"enabled":false,"command":["custom"]},"external":"managed"}}""")]
    public async Task CatalogRegistration_PreservesMcpWithoutInferringSkillsSyntax(bool existingSkills, string mcp)
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        var original = new JsonObject { ["mcp"] = JsonNode.Parse(mcp) };
        if (existingSkills)
        {
            original["skills"] = JsonNode.Parse("""{"paths":["./team-skills"],"urls":[]}""");
        }
        await AgentConfigurationTestContext.WriteAsync(path, original.ToJsonString());

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        var configured = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var skills = Assert.IsType<JsonObject>(configured["skills"]);
        Assert.Equal(["https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/"],
            skills["urls"]!.AsArray().Select(value => (string)value!));
        Assert.True(JsonNode.DeepEquals(original["mcp"], configured["mcp"]));
        Assert.True(JsonNode.DeepEquals(original["skills"]?["paths"], skills["paths"]));
    }

    [Theory]
    [InlineData(false, "v1")]
    [InlineData(false, "v2")]
    [InlineData(true, "v1")]
    [InlineData(true, "v2")]
    public async Task CatalogRegistration_PreservesExistingPinsAcrossSkillsRepresentations(bool array, string catalog)
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        var urls = new JsonArray("https://example.test/skills",
            $"https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/{catalog}/",
            "https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/");
        var original = new JsonObject
        {
            ["autoupdate"] = false,
            ["skills"] = array ? urls : new JsonObject { ["paths"] = new JsonArray("./team-skills"), ["urls"] = urls }
        }.ToJsonString();
        var inherited = array ? """{"skills":{"urls":[]}}""" : """{"skills":[]}""";
        await AgentConfigurationTestContext.WriteAsync(Path.Combine(_context.OpenCodeDirectory, "opencode.json"), inherited);
        await File.WriteAllTextAsync(path, original);

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(results).Status);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Equal(inherited, await File.ReadAllTextAsync(Path.Combine(_context.OpenCodeDirectory, "opencode.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatalogRegistration_InheritsPinsWithoutConvertingDestinationSkills(bool array)
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        var global = Path.Combine(_context.OpenCodeDirectory, "opencode.json");
        var pin = $"https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/{(array ? "v1" : "v2")}/";
        var inherited = new JsonObject
        {
            ["skills"] = array ? new JsonObject { ["urls"] = new JsonArray(pin) } : new JsonArray(pin)
        }.ToJsonString();
        await AgentConfigurationTestContext.WriteAsync(global, inherited);
        await File.WriteAllTextAsync(path, array ? """{"skills":["./team-skills"]}""" : """{"skills":{"urls":["./team-skills"]}}""");

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        var skills = JsonNode.Parse(await File.ReadAllTextAsync(path))!["skills"]!;
        var sources = array ? Assert.IsType<JsonArray>(skills) : Assert.IsType<JsonObject>(skills)["urls"]!.AsArray();
        Assert.Equal(["./team-skills", pin], sources.Select(value => (string)value!));
        Assert.Equal(inherited, await File.ReadAllTextAsync(global));
    }

    [Theory]
    [InlineData("""{"skills":null}""")]
    [InlineData("""{"skills":{"urls":[true]}}""")]
    [InlineData("""{"skills":{"paths":[true]}}""")]
    [InlineData("""{"skills":[{}]}""")]
    public async Task MalformedSkillsShape_FailsWithoutConversion(string existing)
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing);
        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(path));
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData("Project")]
    [InlineData("Global")]
    public async Task JsoncSelection_PreservesInheritedCatalogPinsAndUnrelatedMcp(string scopeName)
    {
        var scope = Enum.Parse<AgentConfigurationScope>(scopeName);
        var directory = scope is AgentConfigurationScope.Project ? _context.Project.FullName : _context.OpenCodeDirectory;
        var json = Path.Combine(directory, "opencode.json");
        var jsonc = Path.Combine(directory, "opencode.jsonc");
        const string pinned = """{"skills":{"urls":["https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/v1/"]},"mcp":{"aspire":{"enabled":false,"command":["custom-aspire","agent","mcp"]}}}""";
        await AgentConfigurationTestContext.WriteAsync(json, pinned);
        await AgentConfigurationTestContext.WriteAsync(jsonc, """{"model":"preserved"}""");

        var results = await _context.ConfigureIntegrationAsync(scope, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.Equal(jsonc, results[0].TargetPath);
        Assert.Equal(pinned, await File.ReadAllTextAsync(json));
        await Verify(await File.ReadAllTextAsync(jsonc), "json")
            .UseFileName($"{nameof(OpenCodeAgentClientTests)}.{nameof(JsoncSelection_PreservesInheritedCatalogPinsAndUnrelatedMcp)}")
            .DisableRequireUniquePrefix();
    }

    [Theory]
    [InlineData("""{"skills":[]}""")]
    [InlineData("{broken")]
    [InlineData("""{"skills":[true]}""")]
    public async Task InlineOverride_IsNotOverwrittenOrMistakenForPersistentActivation(string inline)
    {
        _context.SetVariable("OPENCODE_CONFIG_CONTENT", inline);

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Failed, Assert.Single(results).Status);
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigDirectoryOverride_IsTheOnlyWritableGlobalDestination(bool relative)
    {
        var customDirectory = relative
            ? Path.Combine(_context.Project.FullName, "~", "custom-opencode")
            : Path.Combine(_context.Home.FullName, "custom-opencode");
        _context.SetVariable("OPENCODE_CONFIG_DIR", relative ? Path.Combine("~", "custom-opencode") : customDirectory);
        var inherited = Path.Combine(_context.OpenCodeDirectory, "opencode.json");
        const string pinned = """{"skills":{"urls":["https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/v1/"]}}""";
        await AgentConfigurationTestContext.WriteAsync(inherited, pinned);

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Global, [_context.OpenCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(Path.Combine(customDirectory, "opencode.jsonc"), result.TargetPath);
        Assert.Equal(_context.OpenCode.GetMcpConfigPath(AgentConfigurationScope.Global, _context.Project), result.TargetPath);
        Assert.Equal(pinned, await File.ReadAllTextAsync(inherited));
        Assert.Equal(new[] { inherited, result.TargetPath }.Order(),
            Directory.EnumerateFiles(_context.Home.FullName, "*", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(_context.Project.FullName, "*", SearchOption.AllDirectories)).Order());
        var selectedSkills = JsonNode.Parse(await File.ReadAllTextAsync(result.TargetPath))!["skills"];
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(pinned)!["skills"], selectedSkills));
    }

    [Fact]
    public async Task ExplicitConfigFile_IsTheOnlyWritableDestinationWhenDirectoryOverrideAlsoExists()
    {
        var selected = Path.Combine(_context.Home.FullName, "selected", "custom.jsonc");
        var customDirectory = Path.Combine(_context.Home.FullName, "custom-opencode");
        _context.SetVariable("OPENCODE_CONFIG", selected);
        _context.SetVariable("OPENCODE_CONFIG_DIR", customDirectory);
        var other = Path.Combine(customDirectory, "opencode.jsonc");
        var normal = Path.Combine(_context.OpenCodeDirectory, "opencode.json");
        const string original = """{"model":"preserved"}""";
        await AgentConfigurationTestContext.WriteAsync(other, original);
        await AgentConfigurationTestContext.WriteAsync(normal, original);

        var results = await _context.ConfigureIntegrationAsync(AgentConfigurationScope.Global, [_context.OpenCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(selected, result.TargetPath);
        Assert.Equal(original, await File.ReadAllTextAsync(other));
        Assert.Equal(original, await File.ReadAllTextAsync(normal));
        Assert.Equal(new[] { other, normal, selected }.Order(),
            Directory.EnumerateFiles(_context.Home.FullName, "*", SearchOption.AllDirectories).Order());
        Assert.True(_context.OpenCode.HasIntegrationRegistration(_context.Project, AgentConfigurationScope.Project, TestContext.Current.CancellationToken));
    }

    public void Dispose() => _context.Dispose();
}
