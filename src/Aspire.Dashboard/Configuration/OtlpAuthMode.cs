// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Configuration;

/// <summary>
/// Specifies how clients sending telemetry to the dashboard are authenticated.
/// </summary>
public enum OtlpAuthMode
{
    /// <summary>
    /// Allows clients to send telemetry without authentication. See the
    /// <see href="https://aspire.dev/dashboard/security-considerations/">dashboard security considerations</see>.
    /// When an OTLP endpoint is enabled, the dashboard logs a warning and displays a warning in the UI by default.
    /// </summary>
    Unsecured,

    /// <summary>
    /// Authenticates clients with an API key.
    /// </summary>
    ApiKey,

    /// <summary>
    /// Authenticates clients with a client certificate.
    /// </summary>
    ClientCertificate
}
