// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes the logical and physical names used for a connection-string reference.
/// </summary>
/// <param name="LogicalName">The logical connection name used by application configuration.</param>
/// <param name="LegacyName">The legacy environment-variable name derived directly from the logical name.</param>
/// <param name="PortableName">The portable environment-variable name.</param>
/// <param name="IsExplicit">Whether the physical environment-variable name was explicitly supplied by the source resource.</param>
[Experimental("ASPIRECONNECTIONSTRINGS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed record ConnectionStringEnvironmentVariableNames(
    string LogicalName,
    string LegacyName,
    string PortableName,
    bool IsExplicit)
{
    private const string Prefix = "ConnectionStrings__";

    /// <summary>
    /// Creates the logical and physical environment-variable names for a connection-string reference.
    /// </summary>
    /// <param name="resource">The referenced resource.</param>
    /// <param name="logicalName">The logical connection name.</param>
    /// <returns>The logical and physical environment-variable names.</returns>
    public static ConnectionStringEnvironmentVariableNames Create(IResourceWithConnectionString resource, string logicalName)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(logicalName);

        if (resource.ConnectionStringEnvironmentVariable is { } explicitName)
        {
            return new(logicalName, explicitName, explicitName, IsExplicit: true);
        }

        return new(
            logicalName,
            Prefix + logicalName,
            Prefix + EnvironmentVariableNameEncoder.EncodeConnectionStringName(logicalName),
            IsExplicit: false);
    }

    /// <summary>
    /// Enumerates the distinct physical environment-variable names represented by this value.
    /// </summary>
    /// <returns>The physical environment-variable names.</returns>
    public IEnumerable<string> GetPhysicalNames()
    {
        yield return LegacyName;

        if (!string.Equals(LegacyName, PortableName, StringComparison.OrdinalIgnoreCase))
        {
            yield return PortableName;
        }
    }
}
