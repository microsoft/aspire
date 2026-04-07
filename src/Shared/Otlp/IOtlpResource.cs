// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Otlp.Model;

/// <summary>
/// Interface for OTLP resource data.
/// Used by both Dashboard and CLI.
/// </summary>
public interface IOtlpResource
{
    /// <summary>
    /// Gets the resource name (typically the service.name attribute).
    /// </summary>
    string ResourceName { get; }

    /// <summary>
    /// Gets the instance ID (typically the service.instance.id attribute).
    /// </summary>
    string? InstanceId { get; }
}

/// <summary>
/// Simple implementation of <see cref="IOtlpResource"/> for cases where only the name and instance ID are needed.
/// </summary>
/// <param name="ResourceName">The resource name (typically the service.name attribute).</param>
/// <param name="InstanceId">The instance ID (typically the service.instance.id attribute).</param>
public sealed record SimpleOtlpResource(string ResourceName, string? InstanceId) : IOtlpResource;
