// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Configuration;

namespace Aspire.Cli.Certificates;

/// <summary>
/// Resolves Aspire-specific developer certificate configuration.
/// </summary>
internal static class CertificateConfiguration
{
    public const string NssDbPathsConfigPath = "certificates:nssDbPaths";
    public const string NssDbPathsConfigKey = "certificates.nssDbPaths";

    /// <summary>
    /// Resolves the Aspire NSS database override from CLI configuration.
    /// </summary>
    public static NssDbOverride? ResolveNssDbOverride(IConfiguration configuration)
    {
        var configuredValue = configuration[NssDbPathsConfigPath];
        if (!string.IsNullOrEmpty(configuredValue))
        {
            return new NssDbOverride(configuredValue, NssDbPathsConfigKey);
        }

        return null;
    }

    /// <summary>
    /// An NSS database override and the source name used in diagnostics.
    /// </summary>
    internal sealed record NssDbOverride(string Value, string Source);
}
