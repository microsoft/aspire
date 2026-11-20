// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils.EnvironmentChecker;

namespace Aspire.Cli.Tests.Utils;

public class DeprecatedAgentConfigCheckTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData(".mcp.json", "mcpServers", """{"command":"aspire","args":["mcp","start","--apphost","app.cs"],"env":{"CUSTOM":"keep"}}""")]
    [InlineData(".mcp.json", "mcpServers", """{"command":"custom-aspire","args":["mcp","start"],"unknown":true}""")]
    [InlineData(".vscode/mcp.json", "servers", """{"command":"aspire","args":["mcp","start","--verbose"]}""")]
    [InlineData(".github/copilot/mcp.json", "servers", """{"command":"aspire","args":["mcp","start","--verbose"]}""")]
    [InlineData("opencode.jsonc", "mcp", """{"type":"local","command":["aspire","mcp","start","--apphost","app.cs"],"enabled":false}""")]
    public async Task CheckAsync_CustomizedDeprecatedCommand_RequiresManualMigration(
        string relativePath, string serversKey, string server)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, relativePath);
        var original = $$"""{ "preference": true, "{{serversKey}}": { "aspire": {{server}}, "other": { "command": "keep" } } }""";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, original);
        var config = JsonNode.Parse(original)!.AsObject();
        Assert.False(McpConfigFileHelper.UpdateDeprecatedCommand(config, serversKey));
        var check = new DeprecatedAgentConfigCheck(
            TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot));

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EnvironmentCheckStatus.Warning, result.Status);
        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.DeprecatedConfigManualFix, relativePath),
            result.Fix);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(".mcp.json", "mcpServers", """{"command":"aspire","args":["mcp","start"],"env":{"CUSTOM":"keep"}}""")]
    [InlineData("opencode.jsonc", "mcp", """{"type":"local","command":["aspire","mcp","start"],"enabled":false}""")]
    public async Task CheckAsync_ExactDeprecatedCommand_OffersWorkingAutomaticRepair(
        string relativePath, string serversKey, string server)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, relativePath);
        var original = $$"""{ "{{serversKey}}": { "aspire": {{server}} } }""";
        await File.WriteAllTextAsync(path, original);
        var check = new DeprecatedAgentConfigCheck(
            TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot));

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(AgentCommandStrings.DeprecatedConfigFix, result.Fix);
        var config = JsonNode.Parse(original)!.AsObject();
        Assert.True(McpConfigFileHelper.UpdateDeprecatedCommand(config, serversKey));
        await File.WriteAllTextAsync(path, config.ToJsonString());
        Assert.Empty(await check.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CheckAsync_CustomizedJsonc_ReportsManualMigrationWithoutWriting()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, "opencode.jsonc");
        const string original =
            """
            {
                // Keep this custom flag and the surrounding JSONC.
                "mcp": {
                    "aspire": { "command": ["aspire", "mcp", "start", "--verbose"], },
                    "other": { "url": "https://example.test/a//b" },
                },
                "preference": true,
            }
            """;
        await File.WriteAllTextAsync(path, original);
        var check = new DeprecatedAgentConfigCheck(
            TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot));

        var result = Assert.Single(await check.CheckAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.DeprecatedConfigManualFix, "opencode.jsonc"),
            result.Fix);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }
}
