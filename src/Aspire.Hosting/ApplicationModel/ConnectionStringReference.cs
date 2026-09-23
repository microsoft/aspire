// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Specifies how a <see cref="ConnectionStringReference"/> selects the resource view that provides the connection string.
/// </summary>
public enum ConnectionStringReferenceResolution
{
    /// <summary>
    /// Prefer the effective resource projection and fall back to the canonical owner.
    /// </summary>
    PreferEffectiveResource,

    /// <summary>
    /// Prefer the canonical owner object and fall back to the effective resource projection.
    /// </summary>
    /// <remarks>
    /// This selection does not suppress behavior that the owner's connection-string implementation intentionally
    /// forwards to its projection for compatibility.
    /// </remarks>
    PreferOwner
}

/// <summary>
/// Represents a reference to a connection string.
/// </summary>
public class ConnectionStringReference : IExpressionValue, IManifestExpressionProvider, IValueProvider, IValueWithReferences
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionStringReference"/> class.
    /// </summary>
    /// <param name="resource">The resource whose connection string is referenced.</param>
    /// <param name="optional"><see langword="true"/> to allow the connection string to be unavailable; otherwise, <see langword="false"/>.</param>
    public ConnectionStringReference(IResourceWithConnectionString resource, bool optional)
        : this(resource, optional, ConnectionStringReferenceResolution.PreferEffectiveResource)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionStringReference"/> class.
    /// </summary>
    /// <param name="resource">The resource whose connection string is referenced.</param>
    /// <param name="optional"><see langword="true"/> to allow the connection string to be unavailable; otherwise, <see langword="false"/>.</param>
    /// <param name="resolution">The rule used to select the resource view that provides the connection string.</param>
    public ConnectionStringReference(
        IResourceWithConnectionString resource,
        bool optional,
        ConnectionStringReferenceResolution resolution)
    {
        Resource = resource ?? throw new ArgumentNullException(nameof(resource));
        Optional = optional;
        Resolution = resolution;
    }

    /// <summary>
    /// The resource that the connection string is referencing.
    /// </summary>
    public IResourceWithConnectionString Resource { get; }

    /// <summary>
    /// A flag indicating whether the connection string is optional.
    /// </summary>
    public bool Optional { get; }

    /// <summary>
    /// Gets the rule used to select the resource view that provides the connection string.
    /// </summary>
    public ConnectionStringReferenceResolution Resolution { get; }

    /// <summary>
    /// Gets the resource view that provides the connection string.
    /// </summary>
    /// <remarks>
    /// Provider selection is performed on each access so projections registered after this reference is created
    /// are still honored. Owner preference selects the canonical owner object first, but does not suppress behavior
    /// that the owner's implementation intentionally forwards to its projection for compatibility.
    /// </remarks>
    public IResourceWithConnectionString Provider =>
        Resource.GetEffectiveCapability<IResourceWithConnectionString>(
            preferOwner: Resolution is ConnectionStringReferenceResolution.PreferOwner) ??
        Resource;

    string IManifestExpressionProvider.ValueExpression => Resource.ValueExpression;

    IEnumerable<object> IValueWithReferences.References => [Resource.GetOwnerOrSelf(), Provider.ConnectionStringExpression];

    ValueTask<string?> IValueProvider.GetValueAsync(CancellationToken cancellationToken) =>
        ((IValueProvider)Provider).GetValueAsync(cancellationToken);

    async ValueTask<string?> IValueProvider.GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken)
    {
        var value = await ((IValueProvider)Provider).GetValueAsync(context, cancellationToken).ConfigureAwait(false);
        EnsureValueAvailable(value);

        return value;
    }

    private void EnsureValueAvailable(string? value)
    {
        if (string.IsNullOrEmpty(value) && !Optional)
        {
            ThrowConnectionStringUnavailableException();
        }
    }

    internal void ThrowConnectionStringUnavailableException() => throw new DistributedApplicationException($"The connection string for the resource '{Resource.Name}' is not available.");
}
