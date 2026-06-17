// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Mcp;

public class ListResourcesToolTests
{
    [Fact]
    public async Task ListResourcesTool_ThrowsException_WhenNoAppHostRunning()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("No Aspire AppHost", exception.Message);
        Assert.Contains("aspire start", exception.Message);
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsNoResourcesFound_WhenSnapshotsAreEmpty()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots = [],
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "http://localhost:18888" }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        Assert.NotNull(result.Content);
        Assert.Single(result.Content);
        var textContent = result.Content[0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);
        Assert.Contains("No resources found", textContent.Text);
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsMultipleResources()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "api-service",
                    DisplayName = "API Service",
                    ResourceType = "Project",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "postgres",
                    DisplayName = "PostgreSQL",
                    ResourceType = "Container",
                    State = "Starting"
                }
            ],
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "http://localhost:18888" }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        Assert.Contains("api-service", textContent.Text);
        Assert.Contains("redis", textContent.Text);
        Assert.Contains("postgres", textContent.Text);
    }

    [Fact]
    public async Task ListResourcesTool_IncludesEnvironmentVariableNamesButNotValues()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "api-service",
                    ResourceType = "Project",
                    State = "Running",
                    EnvironmentVariables =
                    [
                        new ResourceSnapshotEnvironmentVariable { Name = "ASPNETCORE_ENVIRONMENT", Value = "Development", IsFromSpec = true },
                        new ResourceSnapshotEnvironmentVariable { Name = "ConnectionStrings__Database", Value = "SuperSecretPassword123", IsFromSpec = true }
                    ]
                }
            ],
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "http://localhost:18888" }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        // Environment variable names should be included
        Assert.Contains("ASPNETCORE_ENVIRONMENT", textContent.Text);
        Assert.Contains("ConnectionStrings__Database", textContent.Text);

        // Environment variable values should NOT be included (to protect sensitive information)
        Assert.DoesNotContain("Development", textContent.Text);
        Assert.DoesNotContain("SuperSecretPassword123", textContent.Text);
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsValidJson()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = new TestAppHostAuxiliaryBackchannel
        {
            ResourceSnapshots =
            [
                new ResourceSnapshot
                {
                    Name = "api-service",
                    DisplayName = "API Service",
                    ResourceType = "Project",
                    State = "Running",
                    StateStyle = "success",
                    HealthStatus = "Healthy",
                    Urls =
                    [
                        new ResourceSnapshotUrl { Name = "http", Url = "http://localhost:5000" }
                    ]
                }
            ],
            DashboardUrlsState = new DashboardUrlsState { BaseUrlWithLoginToken = "http://localhost:18888" }
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        var textContent = result.Content![0] as ModelContextProtocol.Protocol.TextContentBlock;
        Assert.NotNull(textContent);

        // Extract JSON portion from the response (after "# RESOURCE DATA")
        var jsonStartIndex = textContent.Text.IndexOf('[');
        var jsonEndIndex = textContent.Text.LastIndexOf(']') + 1;
        Assert.True(jsonStartIndex >= 0 && jsonEndIndex > jsonStartIndex, "Response should contain JSON array");

        var jsonPortion = textContent.Text.Substring(jsonStartIndex, jsonEndIndex - jsonStartIndex);

        // Verify it's valid JSON
        var jsonDoc = JsonDocument.Parse(jsonPortion);
        Assert.Equal(JsonValueKind.Array, jsonDoc.RootElement.ValueKind);
        Assert.Equal(1, jsonDoc.RootElement.GetArrayLength());

        var resource = jsonDoc.RootElement[0];
        Assert.Equal("api-service", resource.GetProperty("name").GetString());
        Assert.Equal("API Service", resource.GetProperty("display_name").GetString());
        Assert.Equal("Project", resource.GetProperty("resource_type").GetString());
        Assert.Equal("Running", resource.GetProperty("state").GetString());
    }
}

