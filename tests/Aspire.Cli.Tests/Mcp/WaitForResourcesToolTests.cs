// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Mcp.Tools;
using Aspire.Cli.Tests.TestServices;
using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Aspire.Cli.Tests.Mcp;

public class WaitForResourcesToolTests
{
    private const string AppHostPath = "/repo/TestAppHost/TestAppHost.csproj";

    [Theory]
    [InlineData("""{"resourceNames":"api"}""")]
    [InlineData("""{"resourceNames":["api",42]}""")]
    [InlineData("""{"targetState":"ready"}""")]
    [InlineData("""{"timeoutSeconds":0}""")]
    [InlineData("""{"timeoutSeconds":3601}""")]
    [InlineData("""{"timeoutSeconds":1.5}""")]
    [InlineData("""{"resourceName":"api"}""")]
    public async Task WaitForResourcesTool_RejectsInvalidArguments(string argumentsJson)
    {
        var tool = CreateTool(new TestAuxiliaryBackchannelMonitor());
        var arguments = ParseArguments(argumentsJson);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, exception.ErrorCode);
    }

    [Fact]
    public async Task WaitForResourcesTool_RejectsTooManyResourceNames()
    {
        var tool = CreateTool(new TestAuxiliaryBackchannelMonitor());
        var resourceNames = Enumerable.Range(0, 101).Select(static index => $"resource-{index}").ToArray();
        var arguments = ParseArguments(JsonSerializer.Serialize(new { resourceNames }));

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, exception.ErrorCode);
    }

    [Fact]
    public async Task WaitForResourcesTool_RejectsResourceNameThatIsTooLong()
    {
        var tool = CreateTool(new TestAuxiliaryBackchannelMonitor());
        var arguments = ParseArguments(JsonSerializer.Serialize(new { resourceNames = new[] { new string('a', 257) } }));

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).AsTask());

        Assert.Equal(McpErrorCode.InvalidParams, exception.ErrorCode);
    }

    [Fact]
    public async Task WaitForResourcesTool_AcceptsResourceNameWithinUnicodeCharacterLimit()
    {
        var tool = CreateTool(new TestAuxiliaryBackchannelMonitor());
        var resourceName = string.Concat(Enumerable.Repeat("\U0001F680", 256));
        var arguments = ParseArguments(JsonSerializer.Serialize(new { resourceNames = new[] { resourceName } }));

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(CallToolContextTestHelper.Create(arguments), CancellationToken.None).AsTask());

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("""{"resourceNames":[]}""")]
    public async Task WaitForResourcesTool_WaitsForAllVisibleNonExcludedResources_WhenNamesAreOmittedOrEmpty(
        string? argumentsJson)
    {
        var waitCalls = new List<(string ResourceName, string TargetState, int TimeoutSeconds)>();
        var connection = CreateConnection(
            new ResourceSnapshot
            {
                Name = "api",
                DisplayName = "API",
                State = "Running",
                HealthStatus = "Healthy"
            },
            new ResourceSnapshot
            {
                Name = "hidden-proxy",
                State = "Running",
                IsHidden = true
            },
            new ResourceSnapshot
            {
                Name = "secret-store",
                State = "Running",
                Properties =
                {
                    [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
                }
            });
        connection.WaitForResourceHandler = (resourceName, targetState, timeoutSeconds, _) =>
        {
            waitCalls.Add((resourceName, targetState, timeoutSeconds));
            return Task.FromResult(new WaitForResourceResponse
            {
                Success = true,
                State = "Running",
                HealthStatus = "Healthy"
            });
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);
        var arguments = argumentsJson is null ? null : ParseArguments(argumentsJson);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(1, connection.GetResourceSnapshotsCallCount);
        Assert.True(connection.LastGetResourceSnapshotsIncludeHidden);
        Assert.Equal([("api", "healthy", 120)], waitCalls);

        using var json = GetWaitResult(result);
        Assert.Equal(
            ["outcome", "target_state", "resources"],
            json.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("success", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("healthy", json.RootElement.GetProperty("target_state").GetString());

        var resource = Assert.Single(json.RootElement.GetProperty("resources").EnumerateArray());
        Assert.Equal(
            ["name", "state", "health", "outcome"],
            resource.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("api", resource.GetProperty("name").GetString());
        Assert.Equal("Running", resource.GetProperty("state").GetString());
        Assert.Equal("Healthy", resource.GetProperty("health").GetString());
        Assert.Equal("success", resource.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("""{"resourceNames":[]}""", false)]
    [InlineData(null, true)]
    [InlineData("""{"resourceNames":[]}""", true)]
    public async Task WaitForResourcesTool_ReturnsFailureWithoutWaiting_WhenNoEligibleResourcesAreAvailable(
        string? argumentsJson,
        bool includeUnavailableResources)
    {
        var waitCallCount = 0;
        ResourceSnapshot[] snapshots = includeUnavailableResources
            ?
            [
                new ResourceSnapshot
                {
                    Name = "hidden-proxy",
                    State = "Running",
                    IsHidden = true
                },
                new ResourceSnapshot
                {
                    Name = "secret-store",
                    State = "Running",
                    Properties =
                    {
                        [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
                    }
                }
            ]
            : [];
        var connection = CreateConnection(snapshots);
        connection.WaitForResourceHandler = (_, _, _, _) =>
        {
            Interlocked.Increment(ref waitCallCount);
            return Task.FromResult(new WaitForResourceResponse { Success = true });
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);
        var arguments = argumentsJson is null ? null : ParseArguments(argumentsJson);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, waitCallCount);
        Assert.Equal(1, connection.GetResourceSnapshotsCallCount);
        Assert.True(connection.LastGetResourceSnapshotsIncludeHidden);

        using var json = GetWaitResult(result);
        Assert.Equal(
            ["outcome", "target_state", "error", "resources"],
            json.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("failure", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("healthy", json.RootElement.GetProperty("target_state").GetString());
        Assert.Equal(
            "No eligible resources were found in the selected AppHost.",
            json.RootElement.GetProperty("error").GetString());
        Assert.Empty(json.RootElement.GetProperty("resources").EnumerateArray());
    }

    [Fact]
    public async Task WaitForResourcesTool_ResolvesRuntimeNamesBeforeUnambiguousDisplayNames()
    {
        var waitCalls = new List<(string ResourceName, string TargetState, int TimeoutSeconds)>();
        var connection = CreateConnection(
            new ResourceSnapshot
            {
                Name = "api-runtime",
                DisplayName = "API",
                State = "Starting"
            },
            new ResourceSnapshot
            {
                Name = "worker-runtime",
                DisplayName = "Worker",
                State = "Starting"
            });
        connection.WaitForResourceHandler = (resourceName, targetState, timeoutSeconds, _) =>
        {
            waitCalls.Add((resourceName, targetState, timeoutSeconds));
            return Task.FromResult(new WaitForResourceResponse
            {
                Success = true,
                State = "Running"
            });
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);
        var arguments = ParseArguments(
            """
            {
              "resourceNames": ["api-runtime", "Worker"],
              "targetState": "up",
              "timeoutSeconds": 30
            }
            """);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(
            [("api-runtime", "up", 30), ("worker-runtime", "up", 30)],
            waitCalls);

        using var json = GetWaitResult(result);
        Assert.Equal("up", json.RootElement.GetProperty("target_state").GetString());
        Assert.Equal(
            ["api-runtime", "worker-runtime"],
            json.RootElement.GetProperty("resources")
                .EnumerateArray()
                .Select(static resource => resource.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task WaitForResourcesTool_ReportsUnavailableNamedResourcesWithoutWaiting()
    {
        var waitCallCount = 0;
        var connection = CreateConnection(
            new ResourceSnapshot
            {
                Name = "hidden-runtime",
                DisplayName = "Hidden",
                State = "Running",
                IsHidden = true
            },
            new ResourceSnapshot
            {
                Name = "excluded-runtime",
                DisplayName = "Excluded",
                State = "Running",
                HealthStatus = "Healthy",
                Properties =
                {
                    [KnownProperties.Resource.ExcludeFromMcp] = JsonValue.Create(true)
                }
            },
            new ResourceSnapshot
            {
                Name = "replica-a",
                DisplayName = "Replica",
                State = "Running"
            },
            new ResourceSnapshot
            {
                Name = "replica-b",
                DisplayName = "Replica",
                State = "Running"
            });
        connection.WaitForResourceHandler = (_, _, _, _) =>
        {
            Interlocked.Increment(ref waitCallCount);
            return Task.FromResult(new WaitForResourceResponse { Success = true });
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);
        var arguments = ParseArguments(
            """
            {
              "resourceNames": ["Hidden", "Excluded", "Replica", "missing"]
            }
            """);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(0, waitCallCount);
        Assert.Equal(1, connection.GetResourceSnapshotsCallCount);

        using var json = GetWaitResult(result);
        Assert.Equal("failure", json.RootElement.GetProperty("outcome").GetString());
        var resources = json.RootElement.GetProperty("resources").EnumerateArray().ToArray();
        Assert.Collection(
            resources,
            resource => AssertResourceFailure(
                resource,
                "Hidden",
                null,
                null,
                "Resource is hidden and cannot be waited for through MCP."),
            resource => AssertResourceFailure(
                resource,
                "Excluded",
                null,
                null,
                "Resource is excluded from MCP."),
            resource => AssertResourceFailure(
                resource,
                "Replica",
                null,
                null,
                "Display name is ambiguous; use an exact runtime name."),
            resource => AssertResourceFailure(
                resource,
                "missing",
                null,
                null,
                "Resource was not found in the selected AppHost."));
    }

    [Fact]
    public async Task WaitForResourcesTool_MapsBoundedOutcomesWithFailurePrecedence()
    {
        var responses = new Dictionary<string, WaitForResourceResponse>(StringComparers.ResourceName)
        {
            ["clean-exit"] = new() { Success = true, State = "Exited" },
            ["slow"] = new()
            {
                Success = false,
                TimedOut = true,
                State = "Running",
                ErrorMessage = "secret=timeout-credential"
            },
            ["disappeared"] = new()
            {
                Success = false,
                ResourceNotFound = true,
                ErrorMessage = "secret=resource-credential"
            },
            ["failed"] = new()
            {
                Success = false,
                State = "FailedToStart",
                ErrorMessage = "secret=terminal-credential"
            },
            ["false-down"] = new()
            {
                Success = true,
                State = "FailedToStart",
                ErrorMessage = "secret=false-success-credential"
            },
            ["custom-state"] = new()
            {
                Success = false,
                State = "secret-custom-state"
            }
        };
        var connection = CreateConnection(
            responses.Keys.Select(static name => new ResourceSnapshot
            {
                Name = name,
                State = "Running"
            }).ToArray());
        connection.WaitForResourceHandler = (resourceName, _, _, _) =>
            Task.FromResult(responses[resourceName]);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);
        var arguments = ParseArguments("""{"targetState":"down"}""");

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            CancellationToken.None).DefaultTimeout();

        using var json = GetWaitResult(result);
        Assert.Equal("failure", json.RootElement.GetProperty("outcome").GetString());
        Assert.Collection(
            json.RootElement.GetProperty("resources").EnumerateArray(),
            resource => AssertResourceOutcome(resource, "clean-exit", "Exited", "success", null),
            resource => AssertResourceOutcome(
                resource,
                "slow",
                "Running",
                "timeout",
                "Timed out waiting for the target state."),
            resource => AssertResourceOutcome(
                resource,
                "disappeared",
                null,
                "failure",
                "Resource was not found while waiting."),
            resource => AssertResourceOutcome(
                resource,
                "failed",
                "FailedToStart",
                "failure",
                "Resource entered a terminal failed state."),
            resource => AssertResourceOutcome(
                resource,
                "false-down",
                "FailedToStart",
                "failure",
                "Resource entered a terminal failed state."),
            resource => AssertResourceOutcome(
                resource,
                "custom-state",
                "unknown",
                "failure",
                "Resource wait failed."));
    }

    [Fact]
    public async Task WaitForResourcesTool_UsesTimeoutOutcomeWhenNoResourceFails()
    {
        var connection = CreateConnection(
            new ResourceSnapshot { Name = "ready", State = "Running" },
            new ResourceSnapshot { Name = "slow", State = "Starting" });
        connection.WaitForResourceHandler = (resourceName, _, _, _) =>
            Task.FromResult(resourceName == "ready"
                ? new WaitForResourceResponse { Success = true, State = "Running", HealthStatus = "Healthy" }
                : new WaitForResourceResponse { Success = false, TimedOut = true, State = "Starting" });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetWaitResult(result);
        Assert.Equal("timeout", json.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(
            ["success", "timeout"],
            json.RootElement.GetProperty("resources")
                .EnumerateArray()
                .Select(static resource => resource.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task WaitForResourcesTool_WaitsOnceForDuplicateResourceSelections()
    {
        var waitCallCount = 0;
        var connection = CreateConnection(
            new ResourceSnapshot
            {
                Name = "api",
                DisplayName = "API",
                State = "Starting"
            });
        connection.WaitForResourceHandler = (_, _, _, _) =>
        {
            Interlocked.Increment(ref waitCallCount);
            return Task.FromResult(new WaitForResourceResponse
            {
                Success = true,
                State = "Running",
                HealthStatus = "Healthy"
            });
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);
        var arguments = ParseArguments("""{"resourceNames":["api","API","api"]}""");

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            CancellationToken.None).DefaultTimeout();

        Assert.Equal(1, waitCallCount);
        using var json = GetWaitResult(result);
        Assert.Equal(
            ["api", "api", "api"],
            json.RootElement.GetProperty("resources")
                .EnumerateArray()
                .Select(static resource => resource.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task WaitForResourcesTool_WaitsConcurrentlyWithOneSharedDeadline()
    {
        var timeProvider = new FakeTimeProvider();
        var receivedTimeouts = new ConcurrentQueue<int>();
        var allWaitsStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWaitsToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedCount = 0;
        var connection = CreateConnection(
            new ResourceSnapshot { Name = "api", State = "Starting" },
            new ResourceSnapshot { Name = "worker", State = "Starting" },
            new ResourceSnapshot { Name = "database", State = "Starting" });
        connection.WaitForResourceHandler = async (_, _, timeoutSeconds, cancellationToken) =>
        {
            receivedTimeouts.Enqueue(timeoutSeconds);
            timeProvider.Advance(TimeSpan.FromSeconds(1));
            if (Interlocked.Increment(ref startedCount) == 3)
            {
                allWaitsStarted.SetResult();
            }

            await allowWaitsToComplete.Task.WaitAsync(cancellationToken);
            return new WaitForResourceResponse { Success = true, State = "Running" };
        };
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor, timeProvider);
        var arguments = ParseArguments("""{"targetState":"up","timeoutSeconds":30}""");

        var waitTask = tool.CallToolAsync(
            CallToolContextTestHelper.Create(arguments),
            CancellationToken.None).AsTask();

        try
        {
            await allWaitsStarted.Task.DefaultTimeout();
            Assert.False(waitTask.IsCompleted);
            Assert.Equal([30, 29, 28], receivedTimeouts);
        }
        finally
        {
            allowWaitsToComplete.TrySetResult();
        }

        var result = await waitTask.DefaultTimeout();
        using var json = GetWaitResult(result);
        Assert.Equal("success", json.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task WaitForResourcesTool_ThrowsBoundedMcpError_WhenConnectionRetrievalFails()
    {
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            ScanAsyncCallback = _ => throw new InvalidOperationException("secret=connection-credential")
        };
        var tool = CreateTool(monitor);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(
                CallToolContextTestHelper.Create(),
                CancellationToken.None).AsTask());

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal("Unable to resolve an Aspire AppHost connection.", exception.Message);
    }

    [Fact]
    public async Task WaitForResourcesTool_ThrowsBoundedMcpError_WhenSnapshotRetrievalFails()
    {
        var connection = CreateConnection();
        connection.GetResourceSnapshotsHandler = _ =>
            throw new InvalidOperationException("secret=snapshot-credential");
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);

        var exception = await Assert.ThrowsAsync<McpProtocolException>(
            () => tool.CallToolAsync(
                CallToolContextTestHelper.Create(),
                CancellationToken.None).AsTask());

        Assert.Equal(McpErrorCode.InternalError, exception.ErrorCode);
        Assert.Equal("Unable to retrieve resources from the selected AppHost.", exception.Message);
        Assert.DoesNotContain(AppHostPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitForResourcesTool_ReportsBoundedFailure_WhenOneWaitThrows()
    {
        var connection = CreateConnection(
            new ResourceSnapshot { Name = "api", State = "Starting" },
            new ResourceSnapshot { Name = "worker", State = "Starting" });
        connection.WaitForResourceHandler = (resourceName, _, _, _) =>
            resourceName == "api"
                ? Task.FromException<WaitForResourceResponse>(
                    new InvalidOperationException("secret=wait-credential"))
                : Task.FromResult(new WaitForResourceResponse
                {
                    Success = true,
                    State = "Running",
                    HealthStatus = "Healthy"
                });
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);

        var result = await tool.CallToolAsync(
            CallToolContextTestHelper.Create(),
            CancellationToken.None).DefaultTimeout();

        using var json = GetWaitResult(result);
        Assert.Equal("failure", json.RootElement.GetProperty("outcome").GetString());
        Assert.Collection(
            json.RootElement.GetProperty("resources").EnumerateArray(),
            resource => AssertResourceOutcome(
                resource,
                "api",
                null,
                "failure",
                "Resource wait failed."),
            resource => AssertResourceOutcome(
                resource,
                "worker",
                "Running",
                "success",
                null));
    }

    [Fact]
    public async Task WaitForResourcesTool_PropagatesConnectionCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var monitor = new TestAuxiliaryBackchannelMonitor
        {
            ScanAsyncCallback = cancellationToken => Task.FromCanceled(cancellationToken)
        };
        var tool = CreateTool(monitor);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.CallToolAsync(
                CallToolContextTestHelper.Create(),
                cancellationSource.Token).AsTask());

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task WaitForResourcesTool_PropagatesSnapshotCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var connection = CreateConnection();
        connection.GetResourceSnapshotsHandler = cancellationToken =>
            Task.FromCanceled<List<ResourceSnapshot>>(cancellationToken);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.CallToolAsync(
                CallToolContextTestHelper.Create(),
                cancellationSource.Token).AsTask());

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task WaitForResourcesTool_PropagatesWaitCancellation()
    {
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        var connection = CreateConnection(new ResourceSnapshot { Name = "api", State = "Starting" });
        connection.WaitForResourceHandler = (_, _, _, cancellationToken) =>
            Task.FromCanceled<WaitForResourceResponse>(cancellationToken);
        var monitor = new TestAuxiliaryBackchannelMonitor();
        monitor.AddConnection(connection.Hash, connection.SocketPath, connection);
        var tool = CreateTool(monitor);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => tool.CallToolAsync(
                CallToolContextTestHelper.Create(),
                cancellationSource.Token).AsTask());

        Assert.Equal(cancellationSource.Token, exception.CancellationToken);
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(static property => property.Name, static property => property.Value.Clone());
    }

    private static TestAppHostAuxiliaryBackchannel CreateConnection(params ResourceSnapshot[] snapshots)
    {
        return new TestAppHostAuxiliaryBackchannel
        {
            AppHostInfo = new AppHostInformation
            {
                AppHostPath = AppHostPath,
                ProcessId = 4242
            },
            ResourceSnapshots = [.. snapshots]
        };
    }

    private static WaitForResourcesTool CreateTool(
        TestAuxiliaryBackchannelMonitor monitor,
        TimeProvider? timeProvider = null)
    {
        return new WaitForResourcesTool(
            monitor,
            new ResourceWaitService(
                timeProvider ?? TimeProvider.System,
                NullLogger<ResourceWaitService>.Instance),
            NullLogger<WaitForResourcesTool>.Instance);
    }

    private static JsonDocument GetWaitResult(CallToolResult result)
    {
        Assert.True(result.IsError is null or false);
        var textContent = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        const string marker = "# WAIT RESULT";
        var markerIndex = textContent.Text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, "Response should contain the wait result marker.");

        return JsonDocument.Parse(textContent.Text[(markerIndex + marker.Length)..].Trim());
    }

    private static void AssertResourceFailure(
        JsonElement resource,
        string name,
        string? state,
        string? health,
        string error)
    {
        Assert.Equal(name, resource.GetProperty("name").GetString());
        Assert.Equal(state, resource.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null);
        Assert.Equal(health, resource.TryGetProperty("health", out var healthElement) ? healthElement.GetString() : null);
        Assert.Equal("failure", resource.GetProperty("outcome").GetString());
        Assert.Equal(error, resource.GetProperty("error").GetString());
    }

    private static void AssertResourceOutcome(
        JsonElement resource,
        string name,
        string? state,
        string outcome,
        string? error)
    {
        Assert.Equal(name, resource.GetProperty("name").GetString());
        Assert.Equal(state, resource.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null);
        Assert.Equal(outcome, resource.GetProperty("outcome").GetString());
        Assert.Equal(error, resource.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null);
    }
}
