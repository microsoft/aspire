// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Provides data about active resources to external components, such as the dashboard.
/// </summary>
public interface IDashboardClient : IResourceRepository, IAsyncDisposable
{
    Task WhenConnected { get; }

    /// <summary>
    /// Gets whether the client object is enabled for use.
    /// </summary>
    /// <remarks>
    /// Users of <see cref="IDashboardClient"/> client should check <see cref="IsEnabled"/> before calling
    /// any other members of this interface, to avoid exceptions.
    /// </remarks>
    bool IsEnabled { get; }

    /// <summary>
    /// Gets whether the selected dashboard data source is read-only.
    /// </summary>
    bool IsReadOnly => false;

    /// <summary>
    /// Gets the current connection state of the client to the resource service.
    /// </summary>
    DashboardConnectionState ConnectionState { get; }

    /// <summary>
    /// An event raised when the connection state changes. Subscribers receive the new state.
    /// </summary>
    event Action<DashboardConnectionState>? ConnectionStateChanged;

    /// <summary>
    /// Explicitly triggers a reconnection attempt to the resource service.
    /// </summary>
    Task ReconnectAsync();

    /// <summary>
    /// Gets the application name advertised by the server.
    /// </summary>
    /// <remarks>
    /// Intended for display in the UI.
    /// </remarks>
    string ApplicationName { get; }

    /// <summary>
    /// Gets the minimum dashboard version required by the connected AppHost,
    /// or <see langword="null"/> if not yet known.
    /// </summary>
    string? MinRequiredVersion { get; }

    IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken);

    Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken);

    Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, ExecuteResourceCommandOptions options, CancellationToken cancellationToken);

    Task<string> UploadFileAsync(Stream fileStream, string fileName, long expectedSize, CancellationToken cancellationToken);
}

/// <summary>
/// Options for executing a resource command through the dashboard client.
/// </summary>
public sealed class ExecuteResourceCommandOptions
{
    /// <summary>
    /// Gets the invocation arguments supplied to the command, keyed by argument name.
    /// </summary>
    public IReadOnlyDictionary<string, Value>? Arguments { get; init; }

    /// <summary>
    /// Gets a value indicating whether command execution should fail instead of prompting for missing input.
    /// </summary>
    public bool NonInteractive { get; init; }
}

public sealed record ResourceViewModelSubscription(
    ImmutableArray<ResourceViewModel> InitialState,
    IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> Subscription);

public sealed record ResourceViewModelChange(
    ResourceViewModelChangeType ChangeType,
    ResourceViewModel Resource);

public enum ResourceViewModelChangeType
{
    /// <summary>
    /// The object was added if new, or updated if not.
    /// </summary>
    Upsert,

    /// <summary>
    /// The object was deleted.
    /// </summary>
    Delete
}
