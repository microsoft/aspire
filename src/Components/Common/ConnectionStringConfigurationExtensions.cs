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

        // IConfiguration only exposes the composed view, while IConfigurationRoot exposes each provider.
        // Walk providers from highest to lowest precedence so a portable value in a higher-precedence
        // provider wins over an exact value in a lower-precedence provider. Within one provider, prefer
        // the exact logical name over its portable alias.
        if (configuration is IConfigurationRoot configurationRoot)
        {
            foreach (var provider in configurationRoot.Providers.Reverse())
            {
                if (provider.TryGet(exactKey, out connectionString))
                {
                    return connectionString is not null;
                }

                if (portableKey != exactKey && provider.TryGet(portableKey, out connectionString))
                {
                    return connectionString is not null;
                }
            }

            connectionString = null;
            return false;
        }

        // Custom IConfiguration implementations do not expose provider ordering, so use the composed
        // lookup as the best available fallback.
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
