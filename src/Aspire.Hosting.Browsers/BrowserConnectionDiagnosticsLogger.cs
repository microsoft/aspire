// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Keep message composition separate from the runtime so tests can pin the diagnostics without a live websocket failure.
internal sealed class BrowserConnectionDiagnosticsLogger(string sessionId, ILogger resourceLogger)
{
    private readonly ILogger _resourceLogger = resourceLogger;
    private readonly string _sessionId = sessionId;

    public void LogSetupFailure(string stage, Exception exception)
    {
        _resourceLogger.LogError("[{SessionId}] {Stage} failed: {Reason}", _sessionId, stage, DescribeConnectionProblem(exception));
    }

    public void LogConnectionLost(Exception exception)
    {
        _resourceLogger.LogWarning("[{SessionId}] Tracked browser debug connection lost: {Reason}. Attempting to reconnect.", _sessionId, DescribeConnectionProblem(exception));
    }

    public void LogReconnectAttemptFailed(int attempt, Exception exception)
    {
        _resourceLogger.LogWarning("[{SessionId}] Reconnect attempt {Attempt} failed: {Reason}", _sessionId, attempt, DescribeConnectionProblem(exception));
    }

    public void LogReconnectFailed(Exception exception)
    {
        _resourceLogger.LogError("[{SessionId}] Unable to reconnect tracked browser debug connection. Closing the tracked browser session. Last error: {Reason}", _sessionId, DescribeConnectionProblem(exception));
    }

    public void LogHostTerminated(Exception exception)
    {
        _resourceLogger.LogError("[{SessionId}] Tracked browser host ended before the tracked page session completed: {Reason}", _sessionId, DescribeConnectionProblem(exception));
    }

    internal static string DescribeConnectionProblem(Exception exception)
    {
        var messages = new List<string>();

        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = string.IsNullOrWhiteSpace(current.Message)
                ? current.GetType().Name
                : $"{current.GetType().Name}: {current.Message}";

            if (!messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }

        return string.Join(" --> ", messages);
    }
}
