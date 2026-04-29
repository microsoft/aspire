// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using HealthStatus = Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus;

namespace Aspire.Hosting;

// Coordinates browser-log commands with dashboard resource state. The running session owns CDP capture; this manager
// owns session ids, resource logs, health reports, and snapshot properties that make failures diagnosable in the dashboard.
internal sealed class BrowserLogsSessionManager : IBrowserLogsSessionManager, IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_browserSessionPropertyJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ResourceLoggerService _resourceLoggerService;
    private readonly ResourceNotificationService _resourceNotificationService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly IBrowserLogsArtifactWriter _artifactWriter;
    private readonly IBrowserLogsRunningSessionFactory _sessionFactory;
    private readonly ConcurrentDictionary<string, ResourceSessionState> _resourceStates = new(StringComparer.Ordinal);
    private int _disposing;

    public BrowserLogsSessionManager(
        ResourceLoggerService resourceLoggerService,
        ResourceNotificationService resourceNotificationService,
        TimeProvider timeProvider,
        ILogger<BrowserLogsSessionManager> logger)
        : this(
            resourceLoggerService,
            resourceNotificationService,
            timeProvider,
            logger,
            new BrowserLogsArtifactWriter(timeProvider),
            new BrowserLogsRunningSessionFactory(logger, timeProvider))
    {
    }

    internal BrowserLogsSessionManager(
        ResourceLoggerService resourceLoggerService,
        ResourceNotificationService resourceNotificationService,
        TimeProvider timeProvider,
        ILogger<BrowserLogsSessionManager> logger,
        IBrowserLogsArtifactWriter? artifactWriter,
        IBrowserLogsRunningSessionFactory sessionFactory)
    {
        _resourceLoggerService = resourceLoggerService;
        _resourceNotificationService = resourceNotificationService;
        _timeProvider = timeProvider;
        _logger = logger;
        _artifactWriter = artifactWriter ?? new BrowserLogsArtifactWriter(timeProvider);
        _sessionFactory = sessionFactory;
    }

    public async Task StartSessionAsync(BrowserLogsResource resource, BrowserConfiguration configuration, string resourceName, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(configuration.Browser);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(url);
        ThrowIfDisposing();

        var resourceState = _resourceStates.GetOrAdd(resourceName, static _ => new ResourceSessionState());
        // Dashboard commands can start/stop browser-log sessions for the same resource while previous targets are still
        // completing. Serialize per resource so session ids, health reports, and properties describe the same observed
        // set of browser targets.
        await resourceState.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposing();

            var sessionSequence = ++resourceState.TotalSessionsLaunched;
            var sessionId = $"session-{sessionSequence:0000}";
            resourceState.LastSessionId = sessionId;
            resourceState.LastTargetUrl = url.ToString();
            resourceState.LastBrowser = configuration.Browser;
            resourceState.LastBrowserExecutable = ChromiumBrowserResolver.TryResolveExecutable(configuration.Browser);
            if (resourceState.ActiveSessions.Count == 0)
            {
                resourceState.LastBrowserHostOwnership = null;
            }
            resourceState.LastError = null;
            resourceState.LastProfile = configuration.Profile;
            resourceState.LastBrowserProcessLifetime = configuration.BrowserProcessLifetime.ToString();

            var resourceLogger = _resourceLoggerService.GetLogger(resourceName);
            resourceLogger.LogInformation("[{SessionId}] Opening tracked browser for '{Url}' using '{Browser}'.", sessionId, url, configuration.Browser);

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
                    configuration,
                    resourceName,
                    url,
                    sessionId,
                    resourceLogger,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                resourceState.LastError = BrowserConnectionDiagnosticsLogger.DescribeConnectionProblem(ex);
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
            resourceState.LastBrowserHostOwnership = session.BrowserHostOwnership.ToString();
            resourceState.LastError = null;
            var completionObserver = session.StartCompletionObserver(async (exitCode, error) =>
            {
                await HandleSessionCompletedAsync(resource, resourceName, resourceState, session.SessionId, exitCode, error).ConfigureAwait(false);
            });

            // WebSocket-backed sessions expose browser-level and page-level CDP endpoints for inspection. Pipe-backed
            // sessions intentionally expose only a transport description because their CDP connection is private.
            resourceState.ActiveSessions[session.SessionId] = new ActiveBrowserSession(
                session.SessionId,
                configuration.AppHostKey,
                configuration.Browser,
                session.BrowserExecutable,
                configuration.Profile,
                session.BrowserDebugEndpoint,
                session.BrowserHostOwnership.ToString(),
                session.ProcessId,
                session.StartedAt,
                session.TargetId,
                url,
                session,
                completionObserver);

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

        void ThrowIfDisposing()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        }
    }

    public async Task<BrowserLogsScreenshotCaptureResult> CaptureScreenshotAsync(string resourceName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ThrowIfDisposing();

        var resourceState = _resourceStates.GetOrAdd(resourceName, static _ => new ResourceSessionState());
        ActiveBrowserSession? activeSession;

        await resourceState.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();

            activeSession = resourceState.LastSessionId is { } lastSessionId &&
                resourceState.ActiveSessions.TryGetValue(lastSessionId, out var lastSession)
                    ? lastSession
                    : resourceState.ActiveSessions.Count == 0
                        ? null
                        : resourceState.ActiveSessions.Values.MaxBy(static session => session.StartedAt);

            if (activeSession is null)
            {
                throw new InvalidOperationException("No active tracked browser session is available to capture.");
            }
        }
        finally
        {
            resourceState.Lock.Release();
        }

        var screenshotBytes = await activeSession.Session.CaptureScreenshotAsync(cancellationToken).ConfigureAwait(false);
        var artifact = await _artifactWriter.WriteArtifactAsync(
            activeSession.AppHostKey,
            resourceName,
            artifactType: "screenshot",
            fileExtension: ".png",
            mimeType: "image/png",
            content: screenshotBytes,
            cancellationToken).ConfigureAwait(false);

        var processDescription = FormatProcessId(activeSession.ProcessId);
        _resourceLoggerService.GetLogger(resourceName).LogInformation(
            "[{SessionId}] Captured browser screenshot artifact '{ArtifactPath}' ({SizeBytes} bytes) from target '{TargetId}' at '{TargetUrl}' using '{Browser}' ({BrowserHostOwnership}, {ProcessDescription}).",
            activeSession.SessionId,
            artifact.FilePath,
            artifact.SizeBytes,
            activeSession.TargetId,
            activeSession.TargetUrl,
            activeSession.Browser,
            activeSession.BrowserHostOwnership,
            processDescription);

        return new BrowserLogsScreenshotCaptureResult(
            activeSession.SessionId,
            activeSession.Browser,
            activeSession.BrowserExecutable,
            activeSession.BrowserHostOwnership,
            activeSession.ProcessId,
            activeSession.TargetId,
            activeSession.TargetUrl,
            artifact);

        void ThrowIfDisposing()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposing, 1);

        var sessionsToStop = new List<IBrowserLogsRunningSession>();
        var completionObservers = new List<Task>();

        foreach (var resourceState in _resourceStates.Values)
        {
            await resourceState.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

            try
            {
                sessionsToStop.AddRange(resourceState.ActiveSessions.Values.Select(static activeSession => activeSession.Session));
                completionObservers.AddRange(resourceState.ActiveSessions.Values.Select(static activeSession => activeSession.CompletionObserver));
            }
            finally
            {
                resourceState.Lock.Release();
            }
        }

        try
        {
            // StopAsync can throw (for example OperationCanceledException from BrowserHostLease's release timeout).
            // Catch per-session so one failure doesn't strand other sessions, and use try/finally below so the locks
            // and session factory are always disposed.
            foreach (var session in sessionsToStop)
            {
                try
                {
                    await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to stop tracked browser session during disposal.");
                }
            }

            try
            {
                await Task.WhenAll(completionObservers).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Tracked browser session completion observer failed during disposal.");
            }
        }
        finally
        {
            foreach (var (_, resourceState) in _resourceStates)
            {
                resourceState.Lock.Dispose();
            }

            if (_sessionFactory is IAsyncDisposable asyncDisposableFactory)
            {
                await asyncDisposableFactory.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleSessionCompletedAsync(
        BrowserLogsResource resource,
        string resourceName,
        ResourceSessionState resourceState,
        string sessionId,
        int? exitCode,
        Exception? error)
    {
        if (Volatile.Read(ref _disposing) != 0)
        {
            return;
        }

        await resourceState.Lock.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        try
        {
            if (!resourceState.ActiveSessions.Remove(sessionId))
            {
                return;
            }

            // Multiple active sessions can share one visible browser. If one tab is closed or crashes, keep the resource
            // running while other tabs are still producing logs; only the last completion controls stop time and exit code.
            if (error is not null)
            {
                resourceState.LastError = BrowserConnectionDiagnosticsLogger.DescribeConnectionProblem(error);
            }

            var completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            var hasActiveSessions = resourceState.ActiveSessions.Count > 0;
            var (stateText, stateStyle) = hasActiveSessions
                ? (KnownResourceStates.Running, KnownResourceStateStyles.Success)
                : resourceState.LastError switch
                {
                    not null => (KnownResourceStates.Exited, KnownResourceStateStyles.Error),
                    null when exitCode is null or 0 => (KnownResourceStates.Finished, KnownResourceStateStyles.Success),
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
            Properties = UpdateProperties(snapshot.Properties, resourceState, propertyUpdates),
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
                $"{FormatProcessId(session.ProcessId)} targeting {session.TargetUrl}",
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
        else if (resourceState.LastError is not null)
        {
            reports.Add(new HealthReportSnapshot(
                BrowserLogsBuilderExtensions.LastErrorPropertyName,
                HealthStatus.Unhealthy,
                resourceState.LastError,
                null)
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
        yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.BrowserSessionsPropertyName, FormatBrowserSessions(resourceState.ActiveSessions.Values));
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

        if (resourceState.LastBrowserHostOwnership is not null)
        {
            yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.BrowserHostOwnershipPropertyName, resourceState.LastBrowserHostOwnership);
        }

        if (resourceState.LastError is not null)
        {
            yield return new ResourcePropertySnapshot(BrowserLogsBuilderExtensions.LastErrorPropertyName, resourceState.LastError);
        }
    }

    private static ImmutableArray<ResourcePropertySnapshot> UpdateProperties(
        ImmutableArray<ResourcePropertySnapshot> properties,
        ResourceSessionState resourceState,
        IEnumerable<ResourcePropertySnapshot> propertyUpdates)
    {
        properties = resourceState.LastBrowser is not null
            ? properties.SetResourceProperty(BrowserLogsBuilderExtensions.BrowserPropertyName, resourceState.LastBrowser)
            : RemoveProperty(properties, BrowserLogsBuilderExtensions.BrowserPropertyName);

        properties = resourceState.LastBrowserExecutable is not null
            ? properties.SetResourceProperty(BrowserLogsBuilderExtensions.BrowserExecutablePropertyName, resourceState.LastBrowserExecutable)
            : RemoveProperty(properties, BrowserLogsBuilderExtensions.BrowserExecutablePropertyName);

        properties = resourceState.LastBrowserHostOwnership is not null
            ? properties.SetResourceProperty(BrowserLogsBuilderExtensions.BrowserHostOwnershipPropertyName, resourceState.LastBrowserHostOwnership)
            : RemoveProperty(properties, BrowserLogsBuilderExtensions.BrowserHostOwnershipPropertyName);

        properties = resourceState.LastError is not null
            ? properties.SetResourceProperty(BrowserLogsBuilderExtensions.LastErrorPropertyName, resourceState.LastError)
            : RemoveProperty(properties, BrowserLogsBuilderExtensions.LastErrorPropertyName);

        properties = resourceState.LastProfile is not null
            ? properties.SetResourceProperty(BrowserLogsBuilderExtensions.ProfilePropertyName, resourceState.LastProfile)
            : RemoveProperty(properties, BrowserLogsBuilderExtensions.ProfilePropertyName);

        properties = resourceState.LastBrowserProcessLifetime is not null
            ? properties.SetResourceProperty(BrowserLogsBuilderExtensions.BrowserProcessLifetimePropertyName, resourceState.LastBrowserProcessLifetime)
            : RemoveProperty(properties, BrowserLogsBuilderExtensions.BrowserProcessLifetimePropertyName);

        return properties.SetResourcePropertyRange(propertyUpdates);
    }

    private static ImmutableArray<ResourcePropertySnapshot> RemoveProperty(ImmutableArray<ResourcePropertySnapshot> properties, string name)
    {
        for (var i = 0; i < properties.Length; i++)
        {
            if (string.Equals(properties[i].Name, name, StringComparisons.ResourcePropertyName))
            {
                return properties.RemoveAt(i);
            }
        }

        return properties;
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
            .Select(static session => $"{session.SessionId} ({FormatProcessId(session.ProcessId)})")
            .ToArray();

        return activeSessions.Length > 0
            ? string.Join(", ", activeSessions)
            : "None";
    }

    private static string FormatBrowserSessions(IEnumerable<ActiveBrowserSession> sessions)
    {
        var activeSessions = sessions
            .OrderBy(static session => session.SessionId, StringComparer.Ordinal)
            .Select(static session => new BrowserSessionPropertyValue(
                session.SessionId,
                session.Browser,
                session.BrowserExecutable,
                session.ProcessId,
                session.Profile,
                session.StartedAt,
                session.TargetUrl.ToString(),
                session.BrowserHostOwnership,
                FormatDebugEndpoint(session.BrowserDebugEndpoint),
                GetPageDebugEndpoint(session.BrowserDebugEndpoint, session.TargetId),
                session.TargetId))
            .ToArray();

        return JsonSerializer.Serialize(activeSessions, s_browserSessionPropertyJsonOptions);
    }

    private static string? GetPageDebugEndpoint(Uri? browserDebugEndpoint, string targetId)
    {
        if (browserDebugEndpoint is null)
        {
            return null;
        }

        var builder = new UriBuilder(browserDebugEndpoint)
        {
            Path = $"/devtools/page/{targetId}"
        };

        return builder.Uri.ToString();
    }

    private sealed class ResourceSessionState
    {
        public SemaphoreSlim Lock { get; } = new(1, 1);

        public Dictionary<string, ActiveBrowserSession> ActiveSessions { get; } = new(StringComparer.Ordinal);

        public int TotalSessionsLaunched { get; set; }

        public string? LastSessionId { get; set; }

        public string? LastTargetUrl { get; set; }

        public string? LastBrowserExecutable { get; set; }

        public string? LastBrowserHostOwnership { get; set; }

        public string? LastError { get; set; }

        public string? LastBrowser { get; set; }

        public string? LastProfile { get; set; }

        public string? LastBrowserProcessLifetime { get; set; }
    }

    private static string FormatProcessId(int? processId) =>
        processId is { } pid ? $"PID {pid}" : "adopted browser";

    private sealed record ActiveBrowserSession(
        string SessionId,
        string? AppHostKey,
        string Browser,
        string BrowserExecutable,
        string? Profile,
        Uri? BrowserDebugEndpoint,
        string BrowserHostOwnership,
        int? ProcessId,
        DateTime StartedAt,
        string TargetId,
        Uri TargetUrl,
        IBrowserLogsRunningSession Session,
        Task CompletionObserver);

    private sealed record BrowserSessionPropertyValue(
        string SessionId,
        string Browser,
        string BrowserExecutable,
        int? ProcessId,
        string? Profile,
        DateTime StartedAt,
        string TargetUrl,
        string BrowserHostOwnership,
        string CdpEndpoint,
        string? PageCdpEndpoint,
        string TargetId);

    private static string FormatDebugEndpoint(Uri? browserDebugEndpoint) =>
        browserDebugEndpoint?.ToString() ?? "pipe";

    private sealed record PendingBrowserSession(
        string SessionId,
        DateTime StartedAt,
        Uri TargetUrl);
}
