// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Aspire.Hosting;

internal interface IBrowserLogsRunningSession
{
    string SessionId { get; }

    string BrowserExecutable { get; }

    int ProcessId { get; }

    DateTime StartedAt { get; }

    void StartCompletionObserver(Func<int, Exception?, Task> onCompleted);

    Task StopAsync(CancellationToken cancellationToken);
}

internal interface IBrowserLogsRunningSessionFactory
{
    Task<IBrowserLogsRunningSession> StartSessionAsync(
        BrowserLogsResource resource,
        string resourceName,
        Uri url,
        string sessionId,
        ILogger resourceLogger,
        CancellationToken cancellationToken);
}

internal sealed class BrowserLogsSessionManager : IBrowserLogsSessionManager, IAsyncDisposable
{
    private readonly ResourceLoggerService _resourceLoggerService;
    private readonly ResourceNotificationService _resourceNotificationService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly IBrowserLogsRunningSessionFactory _sessionFactory;
    private readonly ConcurrentDictionary<string, ResourceSessionState> _resourceStates = new(StringComparer.Ordinal);

    public BrowserLogsSessionManager(
        IFileSystemService fileSystemService,
        ResourceLoggerService resourceLoggerService,
        ResourceNotificationService resourceNotificationService,
        TimeProvider timeProvider,
        ILogger<BrowserLogsSessionManager> logger)
        : this(
            resourceLoggerService,
            resourceNotificationService,
            timeProvider,
            logger,
            new BrowserLogsRunningSessionFactory(fileSystemService, logger, timeProvider))
    {
    }

    internal BrowserLogsSessionManager(
        ResourceLoggerService resourceLoggerService,
        ResourceNotificationService resourceNotificationService,
        TimeProvider timeProvider,
        ILogger<BrowserLogsSessionManager> logger,
        IBrowserLogsRunningSessionFactory sessionFactory)
    {
        _resourceLoggerService = resourceLoggerService;
        _resourceNotificationService = resourceNotificationService;
        _timeProvider = timeProvider;
        _logger = logger;
        _sessionFactory = sessionFactory;
    }

    public async Task StartSessionAsync(BrowserLogsResource resource, string resourceName, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(url);

        var resourceState = _resourceStates.GetOrAdd(resourceName, static _ => new ResourceSessionState());
        await resourceState.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var sessionSequence = ++resourceState.TotalSessionsLaunched;
            var sessionId = $"session-{sessionSequence:0000}";
            resourceState.LastSessionId = sessionId;
            resourceState.LastTargetUrl = url.ToString();

            var resourceLogger = _resourceLoggerService.GetLogger(resourceName);
            resourceLogger.LogInformation("[{SessionId}] Opening tracked browser for '{Url}' using '{Browser}'.", sessionId, url, resource.Browser);

            var launchStartedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var pendingSession = new PendingBrowserSession(sessionId, launchStartedAt, url);

            await PublishResourceSnapshotAsync(
                resource,
                resourceName,
                resourceState,
                stateText: KnownResourceStates.Starting,
                stateStyle: KnownResourceStateStyles.Info,
                pendingSession,
                stopTimeStamp: null,
                exitCode: null).ConfigureAwait(false);

            IBrowserLogsRunningSession session;
            try
            {
                session = await _sessionFactory.StartSessionAsync(
                    resource,
                    resourceName,
                    url,
                    sessionId,
                    resourceLogger,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                resourceLogger.LogError(ex, "[{SessionId}] Failed to open tracked browser for '{Url}'.", sessionId, url);

                await PublishResourceSnapshotAsync(
                    resource,
                    resourceName,
                    resourceState,
                    stateText: resourceState.ActiveSessions.Count > 0 ? KnownResourceStates.Running : KnownResourceStates.FailedToStart,
                    stateStyle: resourceState.ActiveSessions.Count > 0 ? KnownResourceStateStyles.Success : KnownResourceStateStyles.Error,
                    pendingSession: null,
                    stopTimeStamp: resourceState.ActiveSessions.Count == 0 ? _timeProvider.GetUtcNow().UtcDateTime : null,
                    exitCode: null,
                    fallbackStartTimeStamp: launchStartedAt).ConfigureAwait(false);

                throw;
            }

            resourceState.LastBrowserExecutable = session.BrowserExecutable;
            resourceState.ActiveSessions[session.SessionId] = new ActiveBrowserSession(
                session.SessionId,
                session.BrowserExecutable,
                session.ProcessId,
                session.StartedAt,
                url,
                session);

            session.StartCompletionObserver(async (exitCode, error) =>
            {
                await HandleSessionCompletedAsync(resource, resourceName, resourceState, session.SessionId, exitCode, error).ConfigureAwait(false);
            });

            await PublishResourceSnapshotAsync(
                resource,
                resourceName,
                resourceState,
                stateText: KnownResourceStates.Running,
                stateStyle: KnownResourceStateStyles.Success,
                pendingSession: null,
                stopTimeStamp: null,
                exitCode: null).ConfigureAwait(false);
        }
        finally
        {
            resourceState.Lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        var sessionsToStop = new List<IBrowserLogsRunningSession>();

        foreach (var resourceState in _resourceStates.Values)
        {
            await resourceState.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                sessionsToStop.AddRange(resourceState.ActiveSessions.Values.Select(static activeSession => activeSession.Session));
            }
            finally
            {
                resourceState.Lock.Release();
            }
        }

        foreach (var session in sessionsToStop)
        {
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        foreach (var (_, resourceState) in _resourceStates)
        {
            resourceState.Lock.Dispose();
        }
    }

    private async Task HandleSessionCompletedAsync(
        BrowserLogsResource resource,
        string resourceName,
        ResourceSessionState resourceState,
        string sessionId,
        int exitCode,
        Exception? error)
    {
        await resourceState.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (!resourceState.ActiveSessions.Remove(sessionId))
            {
                return;
            }

            var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var hasActiveSessions = resourceState.ActiveSessions.Count > 0;
            var (stateText, stateStyle) = hasActiveSessions
                ? (KnownResourceStates.Running, KnownResourceStateStyles.Success)
                : error switch
                {
                    not null => (KnownResourceStates.Exited, KnownResourceStateStyles.Error),
                    null when exitCode == 0 => (KnownResourceStates.Finished, KnownResourceStateStyles.Success),
                    _ => (KnownResourceStates.Exited, KnownResourceStateStyles.Error)
                };

            await PublishResourceSnapshotAsync(
                resource,
                resourceName,
                resourceState,
                stateText,
                stateStyle,
                pendingSession: null,
                stopTimeStamp: hasActiveSessions ? null : completedAt,
                exitCode: hasActiveSessions ? null : exitCode).ConfigureAwait(false);
        }
        finally
        {
            resourceState.Lock.Release();
        }
    }

