// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Reflection;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a resource that has a connection string associated with it.
/// </summary>
public interface IResourceWithConnectionString : IResource, IExpressionValue, IValueWithReferences
{
    private static readonly ConcurrentDictionary<Type, (bool Legacy, bool Contextual)> s_connectionStringImplementations = new();
    private static readonly MethodInfo s_legacyConnectionStringMethod = typeof(IResourceWithConnectionString).GetMethod(
        nameof(GetConnectionStringAsync), [typeof(CancellationToken)])
        ?? throw new MissingMethodException(typeof(IResourceWithConnectionString).FullName, nameof(GetConnectionStringAsync));
    private static readonly MethodInfo s_contextualConnectionStringMethod = typeof(IResourceWithConnectionString).GetMethod(
        nameof(GetConnectionStringAsync), [typeof(ValueProviderContext), typeof(CancellationToken)])
        ?? throw new MissingMethodException(typeof(IResourceWithConnectionString).FullName, nameof(GetConnectionStringAsync));

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
    /// <param name="cancellationToken">A cancellation token to observe while resolving the connection string. Defaults to <see cref="CancellationToken.None"/>.</param>
    /// <returns>The connection string associated with the resource, when one is available.</returns>
    /// <remarks>
    /// The default implementation evaluates <see cref="ConnectionStringExpression"/> in the specified context,
    /// unless the resource implements <see cref="GetConnectionStringAsync(CancellationToken)"/>. In that case,
    /// the existing custom resolver is preserved. Implement this overload to forward <paramref name="context"/>
    /// through custom resolution for caller attribution and network-specific values.
    /// </remarks>
    public ValueTask<string?> GetConnectionStringAsync(ValueProviderContext context, CancellationToken cancellationToken = default) =>
        GetConnectionStringImplementations(this.GetType()).Legacy
            ? GetConnectionStringAsync(cancellationToken)
            : ConnectionStringExpression.GetValueAsync(context, cancellationToken);

    string IManifestExpressionProvider.ValueExpression => $"{{{Name}.connectionString}}";

    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken) => GetConnectionStringAsync(cancellationToken);

    ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken) =>
        GetConnectionStringImplementations(this.GetType()).Contextual
            ? GetConnectionStringAsync(context, cancellationToken)
            : ConnectionStringExpression.GetValueAsync(context, cancellationToken);

    /// <summary>
    /// Describes the connection string format string used for this resource.
    /// </summary>
    /// <remarks>
    /// Aspire evaluates this expression when injecting the connection string into environment variables or command-line
    /// arguments so it can track parameter waits and redact values derived from secrets.
    /// </remarks>
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

    internal static (bool Legacy, bool Contextual) GetConnectionStringImplementations(Type resourceType) =>
        s_connectionStringImplementations.GetOrAdd(resourceType, static type =>
        {
            // The interface map distinguishes the default fallback from class or derived-interface
            // implementations, including explicit and inherited methods.
            var map = type.GetInterfaceMap(typeof(IResourceWithConnectionString));
            return (HasCustomImplementation(s_legacyConnectionStringMethod), HasCustomImplementation(s_contextualConnectionStringMethod));

            bool HasCustomImplementation(MethodInfo method)
            {
                var index = Array.IndexOf(map.InterfaceMethods, method);
                if (index < 0)
                {
                    throw new MissingMethodException(type.FullName, nameof(GetConnectionStringAsync));
                }

                return map.TargetMethods[index].DeclaringType != typeof(IResourceWithConnectionString);
            }
        });
}
