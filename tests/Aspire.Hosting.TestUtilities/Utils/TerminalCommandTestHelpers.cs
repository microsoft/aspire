// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIRETERMINAL001

namespace Aspire.Hosting.Tests.Utils;

public static class TerminalCommandTestHelpers
{
    public static async Task<AspireTerminal> ExecuteTerminalCommandAsync(
        DistributedApplication app,
        IResource resource,
        string commandName,
        CancellationToken cancellationToken)
    {
        var terminals = app.Services.GetRequiredService<TerminalService>();
        using var subscription = terminals.SubscribeDockTerminals();
        var command = Assert.Single(resource.Annotations.OfType<ResourceCommandAnnotation>(), command => command.Name == commandName);
        var result = await command.ExecuteCommand(new ExecuteCommandContext
        {
            ResourceName = resource.Name,
            Services = app.Services,
            CancellationToken = cancellationToken,
            Arguments = new InteractionInputCollection([]),
            Logger = NullLogger.Instance
        });
        Assert.True(result.Success, result.Message);

        await foreach (var update in subscription.Subscription.WithCancellation(cancellationToken))
        {
            if (update is TerminalChange { ChangeType: TerminalChangeType.Activated } activation)
            {
                Assert.True(terminals.TryGetTerminal(activation.Terminal.Id, out var terminal));
                return terminal;
            }
        }

        throw new InvalidOperationException("The command did not activate a dock terminal.");
    }
}