    private Task PublishResourceSnapshotAsync(
        BrowserLogsResource resource,
        string resourceName,
        ResourceSessionState resourceState,
        string stateText,
        string stateStyle,
        PendingBrowserSession? pendingSession,
        DateTime? stopTimeStamp,
        int? exitCode,
        DateTime? fallbackStartTimeStamp = null)
    {
        var startTimeStamp = GetStartTimeStamp(resourceState, pendingSession?.StartedAt ?? fallbackStartTimeStamp);
        var healthReports = GetHealthReports(resourceState, pendingSession);
        var propertyUpdates = GetPropertyUpdates(resourceState);

        return _resourceNotificationService.PublishUpdateAsync(resource, resourceName, snapshot => snapshot with
        {
            StartTimeStamp = startTimeStamp ?? snapshot.StartTimeStamp,
            StopTimeStamp = resourceState.ActiveSessions.Count > 0 || pendingSession is not null ? null : stopTimeStamp,
            ExitCode = resourceState.ActiveSessions.Count > 0 || pendingSession is not null ? null : exitCode,
            State = new ResourceStateSnapshot(stateText, stateStyle),
            Properties = snapshot.Properties.SetResourcePropertyRange(propertyUpdates),
            HealthReports = healthReports
        });
    }

    private ImmutableArray<HealthReportSnapshot> GetHealthReports(ResourceSessionState resourceState, PendingBrowserSession? pendingSession)
    {
        var runAt = _timeProvider.GetUtcNow().UtcDateTime;
        var reports = new List<HealthReportSnapshot>(resourceState.ActiveSessions.Count + (pendingSession is null ? 0 : 1));

        foreach (var session in resourceState.ActiveSessions.Values.OrderBy(static session => session.SessionId, StringComparer.Ordinal))
        {
            reports.Add(new HealthReportSnapshot(
                session.SessionId,
                HealthStatus.Healthy,
                $"PID {session.ProcessId} targeting {session.TargetUrl}",
                null)
            {
                LastRunAt = runAt
            });
        }

        if (pendingSession is not null)
        {
            reports.Add(new HealthReportSnapshot(
                pendingSession.SessionId,
                Status: null,
                Description: $"Launching tracked browser for {pendingSession.TargetUrl}.",
                ExceptionText: null)
            {
                LastRunAt = runAt
            });
        }

        return [.. reports];
    }

