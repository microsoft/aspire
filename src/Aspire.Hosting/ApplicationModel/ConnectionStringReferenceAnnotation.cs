// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes a connection-string reference and its logical and physical environment names.
/// </summary>
/// <param name="source">The resource that provides the connection-string value.</param>
/// <param name="environmentVariableNames">The logical and physical names for the reference.</param>
/// <param name="optional">Whether the connection string may be absent.</param>
/// <param name="valueExpression">The manifest expression that identifies the referenced value.</param>
[Experimental("ASPIRECONNECTIONSTRINGS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ConnectionStringReferenceAnnotation(
    IResourceWithConnectionString source,
    ConnectionStringEnvironmentVariableNames environmentVariableNames,
    bool optional,
    string valueExpression) : IResourceAnnotation
{
    /// <summary>
    /// Gets the referenced connection-string resource.
    /// </summary>
    public IResourceWithConnectionString Source { get; } = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>
    /// Gets the logical and physical environment-variable names for the reference.
    /// </summary>
    public ConnectionStringEnvironmentVariableNames EnvironmentVariableNames { get; } = environmentVariableNames ?? throw new ArgumentNullException(nameof(environmentVariableNames));

    /// <summary>
    /// Gets a value indicating whether a missing connection string is allowed.
    /// </summary>
    public bool Optional { get; } = optional;

    /// <summary>
    /// Gets the manifest expression that identifies the referenced connection-string value.
    /// </summary>
    public string ValueExpression { get; } = valueExpression ?? throw new ArgumentNullException(nameof(valueExpression));
}
