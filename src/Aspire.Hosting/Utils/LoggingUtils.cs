// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Utils;

internal static class LoggingUtils
{
    [AspireExportIgnore(Reason = "Health check logging suppression is an internal helper and is not part of the ATS surface.")]
    public static void SuppressHealthCheckHttpClientLogging(this IServiceCollection services, string healthCheckName)
    {
        services.AddLogging(configure =>
        {
            // The AddUrlGroup health check makes use of http client factory.
            configure.AddFilter($"System.Net.Http.HttpClient.{healthCheckName}.LogicalHandler", LogLevel.None);
            configure.AddFilter($"System.Net.Http.HttpClient.{healthCheckName}.ClientHandler", LogLevel.None);
        });
    }
}
