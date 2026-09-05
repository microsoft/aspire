// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Hosting.Terminals;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// <summary>The upper limit the number guess dialog starts on.</summary>
    private const int DefaultUpperLimit = 100;

    /// <summary>How long to pause between guesses so the game is watchable rather than instantaneous.</summary>
    private static readonly TimeSpan s_guessInterval = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for the game to print a prompt or a reply before giving up.</summary>
    private static readonly TimeSpan s_promptTimeout = TimeSpan.FromSeconds(30);

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
                var terminalService = commandContext.Services.GetRequiredService<TerminalService>();

                var command = OperatingSystem.IsWindows()
                    ? new TerminalCommand("cmd.exe")
                    : new TerminalCommand("/bin/bash") { Arguments = ["-i", "-l"] };

                // The caller owns the terminal: it starts it, and disposes it here rather than the dialog doing so.
                await using var terminal = terminalService.CreateTerminal(new TerminalLaunchOptions
                {
                    Title = "Shell",
                    Command = command,
                    Placement = TerminalPlacement.Dialog
                });

                terminal.Start();

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
        var terminalService = commandContext.Services.GetRequiredService<TerminalService>();

        // The caller owns the terminal: it starts it, and disposes it here rather than the dialog doing so.
        await using var terminal = terminalService.CreateTerminal(new TerminalLaunchOptions
        {
            Title = title,
            Command = new TerminalCommand("docker")
            {
                Arguments = ["exec", "-it", containerName, .. command]
            },
            Placement = TerminalPlacement.Dialog
        });

        terminal.Start();

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
    /// dialog lifetime, the terminal becomes a tab in the dashboard's terminal dock (Shift+`) that outlives the command
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
                    Command = new TerminalCommand("docker")
                    {
                        Arguments = ["exec", "-it", containerName, "/bin/sh"]
                    }
                });

                // Reveals the dock in every connected browser and switches it to this tab.
                terminal.Start();
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
    /// Adds a command that plays a terminal-based guessing game by driving the process from AppHost code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the "automate an interactive prompt" scenario. Plenty of tools an AppHost needs to invoke are only
    /// available as interactive console programs — they log in, prompt for confirmation, ask which subscription to
    /// use — and there is no API to call instead. An <see cref="InputType.Terminal"/> input plus
    /// <see cref="IAspireTerminal"/>'s automation members lets AppHost code answer those prompts itself while the
    /// human watches it happen, and step in whenever it cannot.
    /// </para>
    /// <para>
    /// The flow is: prompt for the game's upper limit, open a terminal running <c>numberguess.cs</c>, then bisect —
    /// type a guess, read the reply back off the screen, halve the range — until the number is found. The dialog is
    /// then closed from code and replaced with the answer.
    /// </para>
    /// </remarks>
    [AspireExportIgnore(Reason = "Uses TerminalService, interaction service callbacks, and command handlers that are not ATS-compatible.")]
    public static IResourceBuilder<T> WithNumberGuessCommand<T>(this IResourceBuilder<T> resource) where T : IResource
    {
        return resource.WithCommand(
            "terminal-number-guess",
            "Number guess (automated terminal)",
            executeCommand: async commandContext =>
            {
                var interactionService = commandContext.Services.GetRequiredService<IInteractionService>();
                var terminalService = commandContext.Services.GetRequiredService<TerminalService>();

                var limitResult = await interactionService.PromptInputsAsync(
                    "Number guess",
                    "Pick an upper limit. The AppHost will then play the game itself by typing into a terminal and reading the replies back off the screen.",
                    [
                        new InteractionInput
                        {
                            Name = "limit",
                            Label = "Upper limit",
                            InputType = InputType.Number,
                            Value = DefaultUpperLimit.ToString(CultureInfo.InvariantCulture),
                            Required = true
                        }
                    ],
                    cancellationToken: commandContext.CancellationToken);

                if (limitResult.Canceled)
                {
                    return CommandResults.Failure("Canceled");
                }

                // The dialog's number input only guarantees "a number", so clamp rather than trust it. Below 2 there
                // is nothing to bisect, and the upper bound just keeps the game short enough to sit and watch.
                if (!int.TryParse(limitResult.Data["limit"].Value, CultureInfo.InvariantCulture, out var limit))
                {
                    limit = DefaultUpperLimit;
                }
                limit = Math.Clamp(limit, 2, 1_000_000);

                // The command owns the terminal for its whole life: it starts it, drives the game through the
                // handle, and disposes it once the answer has been shown.
                await using var terminal = terminalService.CreateTerminal(new TerminalLaunchOptions
                {
                    Title = "Number guess",
                    Command = BuildNumberGuessCommand(limit),
                    Placement = TerminalPlacement.Dialog
                });

                // Start before the dialog rather than letting the first attach do it, so `dotnet run --file` is
                // already compiling the script while the dialog is being raised.
                terminal.Start();

                using var gameCts = CancellationTokenSource.CreateLinkedTokenSource(commandContext.CancellationToken);

                // Raise the dialog before playing so a browser can attach while the opening moves are still being
                // made — otherwise the human joins after the game is already won.
                var dialogTask = interactionService.PromptInputsAsync(
                    "Number guess",
                    $"Guessing a number between 1 and {limit}. Every keystroke below is being typed by the AppHost.",
                    [
                        new InteractionInput
                        {
                            Name = "game",
                            Label = "Number guess",
                            InputType = InputType.Terminal,
                            Terminal = terminal
                        }
                    ],
                    cancellationToken: gameCts.Token);

                var playTask = PlayNumberGuessAsync(terminal, limit, gameCts.Token);

                // If the human closes the dialog first the terminal is torn down underneath us, so stop playing.
                if (await Task.WhenAny(dialogTask, playTask).ConfigureAwait(false) == dialogTask)
                {
                    await gameCts.CancelAsync();
                    return CommandResults.Failure("Canceled");
                }

                int number;
                int attempts;
                try
                {
                    (number, attempts) = await playTask;
                }
                catch (OperationCanceledException)
                {
                    return CommandResults.Failure("Canceled");
                }
                catch (Exception ex)
                {
                    // Unexpected. Surface the message in the dialog, but log the full exception too: the failure is
                    // otherwise reduced to a one-line string with no stack trace, which is the hardest kind of
                    // demo failure to diagnose.
                    commandContext.Services.GetRequiredService<ILoggerFactory>()
                        .CreateLogger(nameof(TerminalInteractionCommands))
                        .LogError(ex, "The number guess automation failed unexpectedly.");

                    await gameCts.CancelAsync();
                    return CommandResults.Failure(ex.Message);
                }

                // Leave the winning line on screen long enough to read before the dialog disappears.
                await Task.Delay(TimeSpan.FromSeconds(2), commandContext.CancellationToken);

                // Cancelling the token the prompt was started with is how code dismisses its own dialog, so the
                // result replaces the terminal rather than stacking on top of it. The terminal itself is disposed by
                // the `await using` above, once the answer has been shown.
                await gameCts.CancelAsync();
                await dialogTask;

                await interactionService.PromptMessageBoxAsync(
                    "Number guess",
                    $"Found it. The number was {number}, in {attempts} {(attempts == 1 ? "guess" : "guesses")}.",
                    cancellationToken: commandContext.CancellationToken);

                return CommandResults.Success();
            });
    }

    /// <summary>
    /// Plays <c>numberguess.cs</c> to completion by bisecting, and returns the number found and how many guesses it took.
    /// </summary>
    /// <remarks>
    /// Bisection needs at most ceil(log2(limit)) guesses, so the loop is bounded by construction. The guard on an
    /// exhausted range only fires if the game stops answering consistently, which would otherwise spin forever.
    /// </remarks>
    private static async Task<(int Number, int Attempts)> PlayNumberGuessAsync(IAspireTerminal terminal, int limit, CancellationToken cancellationToken)
    {
        // Generous: this is the first automation call, so it is what starts the workload, and a cold
        // `dotnet run --file` has to compile the script before the game prints anything.
        await terminal.WaitForTextAsync($"between 1 and {limit}", TimeSpan.FromMinutes(2), cancellationToken);

        var low = 1;
        var high = limit;

        for (var attempt = 1; low <= high; attempt++)
        {
            await terminal.WaitForTextAsync($"Guess #{attempt}: ", s_promptTimeout, cancellationToken);

            // The whole point of the demo is watching it play, so slow it down to human speed.
            await Task.Delay(s_guessInterval, cancellationToken);

            var guess = low + ((high - low) / 2);
            await terminal.SendTextAsync($"{guess.ToString(CultureInfo.InvariantCulture)}\r", cancellationToken);

            switch (await ReadReplyAsync(terminal, attempt, guess, cancellationToken))
            {
                case NumberGuessReply.Correct:
                    return (guess, attempt);
                case NumberGuessReply.TooLow:
                    low = guess + 1;
                    break;
                case NumberGuessReply.TooHigh:
                    high = guess - 1;
                    break;
            }
        }

        throw new InvalidOperationException("The game ruled out every number in the range without accepting a guess.");
    }

    /// <summary>
    /// Waits for the game's reply to a guess and reads it off the terminal screen.
    /// </summary>
    /// <remarks>
    /// The script tags each reply with its attempt number — <c>&gt;&gt; #3: 42 is too high</c> — so this can match on
    /// the whole reply rather than a prefix. That matters: waiting for <c>"#3: 42 is "</c> and then reading the screen
    /// would race the rest of the line being written. Polling for one of the three complete replies has no such race,
    /// and the attempt number keeps an earlier reply still on screen from being misread as this one.
    /// </remarks>
    private static async Task<NumberGuessReply> ReadReplyAsync(IAspireTerminal terminal, int attempt, int guess, CancellationToken cancellationToken)
    {
        var prefix = $">> #{attempt.ToString(CultureInfo.InvariantCulture)}: {guess.ToString(CultureInfo.InvariantCulture)} is ";
        var deadline = DateTime.UtcNow + s_promptTimeout;

        while (true)
        {
            var screen = terminal.GetScreenText();

            if (screen.Contains(prefix + "correct", StringComparison.Ordinal))
            {
                return NumberGuessReply.Correct;
            }

            if (screen.Contains(prefix + "too low", StringComparison.Ordinal))
            {
                return NumberGuessReply.TooLow;
            }

            if (screen.Contains(prefix + "too high", StringComparison.Ordinal))
            {
                return NumberGuessReply.TooHigh;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"The game did not reply to guess #{attempt} ({guess}) within {s_promptTimeout.TotalSeconds} seconds.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    /// <summary>
    /// Builds the command that runs the <c>numberguess.cs</c> file-based app.
    /// </summary>
    /// <remarks>
    /// The script is copied next to the AppHost binary (see the <c>Scripts\</c> item group in the project file) so it
    /// can be found without knowing where the source tree is. <c>DOTNET_HOST_PATH</c> is preferred over a bare
    /// <c>dotnet</c> so the game runs on the same SDK as the AppHost when one is pinned; file-based apps need .NET 10
    /// or later, which whatever is first on <c>PATH</c> may not be.
    /// </remarks>
    private static TerminalCommand BuildNumberGuessCommand(int limit)
    {
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "Scripts", "numberguess.cs");
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } hostPath ? hostPath : "dotnet";

        return new TerminalCommand(dotnet)
        {
            Arguments = ["run", "--file", scriptPath, "--", limit.ToString(CultureInfo.InvariantCulture)]
        };
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

    /// <summary>
    /// The game's answer to a single guess.
    /// </summary>
    private enum NumberGuessReply
    {
        TooLow,
        TooHigh,
        Correct
    }
}
