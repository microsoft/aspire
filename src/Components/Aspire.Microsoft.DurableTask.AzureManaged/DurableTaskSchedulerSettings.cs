// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Microsoft.DurableTask.AzureManaged;

/// <summary>
/// Provides the client configuration settings for connecting to a Durable Task Scheduler.
/// </summary>
/// <remarks>
/// <para>
/// Settings are read from the <c>Aspire:Microsoft:DurableTask:AzureManaged</c> configuration section.
/// Named instances use <c>Aspire:Microsoft:DurableTask:AzureManaged:{name}</c>, which takes precedence
/// over the top-level section when both are present.
/// </para>
/// </remarks>
/// <example>
/// Configure settings via <c>appsettings.json</c>:
/// <code>
/// {
///   "Aspire": {
///     "Microsoft": {
///       "DurableTask": {
///         "AzureManaged": {
///           "DisableHealthChecks": false,
///           "DisableTracing": false
///         }
///       }
///     }
///   }
/// }
/// </code>
/// </example>
public sealed class DurableTaskSchedulerSettings
{
    /// <summary>
    /// Gets or sets the connection string for the Durable Task Scheduler.
    /// </summary>
    /// <remarks>
    /// The connection string typically has the format
    /// <c>Endpoint=http://...;Authentication=None;TaskHub=MyHub</c>.
    /// </remarks>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the health check is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry tracing is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableTracing { get; set; }
}
