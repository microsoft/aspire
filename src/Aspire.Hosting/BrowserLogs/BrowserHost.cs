// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using System.Globalization;
using System.Text;
using Aspire.Hosting.Dcp.Process;
using Aspire.Hosting.Resources;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Base implementation for browser hosts. It centralizes the shared mechanics for creating per-page sessions
// while concrete hosts decide who owns the browser process lifetime.
internal abstract class BrowserHost(
    BrowserHostIdentity identity,
    BrowserHostOwnership ownership,
    Uri debugEndpoint,
    string browserDisplayName,
    ILogger<BrowserLogsSessionManager> logger,
    TimeProvider timeProvider,
    bool reuseInitialBlankTarget) : IBrowserHost
{
    private readonly ILogger<BrowserLogsSessionManager> _logger = logger;
    private readonly bool _reuseInitialBlankTarget = reuseInitialBlankTarget;
    private readonly TimeProvider _timeProvider = timeProvider;

    public BrowserHostIdentity Identity { get; } = identity;

    public BrowserHostOwnership Ownership { get; } = ownership;

    public Uri DebugEndpoint { get; } = debugEndpoint;

    public abstract int? ProcessId { get; }

    public string BrowserDisplayName { get; } = browserDisplayName;

    public abstract Task Termination { get; }

    public virtual async Task<IBrowserLogsCdpConnection> CreateCdpConnectionAsync(
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        CancellationToken cancellationToken)
    {
        return await BrowserLogsCdpConnection.ConnectAsync(DebugEndpoint, eventHandler, logger, cancellationToken).ConfigureAwait(false);
    }

    public Task<IBrowserPageSession> CreatePageSessionAsync(
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken)
    {
        return CreatePageSessionCoreAsync(sessionId, url, connectionDiagnostics, eventHandler, cancellationToken);
    }

    public abstract ValueTask DisposeAsync();

    private async Task<IBrowserPageSession> CreatePageSessionCoreAsync(
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken)
    {
        return await BrowserPageSession.StartAsync(
            this,
            sessionId,
            url,
            connectionDiagnostics,
            eventHandler,
            _logger,
            _timeProvider,
            _reuseInitialBlankTarget,
            cancellationToken).ConfigureAwait(false);
    }
}

// Host implementation for browsers Aspire starts itself. Owned hosts are responsible for spawning Chromium with a
// browser-level CDP endpoint, writing adoption metadata, and terminating the browser when the final lease is released.
internal sealed class OwnedBrowserHost : BrowserHost
{
    // Browser startup is a local process + file hand-off. Give Chromium enough time to initialize under CI/dev-machine
    // load, poll frequently enough for a responsive dashboard command, and cap shutdown so AppHost disposal cannot hang
    // forever on a stuck browser process.
    // Browser launch races against itself: the OS spawns the process, the process starts up, picks a remote-debugging
    // port, and writes DevToolsActivePort. 30 seconds covers cold-start cases (large profile, AV scan, slow disk) while
    // still failing fast enough to surface a wedged launch. The 100 ms poll interval is short enough to feel instant
    // for warm starts but long enough to avoid burning a core busy-spinning on the file system.
    private static readonly TimeSpan s_browserEndpointTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_browserEndpointPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly BrowserLogsUserDataDirectory _userDataDirectory;
    private readonly IAsyncDisposable _processLifetime;
    private readonly Task<ProcessResult> _processTask;
    private readonly Task _termination;
    private int _disposed;

    private OwnedBrowserHost(
        BrowserHostIdentity identity,
        Uri debugEndpoint,
        string browserDisplayName,
        int processId,
        BrowserLogsUserDataDirectory userDataDirectory,
        IAsyncDisposable processLifetime,
        Task<ProcessResult> processTask,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider)
        : base(identity, BrowserHostOwnership.Owned, debugEndpoint, browserDisplayName, logger, timeProvider, reuseInitialBlankTarget: true)
    {
        _processLifetime = processLifetime;
        _processTask = processTask;
        _termination = processTask;
        _userDataDirectory = userDataDirectory;
        ProcessId = processId;
    }

    public override int? ProcessId { get; }

    public override Task Termination => _termination;

