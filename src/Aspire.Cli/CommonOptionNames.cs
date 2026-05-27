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
    /// <c>--version</c>, <c>--help</c>, <c>-h</c>, and <c>-?</c> are recursive in
    /// System.CommandLine — they bind at any depth — so they really are informational at any
    /// position (e.g. <c>aspire run --help</c> is genuinely a help invocation, not a
    /// <c>run</c> invocation).
    /// </para>
    /// <para>
    /// <c>--info</c>, by contrast, is intentionally wired as a root-only non-recursive option
    /// on <c>RootCommand</c>. <c>aspire --info</c> fires the info action; <c>aspire run --info</c>
    /// runs the <c>run</c> subcommand with an unmatched <c>--info</c> token. This helper mirrors
    /// that binding: <c>--info</c> is treated as informational only when it appears before any
    /// subcommand token in <paramref name="args"/>. Otherwise <c>aspire run --info</c> would
    /// silently suppress telemetry and skip the first-run sentinel for a real <c>run</c>
    /// invocation.
    /// </para>
    /// <para>
    /// The first non-option token (one that does not start with <c>-</c>) is treated as the
    /// subcommand boundary. A pathological case like <c>aspire --format json --info</c> — where
    /// an option value happens to look like a subcommand — would short-circuit the <c>--info</c>
    /// check; in practice the root-level <c>--format</c> validator rejects that parse anyway
    /// when it's not paired with <c>--info</c>.
    /// </para>
    /// </remarks>
    public static bool IsInformationalInvocation(string[]? args)
    {
        if (args is null)
        {
            return false;
        }

        var sawSubcommand = false;
        foreach (var arg in args)
        {
            if (!sawSubcommand && arg == Info)
            {
                return true;
            }

            if (arg is Version or Help or HelpShort or HelpAlt)
            {
                return true;
            }

            if (!sawSubcommand && arg.Length > 0 && arg[0] != '-')
            {
                sawSubcommand = true;
            }
        }

        return false;
    }
}
