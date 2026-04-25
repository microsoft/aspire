// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

// A browser instance that one or more tracked log sessions can share. A host either owns the browser process
// (Owned) or is connected to a browser someone else launched (Adopted). This distinction drives lifetime: an
// Owned host can terminate its process on disposal, an Adopted host must never close the user's real browser.
internal interface IBrowserHost : IAsyncDisposable
{
    BrowserHostIdentity Identity { get; }

    BrowserHostOwnership Ownership { get; }

    // The CDP browser-level WebSocket endpoint. Stable for the lifetime of the host.
    Uri DebugEndpoint { get; }

    // Null for Adopted hosts (we can't always discover the PID of a browser we didn't spawn) and may also become
    // null for Owned hosts after the process has exited.
    int? ProcessId { get; }

    // Browser identification surfaced in dashboard properties. e.g. "Microsoft Edge", "Google Chrome".
    string BrowserDisplayName { get; }

    // Completes when the host should be considered dead: the underlying process exited (Owned) or the CDP socket
    // closed permanently (either ownership). Sessions subscribe to this to fail fast instead of waiting on the
    // target lifecycle alone.
    Task Completion { get; }

    // Acquires a logical reference. Each call must be paired with ReleaseAsync. The host disposes itself when
    // the last reference is released. Must be cheap and synchronous to keep the registry path simple.
    void Acquire();

    // Releases a logical reference. When the count reaches zero the host disposes its CDP connection and (for
    // Owned hosts) terminates the browser process.
    Task ReleaseAsync(CancellationToken cancellationToken);
}

// Stable identity used by the host registry to decide whether two requests can share a host. Two settings that
// produce the same identity must be safe to back with the same browser process.
//
// Keyed by (executable, user-data-root) only. Profile directory is intentionally NOT part of the identity:
// Chromium's singleton is keyed by user-data-dir, so launches for different profiles under the same user data
// root are forwarded into the same browser process. Profile selection is therefore a per-target concern, not a
// per-host concern.
//
// Both paths are normalized in the constructor: rooted via Path.GetFullPath, trailing separators trimmed, and
// (on Windows only) compared case-insensitively. This ensures paths that differ only in casing, slashes, or a
// trailing separator collapse to the same identity, so the registry actually shares hosts in practice.
internal readonly struct BrowserHostIdentity : IEquatable<BrowserHostIdentity>
{
    private static readonly StringComparer s_pathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public BrowserHostIdentity(string executablePath, string userDataRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRootPath);

        ExecutablePath = NormalizePath(executablePath);
        UserDataRootPath = NormalizePath(userDataRootPath);
    }

    public string ExecutablePath { get; }

    public string UserDataRootPath { get; }

    public bool Equals(BrowserHostIdentity other) =>
        s_pathComparer.Equals(ExecutablePath, other.ExecutablePath) &&
        s_pathComparer.Equals(UserDataRootPath, other.UserDataRootPath);

    public override bool Equals(object? obj) => obj is BrowserHostIdentity other && Equals(other);

    // Defensive against default(BrowserHostIdentity) which leaves the path strings null. StringComparer
    // throws on null, so coalesce to empty before hashing. A default-constructed identity is never a valid
    // registry key but should not crash if one accidentally ends up in a hash set.
    public override int GetHashCode() =>
        HashCode.Combine(
            s_pathComparer.GetHashCode(ExecutablePath ?? string.Empty),
            s_pathComparer.GetHashCode(UserDataRootPath ?? string.Empty));

    public override string ToString() => $"{ExecutablePath} ({UserDataRootPath})";

    public static bool operator ==(BrowserHostIdentity left, BrowserHostIdentity right) => left.Equals(right);

    public static bool operator !=(BrowserHostIdentity left, BrowserHostIdentity right) => !left.Equals(right);

    private static string NormalizePath(string path)
    {
        var rooted = Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(rooted);
    }
}

internal enum BrowserHostOwnership
{
    // We launched the browser process. Disposing the host kills the process and deletes our endpoint metadata.
    Owned,

    // We connected to a browser someone else launched (typically the user's already-running browser). Disposing
    // only closes our CDP connection and any tracked targets we created. The browser keeps running.
    Adopted,
}
