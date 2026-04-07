// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Kusto.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Aspire.Hosting.Azure;

internal static class AzureKustoHealthCheckBuilderExtensions
{
    /// <summary>
    /// Adds a Kusto health check to the health check builder.
    /// </summary>
    /// <param name="builder">The health check builder.</param>
    /// <param name="name">The name of the health check.</param>
    /// <param name="isCluster">Whether to use a cluster health check or a database health check.</param>
    /// <param name="connectionStringFactory">The Kusto connection string builder.</param>
    /// <returns>The updated health check builder.</returns>
    /// <remarks>This method is not available in polyglot app hosts.</remarks>
    [AspireExportIgnore(Reason = "IHealthChecksBuilder extensions and IServiceProvider callbacks are not ATS-compatible.")]
    public static IHealthChecksBuilder AddAzureKustoHealthCheck(this IHealthChecksBuilder builder, string name, bool isCluster, Func<IServiceProvider, KustoConnectionStringBuilder> connectionStringFactory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(connectionStringFactory);

        var registration = new HealthCheckRegistration(name, sp => new AzureKustoHealthCheck(connectionStringFactory(sp), isCluster), failureStatus: default, tags: default, timeout: default);
        return builder.Add(registration);
    }
}
