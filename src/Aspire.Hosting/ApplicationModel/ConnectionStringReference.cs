// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a reference to a connection string.
/// </summary>
public class ConnectionStringReference(IResourceWithConnectionString resource, bool optional) : IExpressionValue, IManifestExpressionProvider, IValueProvider, IValueWithReferences
{
    private readonly ReferenceExpression? _connectionStringExpression;

#pragma warning disable ASPIRECONNECTIONSTRINGS001
    internal ConnectionStringReference(
        IResourceWithConnectionString resource,
        bool optional,
        ConnectionStringEnvironmentVariableNames environmentVariableNames,
        string valueName,
        ReferenceExpression? connectionStringExpression) : this(resource, optional)
    {
        EnvironmentVariableNames = environmentVariableNames ?? throw new ArgumentNullException(nameof(environmentVariableNames));
        ValueName = valueName ?? throw new ArgumentNullException(nameof(valueName));
        _connectionStringExpression = connectionStringExpression;
    }
#pragma warning restore ASPIRECONNECTIONSTRINGS001

    /// <summary>
    /// The resource that the connection string is referencing.
    /// </summary>
    public IResourceWithConnectionString Resource { get; } = resource ?? throw new ArgumentNullException(nameof(resource));

    /// <summary>
    /// A flag indicating whether the connection string is optional.
    /// </summary>
    public bool Optional { get; } = optional;

    /// <summary>
    /// Gets the logical and physical environment-variable names when this reference represents
    /// a generated connection-string injection, or <see langword="null"/> for a standalone value reference.
    /// </summary>
    [Experimental("ASPIRECONNECTIONSTRINGS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public ConnectionStringEnvironmentVariableNames? EnvironmentVariableNames { get; }

    /// <summary>
    /// Gets the expression for the referenced connection-string value.
    /// </summary>
    /// <remarks>
    /// Uses the resource's current connection-string expression unless the reference selects
    /// another connection-string value, such as an HTTP connection on a resource with multiple protocols.
    /// </remarks>
    public ReferenceExpression ConnectionStringExpression => _connectionStringExpression ?? Resource.ConnectionStringExpression;

    // Alias equivalence must not depend on mutable expression text or last-wins optionality.
    internal string ValueName { get; } = nameof(IResourceWithConnectionString.ConnectionStringExpression);

    string IManifestExpressionProvider.ValueExpression => _connectionStringExpression?.ValueExpression ?? Resource.ValueExpression;

    IEnumerable<object> IValueWithReferences.References => _connectionStringExpression is { } expression ? [Resource, expression] : [Resource];

    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
    {
        return _connectionStringExpression is { } expression
            ? expression.GetValueAsync(cancellationToken)
            : Resource.GetValueAsync(cancellationToken);
    }

    async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        var value = _connectionStringExpression is { } expression
            ? await expression.GetValueAsync(context, cancellationToken).ConfigureAwait(false)
            : await Resource.GetValueAsync(context, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(value) && !Optional)
        {
            ThrowConnectionStringUnavailableException();
        }

        return value;
    }

    internal void ThrowConnectionStringUnavailableException() => throw new DistributedApplicationException($"The connection string for the resource '{Resource.Name}' is not available.");

}
