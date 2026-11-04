// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Azure;

/// <summary>
/// Configures an Azure API Management Application Insights diagnostic.
/// </summary>
[AspireDto]
public sealed class AzureApiManagementDiagnosticOptions
{
    /// <summary>
    /// Gets the percentage of requests to sample, from 0 to 100.
    /// </summary>
    public double SamplingPercentage { get; init; } = 100;

    /// <summary>
    /// Gets the diagnostic verbosity.
    /// </summary>
    public AzureApiManagementDiagnosticVerbosity Verbosity { get; init; } = AzureApiManagementDiagnosticVerbosity.Information;

    /// <summary>
    /// Gets whether the client IP address is included in telemetry.
    /// </summary>
    public bool LogClientIp { get; init; }
}
