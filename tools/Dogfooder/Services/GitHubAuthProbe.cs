// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// Probes the local <c>gh</c> CLI to verify the user is authenticated against
/// <c>github.com</c> and fetches a bearer token via <c>gh auth token</c>. We
/// rely on <c>gh</c> rather than asking the user for a PAT because (a) it's
/// already what the team uses elsewhere in dev flows and (b) it avoids us
/// having to store tokens on disk ourselves.
/// </summary>
internal interface IGitHubAuthProbe
{
    Task<GitHubAuthStatus> CheckAuthAsync(CancellationToken cancellationToken);
    Task<string?> GetTokenAsync(CancellationToken cancellationToken);
}

internal sealed record GitHubAuthStatus(bool IsAuthenticated, string Detail);

internal sealed class GitHubAuthProbe : IGitHubAuthProbe
{
    public async Task<GitHubAuthStatus> CheckAuthAsync(CancellationToken cancellationToken)
    {
        // `gh auth status` exits non-zero when not logged in. It emits its
        // human-facing output to stderr (yes, even on success), so capture
        // both streams and prefer stderr for the displayed detail because
        // that's where the "Logged in to github.com as <user>" line lives.
        var (exitCode, stdout, stderr) = await RunAsync("gh", ["auth", "status", "--hostname", "github.com"], cancellationToken)
            .ConfigureAwait(false);

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        detail = detail.Trim();

        return exitCode == 0
            ? new GitHubAuthStatus(true, detail.Length == 0 ? "Authenticated." : detail)
            : new GitHubAuthStatus(false, detail.Length == 0 ? "Not authenticated. Run `gh auth login`." : detail);
    }

    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunAsync("gh", ["auth", "token"], cancellationToken)
            .ConfigureAwait(false);

        if (exitCode != 0)
        {
            return null;
        }

        var token = stdout.Trim();
        return token.Length == 0 ? null : token;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            // Most common cause: gh not on PATH. Surface this through stderr
            // so the validation UI's remediation hint is meaningful.
            return (-1, "", $"Failed to launch `{fileName}`: {ex.Message}");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
