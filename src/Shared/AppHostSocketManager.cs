// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Represents a discovered auxiliary backchannel socket.
/// </summary>
internal interface IAppHostSocket
{
    /// <summary>
    /// Gets the socket's filesystem path.
    /// </summary>
    string SocketPath { get; }

    /// <summary>
    /// Gets the owning process identifier encoded in the socket name, when available.
    /// </summary>
    int? ProcessId { get; }

    /// <summary>
    /// Opens a connection to the socket.
    /// </summary>
    ValueTask<Socket> ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to delete the socket file.
    /// </summary>
    bool TryDelete();
}

/// <summary>
/// A directory that can contain auxiliary backchannel sockets, paired with the glob pattern that
/// matches candidate socket files within it.
/// </summary>
/// <param name="DirectoryPath">The directory to search or watch.</param>
/// <param name="SearchPattern">The glob pattern matching candidate socket files.</param>
internal sealed record AppHostSocketDirectory(string DirectoryPath, string SearchPattern);

/// <summary>
/// Creates and finds auxiliary backchannel sockets for an AppHost.
/// </summary>
internal static class AppHostSocketManager
{
    private const int ListenBacklog = 10;

    /// <summary>
    /// Creates a bound, listening auxiliary backchannel socket for an AppHost.
    /// </summary>
    public static AppHostSocketListener CreateSocket(
        string? appHostPath,
        string homeDirectory,
        int processId,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var backchannelsDirectory = BackchannelConstants.GetBackchannelsDirectory(homeDirectory);
        Directory.CreateDirectory(backchannelsDirectory);

        string appHostId;
        if (string.IsNullOrEmpty(appHostPath))
        {
            appHostId = BackchannelConstants.ComputeAppHostId(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            PruneOrphanedSockets(appHostPath, homeDirectory, processId, logger);
            appHostId = BackchannelConstants.ComputeAppHostId(PathNormalizer.ResolveToFilesystemPath(appHostPath));
        }

        string socketPath;
        do
        {
            socketPath = BackchannelConstants.ComputeSocketPathFromAppHostId(appHostId, homeDirectory, processId);
        }
        while (File.Exists(socketPath));

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            var appHostSocket = new AppHostSocket(socketPath, logger);
            var endpoint = new UnixDomainSocketEndPoint(appHostSocket.SocketPath);
            socket.Bind(endpoint);
            socket.Listen(ListenBacklog);
            return new AppHostSocketListener(socket, appHostSocket);
        }
        catch
        {
            socket.Dispose();
            new AppHostSocket(socketPath, logger).TryDelete();
            throw;
        }
    }

    /// <summary>
    /// Finds candidate auxiliary backchannel sockets belonging to an AppHost.
    /// PID-qualified orphaned sockets are removed before results are returned.
    /// </summary>
    public static IReadOnlyList<IAppHostSocket> FindSockets(
        string appHostPath,
        string homeDirectory,
        int currentProcessId,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(appHostPath);
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var socketPaths = GetSocketKeyPaths(appHostPath)
            .SelectMany(path => BackchannelConstants.FindMatchingSockets(path, homeDirectory))
            .Distinct(StringComparers.FileSystemPath);
        return CreateSocketHandles(socketPaths, currentProcessId, logger);
    }

    /// <summary>
    /// Gets the directories that can contain auxiliary backchannel sockets, in search order.
    /// </summary>
    /// <remarks>
    /// Compact socket file names carry no prefix so the socket path stays under the platform's
    /// AF_UNIX byte limit, so that directory must be searched with a match-all pattern and the
    /// results filtered by name format. Released AppHosts wrote to a separate legacy directory
    /// using both <c>auxi.sock.</c> and <c>aux.sock.</c> prefixes.
    /// </remarks>
    public static IReadOnlyList<AppHostSocketDirectory> GetSocketDirectories(string homeDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);

