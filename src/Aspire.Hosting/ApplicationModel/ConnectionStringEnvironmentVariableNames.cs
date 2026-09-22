// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes the logical and physical names used for a connection-string reference.
/// </summary>
[Experimental("ASPIRECONNECTIONSTRINGS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed record ConnectionStringEnvironmentVariableNames
{
    private const string Prefix = "ConnectionStrings__";

    internal ConnectionStringEnvironmentVariableNames(string logicalName, string originalName, string portableName, bool isExplicit)
    {
        LogicalName = logicalName;
        OriginalName = originalName;
        PortableName = portableName;
        IsExplicit = isExplicit;
    }

    /// <summary>
    /// Gets the logical connection name used by application configuration.
    /// </summary>
    public string LogicalName { get; init; }

    /// <summary>
    /// Gets the original environment-variable name derived directly from the logical name.
    /// </summary>
    public string OriginalName { get; init; }

    /// <summary>
    /// Gets the portable environment-variable name.
    /// </summary>
    public string PortableName { get; init; }

    /// <summary>
    /// Gets whether the physical environment-variable name was explicitly supplied by the source resource.
    /// </summary>
    public bool IsExplicit { get; init; }

    /// <summary>
    /// Deconstructs the logical and physical names for a connection-string reference.
    /// </summary>
    /// <param name="LogicalName">The logical connection name used by application configuration.</param>
    /// <param name="OriginalName">The original environment-variable name derived directly from the logical name.</param>
    /// <param name="PortableName">The portable environment-variable name.</param>
    /// <param name="IsExplicit">Whether the physical environment-variable name was explicitly supplied by the source resource.</param>
    public void Deconstruct(out string LogicalName, out string OriginalName, out string PortableName, out bool IsExplicit)
    {
        LogicalName = this.LogicalName;
        OriginalName = this.OriginalName;
        PortableName = this.PortableName;
        IsExplicit = this.IsExplicit;
    }

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
            return new(logicalName, explicitName, explicitName, isExplicit: true);
        }

        return new(
            logicalName,
            Prefix + logicalName,
            Prefix + EnvironmentVariableNameEncoder.EncodeConnectionStringName(logicalName),
            isExplicit: false);
    }

    /// <summary>
    /// Enumerates the distinct physical environment-variable names represented by this value.
    /// </summary>
    /// <returns>The physical environment-variable names.</returns>
    public IEnumerable<string> GetPhysicalNames()
    {
        yield return OriginalName;

        if (!string.Equals(OriginalName, PortableName, StringComparison.OrdinalIgnoreCase))
        {
            yield return PortableName;
        }
    }
}
