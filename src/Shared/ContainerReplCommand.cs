// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Terminals;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETERMINAL001
#pragma warning disable ASPIRECONTAINERRUNTIME001

namespace Aspire.Hosting;

internal static class ContainerReplCommand
{
    internal static IResourceBuilder<T> WithReplCommand<T>(
        this IResourceBuilder<T> builder,
        Func<CancellationToken, Task<TerminalLaunchOptions>> createOptions) where T : ContainerResource
    {
        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            return builder;
        }

        return builder.WithCommand("repl", "REPL", async context =>
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var notifications = context.Services.GetRequiredService<ResourceNotificationService>();
            if (!notifications.TryGetCurrentState(context.ResourceName, out var resourceEvent) ||
                resourceEvent.Snapshot.State?.Text != KnownResourceStates.Running)
            {
                return CommandResults.Failure("The container is not running.");
            }

            // The runtime ID follows container restarts and Aspire's generated name suffixes.
            var containerId = GetContainerId(resourceEvent.Snapshot);
            if (string.IsNullOrEmpty(containerId))
            {
                return CommandResults.Failure("The container ID is not available yet.");
            }

            var runtime = await context.Services.GetRequiredService<IContainerRuntimeResolver>()
                .ResolveAsync(context.CancellationToken).ConfigureAwait(false);
            var options = await createOptions(context.CancellationToken).ConfigureAwait(false);
            var execOptions = CreateExecOptions(options, runtime.Name.ToLowerInvariant(), containerId);
            context.CancellationToken.ThrowIfCancellationRequested();

            var terminals = context.Services.GetRequiredService<TerminalService>();
            // Dock terminals outlive the command and are disposed when closed or when the AppHost shuts down.
            var terminal = terminals.CreateTerminal(execOptions);
            terminal.Start();
            terminal.Show();

            return CommandResults.Success();
        }, new CommandOptions
        {
            Description = "Open an authenticated REPL inside the running container in the terminal dock.",
            IconName = "WindowConsole",
            UpdateState = context => context.ResourceSnapshot.State?.Text == KnownResourceStates.Running &&
                !string.IsNullOrEmpty(GetContainerId(context.ResourceSnapshot))
                    ? ResourceCommandState.Enabled
                    : ResourceCommandState.Disabled
        });
    }

    internal static TerminalLaunchOptions CreateExecOptions(TerminalLaunchOptions options, string runtime, string containerId)
    {
        var execOptions = new TerminalLaunchOptions
        {
            Title = options.Title,
            Executable = runtime,
            Arguments = ["exec", "-it"],
            Placement = TerminalPlacement.Dock
        };

        foreach (var (name, value) in options.EnvironmentVariables)
        {
            // `docker exec --env PGPASSWORD` forwards the CLI's environment without exposing the value in argv.
            execOptions.Arguments.Add("--env");
            execOptions.Arguments.Add(name);
            execOptions.EnvironmentVariables[name] = value;
        }

        execOptions.Arguments.Add(containerId);
        execOptions.Arguments.Add(options.Executable);
        foreach (var argument in options.Arguments)
        {
            execOptions.Arguments.Add(argument);
        }

        return execOptions;
    }

    private static string? GetContainerId(CustomResourceSnapshot snapshot) =>
        snapshot.Properties.FirstOrDefault(property => property.Name == "container.id")?.Value as string;
}