    private static IEnumerable<ResourcePropertySnapshot> GetPropertyUpdates(ResourceSessionState resourceState)
    {
        yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.ActiveSessionCountPropertyName, resourceState.ActiveSessions.Count);
        yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.ActiveSessionsPropertyName, FormatActiveSessions(resourceState.ActiveSessions.Values));
        yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.TotalSessionsLaunchedPropertyName, resourceState.TotalSessionsLaunched);

        if (resourceState.LastSessionId is not null)
        {
            yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.LastSessionPropertyName, resourceState.LastSessionId);
        }

        if (resourceState.LastTargetUrl is not null)
        {
            yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.TargetUrlPropertyName, resourceState.LastTargetUrl);
        }

        if (resourceState.LastBrowserExecutable is not null)
        {
            yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, resourceState.LastBrowserExecutable);
        }
    }

    private static DateTime? GetStartTimeStamp(ResourceSessionState resourceState, DateTime? fallbackStartTimeStamp)
    {
        if (resourceState.ActiveSessions.Count > 0)
        {
            return resourceState.ActiveSessions.Values.MinBy(static session => session.StartedAt)?.StartedAt;
        }

        return fallbackStartTimeStamp;
    }

    private static string FormatActiveSessions(IEnumerable<ActiveBrowserSession> sessions)
    {
        var activeSessions = sessions
            .OrderBy(static session => session.SessionId, StringComparer.Ordinal)
            .Select(static session => $"{session.SessionId} (PID {session.ProcessId})")
            .ToArray();

        return activeSessions.Length > 0
            ? string.Join(", ", activeSessions)
            : "None";
    }

    internal sealed class BrowserEventLogger(string sessionId, ILogger resourceLogger)
    {
        private readonly string _sessionId = sessionId;
        private readonly ILogger _resourceLogger = resourceLogger;
        private readonly Dictionary<string, BrowserNetworkRequestState> _networkRequests = new(StringComparer.Ordinal);

        public void HandleEvent(BrowserLogsProtocolEvent protocolEvent)
        {
            switch (protocolEvent)
            {
                case BrowserLogsConsoleApiCalledEvent consoleApiCalledEvent:
                    LogConsoleMessage(consoleApiCalledEvent.Parameters);
                    break;
                case BrowserLogsExceptionThrownEvent exceptionThrownEvent:
                    LogUnhandledException(exceptionThrownEvent.Parameters);
                    break;
                case BrowserLogsLogEntryAddedEvent logEntryAddedEvent:
                    LogEntryAdded(logEntryAddedEvent.Parameters);
                    break;
                case BrowserLogsRequestWillBeSentEvent requestWillBeSentEvent:
                    TrackRequestStarted(requestWillBeSentEvent.Parameters);
                    break;
                case BrowserLogsResponseReceivedEvent responseReceivedEvent:
                    TrackResponseReceived(responseReceivedEvent.Parameters);
                    break;
                case BrowserLogsLoadingFinishedEvent loadingFinishedEvent:
                    TrackRequestCompleted(loadingFinishedEvent.Parameters);
                    break;
                case BrowserLogsLoadingFailedEvent loadingFailedEvent:
                    TrackRequestFailed(loadingFailedEvent.Parameters);
                    break;
            }
        }

        private void LogConsoleMessage(BrowserLogsRuntimeConsoleApiCalledParameters parameters)
        {
            var level = parameters.Type ?? "log";
            var message = parameters.Args is { Length: > 0 }
                ? string.Join(" ", parameters.Args.Select(FormatRemoteObject).Where(static value => !string.IsNullOrEmpty(value)))
                : string.Empty;

            WriteLog(MapConsoleLevel(level), $"[console.{level}] {message}".TrimEnd());
        }

        private void LogUnhandledException(BrowserLogsExceptionThrownParameters parameters)
        {
            var exceptionDetails = parameters.ExceptionDetails;
            if (exceptionDetails is null)
            {
                return;
            }

            var message = exceptionDetails.Exception?.Description
                ?? exceptionDetails.Text
                ?? "Unhandled browser exception";

            var location = GetLocationSuffix(exceptionDetails);
            WriteLog(LogLevel.Error, $"[exception] {message}{location}");
        }

        private void LogEntryAdded(BrowserLogsLogEntryAddedParameters parameters)
        {
            var entry = parameters.Entry;
            if (entry is null)
            {
                return;
            }

            var level = entry.Level ?? "info";
            var text = entry.Text ?? string.Empty;
            var location = GetLocationSuffix(entry);

            WriteLog(MapLogEntryLevel(level), $"[log.{level}] {text}{location}".TrimEnd());
        }

        private void TrackRequestStarted(BrowserLogsRequestWillBeSentParameters parameters)
        {
            if (parameters.RequestId is not { Length: > 0 } requestId || parameters.Request is not { } request)
            {
                return;
            }

            var url = request.Url;
            var method = request.Method;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(method))
            {
                return;
            }

            if (parameters.RedirectResponse is not null &&
                _networkRequests.Remove(requestId, out var redirectedRequest))
            {
                UpdateResponse(redirectedRequest, parameters.RedirectResponse);
                LogCompletedRequest(redirectedRequest, parameters.Timestamp, encodedDataLength: null, redirectUrl: url);
            }

            _networkRequests[requestId] = new BrowserNetworkRequestState
            {
                Method = method,
                ResourceType = NormalizeResourceType(parameters.Type),
                StartTimestamp = parameters.Timestamp,
                Url = url
            };
        }

        private void TrackResponseReceived(BrowserLogsResponseReceivedParameters parameters)
        {
            if (parameters.RequestId is not { Length: > 0 } requestId ||
                !_networkRequests.TryGetValue(requestId, out var request))
            {
                return;
            }

            if (parameters.Response is not null)
            {
                UpdateResponse(request, parameters.Response);
            }

            if (parameters.Type is { Length: > 0 } resourceType)
            {
                request.ResourceType = NormalizeResourceType(resourceType);
            }
        }

        private void TrackRequestCompleted(BrowserLogsLoadingFinishedParameters parameters)
        {
            if (parameters.RequestId is not { Length: > 0 } requestId ||
                !_networkRequests.Remove(requestId, out var request))
            {
                return;
            }

            LogCompletedRequest(request, parameters.Timestamp, parameters.EncodedDataLength, redirectUrl: null);
        }

        private void TrackRequestFailed(BrowserLogsLoadingFailedParameters parameters)
        {
            if (parameters.RequestId is not { Length: > 0 } requestId ||
                !_networkRequests.Remove(requestId, out var request))
            {
                return;
            }

            var details = new List<string>();

            if (FormatDuration(request.StartTimestamp, parameters.Timestamp) is { Length: > 0 } duration)
            {
                details.Add(duration);
            }

            if (parameters.Canceled == true)
            {
                details.Add("canceled");
            }

            if (!string.IsNullOrEmpty(parameters.BlockedReason))
            {
                details.Add($"blocked={parameters.BlockedReason}");
            }

            WriteLog(LogLevel.Warning, $"[network.{request.ResourceType}] {request.Method} {request.Url} failed: {parameters.ErrorText ?? "Request failed"}{FormatDetails(details)}");
        }

        private void LogCompletedRequest(BrowserNetworkRequestState request, double? completedTimestamp, double? encodedDataLength, string? redirectUrl)
        {
            var details = new List<string>();

            if (FormatDuration(request.StartTimestamp, completedTimestamp) is { Length: > 0 } duration)
            {
                details.Add(duration);
            }

            if (encodedDataLength is > 0)
            {
                details.Add($"{Math.Round(encodedDataLength.Value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)} B");
            }

            if (request.FromDiskCache == true)
            {
                details.Add("disk-cache");
            }

            if (request.FromServiceWorker == true)
            {
                details.Add("service-worker");
            }

            if (!string.IsNullOrEmpty(redirectUrl))
            {
                details.Add($"redirect to {redirectUrl}");
            }

            var statusText = request.StatusCode is int statusCode
                ? string.IsNullOrEmpty(request.StatusText)
                    ? $" -> {statusCode}"
                    : $" -> {statusCode} {request.StatusText}"
                : redirectUrl is null
                    ? " completed"
                    : " -> redirect";

            WriteLog(LogLevel.Information, $"[network.{request.ResourceType}] {request.Method} {request.Url}{statusText}{FormatDetails(details)}");
        }

        private static void UpdateResponse(BrowserNetworkRequestState request, BrowserLogsResponse response)
        {
            request.Url = response.Url ?? request.Url;
            request.StatusCode = response.Status;
            request.StatusText = response.StatusText;
            request.FromDiskCache = response.FromDiskCache;
            request.FromServiceWorker = response.FromServiceWorker;
        }

        private void WriteLog(LogLevel logLevel, string message)
        {
            var sessionMessage = $"[{_sessionId}] {message}";

            switch (logLevel)
            {
                case LogLevel.Error:
                case LogLevel.Critical:
                    _resourceLogger.LogError("{Message}", sessionMessage);
                    break;
                case LogLevel.Warning:
                    _resourceLogger.LogWarning("{Message}", sessionMessage);
                    break;
                case LogLevel.Debug:
                case LogLevel.Trace:
                    _resourceLogger.LogDebug("{Message}", sessionMessage);
                    break;
                default:
                    _resourceLogger.LogInformation("{Message}", sessionMessage);
                    break;
            }
        }

        private static string NormalizeResourceType(string? resourceType) =>
            string.IsNullOrEmpty(resourceType)
                ? "request"
                : resourceType.ToLowerInvariant();

        private static string? FormatDuration(double? startTimestamp, double? endTimestamp)
        {
            if (startTimestamp is null || endTimestamp is null || endTimestamp < startTimestamp)
            {
                return null;
            }

            var durationMs = Math.Round((endTimestamp.Value - startTimestamp.Value) * 1000, MidpointRounding.AwayFromZero);
            return $"{durationMs.ToString(CultureInfo.InvariantCulture)} ms";
        }

        private static string FormatDetails(IReadOnlyList<string> details) =>
            details.Count > 0
                ? $" ({string.Join(", ", details)})"
                : string.Empty;

        private static LogLevel MapConsoleLevel(string level) => level switch
        {
            "error" or "assert" => LogLevel.Error,
            "warning" or "warn" => LogLevel.Warning,
            "debug" => LogLevel.Debug,
            _ => LogLevel.Information
        };

        private static LogLevel MapLogEntryLevel(string level) => level switch
        {
            "error" => LogLevel.Error,
            "warning" => LogLevel.Warning,
            "verbose" => LogLevel.Debug,
            _ => LogLevel.Information
        };

        private static string FormatRemoteObject(BrowserLogsProtocolRemoteObject remoteObject)
        {
            if (remoteObject.Value is BrowserLogsProtocolValue value)
            {
                return value switch
                {
                    BrowserLogsProtocolStringValue stringValue => stringValue.Value,
                    BrowserLogsProtocolNullValue => "null",
                    BrowserLogsProtocolBooleanValue booleanValue => booleanValue.Value ? bool.TrueString : bool.FalseString,
                    BrowserLogsProtocolNumberValue numberValue => numberValue.RawValue,
                    _ => FormatStructuredValue(value)
                };
            }

            if (!string.IsNullOrEmpty(remoteObject.UnserializableValue))
            {
                return remoteObject.UnserializableValue;
            }

            return remoteObject.Description ?? string.Empty;
        }

        private static string FormatStructuredValue(BrowserLogsProtocolValue value)
        {
            var builder = new StringBuilder();
            AppendStructuredValue(builder, value);
            return builder.ToString();
        }

        private static void AppendStructuredValue(StringBuilder builder, BrowserLogsProtocolValue value)
        {
            switch (value)
            {
                case BrowserLogsProtocolArrayValue arrayValue:
                    builder.Append('[');
                    for (var i = 0; i < arrayValue.Items.Count; i++)
                    {
                        if (i > 0)
                        {
                            builder.Append(',');
                        }

                        AppendStructuredValue(builder, arrayValue.Items[i]);
                    }

                    builder.Append(']');
                    break;
                case BrowserLogsProtocolBooleanValue booleanValue:
                    builder.Append(booleanValue.Value ? "true" : "false");
                    break;
                case BrowserLogsProtocolNullValue:
                    builder.Append("null");
                    break;
                case BrowserLogsProtocolNumberValue numberValue:
                    builder.Append(numberValue.RawValue);
                    break;
                case BrowserLogsProtocolObjectValue objectValue:
                    builder.Append('{');
                    var needsComma = false;
                    foreach (var (propertyName, propertyValue) in objectValue.Properties)
                    {
                        if (needsComma)
                        {
                            builder.Append(',');
                        }

                        needsComma = true;
                        AppendEscapedString(builder, propertyName);
                        builder.Append(':');
                        AppendStructuredValue(builder, propertyValue);
                    }

                    builder.Append('}');
                    break;
                case BrowserLogsProtocolStringValue stringValue:
                    AppendEscapedString(builder, stringValue.Value);
                    break;
            }
        }

        private static void AppendEscapedString(StringBuilder builder, string value)
        {
            builder.Append('"');

            foreach (var character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (char.IsControl(character))
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            builder.Append('"');
        }

        private static string GetLocationSuffix(BrowserLogsSourceLocation details)
        {
            var url = details.Url;
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }

            var lineNumber = details.LineNumber + 1;
            var columnNumber = details.ColumnNumber + 1;

            if (lineNumber > 0 && columnNumber > 0)
            {
                return $" ({url}:{lineNumber}:{columnNumber})";
            }

            return $" ({url})";
        }

        private sealed class BrowserNetworkRequestState
        {
            public bool? FromDiskCache { get; set; }

            public bool? FromServiceWorker { get; set; }

            public required string Method { get; set; }

            public required string ResourceType { get; set; }

            public double? StartTimestamp { get; set; }

            public int? StatusCode { get; set; }

            public string? StatusText { get; set; }

            public required string Url { get; set; }
        }
    }

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

    private sealed class RunningSession : IBrowserLogsRunningSession
    {
        private static readonly TimeSpan s_browserEndpointTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan s_browserShutdownTimeout = TimeSpan.FromSeconds(5);
        private static readonly TimeSpan s_connectionRecoveryDelay = TimeSpan.FromMilliseconds(200);
        private static readonly TimeSpan s_connectionRecoveryTimeout = TimeSpan.FromSeconds(5);

        private readonly BrowserEventLogger _eventLogger;
        private readonly BrowserConnectionDiagnosticsLogger _connectionDiagnostics;
        private readonly ILogger<BrowserLogsSessionManager> _logger;
        private readonly BrowserLogsResource _resource;
        private readonly ILogger _resourceLogger;
        private readonly string _resourceName;
        private readonly string _sessionId;
        private readonly CancellationTokenSource _stopCts = new();
        private readonly TimeProvider _timeProvider;
        private readonly Uri _url;
        private readonly TempDirectory _userDataDirectory;

        private string? _browserExecutable;
        private Uri? _browserEndpoint;
        private Task<ProcessResult>? _browserProcessTask;
        private IAsyncDisposable? _browserProcessLifetime;
        private ChromeDevToolsConnection? _connection;
        private Task<BrowserSessionResult>? _completion;
        private int _cleanupState;
        private int? _processId;
        private string? _targetId;
        private string? _targetSessionId;

        private RunningSession(
            BrowserLogsResource resource,
            string resourceName,
            string sessionId,
            Uri url,
            TempDirectory userDataDirectory,
            ILogger resourceLogger,
            ILogger<BrowserLogsSessionManager> logger,
            TimeProvider timeProvider)
        {
            _eventLogger = new BrowserEventLogger(sessionId, resourceLogger);
            _connectionDiagnostics = new BrowserConnectionDiagnosticsLogger(sessionId, resourceLogger);
            _logger = logger;
            _resource = resource;
            _resourceLogger = resourceLogger;
            _resourceName = resourceName;
            _sessionId = sessionId;
            _timeProvider = timeProvider;
            _url = url;
            _userDataDirectory = userDataDirectory;
        }

        public string SessionId => _sessionId;

        public string BrowserExecutable => _browserExecutable ?? throw new InvalidOperationException("Browser executable is not available before the session starts.");

        public int ProcessId => _processId ?? throw new InvalidOperationException("Browser process has not started.");

        public DateTime StartedAt { get; private set; }

        private Task<BrowserSessionResult> Completion => _completion ?? throw new InvalidOperationException("Session has not been started.");

        public static async Task<RunningSession> StartAsync(
            BrowserLogsResource resource,
            string resourceName,
            string sessionId,
            Uri url,
            IFileSystemService fileSystemService,
            ILogger resourceLogger,
            ILogger<BrowserLogsSessionManager> logger,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
        {
            var userDataDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-browser-logs");
            var session = new RunningSession(resource, resourceName, sessionId, url, userDataDirectory, resourceLogger, logger, timeProvider);

            try
            {
                await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
                session._completion = session.MonitorAsync();
                return session;
            }
            catch
            {
                await session.CleanupAsync().ConfigureAwait(false);
                throw;
            }
        }

        public void StartCompletionObserver(Func<int, Exception?, Task> onCompleted)
        {
            _ = ObserveCompletionAsync(onCompleted);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopCts.Cancel();

            if (_connection is not null)
            {
                try
                {
                    await _connection.CloseBrowserAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to close tracked browser for resource '{ResourceName}' via CDP.", _resourceName);
                }
            }

            if (_browserProcessTask is { IsCompleted: false } browserProcessTask)
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(s_browserShutdownTimeout);

                try
                {
                    await browserProcessTask.WaitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await DisposeBrowserProcessAsync().ConfigureAwait(false);
                }
            }

            try
            {
                await Completion.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _browserExecutable = ResolveBrowserExecutable(_resource.Browser);
            if (_browserExecutable is null)
            {
                throw new InvalidOperationException($"Unable to locate browser '{_resource.Browser}'. Specify an installed Chromium-based browser or an explicit executable path.");
            }

            var devToolsActivePortFilePath = GetDevToolsActivePortFilePath();
            await StartBrowserProcessAsync(cancellationToken).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Started tracked browser process '{BrowserExecutable}'.", _sessionId, _browserExecutable);
            _resourceLogger.LogInformation("[{SessionId}] Waiting for tracked browser debug endpoint metadata in '{DevToolsActivePortFilePath}'.", _sessionId, devToolsActivePortFilePath);

            try
            {
                _browserEndpoint = await WaitForBrowserEndpointAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _connectionDiagnostics.LogSetupFailure("Discovering the tracked browser debug endpoint", ex);
                throw;
            }

            _resourceLogger.LogInformation("[{SessionId}] Discovered tracked browser debug endpoint '{Endpoint}'.", _sessionId, _browserEndpoint);

            try
            {
                await ConnectAsync(createTarget: true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _connectionDiagnostics.LogSetupFailure("Setting up the tracked browser debug connection", ex);
                throw;
            }

            _resourceLogger.LogInformation("[{SessionId}] Tracking browser console logs for '{Url}'.", _sessionId, _url);
        }

        private async Task StartBrowserProcessAsync(CancellationToken cancellationToken)
        {
            var processStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var browserExecutable = _browserExecutable ?? throw new InvalidOperationException("Browser executable was not resolved.");
            var processSpec = new ProcessSpec(browserExecutable)
            {
                Arguments = BuildBrowserArguments(),
                InheritEnv = true,
                OnErrorData = error => _logger.LogTrace("[{SessionId}] Tracked browser stderr: {Line}", _sessionId, error),
                OnOutputData = output => _logger.LogTrace("[{SessionId}] Tracked browser stdout: {Line}", _sessionId, output),
                OnStart = processId =>
                {
                    _processId = processId;
                    processStarted.TrySetResult(processId);
                },
                ThrowOnNonZeroReturnCode = false
            };

            var (browserProcessTask, browserProcessLifetime) = ProcessUtil.Run(processSpec);
            _browserProcessTask = browserProcessTask;
            _browserProcessLifetime = browserProcessLifetime;
            StartedAt = _timeProvider.GetUtcNow().UtcDateTime;

            await processStarted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        private string BuildBrowserArguments()
        {
            return BuildCommandLine(
            [
                $"--user-data-dir={_userDataDirectory.Path}",
                "--remote-debugging-port=0",
                "--no-first-run",
                "--no-default-browser-check",
                "--new-window",
                "--allow-insecure-localhost",
                "--ignore-certificate-errors",
                "about:blank"
            ]);
        }

        private async Task ConnectAsync(bool createTarget, CancellationToken cancellationToken)
        {
            var browserEndpoint = _browserEndpoint ?? throw new InvalidOperationException("Browser debugging endpoint is not available.");

            await DisposeConnectionAsync().ConfigureAwait(false);

            _connection = await ExecuteConnectionStageAsync(
                "Connecting to the tracked browser debug endpoint",
                () => ChromeDevToolsConnection.ConnectAsync(browserEndpoint, HandleEventAsync, _logger, cancellationToken)).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Connected to the tracked browser debug endpoint.", _sessionId);

            if (createTarget)
            {
                var createTargetResult = await ExecuteConnectionStageAsync(
                    "Creating the tracked browser target",
                    () => _connection.CreateTargetAsync(cancellationToken)).ConfigureAwait(false);
                _targetId = createTargetResult.TargetId
                    ?? throw new InvalidOperationException("Browser target creation did not return a target id.");
                _resourceLogger.LogInformation("[{SessionId}] Created tracked browser target '{TargetId}'.", _sessionId, _targetId);
            }

            if (_targetId is null)
            {
                throw new InvalidOperationException("Tracked browser target id is not available.");
            }

            var attachToTargetResult = await ExecuteConnectionStageAsync(
                "Attaching to the tracked browser target",
                () => _connection.AttachToTargetAsync(_targetId, cancellationToken)).ConfigureAwait(false);
            _targetSessionId = attachToTargetResult.SessionId
                ?? throw new InvalidOperationException("Browser target attachment did not return a session id.");
            _resourceLogger.LogInformation("[{SessionId}] Attached to the tracked browser target.", _sessionId);

            await ExecuteConnectionStageAsync(
                "Enabling tracked browser instrumentation",
                () => _connection.EnablePageInstrumentationAsync(_targetSessionId, cancellationToken)).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Enabled tracked browser logging.", _sessionId);

            if (createTarget)
            {
                await ExecuteConnectionStageAsync(
                    "Navigating the tracked browser target",
                    () => _connection.NavigateAsync(_targetSessionId, _url, cancellationToken)).ConfigureAwait(false);
                _resourceLogger.LogInformation("[{SessionId}] Navigated tracked browser to '{Url}'.", _sessionId, _url);
            }
        }

        private static async Task<TResult> ExecuteConnectionStageAsync<TResult>(string stage, Func<Task<TResult>> action)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{stage} failed.", ex);
            }
        }

        private static async Task ExecuteConnectionStageAsync(string stage, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException($"{stage} failed.", ex);
            }
        }

        private async Task<BrowserSessionResult> MonitorAsync()
        {
            try
            {
                var browserProcessTask = _browserProcessTask ?? throw new InvalidOperationException("Browser process task is not available.");

                while (true)
                {
                    var connection = _connection ?? throw new InvalidOperationException("Tracked browser debug connection is not available.");
                    var completedTask = await Task.WhenAny(browserProcessTask, connection.Completion).ConfigureAwait(false);

                    if (completedTask == browserProcessTask)
                    {
                        var processResult = await browserProcessTask.ConfigureAwait(false);
                        if (!_stopCts.IsCancellationRequested)
                        {
                            _resourceLogger.LogInformation("[{SessionId}] Tracked browser exited with code {ExitCode}.", _sessionId, processResult.ExitCode);
                        }

                        return new BrowserSessionResult(processResult.ExitCode, Error: null);
                    }

                    Exception? connectionError = null;
                    try
                    {
                        await connection.Completion.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        connectionError = ex;
                    }

                    if (_stopCts.IsCancellationRequested)
                    {
                        var processResult = await browserProcessTask.ConfigureAwait(false);
                        return new BrowserSessionResult(processResult.ExitCode, Error: null);
                    }

                    connectionError ??= new InvalidOperationException("The tracked browser debug connection closed without reporting a reason.");

                    if (await TryReconnectAsync(connectionError).ConfigureAwait(false))
                    {
                        continue;
                    }

                    await DisposeBrowserProcessAsync().ConfigureAwait(false);

                    var exitResult = await browserProcessTask.ConfigureAwait(false);
                    return new BrowserSessionResult(exitResult.ExitCode, connectionError);
                }
            }
            finally
            {
                await CleanupAsync().ConfigureAwait(false);
            }
        }

        private async Task<bool> TryReconnectAsync(Exception? connectionError)
        {
            if (_browserEndpoint is null || _targetId is null)
            {
                return false;
            }

            connectionError ??= new InvalidOperationException("The tracked browser debug connection closed without reporting a reason.");
            _connectionDiagnostics.LogConnectionLost(connectionError);
            await DisposeConnectionAsync().ConfigureAwait(false);

            var reconnectDeadline = _timeProvider.GetUtcNow() + s_connectionRecoveryTimeout;
            Exception? lastError = connectionError;
            var attempt = 0;

            while (!_stopCts.IsCancellationRequested && _timeProvider.GetUtcNow() < reconnectDeadline)
            {
                if (_browserProcessTask?.IsCompleted == true)
                {
                    return false;
                }

                try
                {
                    attempt++;
                    await ConnectAsync(createTarget: false, _stopCts.Token).ConfigureAwait(false);
                    _resourceLogger.LogInformation("[{SessionId}] Reconnected tracked browser debug connection.", _sessionId);
                    return true;
                }
                catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
                {
                    return false;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    _connectionDiagnostics.LogReconnectAttemptFailed(attempt, ex);
                    await DisposeConnectionAsync().ConfigureAwait(false);
                }

                try
                {
                    await Task.Delay(s_connectionRecoveryDelay, _stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopCts.IsCancellationRequested)
                {
                    return false;
                }
            }

            if (lastError is not null)
            {
                _connectionDiagnostics.LogReconnectFailed(lastError);
                _logger.LogDebug(lastError, "Timed out reconnecting tracked browser debug session for resource '{ResourceName}' and session '{SessionId}'.", _resourceName, _sessionId);
            }

            return false;
        }

        private ValueTask HandleEventAsync(BrowserLogsProtocolEvent protocolEvent)
        {
            if (!string.Equals(protocolEvent.SessionId, _targetSessionId, StringComparison.Ordinal))
            {
                return ValueTask.CompletedTask;
            }

            _eventLogger.HandleEvent(protocolEvent);
            return ValueTask.CompletedTask;
        }

        private async Task ObserveCompletionAsync(Func<int, Exception?, Task> onCompleted)
        {
            try
            {
                var result = await Completion.ConfigureAwait(false);
                await onCompleted(result.ExitCode, result.Error).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tracked browser completion observer failed for resource '{ResourceName}' and session '{SessionId}'.", _resourceName, _sessionId);
            }
        }

        private async Task CleanupAsync()
        {
            if (Interlocked.Exchange(ref _cleanupState, 1) != 0)
            {
                return;
            }

            await DisposeConnectionAsync().ConfigureAwait(false);
            await DisposeBrowserProcessAsync().ConfigureAwait(false);
            _stopCts.Dispose();
            _userDataDirectory.Dispose();
        }

        private async Task DisposeBrowserProcessAsync()
        {
            var browserProcessLifetime = _browserProcessLifetime;
            _browserProcessLifetime = null;

            if (browserProcessLifetime is not null)
            {
                await browserProcessLifetime.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task DisposeConnectionAsync()
        {
            var connection = _connection;
            _connection = null;

            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async Task<Uri> WaitForBrowserEndpointAsync(CancellationToken cancellationToken)
        {
            var devToolsActivePortFilePath = GetDevToolsActivePortFilePath();
            var timeoutAt = _timeProvider.GetUtcNow() + s_browserEndpointTimeout;

            while (_timeProvider.GetUtcNow() < timeoutAt)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (File.Exists(devToolsActivePortFilePath))
                    {
                        var contents = await File.ReadAllTextAsync(devToolsActivePortFilePath, cancellationToken).ConfigureAwait(false);
                        if (TryParseBrowserDebugEndpoint(contents) is { } browserEndpoint)
                        {
                            return browserEndpoint;
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException($"Timed out waiting for the tracked browser to write '{devToolsActivePortFilePath}'.");
        }

        private string GetDevToolsActivePortFilePath()
        {
            return Path.Combine(_userDataDirectory.Path, "DevToolsActivePort");
        }

        private static string? ResolveBrowserExecutable(string browser)
        {
            if (Path.IsPathRooted(browser) && File.Exists(browser))
            {
                return browser;
            }

            foreach (var candidate in GetBrowserCandidates(browser))
            {
                if (Path.IsPathRooted(candidate))
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                else if (PathLookupHelper.FindFullPathFromPath(candidate) is { } resolvedPath)
                {
                    return resolvedPath;
                }
            }

            return PathLookupHelper.FindFullPathFromPath(browser);
        }

        private static IEnumerable<string> GetBrowserCandidates(string browser)
        {
            if (OperatingSystem.IsMacOS())
            {
                return browser.ToLowerInvariant() switch
                {
                    "msedge" or "edge" =>
                    [
                        "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                        "msedge"
                    ],
                    "chrome" or "google-chrome" =>
                    [
                        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                        "google-chrome",
                        "chrome"
                    ],
                    _ => [browser]
                };
            }

            if (OperatingSystem.IsWindows())
            {
                var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

                return browser.ToLowerInvariant() switch
                {
                    "msedge" or "edge" =>
                    [
                        Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                        Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                        "msedge.exe"
                    ],
                    "chrome" or "google-chrome" =>
                    [
                        Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                        Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                        "chrome.exe"
                    ],
                    _ => [browser]
                };
            }

            return browser.ToLowerInvariant() switch
            {
                "msedge" or "edge" => ["microsoft-edge", "microsoft-edge-stable", "msedge"],
                "chrome" or "google-chrome" => ["google-chrome", "google-chrome-stable", "chrome", "chromium-browser", "chromium"],
                _ => [browser]
            };
        }

        private static string BuildCommandLine(IReadOnlyList<string> arguments)
        {
            var builder = new StringBuilder();

            for (var i = 0; i < arguments.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                AppendCommandLineArgument(builder, arguments[i]);
            }

            return builder.ToString();
        }

        // Adapted from dotnet/runtime PasteArguments.AppendArgument so ProcessSpec can safely represent Chromium flags.
        private static void AppendCommandLineArgument(StringBuilder builder, string argument)
        {
            if (argument.Length != 0 && !argument.AsSpan().ContainsAny(' ', '\t', '"'))
            {
                builder.Append(argument);
                return;
            }

            builder.Append('"');

            var index = 0;
            while (index < argument.Length)
            {
                var character = argument[index++];
                if (character == '\\')
                {
                    var backslashCount = 1;
                    while (index < argument.Length && argument[index] == '\\')
                    {
                        index++;
                        backslashCount++;
                    }

                    if (index == argument.Length)
                    {
                        builder.Append('\\', backslashCount * 2);
                    }
                    else if (argument[index] == '"')
                    {
                        builder.Append('\\', backslashCount * 2 + 1);
                        builder.Append('"');
                        index++;
                    }
                    else
                    {
                        builder.Append('\\', backslashCount);
                    }

                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\');
                    builder.Append('"');
                    continue;
                }

                builder.Append(character);
            }

            builder.Append('"');
        }

        private sealed record BrowserSessionResult(int ExitCode, Exception? Error);

        private sealed class ChromeDevToolsConnection : IAsyncDisposable
        {
            private static readonly TimeSpan s_commandTimeout = TimeSpan.FromSeconds(10);

            private readonly CancellationTokenSource _disposeCts = new();
            private readonly Func<BrowserLogsProtocolEvent, ValueTask> _eventHandler;
            private readonly ILogger<BrowserLogsSessionManager> _logger;
            private readonly ConcurrentDictionary<long, IPendingCommand> _pendingCommands = new();
            private readonly Task _receiveLoop;
            private readonly SemaphoreSlim _sendLock = new(1, 1);
            private readonly ClientWebSocket _webSocket;
            private long _nextCommandId;

            private ChromeDevToolsConnection(ClientWebSocket webSocket, Func<BrowserLogsProtocolEvent, ValueTask> eventHandler, ILogger<BrowserLogsSessionManager> logger)
            {
                _eventHandler = eventHandler;
                _logger = logger;
                _webSocket = webSocket;
                _receiveLoop = Task.Run(ReceiveLoopAsync);
            }

            public Task Completion => _receiveLoop;

            public static async Task<ChromeDevToolsConnection> ConnectAsync(
                Uri webSocketUri,
                Func<BrowserLogsProtocolEvent, ValueTask> eventHandler,
                ILogger<BrowserLogsSessionManager> logger,
                CancellationToken cancellationToken)
            {
                var webSocket = new ClientWebSocket();
                webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                await webSocket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);
                return new ChromeDevToolsConnection(webSocket, eventHandler, logger);
            }

            public Task<BrowserLogsCreateTargetResult> CreateTargetAsync(CancellationToken cancellationToken)
            {
                return SendCommandAsync(
                    BrowserLogsProtocol.TargetCreateTargetMethod,
                    sessionId: null,
                    static writer => writer.WriteString("url", "about:blank"),
                    BrowserLogsProtocol.ParseCreateTargetResponse,
                    cancellationToken);
            }

            public Task<BrowserLogsAttachToTargetResult> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
            {
                return SendCommandAsync(
                    BrowserLogsProtocol.TargetAttachToTargetMethod,
                    sessionId: null,
                    writer =>
                    {
                        writer.WriteString("targetId", targetId);
                        writer.WriteBoolean("flatten", true);
                    },
                    BrowserLogsProtocol.ParseAttachToTargetResponse,
                    cancellationToken);
            }

            public async Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
            {
                await SendCommandAsync(BrowserLogsProtocol.RuntimeEnableMethod, sessionId, writeParameters: null, BrowserLogsProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
                await SendCommandAsync(BrowserLogsProtocol.LogEnableMethod, sessionId, writeParameters: null, BrowserLogsProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
                await SendCommandAsync(BrowserLogsProtocol.PageEnableMethod, sessionId, writeParameters: null, BrowserLogsProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
                await SendCommandAsync(BrowserLogsProtocol.NetworkEnableMethod, sessionId, writeParameters: null, BrowserLogsProtocol.ParseCommandAckResponse, cancellationToken).ConfigureAwait(false);
            }

            public Task<BrowserLogsCommandAck> NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken)
            {
                return SendCommandAsync(
                    BrowserLogsProtocol.PageNavigateMethod,
                    sessionId,
                    writer => writer.WriteString("url", url.ToString()),
                    BrowserLogsProtocol.ParseCommandAckResponse,
                    cancellationToken);
            }

            public Task<BrowserLogsCommandAck> CloseBrowserAsync(CancellationToken cancellationToken)
            {
                return SendCommandAsync(
                    BrowserLogsProtocol.BrowserCloseMethod,
                    sessionId: null,
                    writeParameters: null,
                    BrowserLogsProtocol.ParseCommandAckResponse,
                    cancellationToken);
            }

            public async ValueTask DisposeAsync()
            {
                _disposeCts.Cancel();

                try
                {
                    if (_webSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposed", CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch
                {
                    _webSocket.Abort();
                }
                finally
                {
                    _webSocket.Dispose();
                }

                try
                {
                    await _receiveLoop.ConfigureAwait(false);
                }
                catch
                {
                }

                _disposeCts.Dispose();
                _sendLock.Dispose();
            }

            private async Task<TResult> SendCommandAsync<TResult>(
                string method,
                string? sessionId,
                Action<Utf8JsonWriter>? writeParameters,
                ResponseParser<TResult> parseResponse,
                CancellationToken cancellationToken)
            {
                var commandId = Interlocked.Increment(ref _nextCommandId);
                var pendingCommand = new PendingCommand<TResult>(parseResponse);
                _pendingCommands[commandId] = pendingCommand;

                try
                {
                    using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
                    sendCts.CancelAfter(s_commandTimeout);

                    using var registration = sendCts.Token.Register(static state =>
                    {
                        ((IPendingCommand)state!).SetCanceled();
                    }, pendingCommand);

                    var payload = BrowserLogsProtocol.CreateCommandFrame(commandId, method, sessionId, writeParameters);
                    _logger.LogTrace("Tracked browser protocol -> {Frame}", BrowserLogsProtocol.DescribeFrame(payload));

                    var lockHeld = false;
                    try
                    {
                        await _sendLock.WaitAsync(sendCts.Token).ConfigureAwait(false);
                        lockHeld = true;
                        await _webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, sendCts.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        if (lockHeld)
                        {
                            _sendLock.Release();
                        }
                    }

                    return await pendingCommand.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_disposeCts.IsCancellationRequested)
                {
                    throw new TimeoutException($"Timed out waiting for a tracked browser protocol response to '{method}'.");
                }
                finally
                {
                    _pendingCommands.TryRemove(commandId, out _);
                }
            }

            private async Task ReceiveLoopAsync()
            {
                var buffer = new byte[16 * 1024];
                using var messageBuffer = new MemoryStream();
                Exception? terminalException = null;

                try
                {
                    while (!_disposeCts.IsCancellationRequested && _webSocket.State is WebSocketState.Open or WebSocketState.CloseSent)
                    {
                        var result = await _webSocket.ReceiveAsync(buffer, _disposeCts.Token).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            terminalException = CreateUnexpectedConnectionClosureException(result);
                            break;
                        }

                        messageBuffer.Write(buffer, 0, result.Count);
                        if (!result.EndOfMessage)
                        {
                            continue;
                        }

                        var frame = messageBuffer.ToArray();
                        messageBuffer.SetLength(0);

                        _logger.LogTrace("Tracked browser protocol <- {Frame}", BrowserLogsProtocol.DescribeFrame(frame));

                        try
                        {
                            var header = BrowserLogsProtocol.ParseMessageHeader(frame);
                            if (header.Id is long commandId)
                            {
                                if (_pendingCommands.TryGetValue(commandId, out var pendingCommand))
                                {
                                    pendingCommand.SetResult(frame);
                                }

                                continue;
                            }

                            if (header.Method is not null && BrowserLogsProtocol.ParseEvent(header, frame) is { } protocolEvent)
                            {
                                await _eventHandler(protocolEvent).ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            terminalException = new InvalidOperationException(
                                $"Tracked browser protocol receive loop failed while processing frame {BrowserLogsProtocol.DescribeFrame(frame)}.",
                                ex);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    terminalException = ex;
                }
                finally
                {
                    terminalException ??= new InvalidOperationException("Browser debug connection closed.");

                    foreach (var pendingCommand in _pendingCommands.Values)
                    {
                        pendingCommand.SetException(terminalException);
                    }
                }

                if (!_disposeCts.IsCancellationRequested)
                {
                    throw terminalException ?? new InvalidOperationException("Browser debug connection closed.");
                }
            }

            private static InvalidOperationException CreateUnexpectedConnectionClosureException(WebSocketReceiveResult result)
            {
                if (result.CloseStatus is { } closeStatus)
                {
                    if (!string.IsNullOrWhiteSpace(result.CloseStatusDescription))
                    {
                        return new InvalidOperationException($"Browser debug connection closed by the remote endpoint with status '{closeStatus}' ({(int)closeStatus}): {result.CloseStatusDescription}");
                    }

                    return new InvalidOperationException($"Browser debug connection closed by the remote endpoint with status '{closeStatus}' ({(int)closeStatus}).");
                }

                return new InvalidOperationException("Browser debug connection closed by the remote endpoint without a close status.");
            }

            private interface IPendingCommand
            {
                void SetCanceled();

                void SetException(Exception exception);

                void SetResult(ReadOnlyMemory<byte> framePayload);
            }

            private delegate TResult ResponseParser<TResult>(ReadOnlySpan<byte> framePayload);

            private sealed class PendingCommand<TResult>(ResponseParser<TResult> parseResponse) : IPendingCommand
            {
                private readonly ResponseParser<TResult> _parseResponse = parseResponse;
                private readonly TaskCompletionSource<TResult> _taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

                public Task<TResult> Task => _taskCompletionSource.Task;

                public void SetCanceled()
                {
                    _taskCompletionSource.TrySetCanceled();
                }

                public void SetException(Exception exception)
                {
                    _taskCompletionSource.TrySetException(exception);
                }

                public void SetResult(ReadOnlyMemory<byte> framePayload)
                {
                    try
                    {
                        _taskCompletionSource.TrySetResult(_parseResponse(framePayload.Span));
                    }
                    catch (Exception ex)
                    {
                        _taskCompletionSource.TrySetException(ex);
                    }
                }
            }
        }
    }

    internal static Uri? TryParseBrowserDebugEndpoint(string activePortFileContents)
    {
        if (string.IsNullOrWhiteSpace(activePortFileContents))
        {
            return null;
        }

        using var reader = new StringReader(activePortFileContents);
        var portLine = reader.ReadLine();
        var browserPathLine = reader.ReadLine();

        if (!int.TryParse(portLine, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port <= 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(browserPathLine))
        {
            return null;
        }

        if (!browserPathLine.StartsWith("/", StringComparison.Ordinal))
        {
            browserPathLine = $"/{browserPathLine}";
        }

        return Uri.TryCreate($"ws://127.0.0.1:{port}{browserPathLine}", UriKind.Absolute, out var browserEndpoint)
            ? browserEndpoint
            : null;
    }

    private sealed class BrowserLogsRunningSessionFactory : IBrowserLogsRunningSessionFactory
    {
        private readonly IFileSystemService _fileSystemService;
        private readonly ILogger<BrowserLogsSessionManager> _logger;
        private readonly TimeProvider _timeProvider;

        public BrowserLogsRunningSessionFactory(IFileSystemService fileSystemService, ILogger<BrowserLogsSessionManager> logger, TimeProvider timeProvider)
        {
            _fileSystemService = fileSystemService;
            _logger = logger;
            _timeProvider = timeProvider;
        }

        public async Task<IBrowserLogsRunningSession> StartSessionAsync(
            BrowserLogsResource resource,
            string resourceName,
            Uri url,
            string sessionId,
            ILogger resourceLogger,
            CancellationToken cancellationToken)
        {
            return await RunningSession.StartAsync(
                resource,
                resourceName,
                sessionId,
                url,
                _fileSystemService,
                resourceLogger,
                _logger,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ResourceSessionState
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public Dictionary<string, ActiveBrowserSession> ActiveSessions { get; } = new(StringComparer.Ordinal);

        public int TotalSessionsLaunched { get; set; }

        public string? LastSessionId { get; set; }

        public string? LastTargetUrl { get; set; }

        public string? LastBrowserExecutable { get; set; }
    }

    private sealed record ActiveBrowserSession(
        string SessionId,
        string BrowserExecutable,
        int ProcessId,
        DateTime StartedAt,
        Uri TargetUrl,
        IBrowserLogsRunningSession Session);

    private sealed record PendingBrowserSession(
        string SessionId,
        DateTime StartedAt,
        Uri TargetUrl);
}
