// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal interface IBrowserLogsRunningSession
{
    string SessionId { get; }

    string BrowserExecutable { get; }

    Uri BrowserDebugEndpoint { get; }

    int? ProcessId { get; }

    DateTime StartedAt { get; }

    string TargetId { get; }

    Task StartCompletionObserver(Func<int?, Exception?, Task> onCompleted);

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

internal sealed class BrowserLogsRunningSessionFactory : IBrowserLogsRunningSessionFactory, IAsyncDisposable
{
    private readonly BrowserHostRegistry _browserHostRegistry;
    private readonly ILogger<BrowserLogsSessionManager> _logger;
    private readonly TimeProvider _timeProvider;

    public BrowserLogsRunningSessionFactory(IFileSystemService fileSystemService, ILogger<BrowserLogsSessionManager> logger, TimeProvider timeProvider)
    {
        _browserHostRegistry = new BrowserHostRegistry(fileSystemService, logger, timeProvider);
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
            _browserHostRegistry,
            resourceLogger,
            _logger,
            _timeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _browserHostRegistry.DisposeAsync();
}

// Owns one launched browser instance and its attached CDP target. The manager keeps aggregate dashboard state;
// this type keeps per-browser lifecycle, diagnostics, and recovery.
internal sealed class BrowserLogsRunningSession : IBrowserLogsRunningSession
{
    private readonly BrowserEventLogger _eventLogger;
    private readonly BrowserConnectionDiagnosticsLogger _connectionDiagnostics;
    private readonly BrowserHostRegistry _browserHostRegistry;
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
    private BrowserHostLease? _browserHostLease;
    private Task<BrowserSessionResult>? _completion;
    private int _cleanupState;
    private int? _processId;
    private string? _targetId;
    private IBrowserTargetSession? _targetSession;
    private string? _targetSessionId;

    private BrowserLogsRunningSession(
        BrowserLogsSettings settings,
        string resourceName,
        string sessionId,
        Uri url,
        BrowserHostRegistry browserHostRegistry,
        ILogger resourceLogger,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider)
    {
        _eventLogger = new BrowserEventLogger(sessionId, resourceLogger);
        _connectionDiagnostics = new BrowserConnectionDiagnosticsLogger(sessionId, resourceLogger);
        _browserHostRegistry = browserHostRegistry;
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

    public int? ProcessId => _processId;

    public DateTime StartedAt { get; private set; }

    public string TargetId => _targetId ?? throw new InvalidOperationException("Browser target id is not available before the session starts.");

    private Task<BrowserSessionResult> Completion => _completion ?? throw new InvalidOperationException("Session has not been started.");

    public static async Task<BrowserLogsRunningSession> StartAsync(
        BrowserLogsSettings settings,
        string resourceName,
        string sessionId,
        Uri url,
        BrowserHostRegistry browserHostRegistry,
        ILogger resourceLogger,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var session = new BrowserLogsRunningSession(settings, resourceName, sessionId, url, browserHostRegistry, resourceLogger, logger, timeProvider);

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

    public Task StartCompletionObserver(Func<int?, Exception?, Task> onCompleted)
    {
        return ObserveCompletionAsync(onCompleted);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopCts.Cancel();

        await DisposeTargetSessionAsync().ConfigureAwait(false);
        await DisposeBrowserHostLeaseAsync().ConfigureAwait(false);

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
        try
        {
            _browserHostLease = await _browserHostRegistry.AcquireAsync(_settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _connectionDiagnostics.LogSetupFailure("Acquiring the tracked browser host", ex);
            throw;
        }

        var browserHost = _browserHostLease.Host;
        _browserExecutable = browserHost.Identity.ExecutablePath;
        _browserEndpoint = browserHost.DebugEndpoint;
        _processId = browserHost.ProcessId;
        StartedAt = _timeProvider.GetUtcNow().UtcDateTime;
        _resourceLogger.LogInformation(
            "[{SessionId}] Using {Ownership} tracked browser host '{BrowserExecutable}' at '{Endpoint}'.",
            _sessionId,
            browserHost.Ownership,
            _browserExecutable,
            _browserEndpoint);

        try
        {
            _targetSession = await browserHost.CreateTargetSessionAsync(
                _sessionId,
                _url,
                protocolEvent =>
                {
                    _eventLogger.HandleEvent(protocolEvent);
                    return ValueTask.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
            _targetId = _targetSession.TargetId;
            _targetSessionId = _targetSession.TargetSessionId;
        }
        catch (Exception ex)
        {
            _connectionDiagnostics.LogSetupFailure("Setting up the tracked browser target", ex);
            throw;
        }

        _resourceLogger.LogInformation("[{SessionId}] Tracking browser console logs for '{Url}'.", _sessionId, _url);
    }

    private async Task<BrowserSessionResult> MonitorAsync()
    {
        try
        {
            var targetSession = _targetSession ?? throw new InvalidOperationException("Browser target session is not available.");
            var result = await targetSession.Completion.ConfigureAwait(false);
            return result.CompletionKind switch
            {
                BrowserTargetSessionCompletionKind.Stopped => new BrowserSessionResult(ExitCode: null, Error: null),
                BrowserTargetSessionCompletionKind.TargetClosed => new BrowserSessionResult(ExitCode: null, Error: null),
                BrowserTargetSessionCompletionKind.BrowserExited => new BrowserSessionResult(ExitCode: null, result.Error),
                BrowserTargetSessionCompletionKind.TargetCrashed => new BrowserSessionResult(ExitCode: null, result.Error),
                BrowserTargetSessionCompletionKind.ConnectionLost => new BrowserSessionResult(ExitCode: null, result.Error),
                _ => new BrowserSessionResult(ExitCode: null, Error: null)
            };
        }
        finally
        {
            await CleanupAsync().ConfigureAwait(false);
        }
    }

    private async Task ObserveCompletionAsync(Func<int?, Exception?, Task> onCompleted)
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

        await DisposeTargetSessionAsync().ConfigureAwait(false);
        await DisposeBrowserHostLeaseAsync().ConfigureAwait(false);
        _stopCts.Dispose();
    }

    private async Task DisposeTargetSessionAsync()
    {
        var targetSession = _targetSession;
        _targetSession = null;

        if (targetSession is not null)
        {
            await targetSession.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DisposeBrowserHostLeaseAsync()
    {
        var browserHostLease = _browserHostLease;
        _browserHostLease = null;

        if (browserHostLease is not null)
        {
            await browserHostLease.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal static bool IsGoogleChromeDefaultUserDataDirectory(string browser, string browserExecutable, string userDataDirectory)
    {
        if (GetBrowserKind(browser, browserExecutable) != BrowserKind.Chrome ||
            MatchesBrowser(browser, browserExecutable, "chromium", "chromium-browser") ||
            TryResolveBrowserUserDataDirectory(browser, browserExecutable) is not { } defaultUserDataDirectory)
        {
            return false;
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return comparer.Equals(NormalizePath(userDataDirectory), NormalizePath(defaultUserDataDirectory));

        static string NormalizePath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
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

    internal static string ResolveBrowserProfileDirectory(string userDataDirectory, string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        if (!Directory.Exists(userDataDirectory))
        {
            throw new InvalidOperationException($"Browser user data directory '{userDataDirectory}' was not found.");
        }

        if (TryResolveBrowserProfileDirectoryFromDirectoryEntries(userDataDirectory, profile) is { } directMatch)
        {
            return directMatch;
        }

        var localStatePath = Path.Combine(userDataDirectory, "Local State");
        if (File.Exists(localStatePath))
        {
            try
            {
                using var localStateStream = File.OpenRead(localStatePath);
                using var localStateDocument = JsonDocument.Parse(localStateStream);
                if (TryResolveBrowserProfileDirectory(localStateDocument.RootElement, userDataDirectory, profile) is { } profileDirectory)
                {
                    return profileDirectory;
                }
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Unable to read Chromium profile metadata from '{localStatePath}' while resolving browser profile '{profile}'.",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"Unable to read Chromium profile metadata from '{localStatePath}' while resolving browser profile '{profile}'.",
                    ex);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Chromium profile metadata in '{localStatePath}' is invalid while resolving browser profile '{profile}'.",
                    ex);
            }
        }

        throw new InvalidOperationException(
            $"Browser profile '{profile}' was not found under '{userDataDirectory}'. Specify the profile directory name (for example 'Default' or 'Profile 1') or a browser profile name from Chromium's profile metadata.");
    }

    internal static string? TryResolveBrowserProfileDirectory(JsonElement localStateRoot, string userDataDirectory, string profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);

        if (!localStateRoot.TryGetProperty("profile", out var profileElement) ||
            !profileElement.TryGetProperty("info_cache", out var infoCacheElement) ||
            infoCacheElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? match = null;

        foreach (var profileEntry in infoCacheElement.EnumerateObject())
        {
            if (!Directory.Exists(Path.Combine(userDataDirectory, profileEntry.Name)) ||
                !MatchesBrowserProfile(profileEntry, profile))
            {
                continue;
            }

            if (match is not null && !string.Equals(match, profileEntry.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Browser profile '{profile}' matched multiple Chromium profiles under '{userDataDirectory}'. Specify the profile directory name instead.");
            }

            match = profileEntry.Name;
        }

        return match;
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

    private static string? TryResolveBrowserProfileDirectoryFromDirectoryEntries(string userDataDirectory, string profile)
    {
        foreach (var directoryPath in Directory.EnumerateDirectories(userDataDirectory))
        {
            var directoryName = Path.GetFileName(directoryPath);
            if (string.Equals(directoryName, profile, StringComparison.OrdinalIgnoreCase))
            {
                return directoryName;
            }
        }

        return null;
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

    private static bool MatchesBrowserProfile(JsonProperty profileEntry, string profile)
    {
        return string.Equals(profileEntry.Name, profile, StringComparison.OrdinalIgnoreCase) ||
            MatchesBrowserProfileProperty(profileEntry.Value, "name", profile) ||
            MatchesBrowserProfileProperty(profileEntry.Value, "shortcut_name", profile);
    }

    private static bool MatchesBrowserProfileProperty(JsonElement profileElement, string propertyName, string profile)
    {
        return profileElement.TryGetProperty(propertyName, out var propertyElement) &&
            propertyElement.ValueKind == JsonValueKind.String &&
            string.Equals(propertyElement.GetString(), profile, StringComparison.OrdinalIgnoreCase);
    }

    internal static string BuildCommandLine(IReadOnlyList<string> arguments)
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

    private sealed record BrowserSessionResult(int? ExitCode, Exception? Error);

    private enum BrowserKind
    {
        Unknown,
        Edge,
        Chrome
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
