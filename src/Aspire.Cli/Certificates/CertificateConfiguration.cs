// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting;
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
    /// Resolves the Aspire NSS database override from CLI configuration and its environment compatibility alias.
    /// </summary>
    public static NssDbOverride? ResolveNssDbOverride(IConfiguration configuration, IEnvironment environment)
    {
        var configuredValue = configuration[NssDbPathsConfigPath];
        if (!string.IsNullOrEmpty(configuredValue))
        {
            return new NssDbOverride(configuredValue, NssDbPathsConfigKey);
        }

        var environmentValue = environment.GetEnvironmentVariable(KnownConfigNames.CliDevCertsNssDbPaths);
        if (!string.IsNullOrEmpty(environmentValue))
        {
            return new NssDbOverride(environmentValue, KnownConfigNames.CliDevCertsNssDbPaths);
        }

        return null;
    }

    /// <summary>
    /// An NSS database override and the source name used in diagnostics.
    /// </summary>
    internal sealed record NssDbOverride(string Value, string Source);
}
