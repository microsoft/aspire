// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Parsing;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Spectre.Console;

#if DEBUG
using System.Diagnostics;
#endif

using Aspire.Cli.Bundles;
using Aspire.Cli.Commands.Sdk;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using BaseRootCommand = System.CommandLine.RootCommand;

namespace Aspire.Cli.Commands;

internal sealed class RootCommand : BaseRootCommand
{
    internal const int DefaultCaptureProfileDelaySeconds = 5;

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

    /// <summary>
    /// Root-level <c>--info</c> flag that prints the running CLI's version,
    /// channel, and the list of Aspire CLI installs discovered on this machine.
    /// Root-only (non-recursive): <c>aspire --info</c> fires the install-info
    /// surface, while <c>aspire &lt;subcommand&gt; --info</c> is a separate
    /// operation — the unmatched <c>--info</c> token does not bind at
    /// subcommand scope, the action does not fire, and the subcommand runs
    /// normally. Keeping install enumeration root-only avoids the recursive-option
    /// / subcommand-local <c>--format</c> shadowing trap, where a recursive
    /// root <c>--format</c> would be quietly swallowed by a subcommand's own
    /// <c>--format</c> option. Wired to <see cref="InfoOptionAction"/> in the
    /// constructor so it short-circuits subcommand routing when set on root
    /// (the same way <c>--help</c> and <c>--version</c> do). Per-instance (not
    /// static): the option carries the <see cref="InfoOptionAction"/> bound to
    /// *this* <see cref="RootCommand"/>'s service provider, so concurrent tests
    /// that each build their own <see cref="RootCommand"/> don't fight over
    /// the <see cref="Option.Action"/> mutation.
    /// </summary>
    public readonly Option<bool> InfoOption = new(CommonOptionNames.Info)
    {
        Description = RootCommandStrings.InfoArgumentDescription,
    };

    /// <summary>
    /// Hidden modifier for <see cref="InfoOption"/>. When set, the action
    /// emits only the running CLI's row instead of the full install table.
    /// Reused as the cross-version peer-probe contract: in JSON mode the
    /// output is a single-element <see cref="Acquisition.InstallationInfo"/> array
    /// (see <see cref="Acquisition.PeerInstallProbe"/>). Root-only to match
    /// <see cref="InfoOption"/>. Per-instance for the same reason as
    /// <see cref="InfoOption"/>: the "requires <c>--info</c>" validator added
    /// in the constructor would otherwise accumulate across concurrent
    /// <see cref="RootCommand"/> constructions.
    /// </summary>
    public readonly Option<bool> InfoSelfOption = new("--self")
    {
        Hidden = true
    };