        return
        [
            new AppHostSocketDirectory(BackchannelConstants.GetBackchannelsDirectory(homeDirectory), "*"),
            new AppHostSocketDirectory(BackchannelConstants.GetLegacyBackchannelsDirectory(homeDirectory), "aux*.sock.*")
        ];
    }

    /// <summary>
    /// Finds all candidate auxiliary backchannel sockets.
    /// PID-qualified orphaned sockets are removed before results are returned.
    /// </summary>
    public static IReadOnlyList<IAppHostSocket> FindSockets(
        string homeDirectory,
        int currentProcessId,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        var socketPaths = GetSocketDirectories(homeDirectory)
            .Where(socketDirectory => Directory.Exists(socketDirectory.DirectoryPath))
            .SelectMany(socketDirectory => Directory.GetFiles(socketDirectory.DirectoryPath, socketDirectory.SearchPattern))
            .Where(socketPath => BackchannelConstants.ExtractHash(socketPath) is not null);

        return CreateSocketHandles(socketPaths, currentProcessId, logger);
    }

    /// <summary>
    /// Deletes auxiliary backchannel sockets left behind by crashed instances of the same AppHost.
    /// </summary>
    /// <remarks>
    /// Enumerating an AppHost's sockets prunes any whose owning process is gone. The surviving
    /// handles are discarded here because the caller is about to create its own socket and only
    /// needs the cleanup side effect.
    /// </remarks>
    private static void PruneOrphanedSockets(string appHostPath, string homeDirectory, int currentProcessId, ILogger logger)
    {
        _ = FindSockets(appHostPath, homeDirectory, currentProcessId, logger);
    }

    /// <summary>
    /// Gets the socket keys to search for an AppHost, in order of preference.
    /// </summary>
    /// <remarks>
    /// A socket is keyed off the path spelling its producer was given, so an AppHost started before
    /// a CLI upgrade can be keyed off a spelling the current CLI no longer computes. Searching the
    /// canonical path, the symlink-resolved path, and the raw path covers the spellings previous
    /// releases produced.
    /// <para>
    /// Every key is derived from the caller's spelling, so this cannot match a socket whose producer
    /// used a different casing than the caller does on a case-insensitive filesystem. Closing that
    /// gap would mean connecting to every running AppHost and comparing the path each one reports,
    /// which defeats the purpose of addressing an AppHost by path. Restarting the AppHost keys its
    /// socket canonically and resolves it permanently.
    /// </para>
    /// </remarks>
    private static string[] GetSocketKeyPaths(string appHostPath)
    {
        return
        [
            .. new[]
            {
                PathNormalizer.ResolveToFilesystemPath(appHostPath),
                PathNormalizer.ResolveSymlinks(appHostPath),
                appHostPath
            }.Distinct(StringComparer.Ordinal)
        ];
    }

    private static IReadOnlyList<IAppHostSocket> CreateSocketHandles(
        IEnumerable<string> socketPaths,
        int currentProcessId,
        ILogger logger)
    {
        var sockets = new List<IAppHostSocket>();
        foreach (var socketPath in socketPaths.Distinct(StringComparers.FileSystemPath))
        {
            var appHostSocket = new AppHostSocket(socketPath, logger);
            var pid = BackchannelConstants.ExtractPid(socketPath);
            // A dead PID is a durable verdict: the owning AppHost cannot come back, and it never
            // recreates a socket file it already bound. PID reuse can only produce a false "alive",
            // which just costs a wasted connect attempt, so no recheck is warranted here.
            if (pid is { } pidValue &&
                pidValue != currentProcessId &&
                !BackchannelConstants.ProcessExists(pidValue))
            {
                appHostSocket.TryDelete();
                continue;
            }

            sockets.Add(appHostSocket);
        }

        return sockets;
    }

    private sealed class AppHostSocket(string socketPath, ILogger logger) : IAppHostSocket
    {
        public string SocketPath { get; } = socketPath;

        public int? ProcessId { get; } = BackchannelConstants.ExtractPid(socketPath);

        public async ValueTask<Socket> ConnectAsync(CancellationToken cancellationToken)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancellationToken).ConfigureAwait(false);
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public bool TryDelete()
        {
            try
            {
                if (File.Exists(SocketPath))
                {
                    File.Delete(SocketPath);
                    logger.LogDebug("Cleaned up backchannel socket file: {SocketPath}", SocketPath);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Failed to clean up backchannel socket file: {SocketPath}", SocketPath);
            }

            return false;
        }
    }

    internal sealed class AppHostSocketListener(
        Socket socket,
        IAppHostSocket appHostSocket) : IDisposable
    {
        private bool _disposed;

        public Socket Socket { get; } = socket;

        public string SocketPath => appHostSocket.SocketPath;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Socket.Dispose();
            appHostSocket.TryDelete();
        }
    }
}
