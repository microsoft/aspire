// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a reference to a connection string.
/// </summary>
public class ConnectionStringReference(IResourceWithConnectionString resource, bool optional) : IExpressionValue, IManifestExpressionProvider, IValueProvider, IValueWithReferences
{
    /// <summary>
    /// The resource that the connection string is referencing.
    /// </summary>
    public IResourceWithConnectionString Resource { get; } = resource ?? throw new ArgumentNullException(nameof(resource));

    /// <summary>
    /// A flag indicating whether the connection string is optional.
    /// </summary>
    public bool Optional { get; } = optional;

    string IManifestExpressionProvider.ValueExpression => Resource.ValueExpression;

    IEnumerable<object> IValueWithReferences.References => [Resource];

    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken)
    {
        return Resource.GetValueAsync(cancellationToken);
    }

    async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        var value = await Resource.GetValueAsync(context, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(value) && !Optional)
        {
            ThrowConnectionStringUnavailableException();
        }

        return value;
    }

    internal void ThrowConnectionStringUnavailableException() => throw new DistributedApplicationException($"The connection string for the resource '{Resource.Name}' is not available.");

}
