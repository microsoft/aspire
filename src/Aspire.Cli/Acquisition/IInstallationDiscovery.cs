// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Discovers Aspire CLI installations on this machine for <c>aspire doctor</c>.
/// </summary>
/// <remarks>
/// Two modes:
/// <list type="bullet">
///   <item>
///     <description><see cref="DescribeSelf"/> — cheap path that describes
///     only the currently running CLI. No process spawning, no filesystem
///     walks. Used by the hidden <c>aspire doctor --self</c> peer-probe path.</description>
///   </item>
///   <item>
///     <description><see cref="DiscoverAllAsync"/> — walks <c>$PATH</c> plus
///     well-known install prefixes and asks each peer with required install
///     metadata to self-describe via a child <c>aspire doctor --self --format json</c>
///     call. Used by the default <c>aspire doctor</c> path.</description>
///   </item>
/// </list>
/// </remarks>
internal interface IInstallationDiscovery
{
    /// <summary>
    /// Describes the currently running CLI. The result always has
    /// <see cref="InstallationInfo.Status"/> = <c>ok</c> with version /
    /// channel / route populated from in-process readers.
    /// </summary>
    InstallationInfo DescribeSelf();

    /// <summary>
    /// Discovers all Aspire CLI installations the running CLI can see and
    /// returns one row per unique canonical path. The currently running CLI
    /// is always the first element. Peer rows may have
    /// <see cref="InstallationInfo.Status"/> = <c>notProbed</c> when the
    /// required install metadata is missing or invalid, or <c>failed</c>
    /// when a peer probe was attempted but failed.
    /// </summary>
    Task<IReadOnlyList<InstallationInfo>> DiscoverAllAsync(CancellationToken cancellationToken);
}
