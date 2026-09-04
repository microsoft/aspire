// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;
using Microsoft.Extensions.DependencyInjection;

// InputType.Terminal is an experimental spike. PromptInputsAsync is also experimental.
#pragma warning disable ASPIREINTERACTION001

// AppHost-owned terminals - TerminalService, IAspireTerminal, TerminalCommand - are experimental.
#pragma warning disable ASPIRETERMINAL002

namespace Terminals.AppHost;

/// <summary>
/// Commands that exercise <see cref="InputType.Terminal"/> — an interaction input whose process is owned by the
/// AppHost itself rather than orchestrated by Aspire.
/// </summary>
/// <remarks>
/// This is the counterpart to <c>WithTerminal()</c>. With <c>WithTerminal()</c> the terminal is attached to a resource
/// DCP already runs, and the PTY lives in a separate Aspire.TerminalHost process. Here the AppHost spawns and owns the
/// process, and the session is tunneled to the browser over the dashboard's existing gRPC connection. That makes it
/// possible to shell into things Aspire does not orchestrate — the <c>docker exec</c> commands below are the
/// motivating example.
/// </remarks>
internal static class TerminalInteractionCommands
{
    /// <summary>
    /// Adds a command that opens an interactive shell running as a child process of the AppHost.
    /// </summary>
    /// <remarks>
    /// Nothing about this shell is tied to <paramref name="resource"/>; commands just need a host resource to hang off.
    /// </remarks>
    [AspireExportIgnore(Reason = "Uses interaction service callbacks and command handlers that are not ATS-compatible.")]
    public static IResourceBuilder<T> WithAppHostShellCommand<T>(this IResourceBuilder<T> resource) where T : IResource
    {
        return resource.WithCommand(
            "terminal-interaction-shell",
            "Open shell (interaction terminal)",
            executeCommand: async commandContext =>
            {
                var interactionService = commandContext.Services.GetRequiredService<IInteractionService>();

                // The input describes the workload only. Aspire owns the terminal: it attaches the HMP1 server
                // transport that carries the session over gRPC, then runs and tears down the process.
                var terminal = OperatingSystem.IsWindows()
                    ? new TerminalCommand("cmd.exe")
                    : new TerminalCommand("/bin/bash", "-i", "-l");

                var result = await interactionService.PromptInputsAsync(
                    "AppHost shell",
                    "This shell is a child process of the AppHost. Closing the dialog terminates it.",
                    [
                        new InteractionInput
                        {
                            Name = "shell",
                            Label = "Shell",
                            InputType = InputType.Terminal,
                            Terminal = terminal
                        }
                    ],
                    cancellationToken: commandContext.CancellationToken);

                return result.Canceled
                    ? CommandResults.Failure("Canceled")
                    : CommandResults.Success();
            });
    }

    /// <summary>
    /// Adds a command that shells into this container with <c>docker exec -it &lt;container&gt; /bin/sh</c>.
    /// </summary>
    [AspireExportIgnore(Reason = "Uses interaction service callbacks and command handlers that are not ATS-compatible.")]
    public static IResourceBuilder<ContainerResource> WithContainerShellCommand(this IResourceBuilder<ContainerResource> container)
    {
        return container.WithCommand(
            "terminal-interaction-docker",
            "Shell into container (docker exec)",
            executeCommand: commandContext => ExecIntoContainerAsync(
                commandContext,
                ResolveContainerName(container.Resource),
                ["/bin/sh"],
                title: $"Shell into '{container.Resource.Name}'",
                message: $"Runs `docker exec -it {ResolveContainerName(container.Resource)} /bin/sh` from the AppHost process."));
    }

    /// <summary>
    /// Adds a command that opens a Node REPL inside this container with <c>docker exec -it &lt;container&gt; node</c>.
    /// </summary>
    /// <remarks>
    /// The Node REPL is a readline app, so it exercises cursor addressing, history, and tab completion across the
    /// tunnel in a way a plain shell prompt does not.
    /// </remarks>
    [AspireExportIgnore(Reason = "Uses interaction service callbacks and command handlers that are not ATS-compatible.")]
    public static IResourceBuilder<ContainerResource> WithNodeReplCommand(this IResourceBuilder<ContainerResource> container)
    {
        return container.WithCommand(
            "terminal-interaction-node",
            "Node REPL (docker exec)",
            executeCommand: commandContext => ExecIntoContainerAsync(
                commandContext,
                ResolveContainerName(container.Resource),
                ["node"],
                title: "Node REPL",
                message: $"Runs `docker exec -it {ResolveContainerName(container.Resource)} node` from the AppHost process."));
    }

