// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
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
/// <item>CLI -> AppHost: Commands (stop, get info, etc.)</item>
/// <item>AppHost -> CLI: Status updates, events</item>
/// </list>
/// <para>
/// <strong>Socket File Location</strong>
/// </para>
/// <para>
/// Compact socket files are stored in: <c>~/.aspire/cli/bch/</c>
/// </para>
/// <para>
/// <strong>Socket Naming Format</strong>
/// </para>
/// <para>
/// Compact format: <c>{appHostId}{instanceId}.{pid}</c>
/// </para>
/// <list type="bullet">
/// <item><c>{appHostId}</c> - xxHash(AppHost project path) encoded as 11 base64url chars - identifies the AppHost project</item>
/// <item><c>{instanceId}</c> - 48-bit random identifier encoded as 8 base64url chars - makes each socket name non-deterministic</item>
/// <item><c>{pid}</c> - Process ID of the AppHost - identifies the specific instance</item>
/// </list>
/// <para>
/// Legacy current format: <c>auxi.sock.{appHostHash}.{instanceHash}.{pid}</c>
/// </para>
/// <para>
/// Legacy previous format: <c>auxi.sock.{appHostHash}.{pid}</c>
/// </para>
/// <para>
/// Legacy old format: <c>auxi.sock.{appHostHash}</c>
/// </para>
/// </remarks>
internal static class BackchannelConstants
{
    /// <summary>
    /// Prefix for legacy auxiliary backchannel sockets.
    /// </summary>
    /// <remarks>
    /// Uses "auxi" instead of "aux" because "aux" is a reserved device name on Windows
    /// (from DOS days: CON, PRN, AUX, NUL, COM1-9, LPT1-9). Using "aux" causes
    /// "SocketException: A socket operation encountered a dead network" on Windows.
    /// </remarks>
    public const string SocketPrefix = "auxi.sock";

    /// <summary>
    /// Number of hex characters to use from the stable xxHash-based legacy AppHost identifier.
    /// </summary>
    public const int HashLength = 16;

    /// <summary>
    /// Number of hex characters to use for compact legacy local identifiers.
    /// </summary>
    public const int CompactIdentifierLength = 12;

    /// <summary>
    /// Number of hex characters to use from the randomized legacy instance identifier.
    /// </summary>
    public const int InstanceHashLength = CompactIdentifierLength;

    /// <summary>
    /// Number of base64url characters in a compact AppHost identifier.
    /// </summary>
    public const int CompactAppHostIdLength = 11;

    /// <summary>
    /// Number of base64url characters in a compact instance identifier.
    /// </summary>
    public const int CompactInstanceIdLength = 8;

    private const int CompactAppHostIdByteCount = 8;
    private const int CompactInstanceIdByteCount = 6;
    private const int MacOSSocketPathBytesIncludingNull = 104;
    private const int DefaultSocketPathBytesIncludingNull = 108;

    /// <summary>
    /// Gets the compact backchannels directory path for the given home directory.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The full path to the compact backchannels directory.</returns>
    public static string GetBackchannelsDirectory(string homeDirectory)
        => Path.Combine(homeDirectory, ".aspire", "cli", "bch");

    /// <summary>
    /// Gets the legacy backchannels directory path for the given home directory.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The full path to the legacy backchannels directory.</returns>
    public static string GetLegacyBackchannelsDirectory(string homeDirectory)
        => Path.Combine(homeDirectory, ".aspire", "cli", "backchannels");

    /// <summary>
    /// Computes the compact AppHost identifier from an AppHost path.
    /// </summary>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <returns>An 11-character base64url string.</returns>
    public static string ComputeAppHostId(string appHostPath)
    {
        return ComputeStableBase64UrlIdentifier(NormalizePath(appHostPath), CompactAppHostIdByteCount);
    }

