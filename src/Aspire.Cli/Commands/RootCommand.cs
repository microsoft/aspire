// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using Microsoft.Extensions.Logging;
using Spectre.Console;

#if DEBUG
using System.Globalization;
using System.Diagnostics;
#endif

using Aspire.Cli.Bundles;
using Aspire.Cli.Commands.Sdk;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using BaseRootCommand = System.CommandLine.RootCommand;

namespace Aspire.Cli.Commands;

internal sealed class RootCommand : BaseRootCommand
{
    public static readonly Option<bool> DebugOption = new(CommonOptionNames.Debug, CommonOptionNames.DebugShort)
    {
        Description = RootCommandStrings.DebugArgumentDescription,
        Recursive = true,
        Hidden = true // Hidden for backward compatibility, use --log-level instead
    };

    public static readonly Option<LogLevel?> DebugLevelOption = new("--log-level", "-l")
    {
        Description = RootCommandStrings.DebugLevelArgumentDescription,
        Recursive = true
    };

    public static readonly Option<bool> NonInteractiveOption = new(CommonOptionNames.NonInteractive)
    {
        Description = RootCommandStrings.NonInteractiveArgumentDescription,
        Recursive = true
    };

    public static readonly Option<bool> NoLogoOption = new(CommonOptionNames.NoLogo)
    {
        Description = RootCommandStrings.NoLogoArgumentDescription,
        Recursive = true
    };

    public static readonly Option<bool> BannerOption = new(CommonOptionNames.Banner)
    {
        Description = RootCommandStrings.BannerArgumentDescription,
        Recursive = true
    };

    public static readonly Option<bool> WaitForDebuggerOption = new(CommonOptionNames.WaitForDebugger)
    {
        Description = RootCommandStrings.WaitForDebuggerArgumentDescription,
        Recursive = true,
        DefaultValueFactory = _ => false
    };

    public static readonly Option<bool> CliWaitForDebuggerOption = new(CommonOptionNames.CliWaitForDebugger)
    {
        Description = RootCommandStrings.CliWaitForDebuggerArgumentDescription,
        Recursive = true,
        Hidden = true,
        DefaultValueFactory = _ => false
    };

    /// <summary>
    /// Global options that should be passed through to child CLI processes when spawning.
    /// Add new global options here to ensure they are forwarded during detached mode execution.
    /// </summary>
    private static readonly (Option Option, Func<ParseResult, string[]?> GetArgs)[] s_childProcessOptions =
    [
        (DebugOption, pr => pr.GetValue(DebugOption) ? ["--debug"] : null),
        (DebugLevelOption, pr =>
        {
            var level = pr.GetValue(DebugLevelOption);
            return level.HasValue ? ["--log-level", level.Value.ToString()] : null;
        }),
        (WaitForDebuggerOption, pr => pr.GetValue(WaitForDebuggerOption) ? ["--wait-for-debugger"] : null),
    ];

    /// <summary>
    /// Gets the command-line arguments for global options that should be passed to a child CLI process.
    /// </summary>
    /// <param name="parseResult">The parse result from the current command invocation.</param>
    /// <returns>Arguments to pass to the child process.</returns>
    public static IEnumerable<string> GetChildProcessArgs(ParseResult parseResult)
    {
        foreach (var (_, getArgs) in s_childProcessOptions)
        {
            var args = getArgs(parseResult);
            if (args is not null)
            {
                foreach (var arg in args)
                {
                    yield return arg;
                }
            }
        }
    }

    private readonly IInteractionService _interactionService;
    private readonly IAnsiConsole _ansiConsole;

