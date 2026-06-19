// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Otlp.Serialization;

/// <summary>
/// Request to the <c>POST /api/telemetry/validateToken</c> endpoint.
/// Shared between Dashboard and CLI.
/// </summary>
/// <param name="Token">The browser token to validate.</param>
internal sealed record TelemetryValidateTokenRequest(string Token);

/// <summary>
/// Response from the <c>POST /api/telemetry/validateToken</c> endpoint.
/// Shared between Dashboard and CLI.
/// </summary>
/// <param name="ApiKey">The API key to use for telemetry API access, or <c>null</c> when the API is unsecured.</param>
internal sealed record TelemetryValidateTokenResponse(string? ApiKey);
