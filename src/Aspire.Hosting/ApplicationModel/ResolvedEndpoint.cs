// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a resolved endpoint with computed target and exposed ports.
/// </summary>
public sealed class ResolvedEndpoint
{
    /// <summary>
    /// Gets the original endpoint annotation.
    /// </summary>
    public required EndpointAnnotation Endpoint { get; init; }

    /// <summary>
    /// Gets the computed target port (container/listening port).
    /// The Value may be null if the deployment tool should assign it (typically for default ProjectResource HTTP/HTTPS endpoints).
    /// The IsAllocated flag indicates whether this port was dynamically allocated vs. explicitly specified.
    /// </summary>
    public ResolvedPort TargetPort { get; init; }

    /// <summary>
    /// Gets the computed exposed port (host/external port).
    /// The Value may be null if it should default to TargetPort or standard ports (80/443).
    /// The IsAllocated flag indicates whether this port was dynamically allocated vs. explicitly specified.
    /// </summary>
    public ResolvedPort ExposedPort { get; init; }
}
