// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.WebSockets;
using Aspire.Hosting.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserLogsSessionManagerTests
{
    [Fact]
    public void TryParseBrowserDebugEndpoint_ReturnsBrowserWebSocketUri()
    {
        var endpoint = BrowserLogsSessionManager.TryParseBrowserDebugEndpoint("""
            51943
            /devtools/browser/4c8404fb-06f8-45f0-9d89-112233445566
            """);

        Assert.NotNull(endpoint);
        Assert.Equal("ws://127.0.0.1:51943/devtools/browser/4c8404fb-06f8-45f0-9d89-112233445566", endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-port")]
    [InlineData("51943")]
    public void TryParseBrowserDebugEndpoint_ReturnsNullForInvalidMetadata(string metadata)
    {
        var endpoint = BrowserLogsSessionManager.TryParseBrowserDebugEndpoint(metadata);

        Assert.Null(endpoint);
    }

    [Fact]
    public async Task BrowserConnectionDiagnosticsLogger_LogsConnectionProblems()
    {
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceName = "web-browser-logs";
        var diagnostics = new BrowserLogsSessionManager.BrowserConnectionDiagnosticsLogger("session-0001", resourceLoggerService.GetLogger(resourceName));

        var logs = await CaptureLogsAsync(resourceLoggerService, resourceName, targetLogCount: 4, () =>
        {
            diagnostics.LogSetupFailure(
                "Setting up the tracked browser debug connection",
                new InvalidOperationException("Connecting to the tracked browser debug endpoint failed.", new TimeoutException("Timed out waiting for a tracked browser protocol response to 'Target.attachToTarget'.")));
            diagnostics.LogConnectionLost(
                new InvalidOperationException("Browser debug connection closed by the remote endpoint with status 'EndpointUnavailable' (1001): browser crashed"));
            diagnostics.LogReconnectAttemptFailed(
                2,
                new InvalidOperationException("Attaching to the tracked browser target failed.", new TimeoutException("Timed out waiting for a tracked browser protocol response to 'Target.attachToTarget'.")));
            diagnostics.LogReconnectFailed(
                new InvalidOperationException("Connecting to the tracked browser debug endpoint failed.", new WebSocketException("Connection refused")));
        });

        Assert.Collection(
            logs,
            log => Assert.Equal(
                "2000-12-29T20:59:59.0000000Z [session-0001] Setting up the tracked browser debug connection failed: InvalidOperationException: Connecting to the tracked browser debug endpoint failed. --> TimeoutException: Timed out waiting for a tracked browser protocol response to 'Target.attachToTarget'.",
                log.Content),
            log => Assert.Equal(
                "2000-12-29T20:59:59.0000000Z [session-0001] Tracked browser debug connection lost: InvalidOperationException: Browser debug connection closed by the remote endpoint with status 'EndpointUnavailable' (1001): browser crashed. Attempting to reconnect.",
                log.Content),
            log => Assert.Equal(
                "2000-12-29T20:59:59.0000000Z [session-0001] Reconnect attempt 2 failed: InvalidOperationException: Attaching to the tracked browser target failed. --> TimeoutException: Timed out waiting for a tracked browser protocol response to 'Target.attachToTarget'.",
                log.Content),
            log => Assert.Equal(
                "2000-12-29T20:59:59.0000000Z [session-0001] Unable to reconnect tracked browser debug connection. Closing the tracked browser session. Last error: InvalidOperationException: Connecting to the tracked browser debug endpoint failed. --> WebSocketException: Connection refused",
                log.Content));
    }

    private static async Task<IReadOnlyList<LogLine>> CaptureLogsAsync(ResourceLoggerService resourceLoggerService, string resourceName, int targetLogCount, Action writeLogs)
    {
        var subscribedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watchTask = ConsoleLoggingTestHelpers.WatchForLogsAsync(resourceLoggerService.WatchAsync(resourceName), targetLogCount);

        _ = Task.Run(async () =>
        {
            await foreach (var subscriber in resourceLoggerService.WatchAnySubscribersAsync())
            {
                if (subscriber.Name == resourceName && subscriber.AnySubscribers)
                {
                    subscribedTcs.TrySetResult();
                    return;
                }
            }
        });

        await subscribedTcs.Task.DefaultTimeout();
        writeLogs();

        return await watchTask.DefaultTimeout();
    }
}
