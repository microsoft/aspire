// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli;

/// <summary>
/// Common command-line option names used for manual argument checks.
/// </summary>
internal static class CommonOptionNames
{
    public const string Version = "--version";
    public const string VersionShort = "-v";
    public const string Help = "--help";
    public const string HelpShort = "-h";
    public const string HelpAlt = "-?";
    public const string NoLogo = "--nologo";
    public const string Banner = "--banner";
    public const string Debug = "--debug";
    public const string DebugShort = "-d";
    public const string NonInteractive = "--non-interactive";
    public const string WaitForDebugger = "--wait-for-debugger";
    public const string CliWaitForDebugger = "--cli-wait-for-debugger";
    public const string StartDebugSession = "--start-debug-session";
    public const string CaptureProfile = "--capture-profile";
    public const string Self = "--self";
    public const string Info = "--info";

    /// <summary>
    /// Help and long-version options that are recognized as informational wherever they appear in
    /// the raw arguments (including after a subcommand, e.g. "doctor --help"). None take a value.
    /// The short version alias <c>-v</c> is excluded because subcommands can use it
    /// for their own options.
    /// </summary>
    private static readonly string[] s_helpVersionOptionNames =
        [Version, Help, HelpShort, HelpAlt];

    // Root options declared by RootCommand (src/Aspire.Cli/Commands/RootCommand.cs) that
    // *unconditionally* consume a following token as their value. Kept in sync manually:
    // DebugLevelOption ("--log-level"/"-l"), CaptureProfileOutputOption
    // ("--capture-profile-output"), CaptureProfileDelayOption ("--capture-profile-delay"), and
    // FormatOption ("--format"). Every other root option is an Option<bool> flag, which only
    // *conditionally* consumes a following token when it is a literal "true"/"false" (see
    // s_boolRootOptionNames below and the explicit-value handling for --info). This list exists
    // so a value that is literally "--info" (e.g. `--log-level --info run`) is recognized as that
    // option's value rather than a distinct --info flag.
    //
    // RootCommandTests.ValueTakingRootOptionNames_MatchesRootCommandsActualValueTakingOptions
    // enumerates RootCommand's actual options and fails if this list drifts from reality (e.g. a
    // new value-taking root option is added without updating this array).
    private static readonly string[] s_valueTakingRootOptionNames =
    [
        "--log-level", "-l",
        "--capture-profile-output",
        "--capture-profile-delay",
        "--format",
    ];

    /// <summary>
    /// Test-only view of <see cref="s_valueTakingRootOptionNames"/>, exposed so
    /// <c>RootCommandTests</c> can assert it stays in sync with RootCommand's actual value-taking
    /// options without reaching into private state via reflection.
    /// </summary>
    internal static IReadOnlyCollection<string> ValueTakingRootOptionNamesForTests => s_valueTakingRootOptionNames;

    // All of RootCommand's directly-declared Option<bool> options (name + aliases), including
    // Info and Self. Real System.CommandLine parsing treats a bare bool option (arity 0..1) as
    // implicitly true, but also accepts an explicit two-token value: it consumes the following
    // token as the option's value only when that token literally parses as a bool ("true"/"false",
    // case-insensitive) — anything else (e.g. a subcommand name or another option) is left alone.
    // IsRootInfoInvocation mirrors that so an explicit value like `--debug false` isn't
    // misidentified as the positional/subcommand-boundary token (the original defect this list
    // fixes: `--debug false --info` was stopping at "false" as if it were a subcommand).
    //
    // RootCommandTests.BoolRootOptionNames_MatchesRootCommandsActualBoolOptions enumerates
    // RootCommand's actual Option<bool> fields and fails if this list drifts from reality (e.g. a
    // new bool root option is added without updating this array).
    private static readonly string[] s_boolRootOptionNames =
    [
        Debug, DebugShort,
        NonInteractive,
        NoLogo,
        Banner,
        WaitForDebugger,
        CliWaitForDebugger,
        StartDebugSession,
        CaptureProfile,
        Self,
        Info,
    ];

    /// <summary>
    /// Test-only view of <see cref="s_boolRootOptionNames"/>, exposed so <c>RootCommandTests</c>
    /// can assert it stays in sync with RootCommand's actual Option&lt;bool&gt; options without
    /// reaching into private state via reflection.
    /// </summary>
    internal static IReadOnlyCollection<string> BoolRootOptionNamesForTests => s_boolRootOptionNames;

