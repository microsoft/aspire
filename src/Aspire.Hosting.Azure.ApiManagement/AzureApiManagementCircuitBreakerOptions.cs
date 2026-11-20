// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures a circuit breaker for an Azure API Management backend.
/// </summary>
[AspireDto]
public sealed class AzureApiManagementCircuitBreakerOptions
{
    /// <summary>
    /// Gets or sets the circuit-breaker rule name.
    /// </summary>
    public string Name { get; init; } = "default";

    /// <summary>
    /// Gets or sets the number of failures that opens the circuit.
    /// </summary>
    public int FailureCount { get; init; } = 1;

    /// <summary>
    /// Gets or sets the interval during which failures are counted.
    /// </summary>
    /// <remarks>Must be a whole number of seconds and at least one second. Defaults to 10 seconds.</remarks>
    public TimeSpan FailureInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets the HTTP status-code ranges treated as failures.
    /// </summary>
    public AzureApiManagementStatusCodeRange[] StatusCodeRanges { get; init; } = [];

    /// <summary>
    /// Gets or sets how long the circuit remains open.
    /// </summary>
    /// <remarks>Must be a whole number of seconds and at least one second. Defaults to 10 seconds.</remarks>
    public TimeSpan TripDuration { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets or sets whether API Management honors the backend's <c>Retry-After</c> header.
    /// </summary>
    public bool AcceptRetryAfter { get; init; }
}
