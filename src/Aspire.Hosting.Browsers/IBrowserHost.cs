// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

// A browser instance/process boundary that one or more tracked log sessions can share. A host either owns the
// browser process (Owned) or is connected to a browser someone else launched (Adopted). This distinction drives
// lifetime: an Owned host can terminate its process on disposal, an Adopted host must never close the user's real
// browser.
internal interface IBrowserHost : IAsyncDisposable
{
    BrowserHostIdentity Identity { get; }

    BrowserHostOwnership Ownership { get; }

    // Browser-level WebSocket endpoint for attach/adoption hosts. Null for pipe-backed owned hosts, where CDP is only
    // available through the private host-owned transport.
    Uri? DebugEndpoint { get; }

    int? ProcessId { get; }

    // Browser identification surfaced in dashboard properties. e.g. "Microsoft Edge", "Google Chrome".
    string BrowserDisplayName { get; }

    // Completes when the host itself is no longer usable: the underlying process exited (Owned), the adopted
    // host was disposed, or recovery gave up. Transient CDP socket loss is intentionally not modeled as host
    // termination because sessions can reconnect and reattach to their targets.
    Task Termination { get; }

    // Opens a browser-level CDP connection for a page session. WebSocket-backed hosts create a new connection per
    // session; pipe-backed hosts return a shared/multiplexed connection over the private CDP pipe owned by this AppHost.
    Task<IBrowserLogsCdpConnection> CreateCdpConnectionAsync(
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        ILogger<BrowserLogsSessionManager> logger,
        CancellationToken cancellationToken);

    // Creates a page/tab owned by one tracked browser-log session. The returned session owns only that page target;
    // disposing it must never close the browser process. Host implementations hide CDP event fanout and recovery
    // so callers cannot accidentally share a page target or call Browser.close on an adopted browser.
    Task<IBrowserPageSession> CreatePageSessionAsync(
        string sessionId,
        Uri url,
        BrowserConnectionDiagnosticsLogger connectionDiagnostics,
        Func<BrowserLogsCdpProtocolEvent, ValueTask> eventHandler,
        CancellationToken cancellationToken);
}

internal interface IBrowserPageSession : IAsyncDisposable
{
    string TargetId { get; }

    string TargetSessionId { get; }

    // Completes when this page target is no longer available: the tab was closed/crashed, CDP reported a detach,
    // or the host terminated. Host-level reconnects should reattach and preserve this session when possible.
    Task<BrowserPageSessionResult> Completion { get; }

    Task<BrowserLogsCaptureScreenshotResult> CaptureScreenshotAsync(CancellationToken cancellationToken);
}

// Normalized page-session completion signal consumed by BrowserLogsRunningSession so manager state is independent of
// the exact CDP event or transport failure that ended the page.
internal readonly record struct BrowserPageSessionResult(BrowserPageSessionCompletionKind CompletionKind, Exception? Error);

// Small vocabulary for page lifecycle outcomes. The manager uses this to distinguish normal tab closes from crashes
// or unrecoverable browser connection loss.
internal enum BrowserPageSessionCompletionKind
{
    Stopped,
    PageClosed,
    PageCrashed,
    BrowserExited,
    ConnectionLost
}

// Reference-counted registry handle returned to each running session. Disposing the lease is the only way a session
// releases a shared host, which keeps owned/adopted browser lifetime centralized in BrowserHostRegistry.
internal sealed class BrowserHostLease : IAsyncDisposable
{
    // Lease release acquires the BrowserHostRegistry lock, which is held across CreateHostCoreAsync. Browser startup can
    // be slow, so the release timeout must be long enough to avoid a release-cancellation that strands the registry
    // reference count permanently incremented. We also swallow timeouts at the lease boundary so disposal of an owning
    // session never throws.
    private static readonly TimeSpan s_releaseTimeout = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, ValueTask> _releaseAsync;
    private int _disposed;

    public BrowserHostLease(IBrowserHost host, Func<CancellationToken, ValueTask> releaseAsync)
    {
        Host = host ?? throw new ArgumentNullException(nameof(host));
        _releaseAsync = releaseAsync ?? throw new ArgumentNullException(nameof(releaseAsync));
    }

    public IBrowserHost Host { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        using var releaseCts = new CancellationTokenSource(s_releaseTimeout);
        try
        {
            await _releaseAsync(releaseCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (releaseCts.IsCancellationRequested)
        {
            // Release contended for the registry lock past the timeout. The registry will eventually release on its
            // own DisposeAsync path; do not propagate to our caller (typically a session DisposeAsync) where it would
            // mask other cleanup failures.
        }
    }
}

// Stable identity used by the host registry to decide whether two requests can share a host. Two configurations that
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
        var rooted = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(rooted);
    }
}

// Describes who owns the browser process behind a host. Session disposal uses this to avoid closing a real user browser
// when Aspire merely adopted an existing debug endpoint.
internal enum BrowserHostOwnership
{
    // We launched the browser process. Disposing the host kills the process and deletes our endpoint metadata.
    Owned,

    // We connected to a browser someone else launched. Disposing only closes our CDP connection and any tracked targets
    // we created. The browser keeps running.
    Adopted,
}
