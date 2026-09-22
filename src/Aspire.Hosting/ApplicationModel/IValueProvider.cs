// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides context for value resolution.
/// </summary>
public class ValueProviderContext
{
    // Value providers return only strings, so nested providers use this callback to preserve secret metadata
    // for the resolver without changing the public IValueProvider contract.
    private readonly AsyncLocal<Action?> _sensitiveValueEncountered = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ValueProviderContext"/> class.
    /// </summary>
    public ValueProviderContext()
    {
    }

    internal IDisposable TrackSensitiveValues(Action sensitiveValueEncountered)
    {
        var previous = _sensitiveValueEncountered.Value;
        _sensitiveValueEncountered.Value = previous is null
            ? sensitiveValueEncountered
            : () =>
            {
                sensitiveValueEncountered();
                previous();
            };

        return new SensitiveValueTrackingScope(_sensitiveValueEncountered, previous);
    }

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

    internal void MarkValueAsSensitive() => _sensitiveValueEncountered.Value?.Invoke();

    private sealed class SensitiveValueTrackingScope(
        AsyncLocal<Action?> sensitiveValueEncountered,
        Action? previous) : IDisposable
    {
        public void Dispose() => sensitiveValueEncountered.Value = previous;
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
    public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default) =>
        GetValueAsync(cancellationToken);
}
