// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using Aspire.Shared;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Configures a private directory for Hex1b's Windows PTY helper sockets.
/// </summary>
internal static class Hex1bPtySocketHelper
{
    internal const string SocketDirectoryEnvironmentVariable = "HEX1B_PTY_SHIM_SOCKET_DIR";

    internal static string? CreateSocketPath()
    {
        // Unix PTYs do not use the hex1bpty helper or its filesystem socket.
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var directory = Environment.GetEnvironmentVariable(SocketDirectoryEnvironmentVariable);
        var useDefaultDirectory = string.IsNullOrWhiteSpace(directory);
        if (string.IsNullOrWhiteSpace(directory))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrWhiteSpace(home))
            {
                throw new InvalidOperationException("Cannot configure the PTY socket directory without a user profile directory.");
            }

            directory = Path.Combine(home, SocketDirectoryNames.Aspire, SocketDirectoryNames.Pty);
        }

        directory = Path.GetFullPath(directory);
        // Hex1b secures its immediate parent again at startup. Isolate that ACL rewrite
        // from a caller's validated override (which may also allow SYSTEM).
        var proxyDirectory = Path.Combine(directory, "proxy");
        var socketPath = Path.Combine(proxyDirectory, $"{Guid.NewGuid():N}.socket");
        // Validate the native endpoint length before creating directories. Hex1b snapshots this
        // exact path, so later environment changes cannot redirect a deferred workload.
        _ = new UnixDomainSocketEndPoint(socketPath);
        SocketPermissionHelper.CreateDirectory(directory, repairExisting: useDefaultDirectory);
        SocketPermissionHelper.CreateDirectory(proxyDirectory, repairExisting: true);

        return socketPath;
    }
}
