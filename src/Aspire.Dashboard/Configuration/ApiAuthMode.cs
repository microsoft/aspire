// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Configuration;

/// <summary>
/// Authentication mode for Dashboard API endpoints (MCP and Telemetry API).
/// </summary>
public enum ApiAuthMode
{
    Unsecured,
    ApiKey,
}
