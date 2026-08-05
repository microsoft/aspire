// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a connector trigger parameter value.
/// </summary>
[AspireDto]
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class AzureConnectorGatewayTriggerParameter
{
    /// <summary>
    /// Gets or sets the connector operation parameter name.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the connector operation parameter value.
    /// </summary>
    /// <remarks>
    /// This preview API supports scalar string values. Do not place credentials or other secrets in trigger parameters.
    /// </remarks>
    public required string Value { get; set; }
}