    /// <summary>
    /// Opens an interaction terminal whose process is <c>docker exec -it</c> into <paramref name="containerName"/>.
    /// </summary>
    /// <remarks>
    /// This is the motivating scenario for AppHost-owned terminals: shelling into a container in the app model without
    /// Aspire orchestrating the exec itself. <c>-it</c> is required so docker allocates a TTY on the container side;
    /// Aspire supplies the PTY on this side.
    /// </remarks>
    private static async Task<ExecuteCommandResult> ExecIntoContainerAsync(
        ExecuteCommandContext commandContext,
        string containerName,
        string[] command,
        string title,
        string message)
    {
        var interactionService = commandContext.Services.GetRequiredService<IInteractionService>();

        var terminal = new TerminalCommand("docker", ["exec", "-it", containerName, .. command]);

        var result = await interactionService.PromptInputsAsync(
            title,
            message,
            [
                new InteractionInput
                {
                    Name = "shell",
                    Label = "Container shell",
                    InputType = InputType.Terminal,
                    Terminal = terminal
                }
            ],
            cancellationToken: commandContext.CancellationToken);

        return result.Canceled
            ? CommandResults.Failure("Canceled")
            : CommandResults.Success();
    }

    /// <summary>
    /// Adds a command that opens a dock terminal shelled into this container and drives it with the automation API.
    /// </summary>
    /// <remarks>
    /// This is the counterpart to the interaction-input commands above. Instead of a modal dialog bound to a single
    /// dialog lifetime, the terminal becomes a tab in the dashboard's terminal dock (Ctrl+`) that outlives the command
    /// that created it. It also exercises <c>IAspireTerminal</c>'s automation surface — send input, wait for output,
    /// read the screen — which is how AppHost code can script a terminal it owns.
    /// </remarks>
    [AspireExportIgnore(Reason = "Uses TerminalService and command handlers that are not ATS-compatible.")]
    public static IResourceBuilder<ContainerResource> WithDockShellCommand(this IResourceBuilder<ContainerResource> container)
    {
        return container.WithCommand(
            "terminal-dock-shell",
            "Shell into container (terminal dock)",
            executeCommand: async commandContext =>
            {
                var containerName = ResolveContainerName(container.Resource);
                var terminalService = commandContext.Services.GetRequiredService<TerminalService>();

                // Not disposed here on purpose: the tab is meant to outlive the command. The user closes it from the
                // dock, and TerminalService tears down anything still open when the AppHost shuts down.
                var terminal = terminalService.CreateTerminal(new TerminalLaunchOptions
                {
                    Title = container.Resource.Name,
                    Command = new TerminalCommand("docker", "exec", "-it", containerName, "/bin/sh")
                });

                // Reveals the dock in every connected browser and switches it to this tab.
                terminal.Show();

                try
                {
                    // Automation: type a command and wait for its output. The workload starts on the first automation
                    // call even if nobody has attached a browser yet.
                    await terminal.SendTextAsync("echo aspire-dock-ready\r", commandContext.CancellationToken);
                    await terminal.WaitForTextAsync("aspire-dock-ready", TimeSpan.FromSeconds(10), commandContext.CancellationToken);
                }
                catch (TimeoutException)
                {
                    return CommandResults.Failure("Terminal did not respond to automated input.");
                }

                return CommandResults.Success();
            });
    }

    /// <summary>
    /// Resolves the name docker knows this container by.
    /// </summary>
    /// <remarks>
    /// Without <c>WithContainerName</c>, DCP appends a random suffix to the resource name, so the resource name alone
    /// would not be a valid <c>docker exec</c> target. These playground containers set an explicit name; the fallback
    /// only exists so a misconfigured resource surfaces a docker error rather than throwing here.
    /// </remarks>
    private static string ResolveContainerName(ContainerResource container)
    {
        return container.TryGetLastAnnotation<ContainerNameAnnotation>(out var annotation)
            ? annotation.Name
            : container.Name;
    }
}
