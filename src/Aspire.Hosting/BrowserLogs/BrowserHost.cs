// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREFILESYSTEM001 // Type is for evaluation purposes only

using Aspire.Hosting.Dcp.Process;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

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

    public Task<IBrowserTargetSession> CreateTargetSessionAsync(
        string sessionId,
        Uri url,
        Func<BrowserLogsProtocolEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken)
    {
        return CreateTargetSessionCoreAsync(sessionId, url, eventHandler, cancellationToken);
    }

    public abstract ValueTask DisposeAsync();

    protected static string BuildBrowserArguments(BrowserLogsUserDataDirectory userDataDirectory)
    {
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

        return BrowserLogsRunningSession.BuildCommandLine(arguments);
    }

    private async Task<IBrowserTargetSession> CreateTargetSessionCoreAsync(
        string sessionId,
        Uri url,
        Func<BrowserLogsProtocolEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken)
    {
        return await BrowserTargetSession.StartAsync(
            this,
            sessionId,
            url,
            eventHandler,
            _logger,
            _timeProvider,
            _reuseInitialBlankTarget,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class OwnedBrowserHost : BrowserHost
{
    private static readonly TimeSpan s_browserEndpointTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_browserShutdownTimeout = TimeSpan.FromSeconds(5);

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
        var previousWriteTimeUtc = PrepareBrowserEndpointFile(devToolsActivePortFilePath, logger);
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
            browserEndpoint = await WaitForBrowserEndpointAsync(processTask, devToolsActivePortFilePath, previousWriteTimeUtc, timeProvider, cancellationToken).ConfigureAwait(false);
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

        BrowserEndpointDiscovery.DeleteEndpointMetadata(_userDataDirectory.Path);

        await _processLifetime.DisposeAsync().ConfigureAwait(false);

        using var shutdownCts = new CancellationTokenSource(s_browserShutdownTimeout);
        try
        {
            await _processTask.WaitAsync(shutdownCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _userDataDirectory.Dispose();
    }

    private static async Task<int> WaitForProcessStartAsync(Task<int> processStarted, Task<ProcessResult> processTask, CancellationToken cancellationToken)
    {
        var completedTask = await Task.WhenAny(processStarted, processTask).WaitAsync(cancellationToken).ConfigureAwait(false);
        if (completedTask == processStarted)
        {
            return await processStarted.ConfigureAwait(false);
        }

        var result = await processTask.ConfigureAwait(false);
        throw new InvalidOperationException($"Tracked browser process exited with code {result.ExitCode} before reporting its process id.");
    }

    private static async Task<Uri> WaitForBrowserEndpointAsync(
        Task<ProcessResult> processTask,
        string devToolsActivePortFilePath,
        DateTime? previousWriteTimeUtc,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var timeoutAt = timeProvider.GetUtcNow() + s_browserEndpointTimeout;

        while (timeProvider.GetUtcNow() < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (processTask.IsCompleted)
            {
                var result = await processTask.ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Tracked browser process exited with code {result.ExitCode} before the debug endpoint metadata was written to '{devToolsActivePortFilePath}'.");
            }

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

internal sealed class AdoptedBrowserHost : BrowserHost
{
    private readonly TaskCompletionSource _terminationSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

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
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _terminationSource.TrySetResult();
        }

        return ValueTask.CompletedTask;
    }
}
