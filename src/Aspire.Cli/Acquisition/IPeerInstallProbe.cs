// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Result of asking a peer Aspire CLI binary to self-describe via
/// <c>&lt;peer&gt; doctor --self --format json</c>.
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
/// Implementations MUST enforce a process-wide timeout, a stdout byte cap,
/// and kill the entire process tree on timeout so a hung or runaway peer
/// can't survive past <c>aspire doctor</c>'s lifetime.
/// </summary>
internal interface IPeerInstallProbe
{
    /// <summary>
    /// Runs <c><paramref name="binaryPath"/> doctor --self --format json</c> and
    /// returns either the parsed <see cref="InstallationInfo"/> or a failure
    /// reason. <c>--self</c> bounds the peer to describing only itself so the
    /// probe does not recursively trigger a discovery walk inside the peer.
    /// <c>--format json</c> selects the machine-readable contract (the
    /// human-readable table is the default when <c>--format</c> is omitted).
    /// Never throws for ordinary peer-probe failures (timeout, non-zero
    /// exit, invalid JSON, missing executable); reserve exceptions for
    /// cancellation propagation.
    /// </summary>
    Task<PeerProbeResult> ProbeAsync(string binaryPath, CancellationToken cancellationToken);
}
