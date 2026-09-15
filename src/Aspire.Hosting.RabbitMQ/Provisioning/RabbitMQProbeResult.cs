// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ.Provisioning;

/// <summary>
/// The result of a live broker probe performed by a RabbitMQ resource health check.
/// Kept separate from <c>HealthCheckResult</c> so that resource model classes do not
/// take a dependency on <c>Microsoft.Extensions.Diagnostics.HealthChecks</c>.
/// </summary>
internal readonly record struct RabbitMQProbeResult(bool IsHealthy, string? Description = null)
{
    /// <summary>Gets a healthy probe result.</summary>
    public static RabbitMQProbeResult Healthy { get; } = new(true);

    /// <summary>Returns an unhealthy probe result with the supplied description.</summary>
    public static RabbitMQProbeResult Unhealthy(string description) => new(false, description);
}
