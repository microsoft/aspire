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
    public IResource? Caller { get; init; }

    /// <summary>
    /// The identifier of the network that serves as the context for value resolution.
    /// </summary>
    public NetworkIdentifier? Network { get; init; }
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
    public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default) =>
        GetValueAsync(cancellationToken);
}
