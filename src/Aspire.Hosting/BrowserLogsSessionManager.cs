// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
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
            new BrowserLogsRunningSessionFactory(fileSystemService, logger))
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

        public void HandleEvent(string method, JsonElement parameters)
        {
            switch (method)
            {
                case "Runtime.consoleAPICalled":
                    LogConsoleMessage(parameters);
                    break;
                case "Runtime.exceptionThrown":
                    LogUnhandledException(parameters);
                    break;
                case "Log.entryAdded":
                    LogEntryAdded(parameters);
                    break;
                case "Network.requestWillBeSent":
                    TrackRequestStarted(parameters);
                    break;
                case "Network.responseReceived":
                    TrackResponseReceived(parameters);
                    break;
                case "Network.loadingFinished":
                    TrackRequestCompleted(parameters);
                    break;
                case "Network.loadingFailed":
                    TrackRequestFailed(parameters);
                    break;
            }
        }

        private void LogConsoleMessage(JsonElement parameters)
        {
            var level = TryGetString(parameters, "type") ?? "log";
            var message = parameters.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
                ? string.Join(" ", argsElement.EnumerateArray().Select(FormatRemoteObject).Where(static value => !string.IsNullOrEmpty(value)))
                : string.Empty;

            WriteLog(MapConsoleLevel(level), $"[console.{level}] {message}".TrimEnd());
        }

        private void LogUnhandledException(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("exceptionDetails", out var exceptionDetails))
            {
                return;
            }

            var message = exceptionDetails.TryGetProperty("exception", out var exception) && exception.TryGetProperty("description", out var description)
                ? description.GetString()
                : exceptionDetails.TryGetProperty("text", out var text)
                    ? text.GetString()
                    : "Unhandled browser exception";

            var location = GetLocationSuffix(exceptionDetails);
            WriteLog(LogLevel.Error, $"[exception] {message}{location}");
        }

        private void LogEntryAdded(JsonElement parameters)
        {
            if (!parameters.TryGetProperty("entry", out var entry))
            {
                return;
            }

            var level = TryGetString(entry, "level") ?? "info";
            var text = TryGetString(entry, "text") ?? string.Empty;
            var location = GetLocationSuffix(entry);

            WriteLog(MapLogEntryLevel(level), $"[log.{level}] {text}{location}".TrimEnd());
        }

        private void TrackRequestStarted(JsonElement parameters)
        {
            if (TryGetString(parameters, "requestId") is not { Length: > 0 } requestId)
            {
                return;
            }

            if (!parameters.TryGetProperty("request", out var request))
            {
                return;
            }

            var url = TryGetString(request, "url");
            var method = TryGetString(request, "method");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(method))
            {
                return;
            }

            var startTimestamp = TryGetDouble(parameters, "timestamp");

            if (parameters.TryGetProperty("redirectResponse", out var redirectResponse) &&
                _networkRequests.Remove(requestId, out var redirectedRequest))
            {
                UpdateResponse(redirectedRequest, redirectResponse);
                LogCompletedRequest(redirectedRequest, startTimestamp, encodedDataLength: null, redirectUrl: url);
            }

            var resourceType = NormalizeResourceType(TryGetString(parameters, "type"));
            _networkRequests[requestId] = new BrowserNetworkRequestState
            {
                Method = method,
                Url = url,
                ResourceType = resourceType,
                StartTimestamp = startTimestamp
            };
        }

        private void TrackResponseReceived(JsonElement parameters)
        {
            if (TryGetString(parameters, "requestId") is not { Length: > 0 } requestId ||
                !_networkRequests.TryGetValue(requestId, out var request))
            {
                return;
            }

            if (parameters.TryGetProperty("response", out var response))
            {
                UpdateResponse(request, response);
            }

            if (TryGetString(parameters, "type") is { Length: > 0 } resourceType)
            {
                request.ResourceType = NormalizeResourceType(resourceType);
            }
        }

        private void TrackRequestCompleted(JsonElement parameters)
        {
            if (TryGetString(parameters, "requestId") is not { Length: > 0 } requestId ||
                !_networkRequests.Remove(requestId, out var request))
            {
                return;
            }

            LogCompletedRequest(request, TryGetDouble(parameters, "timestamp"), TryGetDouble(parameters, "encodedDataLength"), redirectUrl: null);
        }

        private void TrackRequestFailed(JsonElement parameters)
        {
            if (TryGetString(parameters, "requestId") is not { Length: > 0 } requestId ||
                !_networkRequests.Remove(requestId, out var request))
            {
                return;
            }

            var errorText = TryGetString(parameters, "errorText") ?? "Request failed";
            var canceled = TryGetBoolean(parameters, "canceled");
            var blockedReason = TryGetString(parameters, "blockedReason");
            var details = new List<string>();

            if (FormatDuration(request.StartTimestamp, TryGetDouble(parameters, "timestamp")) is { Length: > 0 } duration)
            {
                details.Add(duration);
            }

            if (canceled == true)
            {
                details.Add("canceled");
            }

            if (!string.IsNullOrEmpty(blockedReason))
            {
                details.Add($"blocked={blockedReason}");
            }

            WriteLog(LogLevel.Warning, $"[network.{request.ResourceType}] {request.Method} {request.Url} failed: {errorText}{FormatDetails(details)}");
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

        private static void UpdateResponse(BrowserNetworkRequestState request, JsonElement response)
        {
            request.Url = TryGetString(response, "url") ?? request.Url;
            request.StatusCode = TryGetInt32(response, "status");
            request.StatusText = TryGetString(response, "statusText");
            request.FromDiskCache = TryGetBoolean(response, "fromDiskCache");
            request.FromServiceWorker = TryGetBoolean(response, "fromServiceWorker");
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

        private static string? TryGetString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

        private static double? TryGetDouble(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            return property.TryGetDouble(out var value) ? value : null;
        }

        private static int? TryGetInt32(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            if (property.TryGetInt32(out var value))
            {
                return value;
            }

            return property.TryGetDouble(out var doubleValue)
                ? (int)Math.Round(doubleValue, MidpointRounding.AwayFromZero)
                : null;
        }

        private static bool? TryGetBoolean(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var property) && (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
                ? property.GetBoolean()
                : null;

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

        private static string FormatRemoteObject(JsonElement remoteObject)
        {
            if (remoteObject.TryGetProperty("value", out var value))
            {
                return value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Null => "null",
                    JsonValueKind.True => bool.TrueString,
                    JsonValueKind.False => bool.FalseString,
                    _ => value.GetRawText()
                };
            }

            if (remoteObject.TryGetProperty("unserializableValue", out var unserializableValue))
            {
                return unserializableValue.GetString() ?? string.Empty;
            }

            if (remoteObject.TryGetProperty("description", out var description))
            {
                return description.GetString() ?? string.Empty;
            }

            return remoteObject.GetRawText();
        }

        private static string GetLocationSuffix(JsonElement details)
        {
            if (!details.TryGetProperty("url", out var urlElement))
            {
                return string.Empty;
            }

            var url = urlElement.GetString();
            if (string.IsNullOrEmpty(url))
            {
                return string.Empty;
            }

            var lineNumber = details.TryGetProperty("lineNumber", out var lineElement) ? lineElement.GetInt32() + 1 : 0;
            var columnNumber = details.TryGetProperty("columnNumber", out var columnElement) ? columnElement.GetInt32() + 1 : 0;

            if (lineNumber > 0 && columnNumber > 0)
            {
                return $" ({url}:{lineNumber}:{columnNumber})";
            }

            return $" ({url})";
        }

        private sealed class BrowserNetworkRequestState
        {
            public required string Method { get; set; }

            public required string Url { get; set; }

            public required string ResourceType { get; set; }

            public double? StartTimestamp { get; set; }

            public int? StatusCode { get; set; }

            public string? StatusText { get; set; }

            public bool? FromDiskCache { get; set; }

            public bool? FromServiceWorker { get; set; }
        }
    }

    private sealed class RunningSession
        : IBrowserLogsRunningSession
    {
        private static readonly HttpClient s_httpClient = new(new SocketsHttpHandler
        {
            UseProxy = false
        });
        private static readonly JsonSerializerOptions s_jsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly BrowserLogsResource _resource;
        private readonly string _resourceName;
        private readonly string _sessionId;
        private readonly Uri _url;
        private readonly TempDirectory _userDataDirectory;
        private readonly ILogger _resourceLogger;
        private readonly ILogger<BrowserLogsSessionManager> _logger;
        private readonly BrowserEventLogger _eventLogger;
        private readonly CancellationTokenSource _stopCts = new();

        private Process? _process;
        private Task? _stdoutTask;
        private Task? _stderrTask;
        private ChromeDevToolsConnection? _connection;
        private string? _targetSessionId;
        private Task<BrowserSessionResult>? _completion;
        private int _cleanupState;
        private string? _browserExecutable;

        private RunningSession(
            BrowserLogsResource resource,
            string resourceName,
            string sessionId,
            Uri url,
            TempDirectory userDataDirectory,
            ILogger resourceLogger,
            ILogger<BrowserLogsSessionManager> logger)
        {
            _resource = resource;
            _resourceName = resourceName;
            _sessionId = sessionId;
            _url = url;
            _userDataDirectory = userDataDirectory;
            _resourceLogger = resourceLogger;
            _logger = logger;
            _eventLogger = new BrowserEventLogger(sessionId, resourceLogger);
        }

        public string SessionId => _sessionId;

        public string BrowserExecutable => _browserExecutable ?? throw new InvalidOperationException("Browser executable is not available before the session starts.");

        public int ProcessId => _process?.Id ?? throw new InvalidOperationException("Browser process has not started.");

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
            CancellationToken cancellationToken)
        {
            var userDataDirectory = fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-browser-logs");
            var session = new RunningSession(resource, resourceName, sessionId, url, userDataDirectory, resourceLogger, logger);

            try
            {
                await session.InitializeAsync(cancellationToken).ConfigureAwait(false);
                session._completion = session.MonitorAsync();
                return session;
            }
            catch
            {
                await session.CleanupAsync(forceKillProcess: true).ConfigureAwait(false);
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

            if (_process is { HasExited: false })
            {
                using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waitCts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await _process.WaitForExitAsync(waitCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }

                if (!_process.HasExited)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                    {
                        _logger.LogDebug(ex, "Failed to kill tracked browser process for resource '{ResourceName}'.", _resourceName);
                    }
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

            var startInfo = new ProcessStartInfo(_browserExecutable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            var browserDebuggingPort = AllocateBrowserDebuggingPort();
            startInfo.ArgumentList.Add($"--user-data-dir={_userDataDirectory.Path}");
            startInfo.ArgumentList.Add($"--remote-debugging-port={browserDebuggingPort}");
            startInfo.ArgumentList.Add("--no-first-run");
            startInfo.ArgumentList.Add("--no-default-browser-check");
            startInfo.ArgumentList.Add("--new-window");
            startInfo.ArgumentList.Add("--allow-insecure-localhost");
            startInfo.ArgumentList.Add("--ignore-certificate-errors");
            startInfo.ArgumentList.Add("about:blank");

            _process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start tracked browser process '{_browserExecutable}'.");
            StartedAt = DateTime.UtcNow;
            _stdoutTask = DrainStreamAsync(_process.StandardOutput, _stopCts.Token);
            _stderrTask = DrainStreamAsync(_process.StandardError, _stopCts.Token);
            _resourceLogger.LogInformation("[{SessionId}] Started tracked browser process '{BrowserExecutable}'.", _sessionId, _browserExecutable);
            _resourceLogger.LogInformation("[{SessionId}] Waiting for tracked browser debug endpoint on port {Port}.", _sessionId, browserDebuggingPort);

            var browserEndpoint = await WaitForBrowserEndpointAsync(browserDebuggingPort, cancellationToken).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Discovered tracked browser debug endpoint '{Endpoint}'.", _sessionId, browserEndpoint);
            _connection = await ChromeDevToolsConnection.ConnectAsync(browserEndpoint, HandleEventAsync, cancellationToken).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Connected to the tracked browser debug endpoint.", _sessionId);

            var targetId = await _connection.CreateTargetAsync(cancellationToken).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Created tracked browser target '{TargetId}'.", _sessionId, targetId);
            _targetSessionId = await _connection.AttachToTargetAsync(targetId, cancellationToken).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Attached to the tracked browser target.", _sessionId);
            await _connection.EnablePageInstrumentationAsync(_targetSessionId, cancellationToken).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Enabled tracked browser logging.", _sessionId);
            await _connection.NavigateAsync(_targetSessionId, _url, cancellationToken).ConfigureAwait(false);
            _resourceLogger.LogInformation("[{SessionId}] Navigated tracked browser to '{Url}'.", _sessionId, _url);

            _resourceLogger.LogInformation("[{SessionId}] Tracking browser console logs for '{Url}'.", _sessionId, _url);
        }

        private async Task<BrowserSessionResult> MonitorAsync()
        {
            try
            {
                Debug.Assert(_process is not null);
                Debug.Assert(_connection is not null);

                var processExitTask = _process.WaitForExitAsync(CancellationToken.None);
                var completedTask = await Task.WhenAny(processExitTask, _connection.Completion).ConfigureAwait(false);

                Exception? error = null;
                if (completedTask == _connection.Completion)
                {
                    try
                    {
                        await _connection.Completion.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }

                    if (!_stopCts.IsCancellationRequested && !_process.HasExited)
                    {
                        _resourceLogger.LogWarning("[{SessionId}] Tracked browser debug connection closed before the browser process exited.", _sessionId);

                        try
                        {
                            _process.Kill(entireProcessTree: true);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                        {
                            _logger.LogDebug(ex, "Failed to kill tracked browser process after the debug connection closed for resource '{ResourceName}'.", _resourceName);
                        }
                    }
                }

                await processExitTask.ConfigureAwait(false);

                if (!_stopCts.IsCancellationRequested)
                {
                    _resourceLogger.LogInformation("[{SessionId}] Tracked browser exited with code {ExitCode}.", _sessionId, _process.ExitCode);
                }

                return new BrowserSessionResult(_process.ExitCode, error);
            }
            finally
            {
                await CleanupAsync(forceKillProcess: false).ConfigureAwait(false);
            }
        }

        private ValueTask HandleEventAsync(CdpEvent cdpEvent)
        {
            if (!string.Equals(cdpEvent.SessionId, _targetSessionId, StringComparison.Ordinal))
            {
                return ValueTask.CompletedTask;
            }

            _eventLogger.HandleEvent(cdpEvent.Method, cdpEvent.Params);
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

        private async Task CleanupAsync(bool forceKillProcess)
        {
            if (Interlocked.Exchange(ref _cleanupState, 1) != 0)
            {
                return;
            }

            if (forceKillProcess && _process is { HasExited: false })
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    _logger.LogDebug(ex, "Failed to kill tracked browser process during cleanup for resource '{ResourceName}'.", _resourceName);
                }
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            if (_stdoutTask is not null)
            {
                await AwaitQuietlyAsync(_stdoutTask).ConfigureAwait(false);
            }

            if (_stderrTask is not null)
            {
                await AwaitQuietlyAsync(_stderrTask).ConfigureAwait(false);
            }

            _process?.Dispose();
            _stopCts.Dispose();
            _userDataDirectory.Dispose();
        }

        private static async Task AwaitQuietlyAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static async Task DrainStreamAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
            }
        }

        private static async Task<Uri> WaitForBrowserEndpointAsync(int browserDebuggingPort, CancellationToken cancellationToken)
        {
            var browserVersionUri = new Uri($"http://127.0.0.1:{browserDebuggingPort}/json/version", UriKind.Absolute);
            var timeoutAt = TimeProvider.System.GetUtcNow() + TimeSpan.FromSeconds(30);

            while (TimeProvider.System.GetUtcNow() < timeoutAt)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    probeCts.CancelAfter(TimeSpan.FromSeconds(1));

                    using var response = await s_httpClient.GetAsync(browserVersionUri, probeCts.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var version = await response.Content.ReadFromJsonAsync<BrowserVersionResponse>(probeCts.Token).ConfigureAwait(false);
                    if (version?.WebSocketDebuggerUrl is { } browserEndpoint)
                    {
                        return browserEndpoint;
                    }
                }
                catch (HttpRequestException)
                {
                }
                catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
                catch (JsonException)
                {
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("Timed out waiting for the tracked browser to expose its debugging endpoint.");
        }

        private static int AllocateBrowserDebuggingPort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
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

        private sealed record BrowserSessionResult(int ExitCode, Exception? Error);

        private sealed record CdpEvent(string Method, string? SessionId, JsonElement Params);

        private sealed class BrowserVersionResponse
        {
            [JsonPropertyName("webSocketDebuggerUrl")]
            public required Uri WebSocketDebuggerUrl { get; init; }
        }

        private sealed class ChromeDevToolsConnection : IAsyncDisposable
        {
            private readonly ClientWebSocket _webSocket;
            private readonly Func<CdpEvent, ValueTask> _eventHandler;
            private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pendingCommands = new();
            private readonly Task _receiveLoop;
            private long _nextCommandId;

            public ChromeDevToolsConnection(ClientWebSocket webSocket, Func<CdpEvent, ValueTask> eventHandler)
            {
                _webSocket = webSocket;
                _eventHandler = eventHandler;
                _receiveLoop = Task.Run(ReceiveLoopAsync);
            }

            public Task Completion => _receiveLoop;

            public static async Task<ChromeDevToolsConnection> ConnectAsync(Uri webSocketUri, Func<CdpEvent, ValueTask> eventHandler, CancellationToken cancellationToken)
            {
                var webSocket = new ClientWebSocket();
                webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                await webSocket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);
                return new ChromeDevToolsConnection(webSocket, eventHandler);
            }

            public async Task<string> CreateTargetAsync(CancellationToken cancellationToken)
            {
                var result = await SendCommandAsync("Target.createTarget", new { url = "about:blank" }, sessionId: null, cancellationToken).ConfigureAwait(false);
                return result.GetProperty("targetId").GetString()
                    ?? throw new InvalidOperationException("Browser target creation did not return a target id.");
            }

            public async Task<string> AttachToTargetAsync(string targetId, CancellationToken cancellationToken)
            {
                var result = await SendCommandAsync("Target.attachToTarget", new { targetId, flatten = true }, sessionId: null, cancellationToken).ConfigureAwait(false);
                return result.GetProperty("sessionId").GetString()
                    ?? throw new InvalidOperationException("Browser target attachment did not return a session id.");
            }

            public async Task EnablePageInstrumentationAsync(string sessionId, CancellationToken cancellationToken)
            {
                await SendCommandAsync("Runtime.enable", parameters: null, sessionId, cancellationToken).ConfigureAwait(false);
                await SendCommandAsync("Log.enable", parameters: null, sessionId, cancellationToken).ConfigureAwait(false);
                await SendCommandAsync("Page.enable", parameters: null, sessionId, cancellationToken).ConfigureAwait(false);
                await SendCommandAsync("Network.enable", parameters: null, sessionId, cancellationToken).ConfigureAwait(false);
            }

            public Task NavigateAsync(string sessionId, Uri url, CancellationToken cancellationToken) =>
                SendCommandAsync("Page.navigate", new { url = url.ToString() }, sessionId, cancellationToken);

            public Task CloseBrowserAsync(CancellationToken cancellationToken) =>
                SendCommandAsync("Browser.close", parameters: null, sessionId: null, cancellationToken);

            public async ValueTask DisposeAsync()
            {
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
            }

            private async Task<JsonElement> SendCommandAsync(string method, object? parameters, string? sessionId, CancellationToken cancellationToken)
            {
                var commandId = Interlocked.Increment(ref _nextCommandId);
                var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingCommands[commandId] = tcs;

                using var registration = cancellationToken.Register(static state =>
                {
                    var source = (TaskCompletionSource<JsonElement>)state!;
                    source.TrySetCanceled();
                }, tcs);

                var payload = JsonSerializer.SerializeToUtf8Bytes(new CdpCommand
                {
                    Id = commandId,
                    Method = method,
                    Params = parameters,
                    SessionId = sessionId
                }, s_jsonOptions);

                await _webSocket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);

                try
                {
                    return await tcs.Task.ConfigureAwait(false);
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

                try
                {
                    while (_webSocket.State is WebSocketState.Open or WebSocketState.CloseSent)
                    {
                        var result = await _webSocket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            break;
                        }

                        messageBuffer.Write(buffer, 0, result.Count);
                        if (!result.EndOfMessage)
                        {
                            continue;
                        }

                        var message = messageBuffer.ToArray();
                        messageBuffer.SetLength(0);

                        using var document = JsonDocument.Parse(message);
                        var root = document.RootElement;

                        if (root.TryGetProperty("id", out var idElement))
                        {
                            var commandId = idElement.GetInt64();
                            if (_pendingCommands.TryGetValue(commandId, out var pendingCommand))
                            {
                                if (root.TryGetProperty("error", out var error))
                                {
                                    var errorMessage = error.TryGetProperty("message", out var errorMessageElement)
                                        ? errorMessageElement.GetString()
                                        : "Unknown browser protocol error.";
                                    pendingCommand.TrySetException(new InvalidOperationException(errorMessage));
                                }
                                else if (root.TryGetProperty("result", out var responseResult))
                                {
                                    pendingCommand.TrySetResult(responseResult.Clone());
                                }
                                else
                                {
                                    pendingCommand.TrySetResult(default);
                                }
                            }
                        }
                        else if (root.TryGetProperty("method", out var methodElement))
                        {
                            var sessionId = root.TryGetProperty("sessionId", out var sessionIdElement)
                                ? sessionIdElement.GetString()
                                : null;
                            var parameters = root.TryGetProperty("params", out var paramsElement)
                                ? paramsElement.Clone()
                                : default;

                            await _eventHandler(new CdpEvent(methodElement.GetString() ?? string.Empty, sessionId, parameters)).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    foreach (var (_, pendingCommand) in _pendingCommands)
                    {
                        pendingCommand.TrySetException(new InvalidOperationException("Browser debug connection closed."));
                    }
                }
            }

            private sealed class CdpCommand
            {
                [JsonPropertyName("id")]
                public required long Id { get; init; }

                [JsonPropertyName("method")]
                public required string Method { get; init; }

                [JsonPropertyName("params")]
                public object? Params { get; init; }

                [JsonPropertyName("sessionId")]
                public string? SessionId { get; init; }
            }
        }
    }

    private sealed class BrowserLogsRunningSessionFactory : IBrowserLogsRunningSessionFactory
    {
        private readonly IFileSystemService _fileSystemService;
        private readonly ILogger<BrowserLogsSessionManager> _logger;

        public BrowserLogsRunningSessionFactory(IFileSystemService fileSystemService, ILogger<BrowserLogsSessionManager> logger)
        {
            _fileSystemService = fileSystemService;
            _logger = logger;
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
