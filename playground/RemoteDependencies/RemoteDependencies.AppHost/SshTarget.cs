// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace RemoteDependencies;

public sealed record SshTarget
{
    public SshTarget(string host, int port, string user, string identityFile, string knownHostsFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        if (host.StartsWith('-') || user.StartsWith('-') || host.Any(char.IsWhiteSpace) || user.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("SSH host and user must be literal names, not options.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        Host = host;
        Port = port;
        User = user;
        IdentityFile = Path.GetFullPath(identityFile);
        KnownHostsFile = Path.GetFullPath(knownHostsFile);
        if (!File.Exists(IdentityFile) || !File.Exists(KnownHostsFile))
        {
            throw new FileNotFoundException("SSH identity and pinned known_hosts files must exist.");
        }
    }

    public string Host { get; }
    public int Port { get; }
    public string User { get; }
    public string IdentityFile { get; }
    public string KnownHostsFile { get; }

    public Process Start(string command, string? forward = null)
    {
        var info = new ProcessStartInfo("ssh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Ignore user/global SSH configuration: no inherited proxies, forwards or control sockets.
        string[] arguments = ["-F", "/dev/null", "-T", "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=yes", "-o", KnownHostsOption(KnownHostsFile),
            "-o", "GlobalKnownHostsFile=/dev/null", "-o", "IdentitiesOnly=yes",
            "-o", "ConnectTimeout=5", "-o", "ServerAliveInterval=2",
            "-o", "ServerAliveCountMax=2", "-o", "ExitOnForwardFailure=yes",
            "-i", IdentityFile, "-p", Port.ToString(CultureInfo.InvariantCulture), "-l", User];
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        if (forward is not null)
        {
            info.ArgumentList.Add("-L");
            info.ArgumentList.Add(forward);
        }
        info.ArgumentList.Add(Host);
        info.ArgumentList.Add(command);
        return Process.Start(info) ?? throw new InvalidOperationException("Unable to start OpenSSH.");
    }

    public async Task<string> RunAsync(string command, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Start(command);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0)
            {
                throw new SshCommandException(process.ExitCode, error.Trim());
            }

            return output.Trim();
        }
        finally
        {
            Stop(process);
            // Observe stream reads even on timeout/cancellation.
            await ((Task)Task.WhenAll(stdout, stderr)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    // OpenSSH parses this option as a filename list even though it is one process argument.
    internal static string KnownHostsOption(string path) =>
        "UserKnownHostsFile=\"" + path.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal) + "\"";

    public static void Stop(Process process)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    public static async Task StreamAsync(StreamReader reader, ILogger logger, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            logger.LogInformation("{RemoteOutput}", line);
        }
    }
}

internal sealed class SshCommandException(int exitCode, string error)
    : IOException($"Remote command failed ({exitCode}): {error}")
{
    public int ExitCode { get; } = exitCode;
}