    /// <summary>
    /// Computes the legacy hash portion of the socket name from an AppHost path.
    /// </summary>
    /// <remarks>
    /// On Windows the entire path is upper-cased before hashing so that paths differing
    /// only in casing (for example, <c>FileInfo.FullName</c> on the CLI side vs. MSBuild
    /// metadata on the AppHost side) produce the same hash. On other platforms the path
    /// is hashed as-is because case-sensitive APFS exists on macOS and Linux file systems
    /// are case-sensitive.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <returns>A 16-character lowercase hex string.</returns>
    public static string ComputeHash(string appHostPath)
    {
        return ComputeStableIdentifier(NormalizePath(appHostPath), HashLength);
    }

    /// <summary>
    /// Computes the legacy (pre-normalization) hash for backward compatibility.
    /// </summary>
    /// <remarks>
    /// Older AppHost versions hashed the raw path without case normalization.
    /// The CLI uses this to find sockets created by older AppHosts.
    /// Returns <c>null</c> when the legacy hash would be identical to <see cref="ComputeHash"/>.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <returns>A 16-character lowercase hex string, or <c>null</c> if it matches the current hash.</returns>
    public static string? ComputeLegacyHash(string appHostPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var currentHash = ComputeHash(appHostPath);
        var legacyHash = ComputeStableIdentifier(appHostPath, HashLength);

        if (!string.Equals(legacyHash, currentHash, StringComparison.Ordinal))
        {
            return legacyHash;
        }

        // If the input path was already normalized (for example, an uppercase drive letter on Windows),
        // also try the opposite drive-letter casing to preserve compatibility with older AppHosts that
        // may have hashed the same path with a lowercase drive letter from MSBuild metadata.
        if (appHostPath.Length >= 2 && appHostPath[1] == ':' && char.IsLetter(appHostPath[0]))
        {
            var alternateDriveLetter = char.IsUpper(appHostPath[0])
                ? char.ToLowerInvariant(appHostPath[0])
                : char.ToUpperInvariant(appHostPath[0]);
            var alternatePath = alternateDriveLetter + appHostPath[1..];
            var alternateLegacyHash = ComputeStableIdentifier(alternatePath, HashLength);

            if (!string.Equals(alternateLegacyHash, currentHash, StringComparison.Ordinal))
            {
                return alternateLegacyHash;
            }
        }

        return null;
    }

    /// <summary>
    /// Computes all legacy hashes that should be searched for an AppHost path.
    /// </summary>
    /// <remarks>
    /// Returns, in order: the current normalized hash, the drive-letter-only normalized hash
    /// (produced by AppHost versions that only upper-cased the drive letter before hashing),
    /// and any raw-path fallback returned by <see cref="ComputeLegacyHash"/>. Duplicates are
    /// removed.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <returns>All hash variants that should be searched.</returns>
    public static string[] ComputeLegacyHashes(string appHostPath)
    {
        var currentHash = ComputeHash(appHostPath);
        var rawLegacyHash = ComputeLegacyHash(appHostPath);

        var results = new List<string>() { currentHash };
        if (rawLegacyHash is not null && !results.Contains(rawLegacyHash, StringComparer.Ordinal))
        {
            results.Add(rawLegacyHash);
        }

        return results.ToArray();
    }

    /// <summary>
    /// Normalizes the path for consistent hashing.
    /// </summary>
    /// <remarks>
    /// On Windows the entire path is upper-cased with <see cref="string.ToUpperInvariant"/>.
    /// NTFS/ReFS treat paths as case-insensitive, but neither <c>FileInfo.FullName</c> (CLI side)
    /// nor MSBuild metadata (AppHost side) canonicalizes casing against disk, so any segment
    /// can differ between the two sides. <c>ToUpperInvariant</c> is deterministic across
    /// machines and cultures, so both sides always agree on the same hash input.
    /// On macOS and Linux the path is returned unchanged: case-sensitive APFS exists on macOS
    /// and Linux file systems are case-sensitive by default.
    /// </remarks>
    private static string NormalizePath(string path)
    {
        return OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
    }

