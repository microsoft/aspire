// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Configures a private directory for Hex1b's Windows PTY helper sockets.
/// </summary>
internal static class Hex1bPtySocketHelper
{
    internal const string SocketDirectoryEnvironmentVariable = "HEX1B_PTY_SHIM_SOCKET_DIR";

    private static readonly object s_lock = new();

    internal static void Configure()
    {
        // Unix PTYs do not use the hex1bpty helper or its filesystem socket.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        lock (s_lock)
        {
            var directory = Environment.GetEnvironmentVariable(SocketDirectoryEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(directory))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home))
                {
                    throw new InvalidOperationException("Cannot configure the PTY socket directory without a user profile directory.");
                }

                directory = Path.Combine(home, ".aspire", "pty");
            }

            directory = SocketPermissionHelper.CreateDirectory(directory).FullName;

            // Hex1b reads the parent's process environment when the deferred PTY workload starts,
            // then passes the full socket path to hex1bpty via --socket. The workload's Environment
            // dictionary is too late. Keep this process-wide override after terminal/AppHost disposal
            // so concurrent and lazily started terminals continue using the secured directory.
            // Replace with per-workload configuration when Hex1b exposes a socket-path option.
            // https://github.com/mitchdenny/hex1b/blob/v0.168.0/src/Hex1b/WindowsPtySocketPaths.cs
            Environment.SetEnvironmentVariable(SocketDirectoryEnvironmentVariable, directory);
        }
    }
}