    /// <summary>
    /// Determines whether raw process arguments represent an informational invocation that
    /// should opt out of telemetry and suppress first-run/startup output (banner, telemetry
    /// notice, etc.).
    /// </summary>
    /// <remarks>
    /// Help and long-version options (<see cref="Version"/>, <see cref="Help"/>,
    /// <see cref="HelpShort"/>, <see cref="HelpAlt"/>) are recognized wherever
    /// they appear before the "--" app-argument delimiter — including after a
    /// subcommand, e.g. "doctor --help" — matching existing CLI behavior. They
    /// are bare flags that never take a value, so no value-consumption tracking
    /// is required for them, and unlike <see cref="Info"/> they are not sensitive
    /// to the subcommand boundary (only to "--").
    ///
    /// <see cref="Info"/> (<c>--info</c>) and <see cref="VersionShort"/> (<c>-v</c>) are different:
    /// they are recognized only before any subcommand/positional token or the "--" app-argument
    /// delimiter. Subcommands can define their own <c>-v</c> option, and "doctor --info" is the
    /// doctor subcommand's own (nonexistent) argument rather than root <c>--info</c>.
    /// </remarks>
    public static bool IsInformationalInvocation(IReadOnlyList<string> args)
    {
        foreach (var arg in args)
        {
            if (arg == "--")
            {
                // Stop scanning entirely: everything from here on is an app/positional argument.
                break;
            }

            if (Array.IndexOf(s_helpVersionOptionNames, arg) >= 0)
            {
                return true;
            }
        }

        return IsRootScopedInformationalInvocation(args, includeVersionShort: true);
    }

    /// <summary>
    /// Returns whether the raw arguments request a truthy root-level <c>--info</c> action before
    /// any subcommand, positional argument, or application-argument delimiter.
    /// </summary>
    public static bool IsRootInfoInvocation(IReadOnlyList<string> args)
        => IsRootScopedInformationalInvocation(args, includeVersionShort: false);

    private static bool IsRootScopedInformationalInvocation(IReadOnlyList<string> args, bool includeVersionShort)
    {
        var infoRequested = false;

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];

            if (token == "--")
            {
                // Everything after "--" is an app/positional argument, never a root option.
                return false;
            }

            // "--option=value" is a single token, so the value portion can never become a
            // separate "--info" token; only the option-name portion (before '=') matters here.
            var separatorIndex = token.IndexOf('=');
            var optionName = separatorIndex >= 0 ? token[..separatorIndex] : token;

            if (optionName == VersionShort)
            {
                if (includeVersionShort)
                {
                    return true;
                }

                continue;
            }

            if (optionName == Info)
            {
                // --info is Option<bool>, so "--info=false"/"--info=true" (and, per
                // System.CommandLine's own two-token parsing, "--info false"/"--info true") are
                // valid explicit forms alongside the bare "--info" flag. System.CommandLine only
                // treats a following bare token as the option's explicit value when that token
                // parses as a literal bool ("true"/"false", case-insensitive) — anything else
                // (e.g. "run") is left alone and --info is implicitly true. Mirror that here so
                // "--info=false"/"--info false" are correctly NOT classified as informational.
                if (separatorIndex >= 0)
                {
                    var explicitText = token[(separatorIndex + 1)..];
                    infoRequested = !bool.TryParse(explicitText, out var explicitValue) || explicitValue;
                    continue;
                }

                if (i + 1 < args.Count && bool.TryParse(args[i + 1], out var nextTokenValue))
                {
                    // Consume the explicit value so it is not reconsidered as a positional token.
                    infoRequested = nextTokenValue;
                    i++;
                    continue;
                }

                infoRequested = true;
                continue;
            }

            if (separatorIndex < 0 && Array.IndexOf(s_valueTakingRootOptionNames, optionName) >= 0)
            {
                // Consume the next token as this option's value so it is never reconsidered as
                // a distinct --info flag (e.g. `--log-level --info run`, `--format --info run`).
                i++;
                continue;
            }

            if (separatorIndex < 0 && Array.IndexOf(s_boolRootOptionNames, optionName) >= 0
                && i + 1 < args.Count && bool.TryParse(args[i + 1], out _))
            {
                // A bare bool root option (e.g. --debug, --non-interactive) followed by a
                // literal "true"/"false" token is that option's explicit value, per
                // System.CommandLine's own two-token bool parsing (see the --info handling
                // above). Consume it so it is never mistaken for the positional/subcommand
                // token below (the original defect: `--debug false --info` was stopping at
                // "false" as if it were a subcommand boundary).
                i++;
                continue;
            }

            if (!token.StartsWith('-'))
            {
                // The first positional token marks the subcommand boundary; any --info from
                // this point on belongs to the subcommand, not the root command.
                return false;
            }
        }

        return infoRequested;
    }
}
