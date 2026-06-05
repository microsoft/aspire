// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Cli.Commands;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting.Backchannel;
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
    private static readonly TimeSpan s_maxRetryElapsed = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan s_maxRetryDelay = TimeSpan.FromSeconds(1);

    // Compact socket file names have no prefix to reduce the path length as much as possible and avoid socket path length limits,
    // which are much shorter than typical file path limits (e.g. 108 BYTES on Windows/Linux).
    // But this means we need to watch for all files while keeping backchannel sockets in a directory separate 
    // from other CLI-managed files.
    private const string CompactSocketWatchPattern = "*";
    private const string LegacySocketWatchPattern = "aux*.sock.*";

    // Outer key: hash (prefix), Inner key: socketPath, Value: connection
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AppHostAuxiliaryBackchannel>> _connectionsByHash = new();
    private readonly string _backchannelsDirectory = BackchannelConstants.GetBackchannelsDirectory(GetHomeDirectory());
    private readonly string _legacyBackchannelsDirectory = BackchannelConstants.GetLegacyBackchannelsDirectory(GetHomeDirectory());

    // Track known socket files to detect additions and removals
    private readonly HashSet<string> _knownSocketFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly TimeProvider _timeProvider = timeProvider;
    private event Action? ConnectionsChanged;

    /// <summary>
    /// Gets all active AppHost connections, flattened from all hashes.
    /// </summary>
    public IEnumerable<IAppHostAuxiliaryBackchannel> Connections =>
        _connectionsByHash.Values.SelectMany(d => d.Values);

    /// <summary>
    /// Gets connections for a specific AppHost hash (prefix).
    /// </summary>
    /// <param name="hash">The AppHost hash.</param>
    /// <returns>All connections for the given hash, or empty if none.</returns>
    public IEnumerable<IAppHostAuxiliaryBackchannel> GetConnectionsByHash(string hash) =>
        _connectionsByHash.TryGetValue(hash, out var connections) ? connections.Values : [];

    public async IAsyncEnumerable<IReadOnlyList<IAppHostAuxiliaryBackchannel>> WatchConnectionsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var connectionChanges = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true
        });
        void QueueConnectionChange() => connectionChanges.Writer.TryWrite(true);

        ConnectionsChanged += QueueConnectionChange;

        try
        {
            Directory.CreateDirectory(_backchannelsDirectory);
            Directory.CreateDirectory(_legacyBackchannelsDirectory);

            await ProcessDirectoryChangesAsync(cancellationToken).ConfigureAwait(false);
            yield return Connections.ToList();

            using var fileProvider = new PhysicalFileProvider(_backchannelsDirectory);
            fileProvider.UsePollingFileWatcher = true;
            fileProvider.UseActivePolling = true;
            using var legacyFileProvider = new PhysicalFileProvider(_legacyBackchannelsDirectory);
            legacyFileProvider.UsePollingFileWatcher = true;
            legacyFileProvider.UseActivePolling = true;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(
                        WatchConnectionChangesAsync(fileProvider, CompactSocketWatchPattern, cancellationToken),
                        WatchConnectionChangesAsync(legacyFileProvider, LegacySocketWatchPattern, cancellationToken)).ConfigureAwait(false);
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
            var connections = Connections.ToList();

            if (connections.Count == 0)
            {
                return null;
            }

            // Check if a specific AppHost was selected
            if (!string.IsNullOrEmpty(SelectedAppHostPath))
            {
                var selectedConnection = connections.FirstOrDefault(c =>
                    c.AppHostInfo?.AppHostPath != null &&
                    string.Equals(Path.GetFullPath(c.AppHostInfo.AppHostPath), Path.GetFullPath(SelectedAppHostPath), StringComparison.OrdinalIgnoreCase));

                if (selectedConnection != null)
                {
                    return selectedConnection;
                }

                // Clear the selection since the AppHost is no longer available
                SelectedAppHostPath = null;
            }

            // Look for in-scope connections
            var inScopeConnections = connections.Where(c => c.IsInScope).ToList();

            if (inScopeConnections.Count == 1)
            {
                return inScopeConnections[0];
            }

            // Fall back to the first available connection
            return connections.FirstOrDefault();
        }
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

    private static bool IsAppHostInScopeOfDirectory(string? appHostPath, string workingDirectory)
    {
        if (string.IsNullOrEmpty(appHostPath))
        {
            return false;
        }

        // Normalize the paths for comparison
        var normalizedWorkingDirectory = Path.GetFullPath(workingDirectory);
        var normalizedAppHostPath = Path.GetFullPath(appHostPath);

        // Check if the AppHost path is within the working directory
        var relativePath = Path.GetRelativePath(normalizedWorkingDirectory, normalizedAppHostPath);
        return !relativePath.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativePath);
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

            // Ensure both directories exist so the monitor can see compact sockets and
            // sockets created by older AppHosts that still use the legacy location.
            Directory.CreateDirectory(_backchannelsDirectory);
            Directory.CreateDirectory(_legacyBackchannelsDirectory);

            // Scan for existing sockets on startup.
            await ProcessDirectoryChangesAsync(stoppingToken).ConfigureAwait(false);

            // Use file watcher with polling enabled for reliability.
            using var fileProvider = new PhysicalFileProvider(_backchannelsDirectory);
            fileProvider.UsePollingFileWatcher = true;
            fileProvider.UseActivePolling = true;
            using var legacyFileProvider = new PhysicalFileProvider(_legacyBackchannelsDirectory);
            legacyFileProvider.UsePollingFileWatcher = true;
            legacyFileProvider.UseActivePolling = true;

            // Run the watcher loop until cancellation
            var fileWatcherTask = Task.WhenAll(
                RunFileWatcherLoopAsync(fileProvider, CompactSocketWatchPattern, stoppingToken),
                RunFileWatcherLoopAsync(legacyFileProvider, LegacySocketWatchPattern, stoppingToken));

            await fileWatcherTask.ConfigureAwait(false);
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
            _connectionsByHash.Clear();
        }
    }

    private async Task UpdateConnectionsAsync(CancellationToken cancellationToken)
    {
        await ProcessDirectoryChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Task>> ProcessDirectoryChangesAsync(CancellationToken cancellationToken)
    {
        var connectTasks = new List<Task>();
        var failedSockets = new ConcurrentBag<string>();

        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentFiles = new HashSet<string>(GetSocketFiles(), StringComparer.OrdinalIgnoreCase);

            // Find new files (files that exist now but weren't known before)
            var newFiles = currentFiles.Except(_knownSocketFiles, StringComparer.OrdinalIgnoreCase).ToList();
            connectTasks.EnsureCapacity(newFiles.Count);
            foreach (var newFile in newFiles)
            {
                logger.LogDebug("Socket created: {SocketPath}", newFile);
                connectTasks.Add(TryConnectToSocketAsync(newFile, failedSockets, cancellationToken));
            }

            // Find removed files (files that were known but no longer exist)
            var removedFiles = _knownSocketFiles.Except(currentFiles, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var removedFile in removedFiles)
            {
                logger.LogDebug("Socket deleted: {SocketPath}", removedFile);
                var hash = AppHostHelper.ExtractHashFromSocketPath(removedFile);
                if (!string.IsNullOrEmpty(hash) &&
                    _connectionsByHash.TryGetValue(hash, out var connectionsForHash) &&
                    connectionsForHash.TryRemove(removedFile, out var connection))
                {
                    _ = Task.Run(async () => await DisconnectAsync(connection).ConfigureAwait(false), CancellationToken.None);

                    // Clean up empty hash entries
                    if (connectionsForHash.IsEmpty)
                    {
                        _connectionsByHash.TryRemove(hash, out _);
                    }
                }
            }

            // Update the known files set
            _knownSocketFiles.Clear();
            foreach (var file in currentFiles)
            {
                _knownSocketFiles.Add(file);
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

        // Remove failed sockets from known files so they can be retried on next scan
        foreach (var failedSocket in failedSockets)
        {
            if (_knownSocketFiles.Remove(failedSocket))
            {
                logger.LogDebug("Marked failed socket for retry on next scan: {SocketPath}", failedSocket);
            }
        }

        return connectTasks;
    }

    private async Task TryConnectToSocketAsync(string socketPath, ConcurrentBag<string> failedSockets, CancellationToken cancellationToken)
    {
        var hash = AppHostHelper.ExtractHashFromSocketPath(socketPath);
        if (string.IsNullOrEmpty(hash))
        {
            logger.LogWarning("Could not extract hash from socket path: {SocketPath}", socketPath);
            failedSockets.Add(socketPath);
            return;
        }

        // Check if we're already connected to this specific socket
        if (_connectionsByHash.TryGetValue(hash, out var existingConnections) &&
            existingConnections.ContainsKey(socketPath))
        {
            logger.LogDebug("Already connected to socket: {SocketPath}", socketPath);
            return;
        }

        // PID-based orphan detection (for new format sockets with PID in filename)
        var pid = AppHostHelper.ExtractPidFromSocketPath(socketPath);
        if (pid is { } pidValue && !AppHostHelper.ProcessExists(pidValue))
        {
            logger.LogDebug("Socket is orphaned (PID {Pid} not running), skipping: {SocketPath}", pidValue, socketPath);
            // Clean up the orphaned socket with double-check to minimize TOCTOU race window
            // (A new process could theoretically start with the same PID between our checks)
            try
            {
                if (!AppHostHelper.ProcessExists(pidValue))
                {
                    File.Delete(socketPath);
                    logger.LogDebug("Deleted orphaned socket: {SocketPath}", socketPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to delete orphaned socket: {SocketPath}", socketPath);
            }
            failedSockets.Add(socketPath);
            return;
        }

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
                    logger.LogInformation("Connecting to auxiliary socket: {SocketPath}", socketPath);
                }
                else
                {
                    logger.LogDebug("Retrying connection to auxiliary socket: {SocketPath}", socketPath);
                }

                // Connect to the Unix socket
                socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var endpoint = new UnixDomainSocketEndPoint(socketPath);

                await socket.ConnectAsync(endpoint, cancellationToken).ConfigureAwait(false);
                break; // Success - exit retry loop
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused)
            {
                socket?.Dispose();
                socket = null;

                // For sockets without PID (old format from versions before 9.3), if connection is refused and file is old, it's stale.
                // For sockets with PID, we already checked process existence above, so this is transient.
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
            failedSockets.Add(socketPath);
            return;
        }

        try
        {
            // Determine if this AppHost is in scope of the MCP server's working directory
            // We need to do a quick check before full connection to avoid unnecessary work
            var isInScope = true; // Will be updated after we get appHostInfo

            // Use the centralized factory to create the connection
            // This ensures capabilities are always fetched
            var connection = await AppHostAuxiliaryBackchannel.CreateFromSocketAsync(hash, socketPath, isInScope, logger, socket, cancellationToken, profilingTelemetry).ConfigureAwait(false);

            // Update isInScope based on actual appHostInfo now that we have it
            connection.IsInScope = IsAppHostInScope(connection.AppHostInfo?.AppHostPath);

            // Set up disconnect handler
            connection.Rpc!.Disconnected += (sender, args) =>
            {
                logger.LogInformation("Disconnected from AppHost at {SocketPath}: {Reason}", socketPath, args.Reason);
                if (_connectionsByHash.TryGetValue(hash, out var connectionsForHash) &&
                    connectionsForHash.TryRemove(socketPath, out var conn))
                {
                    _ = Task.Run(async () => await DisconnectAsync(conn).ConfigureAwait(false));

                    // Clean up empty hash entries
                    if (connectionsForHash.IsEmpty)
                    {
                        _connectionsByHash.TryRemove(hash, out _);
                    }

                    NotifyConnectionsChanged();
                }
            };

            // Get or create the inner dictionary for this hash
            var connectionsDict = _connectionsByHash.GetOrAdd(hash, _ => new ConcurrentDictionary<string, AppHostAuxiliaryBackchannel>());

            if (connectionsDict.TryAdd(socketPath, connection))
            {
                logger.LogInformation(
                    "Successfully connected to AppHost at {SocketPath}. " +
                    "Hash: {Hash}, " +
                    "AppHost Path: {AppHostPath}, " +
                    "AppHost PID: {AppHostPid}, " +
                    "CLI PID: {CliPid}, " +
                    "In Scope: {InScope}, " +
                    "Supports V2: {SupportsV2}",
                    socketPath,
                    hash,
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to socket: {SocketPath}", socketPath);
            failedSockets.Add(socketPath);
        }
    }

    private bool IsAppHostInScope(string? appHostPath)
    {
        if (string.IsNullOrEmpty(appHostPath))
        {
            return false;
        }

        // Normalize the paths for comparison
        var workingDirectory = Path.GetFullPath(executionContext.WorkingDirectory.FullName);
        var normalizedAppHostPath = Path.GetFullPath(appHostPath);

        // Check if the AppHost path is within the working directory using a robust, cross-platform method
        var relativePath = Path.GetRelativePath(workingDirectory, normalizedAppHostPath);
        // If the relative path starts with ".." or is equal to "..", then it's outside the working directory
        return !relativePath.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relativePath);
    }

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

    private IEnumerable<string> GetSocketFiles()
    {
        if (Directory.Exists(_backchannelsDirectory))
        {
            foreach (var socketPath in Directory.GetFiles(_backchannelsDirectory))
            {
                if (AppHostHelper.ExtractHashFromSocketPath(socketPath) is not null)
                {
                    yield return socketPath;
                }
            }
        }

        if (Directory.Exists(_legacyBackchannelsDirectory))
        {
            // Support both "auxi.sock.*" and "aux.sock.*" for backward compatibility.
            // Note: "aux" is a reserved device name on Windows < 11, but we still scan for it
            // to support sockets created by older AppHost versions.
            foreach (var socketPath in Directory.GetFiles(_legacyBackchannelsDirectory, "aux*.sock.*"))
            {
                yield return socketPath;
            }
        }
    }

    private static string GetHomeDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

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

}