    /// <summary>
    /// Computes the full socket path for an AppHost instance.
    /// </summary>
    /// <remarks>
    /// Called by AppHost when creating the socket. Includes a randomized instance ID and the PID
    /// to ensure uniqueness across multiple instances of the same AppHost.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="processId">The process ID of the AppHost.</param>
    /// <returns>The full socket path including PID.</returns>
    public static string ComputeSocketPath(string appHostPath, string homeDirectory, int processId)
    {
        var appHostId = ComputeAppHostId(appHostPath);
        return ComputeSocketPathFromAppHostId(appHostId, homeDirectory, processId);
    }

    /// <summary>
    /// Computes the full socket path for an AppHost identifier.
    /// </summary>
    /// <param name="appHostId">The compact AppHost identifier.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="processId">The process ID of the AppHost.</param>
    /// <returns>The full socket path including PID.</returns>
    public static string ComputeSocketPathFromAppHostId(string appHostId, string homeDirectory, int processId)
    {
        ValidateCompactAppHostId(appHostId);

        var dir = GetBackchannelsDirectory(homeDirectory);
        var instanceId = CreateRandomBase64UrlIdentifier();
        var socketPath = Path.Combine(dir, $"{appHostId}{instanceId}.{processId.ToString(CultureInfo.InvariantCulture)}");
        ValidateSocketPathLength(socketPath);

        return socketPath;
    }

    /// <summary>
    /// Computes a randomized CLI-managed Unix socket path.
    /// </summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <param name="socketPrefix">The logical socket prefix requested by the caller.</param>
    /// <returns>The full socket path.</returns>
    public static string ComputeCliSocketPath(string homeDirectory, string socketPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPrefix);

        var dir = GetBackchannelsDirectory(homeDirectory);
        var socketName = ComputeSocketFileName(socketPrefix);
        var socketPath = Path.Combine(dir, socketName);
        ValidateSocketPathLength(socketPath);