    private static string BuildBrowserArguments(BrowserLogsUserDataDirectory userDataDirectory)
    {
        // Chromium writes DevToolsActivePort only when remote debugging is enabled. Let it choose the port so
        // playground runs do not collide with a user's existing browser or another AppHost. The initial about:blank
        // page gives owned hosts a predictable first page target that can be navigated instead of leaving an extra
        // blank tab.
        List<string> arguments =
        [
            $"--user-data-dir={userDataDirectory.Path}",
            "--remote-debugging-address=127.0.0.1",
            "--remote-debugging-port=0",
            "--no-first-run",
            "--no-default-browser-check",
            "--new-window",
            "--allow-insecure-localhost"
        ];

        if (userDataDirectory.ProfileDirectoryName is { } profileDirectoryName)
        {
            arguments.Add($"--profile-directory={profileDirectoryName}");
        }

        arguments.Add("about:blank");

        return BuildCommandLine(arguments);
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

    public static async Task<OwnedBrowserHost> StartAsync(
        BrowserHostIdentity identity,
        string browserDisplayName,
        BrowserLogsUserDataDirectory userDataDirectory,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var processStarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var devToolsActivePortFilePath = Path.Combine(userDataDirectory.Path, "DevToolsActivePort");
        // DevToolsActivePort is Chromium's hand-off file for the browser-level websocket. Real profile directories can
        // contain a stale file from a previous run, and a live browser can keep it locked, so remember the previous
        // timestamp and only accept a fresh write from the process we just launched.
        var previousWriteTimeUtc = PrepareBrowserEndpointFile(devToolsActivePortFilePath, logger);
        // Clear Aspire's adoption sidecar before launch so a later AcquireAsync cannot adopt stale metadata while this
        // owned process is still proving which endpoint Chromium actually opened.
        BrowserEndpointDiscovery.DeleteEndpointMetadata(userDataDirectory.Path);

        var processSpec = new ProcessSpec(identity.ExecutablePath)
        {
            Arguments = BuildBrowserArguments(userDataDirectory),
            InheritEnv = true,
            OnErrorData = error => logger.LogTrace("Tracked browser stderr: {Line}", error),
            OnOutputData = output => logger.LogTrace("Tracked browser stdout: {Line}", output),
            OnStart = processId => processStarted.TrySetResult(processId),
            ThrowOnNonZeroReturnCode = false
        };

        var (processTask, processLifetime) = ProcessUtil.Run(processSpec);
        int processId;
        Uri browserEndpoint;
        try
        {
            processId = await WaitForProcessStartAsync(processStarted.Task, processTask, cancellationToken).ConfigureAwait(false);
            browserEndpoint = await WaitForBrowserEndpointAsync(processTask, devToolsActivePortFilePath, previousWriteTimeUtc, logger, timeProvider, cancellationToken).ConfigureAwait(false);
            // Once Chromium has written DevToolsActivePort and responded with a browser endpoint, write our sidecar so a
            // later AppHost run can adopt the same debug-enabled browser instead of opening a second window.
            await BrowserEndpointDiscovery.WriteAsync(identity, userDataDirectory.ProfileDirectoryName, browserEndpoint, processId, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await processLifetime.DisposeAsync().ConfigureAwait(false);
            userDataDirectory.Dispose();
            throw;
        }

        return new OwnedBrowserHost(
            identity,
            browserEndpoint,
            browserDisplayName,
            processId,
            userDataDirectory,
            processLifetime,
            processTask,
            logger,
            timeProvider);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Both Shared and Isolated point at a persistent Aspire-managed user data directory. AppHost shutdown does
        // not close the browser, does not delete the adoption sidecar, and does not delete the user data directory.
        // The next AppHost run reads the sidecar and connects to this browser via CDP. The user closes the browser
        // when they are done with it.
        //
        // We deliberately do not dispose _processLifetime, which would terminate the browser process. The Process
        // handle leaks until the AppHost exits; ProcessDisposable has no finalizer that would kill the process on
        // GC, so the browser keeps running.
        _ = _processLifetime;
        _ = _processTask;
        _ = _userDataDirectory;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task<int> WaitForProcessStartAsync(Task<int> processStarted, Task<ProcessResult> processTask, CancellationToken cancellationToken)
    {
        var completedTask = await Task.WhenAny(processStarted, processTask).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (completedTask == processStarted)
        {
            return await processStarted.ConfigureAwait(false);
        }

        var result = await processTask.ConfigureAwait(false);
        throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsProcessExitedBeforeProcessId, result.ExitCode));
    }

    private static async Task<Uri> WaitForBrowserEndpointAsync(
        Task<ProcessResult> processTask,
        string devToolsActivePortFilePath,
        DateTime? previousWriteTimeUtc,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var timeoutAt = timeProvider.GetUtcNow() + s_browserEndpointTimeout;
        logger.LogTrace("Waiting up to {Timeout} for tracked browser to publish DevToolsActivePort at '{DevToolsActivePortFilePath}'.", s_browserEndpointTimeout, devToolsActivePortFilePath);

        while (timeProvider.GetUtcNow() < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (processTask.IsCompleted)
            {
                var result = await processTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    string.Format(CultureInfo.CurrentCulture, MessageStrings.BrowserLogsProcessExitedBeforeDebugEndpoint, result.ExitCode, devToolsActivePortFilePath));
            }

            try
            {
                if (File.Exists(devToolsActivePortFilePath))
                {
                    if (previousWriteTimeUtc is { } previousWriteTime &&
                        File.GetLastWriteTimeUtc(devToolsActivePortFilePath) <= previousWriteTime)
                    {
                        logger.LogTrace("Ignoring stale tracked browser endpoint metadata '{DevToolsActivePortFilePath}' while waiting for a fresh Chromium write.", devToolsActivePortFilePath);
                        await Task.Delay(s_browserEndpointPollInterval, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var contents = await File.ReadAllTextAsync(devToolsActivePortFilePath, cancellationToken).ConfigureAwait(false);
                    if (ChromiumDevToolsActivePortParser.TryParseBrowserDebugEndpoint(contents) is { } browserEndpoint)
                    {
                        logger.LogTrace("Read tracked browser debug endpoint '{BrowserDebugEndpoint}' from '{DevToolsActivePortFilePath}'.", browserEndpoint, devToolsActivePortFilePath);
                        return browserEndpoint;
                    }

                    logger.LogTrace("Tracked browser endpoint metadata '{DevToolsActivePortFilePath}' was present but not parseable yet.", devToolsActivePortFilePath);
                }
            }
            catch (IOException ex)
            {
                logger.LogTrace(ex, "Unable to read tracked browser endpoint metadata '{DevToolsActivePortFilePath}' yet.", devToolsActivePortFilePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogTrace(ex, "Unable to read tracked browser endpoint metadata '{DevToolsActivePortFilePath}' yet.", devToolsActivePortFilePath);
            }

            await Task.Delay(s_browserEndpointPollInterval, cancellationToken).ConfigureAwait(false);
        }

        throw new TimeoutException($"Timed out waiting for the tracked browser to write '{devToolsActivePortFilePath}'.");
    }

    private static DateTime? PrepareBrowserEndpointFile(string devToolsActivePortFilePath, ILogger logger)
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
            logger.LogDebug(ex, "Unable to delete stale tracked browser endpoint metadata '{DevToolsActivePortFilePath}'. Waiting for a fresh file instead.", devToolsActivePortFilePath);
            return previousWriteTimeUtc;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Unable to delete stale tracked browser endpoint metadata '{DevToolsActivePortFilePath}'. Waiting for a fresh file instead.", devToolsActivePortFilePath);
            return previousWriteTimeUtc;
        }
    }
}

// Host implementation for browsers Aspire discovers from validated endpoint metadata. Adopted hosts create and close
// tracked targets, but never terminate the browser process because it may be the user's normal browser.
internal sealed class AdoptedBrowserHost : BrowserHost
{
    private readonly TaskCompletionSource _terminationSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // An adopted browser may already contain user-owned tabs. Always create a new target for Aspire rather than reusing
    // an arbitrary about:blank page that happened to exist in the user's real browser.
    public AdoptedBrowserHost(
        BrowserHostIdentity identity,
        Uri debugEndpoint,
        string browserDisplayName,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider)
        : base(identity, BrowserHostOwnership.Adopted, debugEndpoint, browserDisplayName, logger, timeProvider, reuseInitialBlankTarget: false)
    {
    }

    public override int? ProcessId => null;

    public override Task Termination => _terminationSource.Task;

    public override ValueTask DisposeAsync()
    {
        _terminationSource.TrySetResult();

        return ValueTask.CompletedTask;
    }
}
