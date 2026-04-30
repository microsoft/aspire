// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.WebSockets;
using Aspire.Hosting.Tests.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class BrowserConnectionDiagnosticsLoggerTests
{
    [Fact]
    public async Task LogsConnectionProblems()
    {
        var resourceLoggerService = ConsoleLoggingTestHelpers.GetResourceLoggerService();
        var resourceName = "web-browser-logs";
        var diagnostics = new BrowserConnectionDiagnosticsLogger("session-0001", resourceLoggerService.GetLogger(resourceName));

        var logs = await ConsoleLoggingTestHelpers.CaptureLogsAsync(resourceLoggerService, resourceName, targetLogCount: 4, () =>
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
}
