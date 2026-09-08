// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Cli.Commands;
using Aspire.Cli.Git;
using Aspire.Cli.Telemetry;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Background service that monitors the auxiliary backchannel directory and maintains
/// connections to all running AppHost instances.
/// </summary>
internal sealed class AuxiliaryBackchannelMonitor(
    ILogger<AuxiliaryBackchannelMonitor> logger,
    CliExecutionContext executionContext,
    TimeProvider timeProvider,
    ProfilingTelemetry profilingTelemetry) : BackgroundService, IAuxiliaryBackchannelMonitor
{
    /// <summary>
    /// Identifies the log written on each connect attempt, so tests can observe attempts without
    /// depending on the wording of the message.
    /// </summary>
    internal static EventId ConnectingToSocketEvent { get; } = new(1, nameof(ConnectingToSocketEvent));

    private static readonly TimeSpan s_maxRetryElapsed = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_maxRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_initialUnreachableRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_maxUnreachableRetryDelay = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, AppHostAuxiliaryBackchannel> _connectionsBySocketPath = new(StringComparers.FileSystemPath);
    private readonly IReadOnlyList<AppHostSocketDirectory> _socketDirectories = AppHostSocketManager.GetSocketDirectories(executionContext.HomeDirectory.FullName);

    // Track known socket files to detect additions and removals
    private readonly HashSet<string> _knownSocketPaths = new(StringComparers.FileSystemPath);

    // Sockets that exhausted the connect retry budget but that we are not allowed to delete.
    // See MarkUnreachable for why these need a backoff instead of an immediate retry.
    private readonly ConcurrentDictionary<string, UnreachableSocket> _unreachableSockets = new(StringComparers.FileSystemPath);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider;
    private event Action? ConnectionsChanged;

    /// <summary>
    /// Gets all active AppHost connections, flattened from all hashes.
    /// </summary>
    public IEnumerable<IAppHostAuxiliaryBackchannel> Connections =>
        _connectionsBySocketPath.Values;

    public async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> WatchConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connectionChanges = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        void QueueConnectionChange() => connectionChanges.Writer.TryWrite(true);

        ConnectionsChanged += QueueConnectionChange;
        List<PhysicalFileProvider>? fileProviders = null;

        try
        {
            await ProcessDirectoryChangesAsync(cancellationToken).ConfigureAwait(false);
            yield return Connections.ToList();

            fileProviders = CreateFileProviders();

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(
                        fileProviders.Select((fileProvider, index) =>
                            WatchConnectionChangesAsync(fileProvider, _socketDirectories[index].SearchPattern, cancellationToken))).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Expected when the follow command stops.
                }
                finally
                {
                    connectionChanges.Writer.TryComplete();
                }
            }, CancellationToken.None);

            await foreach (var _ in connectionChanges.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return Connections.ToList();
            }
        }
        finally
        {
            ConnectionsChanged -= QueueConnectionChange;
            connectionChanges.Writer.TryComplete();
            DisposeFileProviders(fileProviders);
        }

        async Task WatchConnectionChangesAsync(IFileProvider fileProvider, string watchPattern, CancellationToken cancellationToken)
        {
            await foreach (var _ in WatchForChangesAsync(fileProvider, watchPattern, cancellationToken).ConfigureAwait(false))
            {
                await ProcessDirectoryChangesAsync(cancellationToken).ConfigureAwait(false);
                QueueConnectionChange();
            }
        }
    }

    private void NotifyConnectionsChanged()
    {
        ConnectionsChanged?.Invoke();
    }

    /// <summary>
    /// Gets or sets the path to the selected AppHost. When set, this AppHost will be used for MCP operations.
    /// </summary>
    public string? SelectedAppHostPath { get; set; }

    /// <summary>
    /// Gets the currently selected AppHost connection based on the selection logic.
    /// </summary>
    public IAppHostAuxiliaryBackchannel? SelectedConnection
    {
        get
        {
            var selectedAppHostPath = SelectedAppHostPath;
            var connection = SelectConnection(Connections, ref selectedAppHostPath);

            // SelectConnection clears the selection when the chosen AppHost is gone.
            SelectedAppHostPath = selectedAppHostPath;
            return connection;
        }
    }

    /// <summary>
    /// Applies the AppHost selection policy: an explicit selection wins, then a single in-scope
    /// connection, then whatever is available.
    /// </summary>
    /// <remarks>
    /// Kept separate from the property so test doubles can reuse the real policy instead of carrying
    /// their own copy of it, which would let the two drift and make selection tests vacuous.
    /// </remarks>
    /// <param name="connections">The currently established connections.</param>
    /// <param name="selectedAppHostPath">
    /// The explicitly selected AppHost path. Set to <see langword="null"/> when it no longer matches
    /// any connection, so the caller stops trying to honor a selection that has gone away.
    /// </param>
    internal static IAppHostAuxiliaryBackchannel? SelectConnection(
        IEnumerable<IAppHostAuxiliaryBackchannel> connections,
        ref string? selectedAppHostPath)
    {
        var candidates = connections.ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // Check if a specific AppHost was selected
        if (!string.IsNullOrEmpty(selectedAppHostPath))
        {
            var selectedPath = selectedAppHostPath;
            var selectedConnection = candidates.FirstOrDefault(c =>
                c.AppHostInfo?.AppHostPath is { } candidatePath &&
                AppHostPathComparer.PathsEqual(candidatePath, selectedPath));

            if (selectedConnection != null)
            {
                return selectedConnection;
            }

            // Clear the selection since the AppHost is no longer available
            selectedAppHostPath = null;
        }

        // Look for in-scope connections
        var inScopeConnections = candidates.Where(c => c.IsInScope).ToList();

        if (inScopeConnections.Count == 1)
        {
            return inScopeConnections[0];
        }

        // Fall back to the first available connection
        return candidates[0];
    }

    /// <summary>
    /// Gets all connections that are within the scope of the specified working directory.
    /// </summary>
    public IReadOnlyList<IAppHostAuxiliaryBackchannel> GetConnectionsForWorkingDirectory(DirectoryInfo workingDirectory)
    {
        return Connections
            .Where(c => IsAppHostInScopeOfDirectory(c.AppHostInfo?.AppHostPath, workingDirectory.FullName))
            .ToList();
    }

    /// <summary>
    /// Determines whether <paramref name="appHostPath"/> lives within <paramref name="workingDirectory"/>
    /// and in the same git worktree. Nested linked worktrees are out of scope of the primary checkout.
    /// This is the single in-scope implementation shared by <see cref="IsAppHostInScope"/>.
    /// </summary>
    internal static bool IsAppHostInScopeOfDirectory(string? appHostPath, string workingDirectory)
    {
        if (string.IsNullOrEmpty(appHostPath))
        {
            return false;
        }

        // Resolve symlinks and filesystem aliases on both operands. The OS reports a process's
        // current directory in physical form (for example macOS temp dirs under /var -> /private/var),
        // while a file-based AppHost can report its path unresolved, so comparing without filesystem
        // normalization would treat an in-scope AppHost as out of scope.
        var normalizedWorkingDirectory = PathNormalizer.ResolveToFilesystemPath(workingDirectory);
        var normalizedAppHostPath = PathNormalizer.ResolveToFilesystemPath(appHostPath);

        // Check if the AppHost path is within the working directory
        var relativePath = Path.GetRelativePath(normalizedWorkingDirectory, normalizedAppHostPath);
        if (relativePath.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        // Path containment alone treats a nested linked worktree (for example
        // repo/.worktrees/feature) as in-scope of the primary checkout. Stop and ps
        // should stay inside the current worktree unless --apphost/--all is used.
        return GitWorktree.IsSameWorktreeScope(normalizedAppHostPath, normalizedWorkingDirectory);
    }

    /// <summary>
    /// Triggers an immediate scan of the backchannels directory for new/removed AppHosts.
    /// </summary>
    public Task ScanAsync(CancellationToken cancellationToken = default)
    {
        return UpdateConnectionsAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait for the command to be selected, with a timeout
            // If timeout occurs or no command is set, monitoring is not needed
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

            var command = await executionContext.CommandSelected.Task.WaitAsync(combined.Token).ConfigureAwait(false);

            // Only monitor if the command is MCP start command (run --detach uses manual scanning)
            if (command is not McpStartCommand)
            {
                logger.LogDebug("Current command is not MCP start command. Auxiliary backchannel monitoring disabled.");
                return;
            }

            logger.LogInformation("Starting auxiliary backchannel monitor for {CommandType}", command.GetType().Name);

            // Scan for existing sockets on startup.
            await ProcessDirectoryChangesAsync(stoppingToken).ConfigureAwait(false);

            var fileProviders = CreateFileProviders();
            try
            {
                // Run the watcher loops until cancellation.
                await Task.WhenAll(
                    fileProviders.Select((fileProvider, index) =>
                        RunFileWatcherLoopAsync(fileProvider, _socketDirectories[index].SearchPattern, stoppingToken))).ConfigureAwait(false);
            }
            finally
            {
                DisposeFileProviders(fileProviders);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Auxiliary backchannel monitor stopping");
        }
        catch (OperationCanceledException)
        {
            // Timeout occurred - no command was selected, monitoring not needed
            logger.LogDebug("No command selected within timeout. Auxiliary backchannel monitoring not needed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in auxiliary backchannel monitor");
        }
        finally
        {
            // Clean up all connections in parallel
            var disconnectTasks = Connections.Select(DisconnectAsync);
            await Task.WhenAll(disconnectTasks).ConfigureAwait(false);
            _connectionsBySocketPath.Clear();
        }
    }

    private async Task UpdateConnectionsAsync(CancellationToken cancellationToken)
    {
        await ProcessDirectoryChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Creates a polling file provider for every directory that can contain backchannel sockets.
    /// </summary>
    /// <remarks>
    /// The directories are created first because <see cref="PhysicalFileProvider"/> requires an
    /// existing root, and because sockets from older AppHosts still land in the legacy location.
    /// Polling is enabled because the sockets are created by other processes and native change
    /// notifications for socket files are not reliable across platforms.
    /// </remarks>
    private List<PhysicalFileProvider> CreateFileProviders()
    {
        var fileProviders = new List<PhysicalFileProvider>(_socketDirectories.Count);
        try
        {
            foreach (var socketDirectory in _socketDirectories)
            {
                Directory.CreateDirectory(socketDirectory.DirectoryPath);
                fileProviders.Add(new PhysicalFileProvider(socketDirectory.DirectoryPath)
                {
                    UsePollingFileWatcher = true,
                    UseActivePolling = true
                });
            }
        }
        catch
        {
            // Each provider already constructed owns an active polling timer, and the caller's
            // finally only runs once this method returns a list to assign, so a failure partway
            // through the loop would leak every provider created before it.
            DisposeFileProviders(fileProviders);
            throw;
        }

        return fileProviders;
    }

    private static void DisposeFileProviders(List<PhysicalFileProvider>? fileProviders)
    {
        if (fileProviders is null)
        {
            return;
        }

        foreach (var fileProvider in fileProviders)
        {
            fileProvider.Dispose();
        }
    }

    private async Task<IReadOnlyList<Task>> ProcessDirectoryChangesAsync(CancellationToken cancellationToken)
    {
        var connectTasks = new List<Task>();
        var failedSockets = new ConcurrentBag<string>();

        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentSockets = AppHostSocketManager.FindSockets(executionContext.HomeDirectory.FullName, Environment.ProcessId, logger);
            var currentSocketPaths = currentSockets.Select(socket => socket.SocketPath).ToHashSet(StringComparers.FileSystemPath);

            // Find new sockets (files that exist now but weren't known before), plus previously
            // unreachable sockets whose backoff has expired.
            var newSockets = currentSockets
                .Where(socket => !_knownSocketPaths.Contains(socket.SocketPath) || TryClaimRetry(socket.SocketPath))
                .ToList();
            connectTasks.EnsureCapacity(newSockets.Count);
            foreach (var newSocket in newSockets)
            {
                logger.LogDebug("Socket created: {SocketPath}", newSocket.SocketPath);
                connectTasks.Add(TryConnectToSocketAsync(newSocket, failedSockets, cancellationToken));
            }

            // Find removed files (files that were known but no longer exist)
            var removedFiles = _knownSocketPaths.Except(currentSocketPaths, StringComparers.FileSystemPath).ToList();
            foreach (var removedFile in removedFiles)
            {
                logger.LogDebug("Socket deleted: {SocketPath}", removedFile);
                ClearUnreachable(removedFile);
                if (_connectionsBySocketPath.TryRemove(removedFile, out var connection))
                {
                    _ = Task.Run(async () => await DisconnectAsync(connection).ConfigureAwait(false), CancellationToken.None);
                }
            }

            // Update the known files set
            _knownSocketPaths.Clear();
            foreach (var socketPath in currentSocketPaths)
            {
                _knownSocketPaths.Add(socketPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Error processing directory changes");
        }
        finally
        {
            _scanLock.Release();
        }

        // Wait for connection attempts to complete, then clean up failed sockets
        if (connectTasks.Count > 0)
        {
            await Task.WhenAll(connectTasks).ConfigureAwait(false);
        }

        // Remove failed sockets from known files so they can be retried on next scan.
        // This reacquires the lock because _knownSocketPaths is a plain HashSet and a concurrent scan
        // (the public ScanAsync or either directory watcher) clears and repopulates it inside the lock.
        if (!failedSockets.IsEmpty)
        {
            await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                foreach (var failedSocket in failedSockets)
                {
                    if (_knownSocketPaths.Remove(failedSocket))
                    {
                        logger.LogDebug("Marked failed socket for retry on next scan: {SocketPath}", failedSocket);
                    }
                }
            }
            finally
            {
                _scanLock.Release();
            }
        }

        return connectTasks;
    }

    private async Task TryConnectToSocketAsync(IAppHostSocket appHostSocket, ConcurrentBag<string> failedSockets, CancellationToken cancellationToken)
    {
        var socketPath = appHostSocket.SocketPath;

        // Check if we're already connected to this specific socket
        if (_connectionsBySocketPath.ContainsKey(socketPath))
        {
            logger.LogDebug("Already connected to socket: {SocketPath}", socketPath);
            return;
        }

        var pid = appHostSocket.ProcessId;
        var maxElapsed = s_maxRetryElapsed;
        var delay = TimeSpan.FromMilliseconds(100);
        var maxDelay = s_maxRetryDelay;
        var start = _timeProvider.GetUtcNow();
        var isFirstAttempt = true;
        Socket? socket = null;

        while (_timeProvider.GetUtcNow() - start < maxElapsed)
        {
            try
            {
                if (!isFirstAttempt)
                {
                    // Give the socket a moment to be ready (exponential backoff)
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                    delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, maxDelay.TotalMilliseconds));
                }

                if (isFirstAttempt)
                {
                    logger.LogInformation(ConnectingToSocketEvent, "Connecting to auxiliary socket: {SocketPath}", socketPath);
                }
                else
                {
                    logger.LogDebug("Retrying connection to auxiliary socket: {SocketPath}", socketPath);
                }

                // Connect to the Unix socket
                socket = await appHostSocket.ConnectAsync(cancellationToken).ConfigureAwait(false);
                break; // Success - exit retry loop
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                socket?.Dispose();
                socket = null;

                // A refusal on a pidless socket (the pre-9.3 format) carries no ownership information,
                // so age is the only available signal: anything past the bind grace window is treated
                // as stale and reclaimed.
                //
                // A refusal on a PID-qualified socket is retried instead. FindSockets already deleted
                // sockets whose owning process is gone, so reaching here usually means the AppHost is
                // mid-startup. It can also mean the AppHost died and an unrelated process inherited its
                // PID, but a refusal cannot distinguish the two: macOS also reports ECONNREFUSED for a
                // live listener with a full backlog. Deleting a live AppHost's socket would make it
                // undiscoverable for the rest of its lifetime, so the retry budget is spent and the
                // socket is then parked by MarkUnreachable rather than reclaimed.
                // TODO: Remove old format support after 9.3 is widely adopted (target: 10.0 release)
                if (isFirstAttempt && !pid.HasValue)
                {
                    // Old format socket - use file age heuristic for backward compatibility
                    var fileInfo = new FileInfo(socketPath);
                    if (fileInfo.Exists)
                    {
                        var socketAge = _timeProvider.GetUtcNow() - fileInfo.CreationTimeUtc;
                        if (socketAge.TotalMilliseconds < 500)
                        {
                            logger.LogDebug("Socket connection refused but file is new ({Age}ms old), will retry: {SocketPath}", (int)socketAge.TotalMilliseconds, socketPath);
                            isFirstAttempt = false;
                            continue;
                        }
                    }

                    logger.LogDebug("Socket connection refused (stale socket): {SocketPath}", socketPath);
                    appHostSocket.TryDelete();
                    failedSockets.Add(socketPath);
                    return;
                }

                logger.LogDebug("Socket not ready yet, will retry: {SocketPath}", socketPath);
                isFirstAttempt = false;
            }
            catch (Exception ex)
            {
                socket?.Dispose();
                logger.LogError(ex, "Failed to connect to socket: {SocketPath}", socketPath);
                return;
            }
        }

        if (socket is null || !socket.Connected)
        {
            logger.LogDebug("Socket connection timed out after {ElapsedSeconds} seconds: {SocketPath}", maxElapsed.TotalSeconds, socketPath);
            if (pid is { } pidValue && !BackchannelConstants.ProcessExists(pidValue))
            {
                appHostSocket.TryDelete();
                failedSockets.Add(socketPath);
                return;
            }

            MarkUnreachable(socketPath);
            return;
        }

        try
        {
            // Determine if this AppHost is in scope of the MCP server's working directory
            // We need to do a quick check before full connection to avoid unnecessary work
            var isInScope = true; // Will be updated after we get appHostInfo

            // Use the centralized factory to create the connection
            // This ensures capabilities are always fetched
            var connection = await AppHostAuxiliaryBackchannel.CreateFromSocketAsync(appHostSocket, isInScope, logger, profilingTelemetry, socket, cancellationToken).ConfigureAwait(false);

            // Update isInScope based on actual appHostInfo now that we have it
            connection.IsInScope = IsAppHostInScope(connection.AppHostInfo?.AppHostPath);

            // Set up disconnect handler
            connection.Rpc!.Disconnected += (sender, args) =>
            {
                logger.LogInformation("Disconnected from AppHost at {SocketPath}: {Reason}", socketPath, args.Reason);
                if (_connectionsBySocketPath.TryRemove(socketPath, out var conn))
                {
                    _ = Task.Run(async () => await DisconnectAsync(conn).ConfigureAwait(false));
                    NotifyConnectionsChanged();
                }
            };

            if (_connectionsBySocketPath.TryAdd(socketPath, connection))
            {
                ClearUnreachable(socketPath);
                logger.LogInformation(
                    "Successfully connected to AppHost at {SocketPath}. " +
                    "AppHost Path: {AppHostPath}, " +
                    "AppHost PID: {AppHostPid}, " +
                    "CLI PID: {CliPid}, " +
                    "In Scope: {InScope}, " +
                    "Supports V2: {SupportsV2}",
                    socketPath,
                    connection.AppHostInfo?.AppHostPath ?? "N/A",
                    connection.AppHostInfo?.ProcessId.ToString(CultureInfo.InvariantCulture) ?? "N/A",
                    connection.AppHostInfo?.CliProcessId?.ToString(CultureInfo.InvariantCulture) ?? "N/A",
                    connection.IsInScope,
                    connection.SupportsV2);

                NotifyConnectionsChanged();
            }
            else
            {
                logger.LogWarning("Failed to add connection for socket {SocketPath}", socketPath);
                await DisconnectAsync(connection).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown, not a property of this socket. Leave it known and unpenalized so the next
            // run starts clean.
            logger.LogDebug("Cancelled while establishing the backchannel for socket: {SocketPath}", socketPath);
        }
        catch (Exception ex)
        {
            // The connect succeeded, so the AppHost is listening; only the RPC handshake failed.
            // Back off rather than adding to failedSockets: that would drop the socket from
            // _knownSocketPaths and make it look new again, so every later scan would pay a full
            // connect plus handshake, which is the unbounded-cost shape MarkUnreachable exists to
            // prevent. An AppHost that was merely mid-startup still recovers once the delay expires.
            logger.LogError(ex, "Failed to connect to socket: {SocketPath}", socketPath);
            MarkUnreachable(socketPath);
        }
    }

    private bool IsAppHostInScope(string? appHostPath)
        => IsAppHostInScopeOfDirectory(appHostPath, executionContext.WorkingDirectory.FullName);

    /// <summary>
    /// Claims a due retry for <paramref name="socketPath"/>, deferring it again so that only one scan
    /// retries a given socket at a time.
    /// </summary>
    /// <remarks>
    /// Sockets are selected under <see cref="_scanLock"/>, but the connect attempts are awaited after it
    /// is released and the backoff is only escalated once the retry budget is exhausted. A scan that
    /// overlaps that window would otherwise re-select the same socket and start a second connect loop,
    /// so a single stale socket could still fan out concurrent retries and defeat the backoff. New
    /// sockets need no equivalent claim because <see cref="_knownSocketPaths"/> is repopulated before
    /// the lock is released.
    /// <para>
    /// The delay is carried forward unchanged so that <see cref="MarkUnreachable"/> keeps doubling from
    /// the same point, and the compare-and-swap makes the claim safe even for callers outside the lock.
    /// </para>
    /// </remarks>
    private bool TryClaimRetry(string socketPath)
    {
        if (!_unreachableSockets.TryGetValue(socketPath, out var state))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (now < state.RetryAfter)
        {
            return false;
        }

        return _unreachableSockets.TryUpdate(socketPath, new UnreachableSocket(now + state.Delay, state.Delay), state);
    }

    /// <summary>
    /// Schedules a backed-off retry for a socket we cannot establish a backchannel over but that we
    /// are not permitted to delete.
    /// </summary>
    /// <remarks>
    /// A connect that fails with <see cref="SocketError.ConnectionRefused"/> cannot distinguish an
    /// orphaned socket whose owner's PID has been recycled (so the liveness check wrongly reports the
    /// owner is alive) from a healthy AppHost whose listen backlog is momentarily full. On macOS both
    /// produce ECONNREFUSED. Deleting is therefore unsafe: an AppHost never recreates its socket file,
    /// so removing a live one makes it undiscoverable for the rest of its lifetime, whereas keeping an
    /// orphan only wastes a connect attempt.
    /// <para>
    /// Simply retrying is not viable either. Retry candidates are chosen by diffing against
    /// <see cref="_knownSocketPaths"/>, so re-arming an unreachable socket makes every later scan pay
    /// the full <see cref="s_maxRetryElapsed"/> budget again, indefinitely. Backing off keeps the
    /// recovery path for a transiently saturated AppHost while bounding the cost of an orphan.
    /// </para>
    /// <para>
    /// The delay is measured with <see cref="_timeProvider"/> rather than a monotonic source because a
    /// wall-clock adjustment can only make a retry happen early or late. Unlike comparing timestamps
    /// across processes, it cannot produce a wrong liveness verdict.
    /// </para>
    /// </remarks>
    private void MarkUnreachable(string socketPath)
    {
        var now = _timeProvider.GetUtcNow();
        var state = _unreachableSockets.AddOrUpdate(
            socketPath,
            _ => new UnreachableSocket(now + s_initialUnreachableRetryDelay, s_initialUnreachableRetryDelay),
            (_, existing) =>
            {
                var delay = TimeSpan.FromTicks(Math.Min(existing.Delay.Ticks * 2, s_maxUnreachableRetryDelay.Ticks));
                return new UnreachableSocket(now + delay, delay);
            });

        logger.LogDebug(
            "Socket unreachable, deferring retry for {DelaySeconds}s: {SocketPath}",
            state.Delay.TotalSeconds,
            socketPath);
    }

    private void ClearUnreachable(string socketPath) => _unreachableSockets.TryRemove(socketPath, out _);

    private static async Task DisconnectAsync(IAppHostAuxiliaryBackchannel connection)
    {
        try
        {
            connection.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the file watcher loop that triggers scans when file changes are detected.
    /// </summary>
    private async Task RunFileWatcherLoopAsync(IFileProvider fileProvider, string watchPattern, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var changed in WatchForChangesAsync(fileProvider, watchPattern, cancellationToken))
            {
                await ProcessDirectoryChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown
        }
    }

    /// <summary>
    /// Watches for file changes in the backchannels directory using change tokens.
    /// </summary>
    private static async IAsyncEnumerable<bool> WatchForChangesAsync(IFileProvider fileProvider, string watchPattern, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var changeToken = fileProvider.Watch(watchPattern);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var registration = changeToken.RegisterChangeCallback(state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), tcs);
            using var cancellationRegistration = cancellationToken.Register(() => tcs.TrySetCanceled());

            bool changed;
            try
            {
                changed = await tcs.Task.ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                yield break;
            }

            yield return changed;
        }
    }

    /// <summary>
    /// Backoff state for a socket that could not be reached and cannot safely be deleted.
    /// </summary>
    /// <param name="RetryAfter">The earliest time another connect attempt should be made.</param>
    /// <param name="Delay">The delay that produced <paramref name="RetryAfter"/>, doubled on each successive failure.</param>
    private sealed record UnreachableSocket(DateTimeOffset RetryAfter, TimeSpan Delay);
}
