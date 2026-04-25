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
// produce the same identity must be safe to back with the same host. Profile directory is part of the identity
// because the same user data root with two different profiles requires two browser processes.
internal readonly record struct BrowserHostIdentity(
    string ExecutablePath,
    string UserDataRootPath,
    string? ProfileDirectory);

internal enum BrowserHostOwnership
{
    // We launched the browser process. Disposing the host kills the process and deletes our endpoint metadata.
    Owned,

    // We connected to a browser someone else launched (typically the user's already-running browser). Disposing
    // only closes our CDP connection and any tracked targets we created. The browser keeps running.
    Adopted,
}
