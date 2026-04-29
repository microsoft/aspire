// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// Base implementation for browser hosts. It centralizes the shared mechanics for creating per-page sessions
// while concrete hosts decide who owns the browser process lifetime.
internal abstract class BrowserHost(
    BrowserHostIdentity identity,
    BrowserHostOwnership ownership,
    Uri? debugEndpoint,
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

    public Uri? DebugEndpoint { get; } = debugEndpoint;

    public abstract int? ProcessId { get; }

    public string BrowserDisplayName { get; } = browserDisplayName;

    public abstract Task Termination { get; }

    public virtual async Task<IBrowserLogsCdpConnection> CreateCdpConnectionAsync(
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        CancellationToken cancellationToken)
    {
        var debugEndpoint = DebugEndpoint ?? throw new InvalidOperationException("Tracked browser host does not expose a WebSocket debug endpoint.");
        return await BrowserLogsCdpConnection.ConnectAsync(debugEndpoint, eventHandler, logger, cancellationToken).ConfigureAwait(false);
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
// private browser-level CDP pipe and cleaning it up when the final lease is released.
internal sealed class OwnedBrowserHost : BrowserHost
{
    private readonly BrowserLogsCdpConnectionMultiplexer _connectionMultiplexer;
    private readonly IBrowserLogsPipeBrowserProcess _process;
    private readonly BrowserLogsUserDataDirectory _userDataDirectory;
    private readonly Task<ProcessResult> _processTask;
    private readonly Task _termination;
    private int _disposed;

    private OwnedBrowserHost(
        BrowserHostIdentity identity,
        string browserDisplayName,
        IBrowserLogsPipeBrowserProcess process,
        BrowserLogsCdpConnectionMultiplexer connectionMultiplexer,
        BrowserLogsUserDataDirectory userDataDirectory,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider)
        : base(identity, BrowserHostOwnership.Owned, debugEndpoint: null, browserDisplayName, logger, timeProvider, reuseInitialBlankTarget: true)
    {
        _connectionMultiplexer = connectionMultiplexer;
        _process = process;
        _processTask = process.ProcessTask;
        _termination = CompleteWhenProcessOrPipeEndsAsync(process.ProcessTask, connectionMultiplexer.Completion);
        _userDataDirectory = userDataDirectory;
        ProcessId = process.ProcessId;
    }

    public override int? ProcessId { get; }

    public override Task Termination => _termination;

    public override Task<IBrowserLogsCdpConnection> CreateCdpConnectionAsync(
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_connectionMultiplexer.CreateConnection(eventHandler));
    }

    private static List<string> BuildBrowserArguments(BrowserLogsUserDataDirectory userDataDirectory)
    {
        // The initial about:blank page gives owned hosts a predictable first page target that can be navigated instead
        // of leaving an extra blank tab.
        List<string> arguments =
        [
            $"--user-data-dir={userDataDirectory.Path}",
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
        return arguments;
    }

    public static async Task<OwnedBrowserHost> StartAsync(
        BrowserHostIdentity identity,
        string browserDisplayName,
        BrowserLogsUserDataDirectory userDataDirectory,
        ILogger<BrowserLogsSessionManager> logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        Func<string, IReadOnlyList<string>, IBrowserLogsPipeBrowserProcess>? startPipeBrowserProcess = null)
    {
        var devToolsActivePortFilePath = Path.Combine(userDataDirectory.Path, "DevToolsActivePort");
        // Pipe-backed launches do not use DevToolsActivePort or the sidecar endpoint file. Clear stale WebSocket
        // hand-off metadata before creating the private-pipe browser so future attach/adoption code doesn't mistake it
        // for current state.
        DeleteBrowserEndpointFile(devToolsActivePortFilePath, logger);
        BrowserEndpointDiscovery.DeleteEndpointMetadata(userDataDirectory.Path);
        startPipeBrowserProcess ??= BrowserLogsPipeBrowserProcessLauncher.Start;

        IBrowserLogsPipeBrowserProcess? process = null;
        BrowserLogsCdpConnectionMultiplexer? connectionMultiplexer = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process = startPipeBrowserProcess(identity.ExecutablePath, BuildBrowserArguments(userDataDirectory));
            connectionMultiplexer = new BrowserLogsCdpConnectionMultiplexer(
                new BrowserLogsPipeCdpTransport(process.BrowserOutput, process.BrowserInput),
                logger);
        }
        catch
        {
            if (connectionMultiplexer is not null)
            {
                await connectionMultiplexer.DisposeAsync().ConfigureAwait(false);
            }

            if (process is not null)
            {
                await process.DisposeAsync().ConfigureAwait(false);
            }

            userDataDirectory.Dispose();
            throw;
        }

        return new OwnedBrowserHost(
            identity,
            browserDisplayName,
            process,
            connectionMultiplexer,
            userDataDirectory,
            logger,
            timeProvider);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            await _connectionMultiplexer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await _process.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _ = _processTask;
                _userDataDirectory.Dispose();
            }
        }
    }

    private static async Task CompleteWhenProcessOrPipeEndsAsync(Task processTask, Task pipeTask)
    {
        // Pipe-backed hosts cannot reconnect: the only CDP pipe is owned by this AppHost process. Treat either browser
        // process exit or pipe failure as host termination so page sessions end instead of running WebSocket-style
        // reconnect loops against a dead private transport.
        _ = await Task.WhenAny(processTask, pipeTask).ConfigureAwait(false);
    }

    private static void DeleteBrowserEndpointFile(string devToolsActivePortFilePath, ILogger logger)
    {
        if (!File.Exists(devToolsActivePortFilePath))
        {
            return;
        }

        try
        {
            File.Delete(devToolsActivePortFilePath);
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Unable to delete stale tracked browser endpoint metadata '{DevToolsActivePortFilePath}'.", devToolsActivePortFilePath);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogDebug(ex, "Unable to delete stale tracked browser endpoint metadata '{DevToolsActivePortFilePath}'.", devToolsActivePortFilePath);
        }
    }
}

// Host implementation for browsers Aspire discovers from validated endpoint metadata. Adopted hosts use WebSocket CDP
// and create/close tracked targets, but never terminate the browser process because it may outlive this AppHost.
internal sealed class AdoptedBrowserHost : BrowserHost
{
    private readonly TaskCompletionSource _terminationSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // An adopted browser may already contain user-owned tabs. Always create a new target for Aspire rather than reusing
    // an arbitrary about:blank page that happened to exist in the browser.
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
