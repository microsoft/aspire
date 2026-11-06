// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Traefik;

/// <summary>
/// The level of Traefik's own logs.
/// </summary>
/// <remarks>
/// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/"/>.
/// </remarks>
public enum TraefikLogLevel
{
    /// <summary>
    /// Everything, including the most detailed tracing of configuration changes.
    /// </summary>
    Trace,

    /// <summary>
    /// Debugging details, such as the configuration Traefik received from each provider.
    /// </summary>
    Debug,

    /// <summary>
    /// Informational messages, warnings, and errors.
    /// </summary>
    Info,

    /// <summary>
    /// Warnings and errors.
    /// </summary>
    Warn,

    /// <summary>
    /// Errors only. This is Traefik's default.
    /// </summary>
    Error,

    /// <summary>
    /// Fatal errors only.
    /// </summary>
    Fatal,

    /// <summary>
    /// Panics only.
    /// </summary>
    Panic,
}

/// <summary>
/// The format of Traefik's access logs.
/// </summary>
public enum TraefikAccessLogFormat
{
    /// <summary>
    /// The Common Log Format, extended with the router, service, and duration. This is Traefik's default.
    /// </summary>
    Common,

    /// <summary>
    /// One JSON object per request, with every field.
    /// </summary>
    Json,
}

/// <summary>
/// Options of Traefik's access logs, set with <c>WithAccessLogs</c>.
/// </summary>
/// <remarks>
/// See <see href="https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/#accesslogs"/>.
/// </remarks>
[AspireExport(ExposeProperties = true)]
public sealed class TraefikAccessLogOptions
{
    /// <summary>
    /// Gets or sets the format of the access logs.
    /// </summary>
    public TraefikAccessLogFormat Format { get; set; }

    /// <summary>
    /// Gets the status codes or ranges to log, for example <c>400-599</c>. When empty, every request is logged.
    /// </summary>
    public IList<string> StatusCodes { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether requests that were retried are logged even when the other filters don't
    /// match.
    /// </summary>
    public bool RetryAttempts { get; set; }

    /// <summary>
    /// Gets or sets the minimum duration of a logged request. Faster requests are only logged when another filter matches.
    /// </summary>
    public TimeSpan? MinDuration { get; set; }

    /// <summary>
    /// Gets the request headers that are logged. Traefik drops every header by default, because they can carry
    /// credentials or personal data. Headers only show up in the JSON format.
    /// </summary>
    public IList<string> KeepHeaders { get; } = [];
}