        return socketPath;
    }

    public static string ComputeSocketFileName(string socketPrefix)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPrefix);

        return $"{GetCompactCliSocketPrefix(socketPrefix)}{CreateRandomBase64UrlIdentifier()}";
    }

    /// <summary>
    /// Computes the socket path prefix for finding compact sockets.
    /// </summary>
    /// <remarks>
    /// Called by CLI when searching for sockets. Since the CLI doesn't know the
    /// AppHost's PID or instance ID, it uses this prefix with a glob pattern to find matching sockets.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>The socket path prefix.</returns>
    public static string ComputeSocketPrefix(string appHostPath, string homeDirectory)
    {
        var dir = GetBackchannelsDirectory(homeDirectory);
        var appHostId = ComputeAppHostId(appHostPath);
        return Path.Combine(dir, appHostId);
    }

    /// <summary>
    /// Finds all socket files matching the given AppHost path.
    /// </summary>
    /// <remarks>
    /// Returns all compact socket files for an AppHost and falls back to legacy
    /// <c>auxi.sock.{hash}</c>, <c>auxi.sock.{hash}.{pid}</c>, and
    /// <c>auxi.sock.{hash}.{instanceHash}.{pid}</c> names for older AppHosts.
    /// </remarks>
    /// <param name="appHostPath">The full path to the AppHost project file.</param>
    /// <param name="homeDirectory">The user's home directory.</param>
    /// <returns>An array of socket file paths, or empty if none found.</returns>
    public static string[] FindMatchingSockets(string appHostPath, string homeDirectory)
    {
        var results = new List<string>();

        var compactDir = GetBackchannelsDirectory(homeDirectory);
        var appHostId = ComputeAppHostId(appHostPath);
        results.AddRange(FindCompactSocketsByAppHostId(compactDir, appHostId));

        var legacyDir = GetLegacyBackchannelsDirectory(homeDirectory);
        foreach (var legacyHash in ComputeLegacyHashes(appHostPath))
        {
            results.AddRange(FindLegacySocketsByPrefix(legacyDir, $"{SocketPrefix}.{legacyHash}"));
            results.AddRange(FindLegacySocketsByPrefix(legacyDir, $"aux.sock.{legacyHash}"));
        }

        return results.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Extracts the AppHost identifier from a socket filename.
    /// </summary>
    /// <remarks>
    /// Works with compact format (<c>{appHostId}{instanceId}.{pid}</c>) and legacy
    /// formats (<c>auxi.sock.{hash}</c>, <c>auxi.sock.{hash}.{pid}</c>,
    /// and <c>auxi.sock.{hash}.{instanceHash}.{pid}</c>).
    /// </remarks>
    /// <param name="socketPath">The full socket path or filename.</param>
    /// <returns>The AppHost identifier or hash portion, or <c>null</c> if the format is unrecognized.</returns>
    public static string? ExtractHash(string socketPath)
    {
        var fileName = Path.GetFileName(socketPath);

        if (TryExtractCompactAppHostId(fileName, out var appHostId))
        {
            return appHostId;
        }

        // Handle legacy current format: auxi.sock.{hash}.{instanceHash}.{pid}
        // Handle legacy previous format: auxi.sock.{hash}.{pid}
        // Handle legacy old format: auxi.sock.{hash}
        if (fileName.StartsWith($"{SocketPrefix}.", StringComparison.Ordinal))
        {
            var afterPrefix = fileName[$"{SocketPrefix}.".Length..];
            var dotIndex = afterPrefix.IndexOf('.');
            return dotIndex > 0 ? afterPrefix[..dotIndex] : afterPrefix;
        }

        // Handle oldest legacy format: aux.sock.{hash}
        if (fileName.StartsWith("aux.sock.", StringComparison.Ordinal))
        {
            var afterPrefix = fileName["aux.sock.".Length..];
            var dotIndex = afterPrefix.IndexOf('.');
            return dotIndex > 0 ? afterPrefix[..dotIndex] : afterPrefix;
        }

        return null;
    }

    /// <summary>
    /// Extracts the PID from a socket filename when one is present.
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
    /// Cleans up orphaned socket files for a specific AppHost identifier or legacy hash.
    /// </summary>
    /// <remarks>
    /// This method only cleans up sockets that include a PID because old format sockets
    /// do not have a PID for orphan detection.
    /// </remarks>
    /// <param name="backchannelsDirectory">The backchannels directory path.</param>
    /// <param name="hash">The AppHost identifier or legacy hash to match.</param>
    /// <param name="currentPid">The current process ID (to avoid deleting own socket).</param>
    /// <param name="prefixedFilesOnly">If true, only delete files that start with the expected prefix (e.g., "auxi.sock.{hash}"). This speeds up file detection, but the prefix is only used by legacy sockets.</param>
    /// <returns>The number of orphaned sockets deleted.</returns>
    public static int CleanupOrphanedSockets(string backchannelsDirectory, string hash, int currentPid, bool prefixedFilesOnly = false)
    {
        var deleted = 0;

        if (!Directory.Exists(backchannelsDirectory))
        {
            return deleted;
        }

        var files = prefixedFilesOnly
            ? Directory.GetFiles(backchannelsDirectory, $"{SocketPrefix}.{hash}*")
            : Directory.GetFiles(backchannelsDirectory);
        foreach (var socketPath in files)
        {
            if (!string.Equals(ExtractHash(socketPath), hash, StringComparison.Ordinal))
            {
                continue;
            }

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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return deleted;
    }

    /// <summary>
    /// Computes a compact stable identifier from a string value.
    /// </summary>
    /// <remarks>
    /// Uses XxHash3 because these identifiers are only used for local naming and lookup. They do not
    /// protect secrets or cross a trust boundary, so a fast non-cryptographic hash is preferable to SHA-2.
    /// </remarks>
    /// <param name="value">The string value to hash.</param>
    /// <param name="length">The number of lowercase hex characters to return.</param>
    /// <returns>A lowercase hex identifier truncated to <paramref name="length"/> characters.</returns>
    public static string ComputeStableIdentifier(string value, int length = CompactIdentifierLength)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        var hashBytes = XxHash3.Hash(Encoding.UTF8.GetBytes(value));
        return ToLowerHexIdentifier(hashBytes, length);
    }

    /// <summary>
    /// Creates a compact randomized hex identifier.
    /// </summary>
    /// <param name="length">The number of lowercase hex characters to return.</param>
    /// <returns>A lowercase hex identifier truncated to <paramref name="length"/> characters.</returns>
    public static string CreateRandomIdentifier(int length = CompactIdentifierLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);

        Span<byte> randomBytes = stackalloc byte[(length + 1) / 2];
        RandomNumberGenerator.Fill(randomBytes);

        return ToLowerHexIdentifier(randomBytes, length);
    }

    /// <summary>
    /// Creates a compact randomized base64url identifier.
    /// </summary>
    /// <param name="byteCount">The number of random bytes to encode.</param>
    /// <returns>An unpadded base64url identifier.</returns>
    public static string CreateRandomBase64UrlIdentifier(int byteCount = CompactInstanceIdByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);

        Span<byte> randomBytes = stackalloc byte[byteCount];
        RandomNumberGenerator.Fill(randomBytes);

        return ToBase64UrlIdentifier(randomBytes);
    }

    /// <summary>
    /// Gets the Unix domain socket path byte limit for the current platform, including the trailing null byte.
    /// </summary>
    /// <returns>The maximum number of bytes including the trailing null byte.</returns>
    public static int GetMaxSocketPathBytesIncludingNull()
        => OperatingSystem.IsMacOS() ? MacOSSocketPathBytesIncludingNull : DefaultSocketPathBytesIncludingNull;

    /// <summary>
    /// Gets the UTF-8 byte count for a Unix domain socket path, including the trailing null byte.
    /// </summary>
    /// <param name="socketPath">The socket path.</param>
    /// <returns>The byte count including the trailing null byte.</returns>
    public static int GetSocketPathByteCountIncludingNull(string socketPath)
    {
        ArgumentNullException.ThrowIfNull(socketPath);

        return Encoding.UTF8.GetByteCount(socketPath) + 1;
    }

    /// <summary>
    /// Validates that a Unix domain socket path fits within the current platform's byte limit.
    /// </summary>
    /// <param name="socketPath">The socket path to validate.</param>
    /// <exception cref="InvalidOperationException">Thrown when the path exceeds the platform byte limit.</exception>
    public static void ValidateSocketPathLength(string socketPath)
    {
        var byteCount = GetSocketPathByteCountIncludingNull(socketPath);
        var maxByteCount = GetMaxSocketPathBytesIncludingNull();

        if (byteCount > maxByteCount)
        {
            throw new InvalidOperationException(
                $"The Unix domain socket path '{socketPath}' is {byteCount.ToString(CultureInfo.InvariantCulture)} bytes including the trailing null byte, " +
                $"which exceeds the {maxByteCount.ToString(CultureInfo.InvariantCulture)}-byte limit on this platform.");
        }
    }

    private static string[] FindCompactSocketsByAppHostId(string directory, string appHostId)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory, appHostId + "*")
            .Where(f =>
            {
                var fileName = Path.GetFileName(f);
                return TryExtractCompactAppHostId(fileName, out var extractedAppHostId) &&
                       string.Equals(extractedAppHostId, appHostId, StringComparison.Ordinal);
            })
            .ToArray();
    }

    /// <summary>
    /// Finds socket files in <paramref name="directory"/> whose names match
    /// <paramref name="prefixFileName"/> in any of the supported legacy socket name formats.
    /// </summary>
    private static string[] FindLegacySocketsByPrefix(string directory, string prefixFileName)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        // Match old format (auxi.sock.{hash}), previous format (auxi.sock.{hash}.{pid}),
        // and current legacy format (auxi.sock.{hash}.{instanceHash}.{pid}).
        var allMatches = Directory.GetFiles(directory, prefixFileName + "*");

        // Filter to only include exact match (old format), .{pid} suffix (previous format),
        // or .{instanceHash}.{pid} suffix (current legacy format). This avoids matching
        // auxi.sock.{hash}abc (different hash that starts with same chars) and files
        // like auxi.sock.{hash}.12345.bak.
        return allMatches.Where(f =>
        {
            var fileName = Path.GetFileName(f);
            if (fileName == prefixFileName)
            {
                return true; // Old format: exact match
            }

            if (!fileName.StartsWith(prefixFileName + ".", StringComparison.Ordinal))
            {
                return false;
            }

            var suffix = fileName[(prefixFileName.Length + 1)..];
            var segments = suffix.Split('.');

            if (segments.Length == 1 &&
                int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return true; // Previous format: prefix followed by integer PID
            }

            return segments.Length == 2 &&
                   IsHex(segments[0]) &&
                   int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _);
        }).ToArray();
    }

    private static bool TryExtractCompactAppHostId(string fileName, out string appHostId)
    {
        appHostId = string.Empty;

        if (fileName.Length == CompactAppHostIdLength && IsBase64UrlIdentifier(fileName))
        {
            appHostId = fileName;
            return true;
        }

        var pidSeparatorIndex = CompactAppHostIdLength + CompactInstanceIdLength;
        if (fileName.Length <= pidSeparatorIndex ||
            fileName[pidSeparatorIndex] != '.')
        {
            return false;
        }

        var candidateAppHostId = fileName[..CompactAppHostIdLength];
        var instanceId = fileName[CompactAppHostIdLength..pidSeparatorIndex];
        var pidText = fileName[(pidSeparatorIndex + 1)..];

        if (IsBase64UrlIdentifier(candidateAppHostId) &&
            IsBase64UrlIdentifier(instanceId) &&
            int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            appHostId = candidateAppHostId;
            return true;
        }

        return false;
    }

    private static void ValidateCompactAppHostId(string appHostId)
    {
        ArgumentException.ThrowIfNullOrEmpty(appHostId);

        if (appHostId.Length != CompactAppHostIdLength || !IsBase64UrlIdentifier(appHostId))
        {
            throw new ArgumentException(
                $"The compact AppHost identifier must be {CompactAppHostIdLength.ToString(CultureInfo.InvariantCulture)} base64url characters.",
                nameof(appHostId));
        }
    }

    private static string ComputeStableBase64UrlIdentifier(string value, int byteCount)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);

        var xxHash = new XxHash3();
        xxHash.Append(Encoding.UTF8.GetBytes(value));
        var hash = xxHash.GetCurrentHash();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteCount, hash.Length);

        return ToBase64UrlIdentifier(hash.AsSpan(0, byteCount));
    }

    private static string GetCompactCliSocketPrefix(string socketPrefix)
    {
        if (socketPrefix.StartsWith("cli", StringComparison.OrdinalIgnoreCase))
        {
            return "c";
        }

        if (socketPrefix.StartsWith("apphost", StringComparison.OrdinalIgnoreCase))
        {
            return "h";
        }

        return "s";
    }

    private static bool IsHex(string value)
        => !string.IsNullOrEmpty(value) && value.All(static c => char.IsAsciiHexDigit(c));

    private static bool IsBase64UrlIdentifier(string value)
        => !string.IsNullOrEmpty(value) && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string ToLowerHexIdentifier(ReadOnlySpan<byte> bytes, int length)
    {
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return hex[..Math.Min(length, hex.Length)];
    }

    private static string ToBase64UrlIdentifier(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
