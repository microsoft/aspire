// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Infrastructure.Tests;

/// <summary>
/// Minimal synchronous <c>git</c> runner for test arrange-phase fixture setup (init a repo, commit
/// files, read <c>ls-files</c>, etc.). This is deliberately a small helper rather than the async
/// <c>NodeCommand</c>/<c>PowerShellCommand</c> abstractions: those run the script under test with
/// output streaming, timeouts, and diagnostics; here git is just scaffolding, so a blocking call that
/// returns stdout (and throws on non-zero exit) is all that's needed.
/// </summary>
internal static class GitCli
{
    /// <summary>
    /// Runs <c>git</c> in <paramref name="workingDirectory"/> with <paramref name="args"/> and returns
    /// trimmed stdout. Throws <see cref="InvalidOperationException"/> on a non-zero exit (with stderr in
    /// the message) so a broken fixture fails loudly instead of producing a confusing downstream error.
    /// </summary>
    public static string Run(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Tests run in non-interactive shells; a globally configured core.pager would otherwise hang
        // read-style commands (log/diff/show) waiting for a 'q'. --no-pager is a no-op for the rest.
        psi.ArgumentList.Add("--no-pager");
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({process.ExitCode}): {stderr}");
        }

        return stdout.Trim();
    }
}
