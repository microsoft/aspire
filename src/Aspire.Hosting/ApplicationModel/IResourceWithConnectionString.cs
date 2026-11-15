// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a resource that has a connection string associated with it.
/// </summary>
public interface IResourceWithConnectionString : IResource, IExpressionValue, IValueWithReferences
{
    /// <summary>
    /// Gets the connection string associated with the resource.
    /// </summary>
    /// <param name="cancellationToken"> A <see cref="CancellationToken"/> to observe while waiting for the task to complete.</param>
    /// <returns>The connection string associated with the resource, when one is available.</returns>
    public ValueTask<string?> GetConnectionStringAsync(CancellationToken cancellationToken = default) =>
        ConnectionStringExpression.GetValueAsync(cancellationToken);

    /// <summary>
    /// Gets the connection string associated with the resource in the specified context.
    /// </summary>
    /// <param name="context">The context identifying the requesting resource, execution mode, and network.</param>
    /// <param name="cancellationToken">A cancellation token to observe while resolving the connection string.</param>
    /// <returns>The connection string associated with the resource, when one is available.</returns>
    /// <remarks>
    /// The default implementation calls <see cref="GetConnectionStringAsync(CancellationToken)"/> to preserve
    /// existing custom resolvers. Implement this overload and forward <paramref name="context"/> to nested
    /// value providers to support caller attribution and network-specific resolution.
    /// <code>
    /// public ValueTask&lt;string?&gt; GetConnectionStringAsync(ValueProviderContext context, CancellationToken cancellationToken)
    ///     =&gt; ConnectionStringExpression.GetValueAsync(context, cancellationToken);
    /// </code>
    /// </remarks>
    public ValueTask<string?> GetConnectionStringAsync(ValueProviderContext context, CancellationToken cancellationToken) =>
        GetConnectionStringAsync(cancellationToken);

    string IManifestExpressionProvider.ValueExpression => $"{{{Name}.connectionString}}";

    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken) => GetConnectionStringAsync(cancellationToken);

    ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken) =>
        GetConnectionStringAsync(context, cancellationToken);

    /// <summary>
    /// Describes the connection string format string used for this resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression { get; }

    /// <summary>
    /// The environment variable name to use for the connection string.
    /// </summary>
    public string? ConnectionStringEnvironmentVariable => null;

    IEnumerable<object> IValueWithReferences.References => [ConnectionStringExpression];

    /// <summary>
    /// Retrieves a collection of connection property name and value pairs associated with the current context.
    /// </summary>
    /// <returns>An enumerable collection of key/value pairs, where each key is the name of a connection property and each value
    /// is its corresponding <see cref="ReferenceExpression"/>. The collection is empty if there are no connection
    /// properties.</returns>
    IEnumerable<KeyValuePair<string, ReferenceExpression>> GetConnectionProperties() => [];
}
