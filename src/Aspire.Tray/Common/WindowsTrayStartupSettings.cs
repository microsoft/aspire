// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace Aspire.Tray;

/// <summary>
/// Owns a per-user Run registration pointing at a stable GUI-only login bootstrap.
/// </summary>
internal sealed class WindowsTrayStartupSettings(
    TrayOptions options, bool nativeFrontend, string sourceExecutable, string bootstrapPath,
    ITrayStartupRegistrationStore store) : RegisteredTrayStartupSettings(store)
{
    private readonly TrayStartupBootstrap _bootstrap = new(sourceExecutable, bootstrapPath);

    protected override string? UnavailableReason
    {
        get
        {
            var reason = TrayStartupEntry.GetUnavailableReason(options, nativeFrontend, windows: true);
            if (reason is not null)
            {
                return reason;
            }
            if (bootstrapPath.Contains('%') || options.StartupCliPath!.Contains('%'))
            {
                return "Windows sign-in startup cannot use paths containing environment-variable delimiters.";
            }
            if (BuildRunCommand(bootstrapPath, options.StartupCliPath!).Length > 260)
            {
                return "The startup command exceeds the Windows Run key's 260-character limit.";
            }
            return _bootstrap.GetUnavailableReason();
        }
    }

    protected override string CreateRegistration() => BuildRunCommand(bootstrapPath, options.StartupCliPath!);
    protected override void PrepareRegistration() => _bootstrap.EnsureCreated();

    internal static string BuildRunCommand(string bootstrap, string cli)
    {
        var start = new ProcessStartInfo(bootstrap);
        foreach (var argument in new[] { "login-start", "--cli", cli })
        {
            start.ArgumentList.Add(argument);
        }
        return TrayLaunchCommand.BuildWindowsCommandLine(start);
    }

    protected override bool IsOwned(string registration)
    {
        var prefix = BuildRunCommand(bootstrapPath, "");
        prefix = prefix[..^2]; // Our canonical final argument is always quoted, including "".
        if (!registration.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }
        var tail = registration.AsSpan(prefix.Length);
        if (tail.Length < 2 || tail[0] != '"' || tail[^1] != '"')
        {
            return false;
        }
        var cli = new StringBuilder();
        // Decode only the single canonical quoted argument emitted above. Windows paths
        // cannot contain quotes, but the canonical round-trip also rejects extra commands.
        for (var i = 1; i < tail.Length - 1; i++)
        {
            if (tail[i] == '"')
            {
                return false;
            }
            if (tail[i] == '\\')
            {
                var start = i;
                while (i < tail.Length - 1 && tail[i] == '\\')
                {
                    i++;
                }
                var count = i - start;
                if (i == tail.Length - 1)
                {
                    if (count % 2 != 0)
                    {
                        return false;
                    }
                    cli.Append('\\', count / 2);
                    break;
                }
                cli.Append('\\', count);
            }
            if (tail[i] == '"')
            {
                return false;
            }
            cli.Append(tail[i]);
        }
        return Path.IsPathFullyQualified(cli.ToString())
            && string.Equals(registration, BuildRunCommand(bootstrapPath, cli.ToString()), StringComparison.Ordinal);
    }
}
