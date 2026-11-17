// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides context for value resolution.
/// </summary>
public class ValueProviderContext
{
    /// <summary>
    /// The execution context for the distributed application.
    /// </summary>
    public DistributedApplicationExecutionContext? ExecutionContext { get; init; }

    /// <summary>
    /// The resource that is requesting the value.
    /// </summary>
    /// <remarks>
    /// Set this together with <see cref="ExecutionContext"/> when resolving values for a resource.
    /// In run mode, unresolved parameter reads can then be reported on the requesting resource.
    /// </remarks>
    public IResource? Caller { get; init; }

    /// <summary>
    /// The identifier of the network that serves as the context for value resolution.
    /// </summary>
    /// <remarks>
    /// When omitted, the caller's default network is used, or localhost if there is no caller.
    /// Use <see cref="ForHost"/> when values are consumed on the host network rather than the caller's network.
    /// </remarks>
    public NetworkIdentifier? Network { get; init; }

    /// <summary>
    /// Creates a context for resolving values on the localhost network while attributing parameter waits to a resource.
    /// </summary>
    /// <param name="executionContext">The execution context for the distributed application.</param>
    /// <param name="caller">The resource requesting the value.</param>
    /// <returns>A context that resolves values on the localhost network for the specified caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="executionContext"/> or <paramref name="caller"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Use this when values are consumed by the AppHost or a browser rather than on the caller's network.
    /// An explicitly selected endpoint network still takes precedence.
    /// </remarks>
    public static ValueProviderContext ForHost(DistributedApplicationExecutionContext executionContext, IResource caller)
    {
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(caller);

        return new()
        {
            ExecutionContext = executionContext,
            Caller = caller,
            Network = KnownNetworkIdentifiers.LocalhostNetwork
        };
    }
}

/// <summary>
/// An interface that allows the value to be provided for an environment variable.
/// </summary>
public interface IValueProvider
{
    /// <summary>
    /// Gets the value for use as an environment variable.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
    /// <returns></returns>
    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the value for use as an environment variable in the specified context.
    /// </summary>
    /// <param name="context">The context identifying the requesting resource, execution mode, and network.</param>
    /// <param name="cancellationToken">A cancellation token to observe while resolving the value.</param>
    /// <returns>The resolved value.</returns>
    /// <remarks>
    /// Implementations that resolve other value providers should pass this context through to them,
    /// preserving caller attribution and network-specific value resolution.
    /// </remarks>
    public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default) =>
        GetValueAsync(cancellationToken);
}
