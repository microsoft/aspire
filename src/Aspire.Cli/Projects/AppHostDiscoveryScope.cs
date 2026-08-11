// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

/// <summary>
/// Controls how aggressively <see cref="IProjectLocator"/> filters candidate AppHost
/// project files during discovery.
/// </summary>
internal enum AppHostDiscoveryScope
{
    /// <summary>
    /// Apply the default filters: prefer <c>git ls-files</c> when the search directory
    /// is inside a git working tree, otherwise walk the filesystem while pruning
    /// well-known dependency, build-output, and cache directories. Used for ambient
    /// discovery from the working directory (for example, <c>aspire ls</c> with no
    /// arguments and <c>aspire run</c> when no <c>--project</c> is supplied).
    /// </summary>
    DefaultFiltered,

    /// <summary>
    /// Apply only the well-known directory skip list and never invoke git. Used when
    /// the user has explicitly pointed at a directory (for example,
    /// <c>aspire run --project ./some-dir</c>) — the directory itself may be ignored
    /// by <c>.gitignore</c>, but the user clearly wants it scanned.
    /// </summary>
    ExplicitDirectory,

    /// <summary>
    /// Disable git filtering and the well-known directory skip list, then walk every
    /// file under the search directory except the NuGet package cache. Matches the
    /// legacy behavior. Used by <c>aspire ls --all</c>.
    /// </summary>
    AllFiles,
}
