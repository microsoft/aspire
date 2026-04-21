// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Globalization;
using System.Text;
using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal interface IBrowserLogsRunningSession
{
    string SessionId { get; }

    string BrowserExecutable { get; }

    Uri BrowserDebugEndpoint { get; }

    int ProcessId { get; }

    DateTime StartedAt { get; }

    string TargetId { get; }

    Task StartCompletionObserver(Func<int, Exception?, Task> onCompleted);

    Task StopAsync(CancellationToken cancellationToken);
}

internal interface IBrowserLogsRunningSessionFactory
{
    Task<IBrowserLogsRunningSession> StartSessionAsync(
        BrowserLogsSettings settings,
        string resourceName,
        Uri url,
        string sessionId,
        ILogger resourceLogger,
        CancellationToken cancellationToken);
}

internal sealed class BrowserLogsRunningSessionFactory : IBrowserLogsRunningSessionFactory
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
        BrowserLogsSettings settings,
        string resourceName,
        Uri url,
        string sessionId,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        return await BrowserLogsRunningSession.StartAsync(
            settings,
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

// Owns one launched browser instance and its attached CDP target. The manager keeps aggregate dashboard state;
// this type keeps per-browser lifecycle, diagnostics, and recovery.
internal sealed class BrowserLogsRunningSession : IBrowserLogsRunningSession
{
    private static readonly TimeSpan s_browserEndpointTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_browserShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_connectionRecoveryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_connectionRecoveryTimeout = TimeSpan.FromSeconds(5);

    private readonly BrowserEventLogger _eventLogger;
    private readonly BrowserConnectionDiagnosticsLogger _connectionDiagnostics;
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly ILogger _resourceLogger;
    private readonly string _resourceName;
    private readonly BrowserLogsSettings _settings;
    private readonly string _sessionId;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TimeProvider _timeProvider;
    private readonly Uri _url;

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
    private BrowserLogsUserDataDirectory? _userDataDirectory;

    private BrowserLogsRunningSession(
        BrowserLogsSettings settings,
        string resourceName,
        string sessionId,
        Uri url,
        IFileSystemService fileSystemService,
        ILogger resourceLogger,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider)
    {
        _eventLogger = new BrowserEventLogger(sessionId, resourceLogger);
        _connectionDiagnostics = new BrowserConnectionDiagnosticsLogger(sessionId, resourceLogger);
        _fileSystemService = fileSystemService;
        _logger = logger;
        _resourceLogger = resourceLogger;
        _resourceName = resourceName;
        _settings = settings;
        _sessionId = sessionId;
        _timeProvider = timeProvider;
        _url = url;
    }

    public string SessionId => _sessionId;

    public string BrowserExecutable => _browserExecutable ?? throw new InvalidOperationException("Browser executable is not available before the session starts.");

    public Uri BrowserDebugEndpoint => _browserEndpoint ?? throw new InvalidOperationException("Browser debugging endpoint is not available before the session starts.");

    public int ProcessId => _processId ?? throw new InvalidOperationException("Browser process has not started.");

    public DateTime StartedAt { get; private set; }

    public string TargetId => _targetId ?? throw new InvalidOperationException("Browser target id is not available before the session starts.");

    private Task<BrowserSessionResult> Completion => _completion ?? throw new InvalidOperationException("Session has not been started.");

    public static async Task<BrowserLogsRunningSession> StartAsync(
        BrowserLogsSettings settings,
        string resourceName,
        string sessionId,
        Uri url,
        IFileSystemService fileSystemService,
        ILogger resourceLogger,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = new BrowserLogsRunningSession(settings, resourceName, sessionId, url, fileSystemService, resourceLogger, logger, timeProvider);

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

    public Task StartCompletionObserver(Func<int, Exception?, Task> onCompleted)
    {
        return ObserveCompletionAsync(onCompleted);
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
            OperationCanceledException? waitCanceledException = null;
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waitCts.CancelAfter(s_browserShutdownTimeout);

            try
            {
                await browserProcessTask.WaitAsync(waitCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                waitCanceledException = ex;
            }

            if (!browserProcessTask.IsCompleted)
            {
                await DisposeBrowserProcessAsync().ConfigureAwait(false);
            }

            if (waitCanceledException is not null && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(waitCanceledException.Message, waitCanceledException, cancellationToken);
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
        _browserExecutable = TryResolveBrowserExecutable(_settings.Browser);
        if (_browserExecutable is null)
        {
            throw new InvalidOperationException($"Unable to locate browser '{_settings.Browser}'. Specify an installed Chromium-based browser or an explicit executable path.");
        }

        _userDataDirectory = CreateUserDataDirectory(_settings.Browser, _browserExecutable, _settings.Profile);
        var devToolsActivePortFilePath = GetDevToolsActivePortFilePath();
        var previousBrowserEndpointWriteTimeUtc = PrepareBrowserEndpointFile(devToolsActivePortFilePath);
        await StartBrowserProcessAsync(cancellationToken).ConfigureAwait(false);
        _resourceLogger.LogInformation("[{SessionId}] Started tracked browser process '{BrowserExecutable}'.", _sessionId, _browserExecutable);
        if (_settings.Profile is not null)
        {
            _resourceLogger.LogInformation("[{SessionId}] Using tracked browser profile '{Profile}'.", _sessionId, _settings.Profile);
        }
        _resourceLogger.LogInformation("[{SessionId}] Waiting for tracked browser debug endpoint metadata in '{DevToolsActivePortFilePath}'.", _sessionId, devToolsActivePortFilePath);

        try
        {
            _browserEndpoint = await WaitForBrowserEndpointAsync(devToolsActivePortFilePath, previousBrowserEndpointWriteTimeUtc, cancellationToken).ConfigureAwait(false);
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
        var userDataDirectory = _userDataDirectory ?? throw new InvalidOperationException("Browser user data directory was not initialized.");
        List<string> arguments =
        [
            $"--user-data-dir={userDataDirectory.Path}",
            "--remote-debugging-port=0",
            "--no-first-run",
            "--no-default-browser-check",
            "--new-window",
            "--allow-insecure-localhost"
        ];

        if (_settings.Profile is not null)
        {
            arguments.Add($"--profile-directory={_settings.Profile}");
        }

        arguments.Add("about:blank");

        return BuildCommandLine(arguments);
    }

    private async Task<string> GetOrCreateTrackedTargetAsync(CancellationToken cancellationToken)
    {
        var targets = await ExecuteConnectionStageAsync(
            "Discovering the tracked browser target",
            () => _connection!.GetTargetsAsync(cancellationToken)).ConfigureAwait(false);

        if (TrySelectTrackedTargetId(targets.TargetInfos) is { } targetId)
        {
            _resourceLogger.LogInformation("[{SessionId}] Reusing tracked browser target '{TargetId}'.", _sessionId, targetId);
            return targetId;
        }

        var createTargetResult = await ExecuteConnectionStageAsync(
            "Creating the tracked browser target",
            () => _connection!.CreateTargetAsync(cancellationToken)).ConfigureAwait(false);
        targetId = createTargetResult.TargetId
            ?? throw new InvalidOperationException("Browser target creation did not return a target id.");
        _resourceLogger.LogInformation("[{SessionId}] Created tracked browser target '{TargetId}'.", _sessionId, targetId);
        return targetId;
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
            _targetId = await GetOrCreateTrackedTargetAsync(cancellationToken).ConfigureAwait(false);
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

    // Wrap the CDP stage boundaries so resource logs can identify which phase failed without losing the inner cause.
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

        // Recovery reuses the existing target instead of creating a second browser/tab. If that cannot be restored
        // quickly, the process is torn down so the resource state matches reality.
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
        // The browser-level websocket can surface events for other targets. Only forward the target attached for
        // this tracked browser session.
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
        _userDataDirectory?.Dispose();
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

    private async Task<Uri> WaitForBrowserEndpointAsync(string devToolsActivePortFilePath, DateTime? previousWriteTimeUtc, CancellationToken cancellationToken)
    {
        var timeoutAt = _timeProvider.GetUtcNow() + s_browserEndpointTimeout;

        // Chromium chooses the actual debugging port when asked for port 0 and writes it to DevToolsActivePort.
        // Waiting on that file avoids the reserve-and-release race of probing a fixed port ahead of launch.
        while (_timeProvider.GetUtcNow() < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ThrowIfBrowserExitedBeforeEndpointWasWrittenAsync(devToolsActivePortFilePath).ConfigureAwait(false);

            try
            {
                if (File.Exists(devToolsActivePortFilePath))
                {
                    if (previousWriteTimeUtc is { } previousWriteTime &&
                        File.GetLastWriteTimeUtc(devToolsActivePortFilePath) <= previousWriteTime)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var contents = await File.ReadAllTextAsync(devToolsActivePortFilePath, cancellationToken).ConfigureAwait(false);
                    if (BrowserLogsDebugEndpointParser.TryParseBrowserDebugEndpoint(contents) is { } browserEndpoint)
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

    private DateTime? PrepareBrowserEndpointFile(string devToolsActivePortFilePath)
    {
        if (!File.Exists(devToolsActivePortFilePath))
        {
            return null;
        }

        var previousWriteTimeUtc = File.GetLastWriteTimeUtc(devToolsActivePortFilePath);

        try
        {
            File.Delete(devToolsActivePortFilePath);
            return null;
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "[{SessionId}] Unable to delete stale tracked browser endpoint metadata '{DevToolsActivePortFilePath}'. Waiting for a fresh file instead.", _sessionId, devToolsActivePortFilePath);
            return previousWriteTimeUtc;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogDebug(ex, "[{SessionId}] Unable to delete stale tracked browser endpoint metadata '{DevToolsActivePortFilePath}'. Waiting for a fresh file instead.", _sessionId, devToolsActivePortFilePath);
            return previousWriteTimeUtc;
        }
    }

    private async Task ThrowIfBrowserExitedBeforeEndpointWasWrittenAsync(string devToolsActivePortFilePath)
    {
        if (_browserProcessTask is not { IsCompleted: true } browserProcessTask)
        {
            return;
        }

        var result = await browserProcessTask.ConfigureAwait(false);
        throw new InvalidOperationException(
            $"Tracked browser process exited with code {result.ExitCode} before the debug endpoint metadata was written to '{devToolsActivePortFilePath}'.");
    }

    private string GetDevToolsActivePortFilePath()
    {
        var userDataDirectory = _userDataDirectory ?? throw new InvalidOperationException("Browser user data directory was not initialized.");
        return Path.Combine(userDataDirectory.Path, "DevToolsActivePort");
    }

    private BrowserLogsUserDataDirectory CreateUserDataDirectory(string browser, string browserExecutable, string? profile)
    {
        if (profile is null)
        {
            return BrowserLogsUserDataDirectory.CreateTemporary(_fileSystemService.TempDirectory.CreateTempSubdirectory("aspire-browser-logs"));
        }

        var userDataDirectory = TryResolveBrowserUserDataDirectory(browser, browserExecutable)
            ?? throw new InvalidOperationException($"Unable to resolve the user data directory for browser '{browser}'. Specify a known browser such as 'msedge' or 'chrome' when using a browser profile.");

        if (!Directory.Exists(userDataDirectory))
        {
            throw new InvalidOperationException($"Browser user data directory '{userDataDirectory}' was not found for browser '{browser}'.");
        }

        var profileDirectory = Path.Combine(userDataDirectory, profile);
        if (!Directory.Exists(profileDirectory))
        {
            throw new InvalidOperationException($"Browser profile '{profile}' was not found under '{userDataDirectory}'.");
        }

        return BrowserLogsUserDataDirectory.CreatePersistent(userDataDirectory);
    }

    internal static string? TryResolveBrowserExecutable(string browser)
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

    internal static string? TryResolveBrowserUserDataDirectory(string browser, string browserExecutable)
    {
        var browserKind = GetBrowserKind(browser, browserExecutable);
        if (browserKind == BrowserKind.Unknown)
        {
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return browserKind switch
            {
                BrowserKind.Edge => Path.Combine(home, "Library", "Application Support", "Microsoft Edge"),
                BrowserKind.Chrome => Path.Combine(home, "Library", "Application Support", "Google", "Chrome"),
                _ => null
            };
        }

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return browserKind switch
            {
                BrowserKind.Edge => Path.Combine(localAppData, "Microsoft", "Edge", "User Data"),
                BrowserKind.Chrome => Path.Combine(localAppData, "Google", "Chrome", "User Data"),
                _ => null
            };
        }

        var homeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return browserKind switch
        {
            BrowserKind.Edge => Path.Combine(homeDirectory, ".config", "microsoft-edge"),
            BrowserKind.Chrome => Path.Combine(
                homeDirectory,
                ".config",
                MatchesBrowser(browser, browserExecutable, "chromium", "chromium-browser") ? "chromium" : "google-chrome"),
            _ => null
        };
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

    private static BrowserKind GetBrowserKind(string browser, string browserExecutable)
    {
        if (MatchesBrowser(browser, browserExecutable, "msedge", "edge", "microsoft-edge"))
        {
            return BrowserKind.Edge;
        }

        if (MatchesBrowser(browser, browserExecutable, "chrome", "google-chrome", "chromium", "chromium-browser"))
        {
            return BrowserKind.Chrome;
        }

        return BrowserKind.Unknown;
    }

    internal static string? TrySelectTrackedTargetId(IReadOnlyList<BrowserLogsTargetInfo>? targetInfos)
    {
        if (targetInfos is null)
        {
            return null;
        }

        var preferredTarget = targetInfos.FirstOrDefault(static targetInfo =>
            string.Equals(targetInfo.Type, "page", StringComparison.Ordinal) &&
            targetInfo.Attached != true &&
            string.Equals(targetInfo.Url, "about:blank", StringComparison.Ordinal));

        if (!string.IsNullOrWhiteSpace(preferredTarget?.TargetId))
        {
            return preferredTarget.TargetId;
        }

        return targetInfos.FirstOrDefault(static targetInfo =>
            string.Equals(targetInfo.Type, "page", StringComparison.Ordinal) &&
            targetInfo.Attached != true &&
            !string.IsNullOrWhiteSpace(targetInfo.TargetId))
            ?.TargetId;
    }

    private static bool MatchesBrowser(string browser, string browserExecutable, params string[] names)
    {
        var browserLower = browser.ToLowerInvariant();
        var executableLower = browserExecutable.ToLowerInvariant();

        foreach (var name in names)
        {
            if (browserLower == name ||
                Path.GetFileNameWithoutExtension(browserLower) == name ||
                executableLower.Contains(name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
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

    private enum BrowserKind
    {
        Unknown,
        Edge,
        Chrome
    }

    private sealed class BrowserLogsUserDataDirectory : IDisposable
    {
        private readonly TempDirectory? _temporaryDirectory;

        private BrowserLogsUserDataDirectory(string path, TempDirectory? temporaryDirectory)
        {
            Path = path;
            _temporaryDirectory = temporaryDirectory;
        }

        public string Path { get; }

        public static BrowserLogsUserDataDirectory CreatePersistent(string path) => new(path, temporaryDirectory: null);

        public static BrowserLogsUserDataDirectory CreateTemporary(TempDirectory temporaryDirectory) => new(temporaryDirectory.Path, temporaryDirectory);

        public void Dispose() => _temporaryDirectory?.Dispose();
    }
}

internal static class BrowserLogsDebugEndpointParser
{
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
}
