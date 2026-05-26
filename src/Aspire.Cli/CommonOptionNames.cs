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
    /// Options that represent informational commands (e.g. --version, --help, --info) which
    /// should opt out of telemetry and suppress the first-run experience. `--info` is included
    /// because its text form does not consume a `--format json` token (handled separately by
    /// <c>HasMachineReadableOutputFormat</c>) but must not consume the one-shot first-run sentinel
    /// or start a tracked telemetry activity.
    /// </summary>
    public static readonly string[] InformationalOptionNames = [Version, Help, HelpShort, HelpAlt, Info];
}
