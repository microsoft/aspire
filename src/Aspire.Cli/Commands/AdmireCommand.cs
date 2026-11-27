// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Resources;
using Aspire.Cli.Widgets;
using Hex1b;
using Hex1b.Input;

namespace Aspire.Cli.Commands;

internal sealed class AdmireCommand(CommonCommandServices services, ConsoleEnvironment consoleEnvironment)
    : BaseCommand("admire", RootCommandStrings.AdmireDescription, services)
{
    internal override HelpGroup HelpGroup => HelpGroup.ToolsAndConfiguration;

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // The resolved identity can include a prerelease label and build metadata:
        // "17.0.0-preview.1+abc123" is displayed as "17.0.0".
        var version = ExecutionContext.IdentityVersion.Split(['-', '+'], 2)[0];
        // Redirected and no-ANSI terminals cannot host a flow app.
        if (!HostEnvironment.SupportsInteractiveOutput || !HostEnvironment.SupportsAnsi)
        {
            var message = string.Format(CultureInfo.CurrentCulture, RootCommandStrings.AdmireFallbackMessageFormat, version);
            InteractionService.DisplayRawText(message);
        }
        else
        {
            try
            {
                await RunSplashAsync(Hex1bTerminal.CreateBuilder()
                    .WithDimensions(consoleEnvironment.Out.Profile.Width, consoleEnvironment.Out.Profile.Height), version, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C in raw mode cancels the flow, not the command's outer token.
                return CommandResult.Cancelled();
            }
        }

        return CommandResult.Success();
    }

    internal static async Task RunSplashAsync(Hex1bTerminalBuilder builder, string version, CancellationToken cancellationToken)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var terminal = builder.WithMouse().WithHex1bFlow(async flow =>
        {
            using var flowStopping = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token, flow.CancellationToken);
            var state = new AdmireSplashState(version);
            var step = flow.Step(ctx => AdmireSplashWidget.Build(ctx, state)
                .InputBindings(bindings => bindings.Ctrl().Key(Hex1bKey.C).Action(_ => stopping.Cancel())),
                options => options.EnableMouse = true);

            await state.Completion.WaitAsync(flowStopping.Token);

            // The particles have returned to the mouse. Freeze the empty viewport,
            // reserving one row for the shell prompt in the normal buffer.
            await step.CompleteAsync(ctx => ctx.VStack(_ =>
                [AdmireSplashWidget.Build(ctx, state).FixedWidth(flow.TerminalWidth)
                    .FixedHeight(Math.Max(1, flow.TerminalHeight - 1))]), flowStopping.Token);
        }, options =>
        {
            options.UseSoftWrapTombstones = true;
            // The builder queries the initial cursor before entering raw mode.
            // A second Console.GetCursorPosition query at flow startup can deadlock
            // with the terminal input pump on Unix, which shares stdin.
            options.CursorRowProvider = () => null;
        }).Build();

        await terminal.RunAsync(stopping.Token);
    }
}
