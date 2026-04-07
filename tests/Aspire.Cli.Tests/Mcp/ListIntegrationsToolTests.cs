// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using System.Text.Json;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;

namespace Aspire.Cli.Tests.Mcp;

public class ListIntegrationsToolTests
{
    [Fact]
    public void ListIntegrationsTool_HasCorrectName()
    {
        var tool = new ListIntegrationsTool(new MockPackagingService(), TestExecutionContextFactory.CreateTestContext(), new MockAuxiliaryBackchannelMonitor());

        Assert.Equal("list_integrations", tool.Name);
    }

    [Fact]
    public void ListIntegrationsTool_HasCorrectDescription()
    {
        var tool = new ListIntegrationsTool(new MockPackagingService(), TestExecutionContextFactory.CreateTestContext(), new MockAuxiliaryBackchannelMonitor());

        Assert.Contains("List available Aspire hosting integrations", tool.Description);
        Assert.Contains("This tool does not require a running AppHost", tool.Description);
    }

    [Fact]
    public void ListIntegrationsTool_GetInputSchema_ReturnsValidSchema()
    {
        var tool = new ListIntegrationsTool(new MockPackagingService(), TestExecutionContextFactory.CreateTestContext(), new MockAuxiliaryBackchannelMonitor());
        var schema = tool.GetInputSchema();

        Assert.Equal(JsonValueKind.Object, schema.ValueKind);
        Assert.True(schema.TryGetProperty("type", out var typeElement));
        Assert.Equal("object", typeElement.GetString());
        Assert.True(schema.TryGetProperty("properties", out var propsElement));
        Assert.Equal(JsonValueKind.Object, propsElement.ValueKind);
        Assert.True(schema.TryGetProperty("description", out var descElement));
        Assert.Contains("This tool takes no input parameters", descElement.GetString());
        Assert.True(schema.TryGetProperty("additionalProperties", out var additionalPropsElement));
        Assert.False(additionalPropsElement.GetBoolean());
    }

    [Fact]
    public async Task ListIntegrationsTool_CallToolAsync_ReturnsEmptyJsonArray_WhenNoPackagesFound()
    {
        var mockPackagingService = new MockPackagingService();
        var tool = new ListIntegrationsTool(mockPackagingService, TestExecutionContextFactory.CreateTestContext(), new MockAuxiliaryBackchannelMonitor());

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        // Verify it's valid JSON with empty integrations array
        using var json = JsonDocument.Parse(textContent.Text);
        Assert.True(json.RootElement.TryGetProperty("integrations", out var integrations));
        Assert.Equal(JsonValueKind.Array, integrations.ValueKind);
        Assert.Equal(0, integrations.GetArrayLength());
    }

    [Fact]
    public async Task ListIntegrationsTool_CallToolAsync_ReturnsJsonWithPackages_WhenPackagesFound()
    {
        var mockPackagingService = new MockPackagingService(new[]
        {
            new Aspire.Shared.NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.0.0" },
            new Aspire.Shared.NuGetPackageCli { Id = "Aspire.Hosting.PostgreSQL", Version = "9.0.0" }
        });
        var tool = new ListIntegrationsTool(mockPackagingService, TestExecutionContextFactory.CreateTestContext(), new MockAuxiliaryBackchannelMonitor());

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        // Verify it's valid JSON with proper structure
        using var json = JsonDocument.Parse(textContent.Text);
        Assert.True(json.RootElement.TryGetProperty("integrations", out var integrations));
        Assert.Equal(JsonValueKind.Array, integrations.ValueKind);
        Assert.Equal(2, integrations.GetArrayLength());

        // Verify the first integration has the expected properties
        var firstIntegration = integrations[0];
        Assert.True(firstIntegration.TryGetProperty("name", out _));
        Assert.True(firstIntegration.TryGetProperty("packageId", out _));
        Assert.True(firstIntegration.TryGetProperty("version", out _));

        // Check that the packages are included (order may vary)
        var packageIds = integrations.EnumerateArray()
            .Select(e => e.GetProperty("packageId").GetString())
            .ToList();
        Assert.Contains("Aspire.Hosting.Redis", packageIds);
        Assert.Contains("Aspire.Hosting.PostgreSQL", packageIds);
    }

    [Fact]
    public async Task ListIntegrationsTool_UsesDefaultChannelOnly()
    {
        // This test verifies that only the default (first) channel is used
        var mockPackagingService = new MockPackagingService(new[]
        {
            new Aspire.Shared.NuGetPackageCli { Id = "Aspire.Hosting.Redis", Version = "9.0.0" }
        });
        var tool = new ListIntegrationsTool(mockPackagingService, TestExecutionContextFactory.CreateTestContext(), new MockAuxiliaryBackchannelMonitor());

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);

        // Verify the result contains packages (confirming the default channel was used)
        using var json = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content![0]).Text);
        Assert.True(json.RootElement.TryGetProperty("integrations", out var integrations));
        Assert.Equal(1, integrations.GetArrayLength());
    }
}

