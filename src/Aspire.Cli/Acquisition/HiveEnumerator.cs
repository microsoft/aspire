// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Describes one direct child directory of <see cref="CliExecutionContext.HivesDirectory"/>.
/// </summary>
internal sealed record HiveInfo(string Name, string Path);

/// <summary>
/// Enumerates hive directories directly under <see cref="CliExecutionContext.HivesDirectory"/>.
/// </summary>
/// <remarks>
/// Only immediate child <em>directories</em> are yielded. Files at the root level and
/// nested subdirectories are both excluded. Yields nothing when the hives root does not
/// exist or is inaccessible.
/// </remarks>
internal interface IHiveEnumerator
{
    /// <summary>
    /// Returns a <see cref="HiveInfo"/> for each direct child directory of
    /// <see cref="CliExecutionContext.HivesDirectory"/>.
    /// </summary>
    /// <param name="cancellationToken">Token checked between filesystem entries.</param>
    IEnumerable<HiveInfo> EnumerateHives(CancellationToken cancellationToken = default);
}

internal sealed class HiveEnumerator(CliExecutionContext executionContext, ILogger<HiveEnumerator> logger) : IHiveEnumerator
{
    /// <summary>
    /// Returns a <see cref="HiveInfo"/> for each direct child directory of
    /// <see cref="CliExecutionContext.HivesDirectory"/>. Cancellation is observed
    /// between enumerated entries via <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="cancellationToken">Token checked between filesystem entries.</param>
    public IEnumerable<HiveInfo> EnumerateHives(CancellationToken cancellationToken = default)
    {
        var hivesRoot = executionContext.HivesDirectory.FullName;

        // EnumerateDirectoriesSafe enumerates direct child directories only (no recursion),
        // skips inaccessible roots with a debug log instead of throwing, and checks the
        // cancellation token before each entry so callers can react promptly.
        foreach (var dir in InstallationCandidateSourceHelpers.EnumerateDirectoriesSafe(hivesRoot, logger, cancellationToken))
        {
            yield return new HiveInfo(Path.GetFileName(dir), dir);
        }
    }
}
