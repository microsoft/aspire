// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Cli.Tests.Agents;

public class OpenCodeConfigurationTests(ITestOutputHelper output) : IDisposable
{
    private readonly AgentConfigurationTestContext _context = new(output);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpEdit_NewHigherPrecedenceOwner_BlocksTheOriginalTarget(bool duringPublication)
    {
        var json = Path.Combine(_context.Project.FullName, "opencode.json");
        var jsonc = Path.ChangeExtension(json, ".jsonc");
        const string original = """{"mcp":{"aspire":{"command":["aspire","mcp","start"]}}}""";
        const string replacement = """{"mcp":{"aspire":{"command":["pinned-aspire"]}}}""";
        await File.WriteAllTextAsync(json, original);
        var edit = Assert.Single(await _context.OpenCode.CreateMcpEditsAsync(
            _context.Project, AgentConfigurationScope.Project, CancellationToken.None));
        Assert.Equal(json, edit.TargetPath);
        var selectedEdit = edit;
        if (duringPublication)
        {
            selectedEdit = edit with
            {
                ApplyAsync = async (root, reader, token) =>
                {
                    var applied = await edit.ApplyAsync(root, reader, token);
                    root["concurrent"] = CallbackJsonConverter.CreateValue(() => File.WriteAllText(jsonc, replacement));

                    return applied;
                }
            };
        }
        else
        {
            await File.WriteAllTextAsync(jsonc, replacement);
        }

        var result = Assert.Single(await AgentConfigurationEditor.ApplyAsync([selectedEdit], CancellationToken.None));

        Assert.Equal(AgentConfigurationStatus.Blocked, result.Status);
        Assert.Equal(AgentCommandStrings.Configuration_ConcurrentChange, result.Message);
        Assert.Empty(result.Changes);
        Assert.Equal(original, await File.ReadAllTextAsync(json));
        Assert.Equal(replacement, await File.ReadAllTextAsync(jsonc));
    }

    [Fact]
    public async Task McpEdit_InheritanceInputChangedDuringPublication_BlocksTheRepair()
    {
        var json = Path.Combine(_context.Project.FullName, "opencode.json");
        var jsonc = Path.ChangeExtension(json, ".jsonc");
        const string original = """{"mcp":{"aspire":{"command":["aspire","mcp","start"]}}}""";
        const string partial = """{"mcp":{"aspire":{"enabled":true}}}""";
        const string replacement = """{"mcp":{"aspire":{"command":["pinned-aspire"]}}}""";
        await File.WriteAllTextAsync(json, original);
        await File.WriteAllTextAsync(jsonc, partial);
        var edit = Assert.Single(await _context.OpenCode.CreateMcpEditsAsync(
            _context.Project, AgentConfigurationScope.Project, CancellationToken.None));
        Assert.Equal(json, edit.TargetPath);
        var concurrentEdit = edit with
        {
            ApplyAsync = async (root, reader, token) =>
            {
                var applied = await edit.ApplyAsync(root, reader, token);
                root["concurrent"] = CallbackJsonConverter.CreateValue(() => File.WriteAllText(jsonc, replacement));

                return applied;
            }
        };

        var result = Assert.Single(await AgentConfigurationEditor.ApplyAsync([concurrentEdit], CancellationToken.None));

        Assert.Equal(AgentConfigurationStatus.Blocked, result.Status);
        Assert.Equal(AgentCommandStrings.Configuration_ConcurrentChange, result.Message);
        Assert.Empty(result.Changes);
        Assert.Equal(original, await File.ReadAllTextAsync(json));
        Assert.Equal(replacement, await File.ReadAllTextAsync(jsonc));
        Assert.Equal(["opencode.json", "opencode.jsonc"],
            _context.Project.EnumerateFiles().Select(file => file.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.None)]
    public async Task McpEdit_RemoteServer_DoesNotReadAnUnusedMalformedSibling(FileShare share)
    {
        var json = Path.Combine(_context.Project.FullName, "opencode.json");
        var jsonc = Path.ChangeExtension(json, ".jsonc");
        const string original = "{broken";
        const string remote = """{"mcp":{"aspire":{"type":"remote","url":"https://example.test/mcp"}}}""";
        await File.WriteAllTextAsync(json, original);
        await File.WriteAllTextAsync(jsonc, remote);
        await using (var unusedSibling = File.Open(json, FileMode.Open, FileAccess.Read, share))
        {
            var edit = Assert.Single(await _context.OpenCode.CreateMcpEditsAsync(
                _context.Project, AgentConfigurationScope.Project, CancellationToken.None));

            var result = Assert.Single(await AgentConfigurationEditor.ApplyAsync([edit], CancellationToken.None));

            Assert.Equal(AgentConfigurationStatus.Unchanged, result.Status);
            Assert.Equal(jsonc, result.TargetPath);
        }

        Assert.Equal(original, await File.ReadAllTextAsync(json));
        Assert.Equal(remote, await File.ReadAllTextAsync(jsonc));
    }

    [Theory]
    [InlineData("Project")]
    [InlineData("Global")]
    public async Task FreshUndetectedClient_RegistersOnlyTheStableV1Catalog(string scopeName)
    {
        var scope = Enum.Parse<AgentConfigurationScope>(scopeName);
        var results = await _context.ConfigureNativeAsync(scope, [_context.OpenCode]).DefaultTimeout();

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        await Verify(await File.ReadAllTextAsync(results[0].TargetPath), "json");
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

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.Equal(path, results[0].TargetPath);
        Assert.Equal(inherited, await File.ReadAllTextAsync(root));
        await Verify(await File.ReadAllTextAsync(path), "json");
    }

    [Fact]
    public async Task Registration_PreservesPinnedCatalogsInsteadOfAddingMain()
    {
        var path = Path.Combine(_context.OpenCodeDirectory, "opencode.json");
        const string existing = """{"skills":{"paths":["./private-skills"],"urls":["https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/v1/"]},"autoupdate":false}""";
        await AgentConfigurationTestContext.WriteAsync(path, existing);

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Global, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(results).Status);
        Assert.Equal(existing, await File.ReadAllTextAsync(path));
        var project = await _context.ConfigureNativeAsync(AgentConfigurationScope.Project, [_context.OpenCode]);
        await Verify(await File.ReadAllTextAsync(Assert.Single(project).TargetPath), "json");
    }

