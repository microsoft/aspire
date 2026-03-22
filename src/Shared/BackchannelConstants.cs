// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Aspire.Hosting.Backchannel;

/// <summary>
/// Shared constants and helpers for backchannel socket communication between
/// AppHost and CLI. These MUST stay in sync between both components.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Architecture Overview</strong>
/// </para>
/// <para>
/// The backchannel is a Unix domain socket that enables bidirectional communication:
/// </para>
/// <list type="bullet">
/// <item>CLI → AppHost: Commands (stop, get info, etc.)</item>
/// <item>AppHost → CLI: Status updates, events</item>
/// </list>
/// <para>
/// <strong>Socket File Location</strong>
/// </para>
/// <para>
/// Socket files are stored in: <c>~/.aspire/cli/backchannels/</c>
/// </para>
/// <para>
/// <strong>Socket Naming Format</strong>
/// </para>
/// <para>
/// New format: <c>auxi.sock.{hash}.{pid}</c>
/// </para>
/// <list type="bullet">
/// <item><c>auxi.sock</c> - Prefix (not "aux" because that's reserved on Windows)</item>
/// <item><c>{hash}</c> - SHA256(AppHost project path)[0:16] - identifies the AppHost project</item>
/// <item><c>{pid}</c> - Process ID of the AppHost - identifies the specific instance</item>
/// </list>
/// <para>
/// Old format (for backward compatibility): <c>auxi.sock.{hash}</c>
/// </para>
/// <para>
/// <strong>Why PID in the Filename?</strong>
/// </para>
/// <list type="bullet">
/// <item>Multiple instances of the same AppHost can run simultaneously</item>
/// <item>Orphan detection: if PID doesn't exist, socket is orphaned and can be deleted</item>
/// <item>Fast cleanup without needing to attempt connection</item>
/// </list>
/// <para>
/// <strong>Backward Compatibility</strong>
/// </para>
/// <para>
/// Old CLI versions use glob pattern <c>aux*.sock.*</c> which matches the new format.
/// Old CLIs will work with new AppHosts, they just won't benefit from PID-based orphan detection.
/// </para>
/// </remarks>
internal static class BackchannelConstants
{
    /// <summary>
    /// Prefix for auxiliary backchannel sockets.
    /// </summary>
    /// <remarks>
    /// Uses "auxi" instead of "aux" because "aux" is a reserved device name on Windows
    /// (from DOS days: CON, PRN, AUX, NUL, COM1-9, LPT1-9). Using "aux" causes
    /// "SocketException: A socket operation encountered a dead network" on Windows.
    /// </remarks>
    public const string SocketPrefix = "auxi.sock";

    /// <summary>
    /// Number of hex characters to use from the SHA256 hash.
    /// </summary>
    /// <remarks>
    /// Using 16 chars (64 bits) balances uniqueness against path length constraints.
    /// Unix socket paths are limited to ~104 characters on most systems.
    /// Full path example: ~/.aspire/cli/backchannels/auxi.sock.bc43b855b6848166.46730
    /// = ~65 characters, well under the limit.
    /// </remarks>
    public const int HashLength = 16;

    /// <summary>
    /// Gets the backchannels directory path for the given home directory.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The full path to the backchannels directory.</returns>
    public static string GetBackchannelsDirectory(string homeDirectory)
        => Path.Combine(homeDirectory, ".aspire", "cli", "backchannels");

