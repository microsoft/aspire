// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Configures a persistent resource to be monitored by a parent process identity.
/// </summary>
internal sealed class ParentProcessLifetimeAnnotation(int parentProcessId, DateTime parentProcessTimestamp) : IResourceAnnotation
{
    /// <summary>
    /// Gets the ID of the parent process to monitor.
    /// </summary>
    public int ParentProcessId { get; } = parentProcessId;

    /// <summary>
    /// Gets the identity timestamp of the parent process to monitor.
    /// </summary>
    public DateTime ParentProcessTimestamp { get; } = parentProcessTimestamp;
}