    [Theory]
    [InlineData("""{"skills":{"urls":[]},"mcp":{"servers":{}}}""")]
    [InlineData("""{"skills":["https://raw.githubusercontent.com/microsoft/aspire-skills/main/opencode/v1/"]}""")]
    [InlineData("""{"mcp":{"servers":{},"aspire":{"type":"local"}}}""")]
    [InlineData("""{"skills":null}""")]
    [InlineData("""{"skills":{"urls":[true]}}""")]
    [InlineData("""{"skills":[{}]}""")]
    [InlineData("""{"mcp":[]}""")]
    public async Task UnsupportedSchemaOrShape_IsBlockedWithoutConversion(string existing)
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing);
        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
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

        var results = await _context.ConfigureNativeAsync(scope, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        Assert.Equal(jsonc, results[0].TargetPath);
        Assert.Equal(pinned, await File.ReadAllTextAsync(json));
        await Verify(await File.ReadAllTextAsync(jsonc), "json");
    }

    [Theory]
    [InlineData("""{"skills":[]}""")]
    [InlineData("{broken")]
    [InlineData("""{"skills":[true]}""")]
    public async Task InlineOverride_IsNotOverwrittenOrMistakenForPersistentActivation(string inline)
    {
        _context.SetVariable("OPENCODE_CONFIG_CONTENT", inline);

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Blocked, Assert.Single(results).Status);
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task ConfigDirectoryOverrideThatAliasesDefault_IsDeduplicatedAndIdempotent()
    {
        _context.SetVariable("OPENCODE_CONFIG_DIR", _context.OpenCodeDirectory);
        var first = await _context.ConfigureNativeAsync(AgentConfigurationScope.Global, [_context.OpenCode]);
        var path = Assert.Single(first).TargetPath;
        var bytes = await File.ReadAllBytesAsync(path);
        var second = await _context.ConfigureNativeAsync(AgentConfigurationScope.Global, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, first[0].Status);
        Assert.Equal(AgentConfigurationStatus.Unchanged, Assert.Single(second).Status);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(_context.Project.EnumerateFileSystemInfos());
    }

    [Fact]
    public async Task ConfigDirectoryOverride_IsTheOnlyWritableGlobalDestination()
    {
        var customDirectory = Path.Combine(_context.Home.FullName, "custom-opencode");
        _context.SetVariable("OPENCODE_CONFIG_DIR", customDirectory);
        var inherited = Path.Combine(_context.OpenCodeDirectory, "opencode.json");
        const string pinned = """{"skills":{"urls":["https://raw.githubusercontent.com/microsoft/aspire-skills/v0.0.3/opencode/v1/"]}}""";
        await AgentConfigurationTestContext.WriteAsync(inherited, pinned);

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Global, [_context.OpenCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(Path.Combine(customDirectory, "opencode.jsonc"), result.TargetPath);
        Assert.Equal(pinned, await File.ReadAllTextAsync(inherited));
        Assert.Equal(new[] { inherited, result.TargetPath }.Order(),
            Directory.EnumerateFiles(_context.Home.FullName, "*", SearchOption.AllDirectories).Order());
        await Verify(await File.ReadAllTextAsync(result.TargetPath), "json");
    }

    [Fact]
    public async Task RelativeGlobalOverride_PreservesTheMcpDestinationSemantics()
    {
        _context.SetVariable("OPENCODE_CONFIG_DIR", Path.Combine("~", "custom-opencode"));
        var expected = Path.Combine(_context.Project.FullName, "~", "custom-opencode", "opencode.jsonc");
        var mcpPath = _context.OpenCode.GetMcpConfigPath(AgentConfigurationScope.Global, _context.Project);

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Global, [_context.OpenCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(expected, result.TargetPath);
        Assert.Equal(mcpPath, result.TargetPath);
        Assert.Equal([expected], Directory.EnumerateFiles(_context.Project.FullName, "*", SearchOption.AllDirectories));
        Assert.Empty(_context.Home.EnumerateFileSystemInfos());
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

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Global, [_context.OpenCode]);

        var result = Assert.Single(results);
        Assert.Equal(AgentConfigurationStatus.Configured, result.Status);
        Assert.Equal(selected, result.TargetPath);
        Assert.Equal(original, await File.ReadAllTextAsync(other));
        Assert.Equal(original, await File.ReadAllTextAsync(normal));
        Assert.Equal(new[] { other, normal, selected }.Order(),
            Directory.EnumerateFiles(_context.Home.FullName, "*", SearchOption.AllDirectories).Order());
        await Verify(await File.ReadAllTextAsync(selected), "json");
    }

    [Theory]
    [InlineData("server-flags", """{"mcp":{"servers":{"custom":{"enabled":false,"command":["custom"]},"external":"managed"}}}""")]
    [InlineData("external-container", """{"mcp":{"servers":[]}}""")]
    public async Task CatalogRegistration_DoesNotValidateUnrelatedMcpDefinitions(string name, string existing)
    {
        var path = Path.Combine(_context.Project.FullName, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(path, existing);

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Project, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        await Verify(await File.ReadAllTextAsync(path), "json").UseParameters(name);
    }

    [Fact]
    public async Task HigherPrecedenceFileAppearingAfterTargetSelection_PreventsWrites()
    {
        var json = Path.Combine(_context.Project.FullName, "opencode.json");
        var jsonc = Path.Combine(_context.Project.FullName, "opencode.jsonc");
        await AgentConfigurationTestContext.WriteAsync(json, "{}");
        var reader = new AgentConfigurationJson.ReadContext();
        await reader.ReadAsync(json, CancellationToken.None);
        await reader.ReadOptionalAsync(jsonc, CancellationToken.None);
        const string replacement = """{"skills":["./private-skills"]}""";
        await AgentConfigurationTestContext.WriteAsync(jsonc, replacement);

        var error = await Assert.ThrowsAsync<AgentConfigurationException>(() => reader.ValidateAsync(CancellationToken.None));
        Assert.Equal(Aspire.Cli.Resources.AgentCommandStrings.Configuration_ConcurrentChange, error.Message);
        Assert.Equal("{}", await File.ReadAllTextAsync(json));
        Assert.Equal(replacement, await File.ReadAllTextAsync(jsonc));
    }

    [Fact]
    public async Task CatalogRegistration_DoesNotRewriteUnrelatedMcpTimeouts()
    {
        var path = Path.Combine(_context.OpenCodeDirectory, "opencode.json");
        await AgentConfigurationTestContext.WriteAsync(path, """{"mcp":{"servers":{},"timeout":{"startup":0}}}""");

        var results = await _context.ConfigureNativeAsync(AgentConfigurationScope.Global, [_context.OpenCode]);

        Assert.Equal(AgentConfigurationStatus.Configured, Assert.Single(results).Status);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.Equal(0, (int)root["mcp"]!["timeout"]!["startup"]!);
        await Verify(await File.ReadAllTextAsync(path), "json");
    }

    public void Dispose() => _context.Dispose();
}
