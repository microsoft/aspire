// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Builds the shell command strings used in agent hook configuration entries, with correct quoting
/// for paths that may contain spaces or apostrophes.
/// </summary>
internal static class HookCommandFormatter
{
    /// <summary>
    /// Quotes a path as a single bash argument. Bash single quotes are literal, so an embedded
    /// apostrophe is closed, escaped, and reopened: <c>'</c> becomes <c>'\''</c>.
    /// Example: <c>/home/o'brien/x.sh</c> → <c>'/home/o'\''brien/x.sh'</c>.
    /// </summary>
    public static string QuoteForBash(string path)
        => "'" + path.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Quotes a path as a single PowerShell literal string. PowerShell single quotes are literal and
    /// an embedded apostrophe is doubled: <c>'</c> becomes <c>''</c>.
    /// Example: <c>C:\Users\o'brien\x.ps1</c> → <c>'C:\Users\o''brien\x.ps1'</c>.
    /// </summary>
    public static string QuoteForPowerShell(string path)
        => "'" + path.Replace("'", "''") + "'";

    /// <summary>
    /// Builds the Unix command that runs the shell hook script via bash. Running through <c>bash</c>
    /// (rather than executing the path directly) avoids depending on the executable bit surviving.
    /// </summary>
    public static string BuildBashCommand(string shellScriptPath)
        => "bash " + QuoteForBash(shellScriptPath);

    /// <summary>
    /// Builds the Windows command that runs the PowerShell hook script. A child <c>powershell</c> is
    /// spawned with an explicit bypass policy so the script runs regardless of the ambient execution
    /// policy, and <c>-NoProfile</c> avoids profile side effects and startup cost.
    /// </summary>
    public static string BuildPowerShellCommand(string powerShellScriptPath)
        => "powershell -NoProfile -ExecutionPolicy Bypass -File " + QuoteForPowerShell(powerShellScriptPath);
}
