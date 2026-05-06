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
public interface IDashboardClient : IAsyncDisposable
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
    /// Gets the application name advertised by the server.
    /// </summary>
    /// <remarks>
    /// Intended for display in the UI.
    /// </remarks>
    string ApplicationName { get; }

    /// <summary>
    /// Gets the current set of resources and a stream of updates.
    /// </summary>
    /// <remarks>
    /// The returned subscription will not complete on its own.
    /// Callers are required to manage the lifetime of the subscription,
    /// using cancellation during enumeration.
    /// </remarks>
    Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets a resource matching the specified name. This resource won't be updated with changes after it is fetched.
    /// </summary>
    ResourceViewModel? GetResource(string resourceName);

    /// <summary>
    /// Get the current resources.
    /// </summary>
    /// <returns></returns>
    IReadOnlyList<ResourceViewModel> GetResources();

    IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken);

    Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a stream of console log messages for the specified resource.
    /// Includes messages logged both before and after this method call.
    /// </summary>
    /// <remarks>
    /// <para>The returned sequence may end when the resource terminates.
    /// It is up to the implementation.</para>
    /// </remarks>
    /// <para>It is important that callers trigger <paramref name="cancellationToken"/>
    /// so that resources owned by the sequence and its consumers can be freed.</para>
    IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, CancellationToken cancellationToken);

    Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, Value? arguments, bool validateOnly, CancellationToken cancellationToken);
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