    /// <summary>
    /// Hidden output-format modifier for <see cref="InfoOption"/>. Accepts
    /// <c>list</c> (the default text rendering) or <c>json</c>. Hidden because
    /// it is only meaningful when paired with <c>--info</c>. Root-only to
    /// match <see cref="InfoOption"/>: keeping <c>--format</c> non-recursive
    /// here avoids shadowing subcommand-local <c>--format</c> options (e.g.
    /// <c>aspire doctor --format json</c>, <c>aspire run --format json</c>),
    /// which both define their own <c>--format</c> with a different value set.
    /// Per-instance for the same reason as <see cref="InfoOption"/>: the
    /// "requires <c>--info</c>" validator added in the constructor would
    /// otherwise accumulate across concurrent <see cref="RootCommand"/>
    /// constructions.
    /// </summary>
    public readonly Option<InfoOutputFormat> InfoFormatOption = new("--format")
    {
        Description = RootCommandStrings.InfoFormatArgumentDescription,
        Hidden = true
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

    public static readonly Option<bool> StartDebugSessionOption = new(CommonOptionNames.StartDebugSession)
    {
        Description = RunCommandStrings.StartDebugSessionArgumentDescription,
        Recursive = true,
        DefaultValueFactory = _ => false
    };

    public static readonly Option<bool> CaptureProfileOption = new("--capture-profile")
    {
        Recursive = true,
        Hidden = true,
        DefaultValueFactory = _ => false
    };

    public static readonly Option<FileInfo?> CaptureProfileOutputOption = new("--capture-profile-output")
    {
        Recursive = true,
        Hidden = true
    };

    public static readonly Option<int> CaptureProfileDelayOption = new("--capture-profile-delay")
    {
        Recursive = true,
        Hidden = true,
        DefaultValueFactory = _ => DefaultCaptureProfileDelaySeconds
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
        LsCommand lsCommand,
        ResourceCommand commandCommand,
        PsCommand psCommand,
        DescribeCommand describeCommand,
        LogsCommand logsCommand,
        IntegrationCommand integrationCommand,
        AddCommand addCommand,
        PublishCommand publishCommand,
        DeployCommand deployCommand,
        DestroyCommand destroyCommand,
        DoCommand doCommand,
        ConfigCommand configCommand,
        CacheCommand cacheCommand,
        CertificatesCommand certificatesCommand,
        DoctorCommand doctorCommand,
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
        IInteractionService interactionService,
        IAnsiConsole ansiConsole,
        InfoOptionAction infoOptionAction)
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
        Options.Add(InfoOption);
        Options.Add(InfoSelfOption);
        Options.Add(InfoFormatOption);
        if (ExtensionHelper.IsExtensionHost(interactionService, out _, out _))
        {
            Options.Add(StartDebugSessionOption);
        }
        Options.Add(CaptureProfileOption);
        Options.Add(CaptureProfileOutputOption);
        Options.Add(CaptureProfileDelayOption);

        // Wire the --info action so it short-circuits subcommand routing the
        // same way --help / --version do. Also gate --self and --format on
        // --info via validators so `aspire --self` and `aspire --format json`
        // (the only shapes that can reach these root-only options without
        // also setting --info) fail parse loudly instead of falling through
        // to grouped help.
        infoOptionAction.BindOptions(InfoSelfOption, InfoFormatOption);
        InfoOption.Action = infoOptionAction;
        var infoOption = InfoOption;
        InfoSelfOption.Validators.Add(result => ValidateRequiresInfo(result, InfoSelfOption, infoOption));
        InfoFormatOption.Validators.Add(result => ValidateRequiresInfo(result, InfoFormatOption, infoOption));

        // Handle standalone 'aspire' or 'aspire --banner' (no subcommand)
        this.SetAction((Func<ParseResult, CancellationToken, Task<int>>)((context, cancellationToken) =>
        {
            var bannerRequested = context.GetValue(BannerOption);
            if (bannerRequested)
            {
                // If --banner was passed, we've already shown it in Main, just exit successfully
                return Task.FromResult((int)CliExitCodes.Success);
            }

            // No subcommand provided - show grouped help but return InvalidCommand to signal usage error
            var writer = _ansiConsole.Profile.Out.Writer;
            var consoleWidth = _ansiConsole.Profile.Width;
            GroupedHelpWriter.WriteHelp(this, writer, consoleWidth);
            return Task.FromResult((int)CliExitCodes.InvalidCommand);
        }));

        Subcommands.Add(newCommand);
        Subcommands.Add(initCommand);
        Subcommands.Add(runCommand);
        Subcommands.Add(stopCommand);
        Subcommands.Add(startCommand);
        Subcommands.Add(waitCommand);
        Subcommands.Add(lsCommand);
        Subcommands.Add(commandCommand);
        Subcommands.Add(psCommand);
        Subcommands.Add(describeCommand);
        Subcommands.Add(logsCommand);
        Subcommands.Add(integrationCommand);
        Subcommands.Add(addCommand);
        Subcommands.Add(publishCommand);
        Subcommands.Add(configCommand);
        Subcommands.Add(cacheCommand);
        Subcommands.Add(certificatesCommand);
        Subcommands.Add(doctorCommand);
        Subcommands.Add(deployCommand);
        Subcommands.Add(destroyCommand);
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

    /// <summary>
    /// Fails parse when <paramref name="option"/> is supplied without
    /// <paramref name="infoOption"/>. <see cref="InfoSelfOption"/> and
    /// <see cref="InfoFormatOption"/> are only meaningful as modifiers on
    /// <c>--info</c>; without this validator <c>aspire --self</c> or
    /// <c>aspire --format json</c> would parse cleanly and either fall
    /// through to grouped help or — worse, for <c>--format</c> — swallow
    /// values intended for a subcommand option.
    /// </summary>
    private static void ValidateRequiresInfo(OptionResult result, Option option, Option<bool> infoOption)
    {
        if (result.Implicit)
        {
            return;
        }

        var info = result.Parent?.GetResult(infoOption);
        if (info is null || info.Implicit)
        {
            result.AddError(string.Format(CultureInfo.CurrentCulture, RootCommandStrings.InfoOptionRequiresInfo, option.Name));
        }
    }
}
