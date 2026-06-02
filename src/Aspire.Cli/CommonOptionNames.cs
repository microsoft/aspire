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
    public const string Info = "--info";
    public const string NoLogo = "--nologo";
    public const string Banner = "--banner";
    public const string Debug = "--debug";
    public const string DebugShort = "-d";
    public const string NonInteractive = "--non-interactive";
    public const string WaitForDebugger = "--wait-for-debugger";
    public const string CliWaitForDebugger = "--cli-wait-for-debugger";
    public const string StartDebugSession = "--start-debug-session";

    /// <summary>
    /// Returns <c>true</c> when <paramref name="args"/> represents an informational invocation
    /// (an invocation whose primary effect is to print diagnostic / version / help / install
    /// information rather than run a user command) and which should therefore opt out of
    /// telemetry, suppress the first-run banner, and skip consuming the one-shot first-run
    /// sentinel.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three positional rules, in priority order:
    /// </para>
    /// <list type="number">
    ///   <item>
    ///     <description>
    ///     A POSIX <c>--</c> end-of-options marker terminates the scan. Tokens after
    ///     <c>--</c> are positional / forwarded args (e.g. <c>aspire run -- --info</c>
    ///     forwards <c>--info</c> to the AppHost) and never bind to a CLI option, so
    ///     <c>--info</c> / <c>--help</c> / <c>--version</c> after <c>--</c> are not
    ///     informational from the CLI's perspective.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <c>--version</c>, <c>--help</c>, <c>-h</c>, and <c>-?</c> are recursive in
    ///     System.CommandLine — they bind at any depth — so they really are
    ///     informational at any position before <c>--</c> (e.g. <c>aspire run --help</c>
    ///     is a help invocation, not a <c>run</c> invocation).
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     <c>--info</c> is intentionally bound as a root-only non-recursive option on
    ///     <c>RootCommand</c>. <c>aspire --info</c> fires the info action; <c>aspire run
    ///     --info</c> runs the <c>run</c> subcommand with an unmatched <c>--info</c>
    ///     token. The scan mirrors that binding: <c>--info</c> is treated as
    ///     informational only when it appears <em>before</em> the first subcommand token
    ///     (and before <c>--</c>).
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>
    /// The "first non-option token = subcommand" heuristic has to handle one subtlety:
    /// root-level options that <em>take a value</em> (<c>--log-level Debug</c>,
    /// <c>--format json</c>, <c>--capture-profile-output path</c>, ...) put their value
    /// in the next token, which doesn't start with <c>-</c>. Without special handling,
    /// the scan would mistake the value for a subcommand and miss a trailing
    /// <c>--info</c>. <see cref="s_rootOptionsTakingAValue"/> lists the affected option
    /// names so the scan skips their value token. The <c>=</c>-form
    /// (<c>--log-level=Debug</c>) is a single token and needs no special handling.
    /// </para>
    /// </remarks>
    public static bool IsInformationalInvocation(string[]? args)
    {
        if (args is null)
        {
            return false;
        }

        var sawSubcommand = false;
        var skipNext = false;
        foreach (var arg in args)
        {
            if (skipNext)
            {
                // Previous token was a root value-taking option (e.g. --log-level);
                // this token is its value, not a subcommand.
                skipNext = false;
                continue;
            }

            // POSIX end-of-options marker. Anything after this is forwarded /
            // positional and cannot trigger a CLI informational action — including
            // a literal `--info` or `--help` (rule 1).
            if (arg == "--")
            {
                return false;
            }

            // Rule 3: --info is root-only / non-recursive, so it only counts as
            // informational before any subcommand boundary.
            if (!sawSubcommand && arg == Info)
            {
                return true;
            }

            // Rule 2: --version / --help / -h / -? are recursive and remain
            // informational at any position before --.
            if (arg is Version or Help or HelpShort or HelpAlt)
            {
                return true;
            }

            if (arg.Length > 0 && arg[0] == '-')
            {
                // Root value-taking option in space-separated form: skip the
                // value token so e.g. `aspire --log-level Debug --info` doesn't
                // treat "Debug" as the subcommand boundary.
                if (!sawSubcommand && s_rootOptionsTakingAValue.Contains(arg))
                {
                    skipNext = true;
                }
                continue;
            }

            // First bare token (and not consumed as an option's value) is the
            // subcommand. After this point, root-only `--info` no longer applies.
            sawSubcommand = true;
        }

        return false;
    }

    /// <summary>
    /// Root-level options that take a value in space-separated form. Used by
    /// <see cref="IsInformationalInvocation"/> to skip the value token so a value
    /// that happens not to start with <c>-</c> (e.g. <c>Debug</c>, <c>json</c>) is
    /// not mistaken for a subcommand boundary. Keep in sync with the non-bool
    /// options declared on <see cref="Commands.RootCommand"/>: bool options
    /// (<c>--debug</c>, <c>--non-interactive</c>, <c>--nologo</c>, <c>--banner</c>,
    /// <c>--wait-for-debugger</c>, <c>--cli-wait-for-debugger</c>,
    /// <c>--start-debug-session</c>, <c>--capture-profile</c>, <c>--info</c>,
    /// <c>--self</c>) don't take a separate value token and don't appear here.
    /// </summary>
    private static readonly HashSet<string> s_rootOptionsTakingAValue = new(StringComparer.Ordinal)
    {
        "--log-level",
        "-l",
        "--format",
        "--capture-profile-output",
        "--capture-profile-delay",
    };
}
