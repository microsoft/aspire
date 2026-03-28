// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents the mode for discovering resource dependencies.
/// </summary>
public enum ResourceDependencyDiscoveryMode
{
    /// <summary>
    /// Discover the full transitive closure of all dependencies.
    /// This includes direct dependencies and all dependencies of those dependencies, recursively.
    /// </summary>
    Recursive,

    /// <summary>
    /// Discover only direct dependencies.
    /// This includes dependencies from annotations (parent, wait, connection string redirect)
    /// and from environment variables and command-line arguments, but does not recurse
    /// into the dependencies of those dependencies.
    /// </summary>
    DirectOnly,
}
