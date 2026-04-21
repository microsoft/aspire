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

internal sealed class BrowserLogsSessionManager : IBrowserLogsSessionManager, IAsyncDisposable
{
    private static readonly JsonSerializerOptions s_browserSessionPropertyJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ResourceLoggerService _resourceLoggerService;
    private readonly ResourceNotificationService _resourceNotificationService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly IBrowserLogsRunningSessionFactory _sessionFactory;
    private readonly ConcurrentDictionary<string, ResourceSessionState> _resourceStates = new(StringComparer.Ordinal);
    private int _disposing;

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

    public async Task StartSessionAsync(BrowserLogsResource resource, BrowserLogsSettings settings, string resourceName, Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(settings.Browser);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceName);
        ArgumentNullException.ThrowIfNull(url);
        ThrowIfDisposing();

        var resourceState = _resourceStates.GetOrAdd(resourceName, static _ => new ResourceSessionState());
        await resourceState.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ThrowIfDisposing();

            var sessionSequence = ++resourceState.TotalSessionsLaunched;
            var sessionId = $"session-{sessionSequence:0000}";
            resourceState.LastSessionId = sessionId;
            resourceState.LastTargetUrl = url.ToString();
            resourceState.LastBrowser = settings.Browser;
            resourceState.LastBrowserExecutable = BrowserLogsRunningSession.TryResolveBrowserExecutable(settings.Browser);
            resourceState.LastProfile = settings.Profile;

            var resourceLogger = _resourceLoggerService.GetLogger(resourceName);
            resourceLogger.LogInformation("[{SessionId}] Opening tracked browser for '{Url}' using '{Browser}'.", sessionId, url, settings.Browser);

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
                    settings,
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
            var completionObserver = session.StartCompletionObserver(async (exitCode, error) =>
            {
                await HandleSessionCompletedAsync(resource, resourceName, resourceState, session.SessionId, exitCode, error).ConfigureAwait(false);
            });

            resourceState.ActiveSessions[session.SessionId] = new ActiveBrowserSession(
                session.SessionId,
                settings.Browser,
                session.BrowserExecutable,
                settings.Profile,
                session.BrowserDebugEndpoint,
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

        foreach (var session in sessionsToStop)
        {
            await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }

        await Task.WhenAll(completionObservers).ConfigureAwait(false);

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

        properties = resourceState.LastProfile is not null
            ? properties.SetResourceProperty(BrowserLogsBuilderExtensions.ProfilePropertyName, resourceState.LastProfile)
            : RemoveProperty(properties, BrowserLogsBuilderExtensions.ProfilePropertyName);

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
            .Select(static session => $"{session.SessionId} (PID {session.ProcessId})")
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
                session.BrowserDebugEndpoint.ToString(),
                GetPageDebugEndpoint(session.BrowserDebugEndpoint, session.TargetId),
                session.TargetId))
            .ToArray();

        return JsonSerializer.Serialize(activeSessions, s_browserSessionPropertyJsonOptions);
    }

    private static string GetPageDebugEndpoint(Uri browserDebugEndpoint, string targetId)
    {
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

        public string? LastBrowser { get; set; }

        public string? LastProfile { get; set; }
    }

    private sealed record ActiveBrowserSession(
        string SessionId,
        string Browser,
        string BrowserExecutable,
        string? Profile,
        Uri BrowserDebugEndpoint,
        int ProcessId,
        DateTime StartedAt,
        string TargetId,
        Uri TargetUrl,
        IBrowserLogsRunningSession Session,
        Task CompletionObserver);

    private sealed record BrowserSessionPropertyValue(
        string SessionId,
        string Browser,
        string BrowserExecutable,
        int ProcessId,
        string? Profile,
        DateTime StartedAt,
        string TargetUrl,
        string CdpEndpoint,
        string PageCdpEndpoint,
        string TargetId);

    private sealed record PendingBrowserSession(
        string SessionId,
        DateTime StartedAt,
        Uri TargetUrl);
}
