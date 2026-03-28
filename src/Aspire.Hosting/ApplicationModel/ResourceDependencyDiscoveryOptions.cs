// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Changes how resource dependencies are discovered.
/// </summary>
public sealed class ResourceDependencyDiscoveryOptions
{
    /// <summary>
    /// Sets the mode for discovering resource dependencies. See <see cref="ResourceDependencyDiscoveryMode"/> for details on the available modes.
    /// 
    /// </summary>
    public ResourceDependencyDiscoveryMode DiscoveryMode { get; init; }

    /// <summary>
    /// When true, unresolved values from annotation callbacks will be cached and reused 
    /// on subsequent evaluations of the same annotation, rather than re-evaluating the callback each time.
    /// </summary>
    public bool CacheAnnotationCallbackResults { get; init; }
}
