// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class ListResourcesToolTests(ITestOutputHelper outputHelper)
{
    private const string AppHostPath = "/repo/TestAppHost/TestAppHost.csproj";

    [Fact]
    public async Task ListResourcesTool_ThrowsException_WhenNoAppHostRunning()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Contains("No Aspire AppHost", exception.Message);
        Assert.Contains("aspire start", exception.Message);
    }

    [Fact]
    public async Task ListResourcesTool_ReportsOutOfScopeAppHostsWithoutExposingTheirIdentity()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection("/other/Private/Private.AppHost.csproj");
        connection.IsInScope = false;
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(
            "Running Aspire AppHosts were found outside the MCP server's working directory scope. " +
            "Use 'list_apphosts' to discover available AppHosts, then 'select_apphost' to choose one.",
            exception.Message);
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsExplicitEmptyResult_WhenSnapshotsAreEmpty()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection());

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        Assert.True(result.IsError is null or false);
        using var json = GetResourceData(result);

        Assert.Equal(["resources"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.False(json.RootElement.TryGetProperty("app_host_path", out _));
        Assert.Empty(json.RootElement.GetProperty("resources").EnumerateArray());
    }

    [Fact]
    public async Task ListResourcesTool_SerializesEmptyCollectionsAsArrays()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(new ResourceSnapshot
            {
                Name = "api-service",
                ResourceType = "Project",
                State = "Running"
            }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resource = json.RootElement.GetProperty("resources")[0];
        Assert.Equal("[]", resource.GetProperty("waiting_for").GetRawText());
        Assert.Equal("[]", resource.GetProperty("urls").GetRawText());
        Assert.Equal("[]", resource.GetProperty("relationships").GetRawText());
    }

    [Fact]
    public async Task ListResourcesTool_FansOutAndDeduplicatesRelationshipsToReplicaRuntimeNames()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(
                new ResourceSnapshot
                {
                    Name = "api",
                    DisplayName = "API",
                    ResourceType = "Project",
                    State = "Running",
                    Relationships =
                    [
                        new ResourceSnapshotRelationship
                        {
                            ResourceName = "Redis",
                            Type = "Reference"
                        },
                        new ResourceSnapshotRelationship
                        {
                            ResourceName = "Redis",
                            Type = "Reference"
                        }
                    ]
                },
                new ResourceSnapshot
                {
                    Name = "redis-instance-1",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis-instance-2",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis-instance-3",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var relationships = json.RootElement
            .GetProperty("resources")[0]
            .GetProperty("relationships")
            .EnumerateArray()
            .ToArray();
        Assert.Equal(3, relationships.Length);
        Assert.All(
            relationships,
            relationship => Assert.Equal("Reference", relationship.GetProperty("type").GetString()));
        Assert.Equal(
            ["redis-instance-1", "redis-instance-2", "redis-instance-3"],
            relationships.Select(relationship => relationship.GetProperty("resource_name").GetString()));
    }

    [Fact]
    public async Task ListResourcesTool_RelationshipFanOutExcludesHiddenDuplicateRuntimeName()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(
                new ResourceSnapshot
                {
                    Name = "api",
                    DisplayName = "API",
                    ResourceType = "Project",
                    State = "Running",
                    Relationships =
                    [
                        new ResourceSnapshotRelationship
                        {
                            ResourceName = "Redis",
                            Type = "Reference"
                        }
                    ]
                },
                new ResourceSnapshot
                {
                    Name = "redis-visible",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis-hidden",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Hidden"
                }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.Equal(
            ["redis-visible"],
            json.RootElement.GetProperty("resources")[0]
                .GetProperty("relationships")
                .EnumerateArray()
                .Select(relationship => relationship.GetProperty("resource_name").GetString()));
        Assert.Equal(
            ["api", "redis-visible"],
            json.RootElement.GetProperty("resources")
                .EnumerateArray()
                .Select(resource => resource.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ListResourcesTool_WaitingForPreservesHiddenDuplicateIdentity()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(
                new ResourceSnapshot
                {
                    Name = "api",
                    DisplayName = "API",
                    ResourceType = "Project",
                    State = "Running",
                    WaitingFor = ["redis-visible", "redis-hidden"]
                },
                new ResourceSnapshot
                {
                    Name = "redis-visible",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Running"
                },
                new ResourceSnapshot
                {
                    Name = "redis-hidden",
                    DisplayName = "Redis",
                    ResourceType = "Container",
                    State = "Hidden"
                }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.Equal(
            ["redis-visible", "redis-hidden"],
            json.RootElement.GetProperty("resources")[0]
                .GetProperty("waiting_for")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(
            ["api", "redis-visible"],
            json.RootElement.GetProperty("resources")
                .EnumerateArray()
                .Select(resource => resource.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ListResourcesTool_DoesNotExposeMatchingExplicitSymlinkedAppHostPath()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var realDirectory = workspace.WorkspaceRoot.CreateSubdirectory("real");
        var realAppHostPath = Path.Combine(realDirectory.FullName, "Symlinked.AppHost.csproj");
        File.WriteAllText(realAppHostPath, "<Project />");
        var symlinkDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "link");

        try
        {
            Directory.CreateSymbolicLink(symlinkDirectory, realDirectory.FullName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Cannot create a directory symlink in this environment: {ex.Message}");
        }

        var symlinkedAppHostPath = Path.Combine(symlinkDirectory, "Symlinked.AppHost.csproj");
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            SelectedAppHostPath = symlinkedAppHostPath
        };
        var connection = CreateConnection(realAppHostPath);
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.False(json.RootElement.TryGetProperty("app_host_path", out _));
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsMultipleResources()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection(
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
            });
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resources = json.RootElement.GetProperty("resources");

        Assert.False(json.RootElement.TryGetProperty("app_host_path", out _));
        Assert.Equal(["api-service", "redis", "postgres"], resources.EnumerateArray().Select(r => r.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task ListResourcesTool_UsesCrossPlatformBasenamesForSources()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(
                new ResourceSnapshot
                {
                    Name = "api-service",
                    ResourceType = "Project",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Project.Path] = JsonValue.Create(@"C:\repo\Api\Api.csproj")
                    }
                },
                new ResourceSnapshot
                {
                    Name = "worker",
                    ResourceType = "Executable",
                    State = "Running",
                    Properties = new Dictionary<string, JsonNode?>
                    {
                        [KnownProperties.Executable.Path] = JsonValue.Create(@"C:\repo\bin\worker.exe")
                    }
                }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resources = json.RootElement.GetProperty("resources");
        Assert.Equal("Api.csproj", resources[0].GetProperty("source").GetString());
        Assert.Equal("worker.exe", resources[1].GetProperty("source").GetString());
    }

    [Fact]
    public async Task ListResourcesTool_AppliesResourceEndpointUrlPolicy()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(new ResourceSnapshot
            {
                Name = "endpoints",
                ResourceType = "Custom",
                State = "Running",
                Urls =
                [
                    new ResourceSnapshotUrl { Name = "tcp", Url = "tcp://cache.example.com:6379" },
                    new ResourceSnapshotUrl { Name = "udp", Url = "udp://dns.example.com:53" },
                    new ResourceSnapshotUrl { Name = "ws", Url = "ws://events.example.com/socket" },
                    new ResourceSnapshotUrl { Name = "wss", Url = "wss://events.example.com/socket" },
                    new ResourceSnapshotUrl { Name = "postgres", Url = "postgresql://db.example.com:5432/catalog" },
                    new ResourceSnapshotUrl { Name = "file", Url = "file:///repo/private.txt" },
                    new ResourceSnapshotUrl { Name = "windows", Url = @"C:\repo\private.txt" }
                ]
            }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var urls = json.RootElement.GetProperty("resources")[0].GetProperty("urls");
        Assert.Equal(
            [
                "tcp://cache.example.com:6379",
                "udp://dns.example.com:53",
                "ws://events.example.com/socket",
                "wss://events.example.com/socket",
                "postgresql://db.example.com:5432/catalog"
            ],
            urls.EnumerateArray()
                .Where(url => url.TryGetProperty("url", out _))
                .Select(url => url.GetProperty("url").GetString()));
        Assert.False(urls[5].TryGetProperty("url", out _));
        Assert.False(urls[6].TryGetProperty("url", out _));
    }

    [Fact]
    public async Task ListResourcesTool_ReturnsOnlyBoundedResourceDataAndSanitizedUrls()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection(
            new ResourceSnapshot
            {
                Name = "api-service",
                DisplayName = "API Service",
                ResourceType = "Project",
                State = "Running",
                WaitingFor = ["Redis"],
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Project.Path] = JsonValue.Create("/repo/Api/Api.csproj"),
                    ["secret.property"] = JsonValue.Create("property-secret")
                },
                EnvironmentVariables =
                [
                    new ResourceSnapshotEnvironmentVariable
                    {
                        Name = "API_PASSWORD",
                        Value = "environment-secret",
                        IsFromSpec = true
                    }
                ],
                Urls =
                [
                    new ResourceSnapshotUrl
                    {
                        Name = "https",
                        Url = "https://endpoint-user:endpoint-password@localhost:5001/api?view=summary&TOKEN=endpoint-secret#access_token=fragment-secret",
                        IsInternal = true,
                        DisplayProperties = new ResourceSnapshotUrlDisplayProperties
                        {
                            DisplayName = "HTTPS"
                        }
                    }
                ],
                Relationships =
                [
                    new ResourceSnapshotRelationship
                    {
                        ResourceName = "Redis",
                        Type = "Reference"
                    }
                ],
                Volumes =
                [
                    new ResourceSnapshotVolume
                    {
                        Source = "/repo/private",
                        Target = "/app/private",
                        MountType = "bind"
                    }
                ],
                HealthReports =
                [
                    new ResourceSnapshotHealthReport
                    {
                        Name = "ready",
                        Status = "Healthy",
                        Description = "health-secret",
                        ExceptionText = "exception-secret"
                    }
                ],
                Commands =
                [
                    new ResourceSnapshotCommand
                    {
                        Name = "connect",
                        State = "Enabled",
                        ArgumentInputs =
                        [
                            new ResourceSnapshotCommandArgument
                            {
                                Name = "password",
                                InputType = "SecretText",
                                Value = "command-secret"
                            }
                        ]
                    }
                ]
            },
            new ResourceSnapshot
            {
                Name = "redis-instance",
                DisplayName = "Redis",
                ResourceType = "Container",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Container.Image] = JsonValue.Create("redis:8"),
                    [KnownProperties.Executable.Path] = JsonValue.Create("/repo/bin/container-secret")
                }
            },
            new ResourceSnapshot
            {
                Name = "worker",
                ResourceType = "Executable",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Executable.Path] = JsonValue.Create("/repo/bin/worker")
                }
            },
            new ResourceSnapshot
            {
                Name = "custom",
                ResourceType = "Custom",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Resource.Source] = JsonValue.Create("generic-source-secret")
                }
            },
            new ResourceSnapshot
            {
                Name = "case-mismatched-project",
                ResourceType = "project",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    [KnownProperties.Project.Path] = JsonValue.Create("/repo/CaseMismatched/CaseMismatched.csproj"),
                    [KnownProperties.Resource.Source] = JsonValue.Create("case-mismatch-secret")
                }
            });
        connection.DashboardUrlsState = new DashboardUrlsState
        {
            BaseUrlWithLoginToken = "https://dashboard-user:dashboard-password@dashboard.localhost:18888/login?t=dashboard-secret&view=resources#access_token=fragment-secret"
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);
        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        var resources = json.RootElement.GetProperty("resources");
        var resource = resources[0];

        Assert.Equal(
            ["name", "display_name", "resource_type", "state", "waiting_for", "source", "dashboard_url", "urls", "relationships"],
            resource.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["Redis"], resource.GetProperty("waiting_for").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("Api.csproj", resource.GetProperty("source").GetString());
        Assert.Equal(
            "https://localhost:5001/api?view=summary",
            resource.GetProperty("urls")[0].GetProperty("url").GetString());
        Assert.Equal(
            ["name", "display_name", "url", "is_internal"],
            resource.GetProperty("urls")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["type", "resource_name"],
            resource.GetProperty("relationships")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("redis-instance", resource.GetProperty("relationships")[0].GetProperty("resource_name").GetString());
        Assert.Equal(
            "https://dashboard.localhost:18888?view=resources&resource=api-service",
            resource.GetProperty("dashboard_url").GetString());
        Assert.Equal("redis:8", resources[1].GetProperty("source").GetString());
        Assert.Equal("worker", resources[2].GetProperty("source").GetString());
        Assert.False(resources[3].TryGetProperty("source", out _));
        Assert.False(resources[4].TryGetProperty("source", out _));
    }

    [Fact]
    public async Task ListResourcesTool_DoesNotMaterializeUnrelatedProperties()
    {
        var cyclicValue = new List<object?>();
        cyclicValue.Add(cyclicValue);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(
            "hash1",
            "socket.hash1",
            CreateConnection(new ResourceSnapshot
            {
                Name = "custom",
                ResourceType = "Custom",
                State = "Running",
                Properties = new Dictionary<string, JsonNode?>
                {
                    ["unrelated.full.property"] = JsonValue.Create<object>(cyclicValue)
                }
            }));
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var result = await tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).DefaultTimeout();

        using var json = GetResourceData(result);
        Assert.Equal(
            ["name", "resource_type", "state", "waiting_for", "dashboard_url", "urls", "relationships"],
            json.RootElement.GetProperty("resources")[0].EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public async Task ListResourcesTool_ThrowsMcpErrorWithoutSensitiveDetails_WhenSnapshotRetrievalFails()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection();
        connection.GetResourceSnapshotsHandler = _ => throw new InvalidOperationException(
            "secret-value /other/Unrelated.AppHost.csproj PID 9876");
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var sink = new TestSink();
        var logger = new TestLogger<ListResourcesTool>(
            new TestLoggerFactory(sink, enabled: true));
        var tool = new ListResourcesTool(monitor, logger);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal("Unable to retrieve resources from the selected AppHost.", exception.Message);
        Assert.DoesNotContain(AppHostPath, exception.Message, StringComparison.Ordinal);
        Assert.Collection(
            sink.Writes,
            write =>
            {
                Assert.Equal($"Using single in-scope AppHost: {AppHostPath}", write.Message);
                Assert.Null(write.Exception);
            },
            write =>
            {
                Assert.Equal(
                    $"Error retrieving resources for AppHost {AppHostPath}: InvalidOperationException",
                    write.Message);
                Assert.Null(write.Exception);
            });
    }

    [Fact]
    public async Task ListResourcesTool_PropagatesRequestCancellationUnchanged()
    {
        using var cancellationSource = new CancellationTokenSource();
        var expectedException = new OperationCanceledException(cancellationSource.Token);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        var connection = CreateConnection();
        connection.GetResourceSnapshotsHandler = cancellationToken =>
        {
            Assert.Equal(cancellationSource.Token, cancellationToken);
            throw expectedException;
        };
        monitor.AddConnection("hash1", "socket.hash1", connection);
        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), cancellationSource.Token).AsTask()).DefaultTimeout();

        Assert.Same(expectedException, exception);
    }

    [Fact]
    public async Task ListResourcesTool_DoesNotExposeCandidateAppHostPaths_WhenMultipleAppHostsAreAvailable()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection("/repo/First/First.AppHost.csproj"));
        monitor.AddConnection("hash2", "socket.hash2", CreateConnection("/repo/Second/Second.AppHost.csproj"));

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal(
            "Multiple Aspire AppHosts are running in the MCP server's working directory scope. " +
            "Use 'select_apphost' to choose the AppHost for this request.",
            exception.Message);
    }

    [Fact]
    public async Task ListResourcesTool_DoesNotFallBack_WhenPinnedAppHostIsUnavailable()
    {
        const string pinnedAppHostPath = "/repo/Pinned/Pinned.AppHost.csproj";
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            SelectedAppHostPath = pinnedAppHostPath
        };
        monitor.AddConnection("hash1", "socket.hash1", CreateConnection("/repo/Other/Other.AppHost.csproj"));

        var tool = new ListResourcesTool(monitor, NullLogger<ListResourcesTool>.Instance);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(), CancellationToken.None).AsTask()).DefaultTimeout();

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal(
            "The selected AppHost is not available. Start that AppHost and retry.",
            exception.Message);
        Assert.DoesNotContain(pinnedAppHostPath, exception.Message, StringComparison.Ordinal);
    }

    private static TestAppHostAuxiliaryBackchannel CreateConnection(params ResourceSnapshot[] snapshots)
        => CreateConnection(AppHostPath, snapshots);

    private static TestAppHostAuxiliaryBackchannel CreateConnection(string appHostPath, params ResourceSnapshot[] snapshots)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = appHostPath,
                ProcessId = 4242
            },
            ResourceSnapshots = [.. snapshots],
            DashboardUrlsState = new DashboardUrlsState
            {
                BaseUrlWithLoginToken = "http://localhost:18888/login?t=dashboard-secret"
            }
        };
    }

    private static JsonDocument GetResourceData(CallToolResult result)
    {
        Assert.NotNull(result.Content);
        var textContent = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        const string marker = "# RESOURCE DATA";
        var markerIndex = textContent.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "Response should contain the resource data marker.");
        var jsonText = textContent.Text[(markerIndex + marker.Length)..].Trim();
        Assert.StartsWith("{", jsonText, StringComparison.Ordinal);

        return JsonDocument.Parse(jsonText);
    }
}
