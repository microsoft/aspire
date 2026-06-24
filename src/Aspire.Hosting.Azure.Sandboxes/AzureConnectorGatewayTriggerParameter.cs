// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents a connector trigger parameter value.
/// </summary>
/// <param name="Name">The connector operation parameter name.</param>
/// <param name="Value">The connector operation parameter value.</param>
[Experimental("ASPIREAZURE001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed record AzureConnectorGatewayTriggerParameter(string Name, string Value);
