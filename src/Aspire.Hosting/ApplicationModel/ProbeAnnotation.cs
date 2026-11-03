// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Enum representing the type of probe.
/// </summary>
public enum ProbeType
{
    /// <summary>
    /// Startup probe.
    /// </summary>
    Startup = 0,

    /// <summary>
    /// Readiness probe.
    /// </summary>
    Readiness = 1,

    /// <summary>
    /// Liveness probe.
    /// </summary>
    Liveness = 2,
}

/// <summary>
/// Represents an annotation that specifies the probes (health, readiness, liveness, etc.) of a resource.
/// </summary>
public abstract class ProbeAnnotation : IResourceAnnotation
{
    /// <summary>
    /// The type of this health probe (startup, readiness or liveness).
    /// </summary>
    public required ProbeType Type { get; init; }

    /// <summary>
    /// The initial delay before the probe should be called.
    /// </summary>
    public int InitialDelaySeconds { get; init; }

    /// <summary>
    /// The period between each probe call.
    /// </summary>
    public int PeriodSeconds { get; init; }

    /// <summary>
    /// Number of seconds after which the probe times out.
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>
    /// Number of failures in a row before considers that the overall check has failed.
    /// </summary>
    public int FailureThreshold { get; init; }

    /// <summary>
    /// Minimum consecutive successes for the probe to be considered successful after having failed.
    /// </summary>
    public int SuccessThreshold { get; init; }
}

/// <summary>
/// Represents an annotation that specifies the HTTP probes (health, readiness, liveness, etc.) of a resource.
/// </summary>
public sealed class EndpointProbeAnnotation : ProbeAnnotation
{
    /// <summary>
    /// The endpoint reference used for the probe
    /// </summary>
    public required EndpointReference EndpointReference { get; init; }

    /// <summary>
    /// The path to the health probe endpoint.
    /// </summary>
    public required string Path { get; init; }
}
