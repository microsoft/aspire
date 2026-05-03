// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Aspire.Dashboard.Model;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;

namespace Aspire.Dashboard.Tests.Shared;

public class TestDashboardClient : IDashboardClient
{
    private readonly Func<string, Channel<IReadOnlyList<ResourceLogLine>>>? _consoleLogsChannelProvider;
    private readonly Func<Channel<IReadOnlyList<ResourceViewModelChange>>>? _resourceChannelProvider;
    private readonly Func<Channel<WatchInteractionsResponseUpdate>>? _interactionChannelProvider;
    private readonly Channel<ResourceCommandResponseViewModel>? _resourceCommandsChannel;
    private readonly Func<string, string, CommandViewModel, Value?, CancellationToken, Task<ResourceCommandResponseViewModel>>? _executeResourceCommand;
    private readonly Channel<WatchInteractionsRequestUpdate>? _sendInteractionUpdateChannel;
    private readonly IList<ResourceViewModel>? _initialResources;

    public bool IsEnabled { get; }
    public Task WhenConnected { get; }
    public string ApplicationName { get; } = "TestApp";

    public TestDashboardClient(
        bool? isEnabled = false,
        string? applicationName = null,
        Func<string, Channel<IReadOnlyList<ResourceLogLine>>>? consoleLogsChannelProvider = null,
        Func<Channel<IReadOnlyList<ResourceViewModelChange>>>? resourceChannelProvider = null,
        Func<Channel<WatchInteractionsResponseUpdate>>? interactionChannelProvider = null,
        Channel<ResourceCommandResponseViewModel>? resourceCommandsChannel = null,
        Func<string, string, CommandViewModel, Value?, CancellationToken, Task<ResourceCommandResponseViewModel>>? executeResourceCommand = null,
        Channel<WatchInteractionsRequestUpdate>? sendInteractionUpdateChannel = null,
        IList<ResourceViewModel>? initialResources = null,
        Task? whenConnected = null)
    {
        IsEnabled = isEnabled ?? false;
        ApplicationName = applicationName ?? "TestApp";
        WhenConnected = whenConnected ?? Task.CompletedTask;
        _consoleLogsChannelProvider = consoleLogsChannelProvider;
        _resourceChannelProvider = resourceChannelProvider;
        _interactionChannelProvider = interactionChannelProvider;
        _resourceCommandsChannel = resourceCommandsChannel;
        _executeResourceCommand = executeResourceCommand;
        _sendInteractionUpdateChannel = sendInteractionUpdateChannel;
        _initialResources = initialResources;
    }

    public ValueTask DisposeAsync()
    {
        return default;
    }

    public Task<ResourceCommandResponseViewModel> ExecuteResourceCommandAsync(string resourceName, string resourceType, CommandViewModel command, Value? arguments, CancellationToken cancellationToken)
    {
        if (_executeResourceCommand is not null)
        {
            return _executeResourceCommand(resourceName, resourceType, command, arguments, cancellationToken);
        }

        if (_resourceCommandsChannel == null)
        {
            throw new InvalidOperationException("No resource command channel set.");
        }

        return _resourceCommandsChannel.Reader.ReadAsync(cancellationToken).AsTask();
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> SubscribeConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_consoleLogsChannelProvider == null)
        {
            throw new InvalidOperationException("No channel provider set.");
        }

        var channel = _consoleLogsChannelProvider(resourceName);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<ResourceLogLine>> GetConsoleLogs(string resourceName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_consoleLogsChannelProvider == null)
        {
            throw new InvalidOperationException("No channel provider set.");
        }

        var channel = _consoleLogsChannelProvider(resourceName);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    public Task<ResourceViewModelSubscription> SubscribeResourcesAsync(CancellationToken cancellationToken)
    {
        if (_resourceChannelProvider == null)
        {
            throw new InvalidOperationException("No channel provider set.");
        }

        var channel = _resourceChannelProvider();

        return Task.FromResult(new ResourceViewModelSubscription(_initialResources?.ToImmutableArray() ?? [], BuildSubscription(channel, cancellationToken)));

        async static IAsyncEnumerable<IReadOnlyList<ResourceViewModelChange>> BuildSubscription(Channel<IReadOnlyList<ResourceViewModelChange>> channel, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
    }

    public IAsyncEnumerable<WatchInteractionsResponseUpdate> SubscribeInteractionsAsync(CancellationToken cancellationToken)
    {
        if (_interactionChannelProvider == null)
        {
            throw new InvalidOperationException("No channel provider set.");
        }

        var channel = _interactionChannelProvider();

        return BuildSubscription(channel, cancellationToken);

        async static IAsyncEnumerable<WatchInteractionsResponseUpdate> BuildSubscription(Channel<WatchInteractionsResponseUpdate> channel, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
    }

    public async Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken)
    {
        if (_sendInteractionUpdateChannel == null)
        {
            throw new InvalidOperationException("No resource command channel set.");
        }

        await _sendInteractionUpdateChannel.Writer.WriteAsync(request, cancellationToken);
    }

    public ResourceViewModel? GetResource(string resourceName) => null;

    public IReadOnlyList<ResourceViewModel> GetResources() => _initialResources?.ToList() ?? [];
}
