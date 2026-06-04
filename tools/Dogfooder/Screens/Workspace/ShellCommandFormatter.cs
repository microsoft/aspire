// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Aspire.Dogfooder.Services;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Formats <see cref="SessionEnvironmentPlan"/> entries into the shell-native
/// command syntax for the launched shell, plus resolves which shell binary
/// to spawn. Kept separate from the window-content builder so the typing
/// logic can be unit-tested without a PTY.
/// </summary>
internal static class ShellCommandFormatter
{
    public enum ShellKind { Bash, Zsh, Pwsh, Cmd }

    public sealed record ShellSpec(string FileName, IReadOnlyList<string> Arguments, ShellKind Kind);

    public static ShellSpec ResolveDefault()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var pwsh = Environment.GetEnvironmentVariable("DOGFOODER_SHELL") ?? "pwsh.exe";
            return new ShellSpec(pwsh, Array.Empty<string>(), DetectKind(pwsh));
        }

        var shell = Environment.GetEnvironmentVariable("DOGFOODER_SHELL")
            ?? Environment.GetEnvironmentVariable("SHELL")
            ?? "/bin/bash";

        // --norc on bash keeps the launched shell from picking up the user's
        // interactive customisations; the goal is a deterministic identity
        // override session, not a personalised shell. Other shells get no
        // extra args.
        var kind = DetectKind(shell);
        var args = kind == ShellKind.Bash ? new[] { "--norc" } : Array.Empty<string>();
        return new ShellSpec(shell, args, kind);
    }

    public static IEnumerable<string> Format(ShellKind shell, SessionEnvironmentPlan plan)
    {
        switch (shell)
        {
            case ShellKind.Pwsh:
                yield return "Write-Host '# aspire-dogfooder: applying identity overrides' -ForegroundColor Cyan";
                if (plan.PathPrependDir is { Length: > 0 } pwshPath)
                {
                    yield return $"$env:PATH = '{EscapePwsh(pwshPath)}' + [IO.Path]::PathSeparator + $env:PATH";
                }
                foreach (var (k, v) in plan.IdentityOverrides)
                {
                    yield return $"$env:{k} = '{EscapePwsh(v)}'";
                }
                yield return "Write-Host '# done. dogfooding session ready.' -ForegroundColor Cyan";
                break;

            case ShellKind.Cmd:
                yield return "echo # aspire-dogfooder: applying identity overrides";
                if (plan.PathPrependDir is { Length: > 0 } cmdPath)
                {
                    yield return $"set PATH={cmdPath};%PATH%";
                }
                foreach (var (k, v) in plan.IdentityOverrides)
                {
                    yield return $"set {k}={v}";
                }
                yield return "echo # done. dogfooding session ready.";
                break;

            default:
                // bash / zsh / sh / other POSIX-flavoured shells.
                yield return "echo '# aspire-dogfooder: applying identity overrides'";
                if (plan.PathPrependDir is { Length: > 0 } posixPath)
                {
                    yield return $"export PATH=\"{EscapePosix(posixPath)}:$PATH\"";
                }
                foreach (var (k, v) in plan.IdentityOverrides)
                {
                    yield return $"export {k}=\"{EscapePosix(v)}\"";
                }
                yield return "echo '# done. dogfooding session ready.'";
                break;
        }
    }

    // Within a POSIX double-quoted string we only have to escape: $ ` " \
    // Backslash MUST be replaced first to avoid double-escaping the others.
    private static string EscapePosix(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    // Within a PowerShell single-quoted string, single quotes are the only
    // escape concern (doubled to literalise). No $ expansion happens inside ''.
    private static string EscapePwsh(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static ShellKind DetectKind(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        return name switch
        {
            "bash" or "sh" => ShellKind.Bash,
            "zsh" => ShellKind.Zsh,
            "pwsh" or "powershell" => ShellKind.Pwsh,
            "cmd" => ShellKind.Cmd,
            _ => ShellKind.Bash, // most plausible default on Unix-likes.
        };
    }
}
