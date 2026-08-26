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
    public const string HelpSlash = "/h";
    public const string HelpAltSlash = "/?";
    public const string NoLogo = "--nologo";
    public const string Banner = "--banner";
    public const string Debug = "--debug";
    public const string DebugShort = "-d";
    public const string NonInteractive = "--non-interactive";
    public const string WaitForDebugger = "--wait-for-debugger";
    public const string CliWaitForDebugger = "--cli-wait-for-debugger";
    public const string StartDebugSession = "--start-debug-session";

    private static readonly HashSet<string> s_rootOptionsWithValues =
    [
        "--log-level",
        "-l",
        "--capture-profile-output",
        "--capture-profile-delay",
        "--log-file"
    ];

    /// <summary>
    /// Determines whether the arguments request root informational output that should opt out of
    /// telemetry and suppress the first-run experience.
    /// </summary>
    internal static bool IsInformationalInvocation(string[]? args)
    {
        if (args is null)
        {
            return false;
        }

        var commandSeen = false;
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg == "--")
            {
                break;
            }

            if (arg is Help or HelpShort or HelpAlt or HelpSlash or HelpAltSlash)
            {
                return true;
            }

            if (!commandSeen && arg is Version or VersionShort)
            {
                return true;
            }

            if (!commandSeen && s_rootOptionsWithValues.Contains(arg))
            {
                index++;
                continue;
            }

            if (!commandSeen && !arg.StartsWith('-'))
            {
                commandSeen = true;
            }
        }

        return false;
    }
}
