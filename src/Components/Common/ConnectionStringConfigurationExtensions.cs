// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Microsoft.Extensions.Configuration;

internal static class ConnectionStringConfigurationExtensions
{
    public static bool TryGetConnectionString(this IConfiguration configuration, string connectionName, [NotNullWhen(true)] out string? connectionString)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrEmpty(connectionName);

        var exactKey = $"ConnectionStrings:{connectionName}";
        var portableConnectionName = EnvironmentVariableNameEncoder.EncodeConnectionStringName(connectionName);
        var portableKey = $"ConnectionStrings:{portableConnectionName}";

        connectionString = configuration[exactKey];

        if (connectionString is not null)
        {
            return true;
        }

        if (portableKey != exactKey)
        {
            connectionString = configuration[portableKey];
            return connectionString is not null;
        }

        return false;
    }
}
