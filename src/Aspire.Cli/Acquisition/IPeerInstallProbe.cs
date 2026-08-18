// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Result of asking a peer Aspire CLI binary to self-describe, in order, via
/// <c>&lt;peer&gt; --info --self --format json</c>, the legacy
/// <c>&lt;peer&gt; doctor --self --format json</c> compatibility fallback,
/// and the <c>&lt;peer&gt; --version</c> compatibility floor.
/// </summary>
internal abstract record PeerProbeResult
{
    /// <summary>Peer responded with a parseable InstallationInfo.</summary>
    public sealed record Ok(InstallationInfo Info) : PeerProbeResult;

    /// <summary>Peer was not probed (or probe failed). <see cref="Reason"/> is human-readable.</summary>
    public sealed record Failed(string Reason) : PeerProbeResult;
}

/// <summary>
/// Spawns a peer Aspire CLI binary to ask it to describe itself.
/// Implementations MUST enforce a process-wide timeout (shared across all
/// compatibility attempts, not reset per attempt), a stdout byte cap, and
/// kill the entire process tree on timeout so a hung or runaway peer can't
/// survive past the caller's lifetime.
/// </summary>
internal interface IPeerInstallProbe
{
    /// <summary>
    /// Probes the peer at <paramref name="binaryPath"/> for its install info,
    /// trying in order: <c>--info --self --format json</c> (current
    /// self-describe contract), <c>doctor --self --format json</c> (legacy
    /// self-describe contract, for peers built before <c>--info --self</c>
    /// existed), and <c>--version</c> (compatibility floor, supported by
    /// every Aspire CLI build). Stops at the first attempt that produces a
    /// usable result. <c>--self</c> bounds the peer to describing only
    /// itself so the probe does not recursively trigger a discovery walk
    /// inside the peer. <c>--format json</c> selects the machine-readable
    /// contract (the human-readable table is the default when
    /// <c>--format</c> is omitted).
    /// Never throws for ordinary peer-probe failures (timeout, non-zero
    /// exit, invalid JSON, missing executable); reserve exceptions for
    /// cancellation propagation.
    /// </summary>
    Task<PeerProbeResult> ProbeAsync(string binaryPath, CancellationToken cancellationToken);
}