    /// <summary>
    /// Computes the hash portion of the socket name from an AppHost path.
    /// </summary>
    /// <remarks>
    /// The hash is case-sensitive. On case-insensitive file systems (Windows, macOS with default settings),
    /// "C:\App\MyApp.csproj" and "C:\app\myapp.csproj" will produce different hashes even though they
    /// reference the same file. This is acceptable because the CLI typically uses the exact path provided
    /// by MSBuild or the user, which should be consistent within a session.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <returns>A 16-character lowercase hex string.</returns>
    public static string ComputeHash(string appHostPath)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(appHostPath));
        return Convert.ToHexString(hashBytes)[..HashLength].ToLowerInvariant();
    }

    /// <summary>
    /// Computes the full socket path for an AppHost instance.
    /// </summary>
    /// <remarks>
    /// Called by AppHost when creating the socket. Includes the PID to ensure
    /// uniqueness across multiple instances of the same AppHost.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="processId">The process ID of the AppHost.</param>
    /// <returns>The full socket path including PID.</returns>
    public static string ComputeSocketPath(string appHostPath, string homeDirectory, int processId)
    {
        var dir = GetBackchannelsDirectory(homeDirectory);
        var hash = ComputeHash(appHostPath);
        return Path.Combine(dir, $"{SocketPrefix}.{hash}.{processId}");
    }

    /// <summary>
    /// Computes the socket path prefix for finding sockets (without PID).
    /// </summary>
    /// <remarks>
    /// Called by CLI when searching for sockets. Since the CLI doesn't know the
    /// AppHost's PID, it uses this prefix with a glob pattern to find matching sockets.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The socket path prefix (without PID suffix).</returns>
    public static string ComputeSocketPrefix(string appHostPath, string homeDirectory)
    {
        var dir = GetBackchannelsDirectory(homeDirectory);
        var hash = ComputeHash(appHostPath);
        return Path.Combine(dir, $"{SocketPrefix}.{hash}");
    }

    /// <summary>
    /// Finds all socket files matching the given AppHost path.
    /// </summary>
    /// <remarks>
    /// Returns all socket files for an AppHost, regardless of PID. This includes
    /// both old format (<c>auxi.sock.{hash}</c>) and new format (<c>auxi.sock.{hash}.{pid}</c>).
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>An array of socket file paths, or empty if none found.</returns>
    public static string[] FindMatchingSockets(string appHostPath, string homeDirectory)
    {
        var prefix = ComputeSocketPrefix(appHostPath, homeDirectory);
        var dir = Path.GetDirectoryName(prefix);
        var prefixFileName = Path.GetFileName(prefix);

        if (dir is null || !Directory.Exists(dir))
        {
            return [];
        }

        // Match both old format (auxi.sock.{hash}) and new format (auxi.sock.{hash}.{pid})
        // Use pattern with "*" to match optional PID suffix
        var allMatches = Directory.GetFiles(dir, prefixFileName + "*");
        
        // Filter to only include exact match (old format) or .{pid} suffix (new format)
        // This avoids matching auxi.sock.{hash}abc (different hash that starts with same chars)
        // and also avoids matching files like auxi.sock.{hash}.12345.bak
        return allMatches.Where(f =>
        {
            var fileName = Path.GetFileName(f);
            if (fileName == prefixFileName)
            {
                return true; // Old format: exact match
            }
            if (fileName.StartsWith(prefixFileName + ".", StringComparison.Ordinal) &&
                int.TryParse(fileName.AsSpan(prefixFileName.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return true; // New format: prefix followed by dot and integer PID
            }
            return false;
        }).ToArray();
    }

    /// <summary>
    /// Extracts the hash from a socket filename.
    /// </summary>
    /// <remarks>
    /// Works with both old format (<c>auxi.sock.{hash}</c>) and new format (<c>auxi.sock.{hash}.{pid}</c>).
    /// </remarks>
    /// <param name="socketPath">The full socket path or filename.</param>
    /// <returns>The hash portion, or <c>null</c> if the format is unrecognized.</returns>
    public static string? ExtractHash(string socketPath)
    {
        var fileName = Path.GetFileName(socketPath);

        // Handle new format: auxi.sock.{hash}.{pid}
        // Handle old format: auxi.sock.{hash}
        if (fileName.StartsWith($"{SocketPrefix}.", StringComparison.Ordinal))
        {
            var afterPrefix = fileName[($"{SocketPrefix}.".Length)..];
            // If there's another dot, it's new format - return just the hash part
            var dotIndex = afterPrefix.IndexOf('.');
            return dotIndex > 0 ? afterPrefix[..dotIndex] : afterPrefix;
        }

        // Handle legacy format: aux.sock.{hash}
        if (fileName.StartsWith("aux.sock.", StringComparison.Ordinal))
        {
            var afterPrefix = fileName["aux.sock.".Length..];
            var dotIndex = afterPrefix.IndexOf('.');
            return dotIndex > 0 ? afterPrefix[..dotIndex] : afterPrefix;
        }

        return null;
    }

    /// <summary>
    /// Extracts the PID from a socket filename (new format only).
    /// </summary>
    /// <param name="socketPath">The full socket path or filename.</param>
    /// <returns>The PID if present and valid, or <c>null</c> for old format sockets.</returns>
    public static int? ExtractPid(string socketPath)
    {
        var fileName = Path.GetFileName(socketPath);
        var lastDot = fileName.LastIndexOf('.');
        if (lastDot > 0 && int.TryParse(fileName[(lastDot + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
        {
            return pid;
        }
        return null;
    }

    /// <summary>
    /// Checks if a process with the given PID exists and is running.
    /// </summary>
    /// <remarks>
    /// Used for orphan detection. If the PID from a socket filename doesn't correspond
    /// to a running process, the socket is orphaned and can be safely deleted.
    /// </remarks>
    /// <param name="pid">The process ID to check.</param>
    /// <returns><c>true</c> if the process exists and is running; otherwise, <c>false</c>.</returns>
    public static bool ProcessExists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process doesn't exist
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has exited
            return false;
        }
    }

    /// <summary>
    /// Cleans up orphaned socket files for a specific AppHost hash.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called by AppHost on startup to clean up sockets from previous crashed instances.
    /// This ensures orphan cleanup happens even if the user has an old CLI that doesn't
    /// support PID-based orphan detection.
    /// </para>
    /// <para>
    /// <strong>Limitation:</strong> This method only cleans up new format sockets (<c>auxi.sock.{hash}.{pid}</c>)
    /// because old format sockets (<c>auxi.sock.{hash}</c>) don't have a PID for orphan detection.
    /// Old format sockets are cleaned up via connection-based detection in the CLI.
    /// </para>
    /// </remarks>
    /// <param name="backchannelsDirectory">The backchannels directory path.</param>
    /// <param name="hash">The AppHost hash to match.</param>
    /// <param name="currentPid">The current process ID (to avoid deleting own socket).</param>
    /// <returns>The number of orphaned sockets deleted.</returns>
    public static int CleanupOrphanedSockets(string backchannelsDirectory, string hash, int currentPid)
    {
        var deleted = 0;

        if (!Directory.Exists(backchannelsDirectory))
        {
            return deleted;
        }

        // Find all sockets for this hash (both old and new format)
        var pattern = $"{SocketPrefix}.{hash}*";
        foreach (var socketPath in Directory.GetFiles(backchannelsDirectory, pattern))
        {
            var pid = ExtractPid(socketPath);
            if (pid.HasValue && pid.Value != currentPid && !ProcessExists(pid.Value))
            {
                try
                {
                    // Double-check before delete to minimize TOCTOU race window
                    // (A new process could theoretically start with the same PID between our checks)
                    if (!ProcessExists(pid.Value))
                    {
                        File.Delete(socketPath);
                        deleted++;
                    }
                }
                catch
                {
                    // Ignore deletion failures
                }
            }
        }

        return deleted;
    }
}
