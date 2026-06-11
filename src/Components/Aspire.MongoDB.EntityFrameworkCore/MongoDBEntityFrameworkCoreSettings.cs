// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.MongoDB.EntityFrameworkCore;

/// <summary>
/// Provides the client configuration settings for connecting to a MongoDB database using EntityFrameworkCore.
/// </summary>
public sealed class MongoDBEntityFrameworkCoreSettings
{
    /// <summary>
    /// Gets or sets the connection string of the MongoDB database to connect to.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Gets or sets the name of the MongoDB database to connect to.
    /// </summary>
    public string? DatabaseName { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the MongoDB health check is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableHealthChecks { get; set; }

    /// <summary>
    /// Gets or sets a integer value that indicates the MongoDB health check timeout in milliseconds.
    /// </summary>
    public int? HealthCheckTimeout { get; set; }

    /// <summary>
    /// Gets or sets a boolean value that indicates whether the OpenTelemetry tracing is disabled or not.
    /// </summary>
    /// <value>
    /// The default value is <see langword="false"/>.
    /// </value>
    public bool DisableTracing { get; set; }
}