    public RootCommand(
        NewCommand newCommand,
        InitCommand initCommand,
        RunCommand runCommand,
        StopCommand stopCommand,
        StartCommand startCommand,
        WaitCommand waitCommand,
        ResourceCommand commandCommand,
        PsCommand psCommand,
        DescribeCommand describeCommand,
        LogsCommand logsCommand,
        AddCommand addCommand,
        PublishCommand publishCommand,
        DeployCommand deployCommand,
        DoCommand doCommand,
        ConfigCommand configCommand,
        CacheCommand cacheCommand,
        CertificatesCommand certificatesCommand,
        DoctorCommand doctorCommand,
        ExecCommand execCommand,
        UpdateCommand updateCommand,
        McpCommand mcpCommand,
        AgentCommand agentCommand,
        TelemetryCommand telemetryCommand,
        ExportCommand exportCommand,
        DashboardCommand dashboardCommand,
        DocsCommand docsCommand,
        SecretCommand secretCommand,
        SdkCommand sdkCommand,
        RestoreCommand restoreCommand,
        SetupCommand setupCommand,
#if DEBUG
        RenderCommand renderCommand,
#endif
        ExtensionInternalCommand extensionInternalCommand,
        IBundleService bundleService,
        IFeatures featureFlags,
        IInteractionService interactionService,
        IAnsiConsole ansiConsole)
        : base(RootCommandStrings.Description)
    {
        _interactionService = interactionService;
        _ansiConsole = ansiConsole;

#if DEBUG
        CliWaitForDebuggerOption.Validators.Add((result) =>
        {

            var waitForDebugger = result.GetValueOrDefault<bool>();

            if (waitForDebugger)
            {
                _interactionService.ShowStatus(
                    string.Format(CultureInfo.CurrentCulture, RootCommandStrings.WaitingForDebugger, Environment.ProcessId),
                    () =>
                    {
                        while (!Debugger.IsAttached)
                        {
                            Thread.Sleep(1000);
                        }

                        Debugger.Break();
                    }, emoji: KnownEmojis.Bug);
            }
        });
#endif

        Options.Add(DebugOption);
        Options.Add(DebugLevelOption);
        Options.Add(NonInteractiveOption);
        Options.Add(NoLogoOption);
        Options.Add(BannerOption);
        Options.Add(WaitForDebuggerOption);
        Options.Add(CliWaitForDebuggerOption);

        // Handle standalone 'aspire' or 'aspire --banner' (no subcommand)
        this.SetAction((context, cancellationToken) =>
        {
            var bannerRequested = context.GetValue(BannerOption);
            if (bannerRequested)
            {
                // If --banner was passed, we've already shown it in Main, just exit successfully
                return Task.FromResult(ExitCodeConstants.Success);
            }

            // No subcommand provided - show grouped help but return InvalidCommand to signal usage error
            var writer = _ansiConsole.Profile.Out.Writer;
            var consoleWidth = _ansiConsole.Profile.Width;
            GroupedHelpWriter.WriteHelp(this, writer, consoleWidth);
            return Task.FromResult(ExitCodeConstants.InvalidCommand);
        });

        Subcommands.Add(newCommand);
        Subcommands.Add(initCommand);
        Subcommands.Add(runCommand);
        Subcommands.Add(stopCommand);
        Subcommands.Add(startCommand);
        Subcommands.Add(waitCommand);
        Subcommands.Add(commandCommand);
        Subcommands.Add(psCommand);
        Subcommands.Add(describeCommand);
        Subcommands.Add(logsCommand);
        Subcommands.Add(addCommand);
        Subcommands.Add(publishCommand);
        Subcommands.Add(configCommand);
        Subcommands.Add(cacheCommand);
        Subcommands.Add(certificatesCommand);
        Subcommands.Add(doctorCommand);
        Subcommands.Add(deployCommand);
        Subcommands.Add(doCommand);
        Subcommands.Add(updateCommand);
        Subcommands.Add(extensionInternalCommand);
        Subcommands.Add(mcpCommand);
        Subcommands.Add(agentCommand);
        Subcommands.Add(telemetryCommand);
        Subcommands.Add(exportCommand);
        Subcommands.Add(docsCommand);
        Subcommands.Add(dashboardCommand);
        Subcommands.Add(secretCommand);

#if DEBUG
        Subcommands.Add(renderCommand);
#endif

        if (bundleService.IsBundle)
        {
            Subcommands.Add(setupCommand);
        }

        if (featureFlags.IsFeatureEnabled(KnownFeatures.ExecCommandEnabled, false))
        {
            Subcommands.Add(execCommand);
        }

        Subcommands.Add(sdkCommand);
        Subcommands.Add(restoreCommand);

        // Replace the default --help action with grouped help output.
        // Add -v as a short alias for --version.
        foreach (var option in Options)
        {
            if (option is HelpOption helpOption)
            {
                helpOption.Action = new GroupedHelpAction(this, _ansiConsole);
            }
            else if (option is VersionOption versionOption)
            {
                versionOption.Aliases.Add("-v");
            }
        }

    }
}
