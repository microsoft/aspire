// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Tray;

internal static class CliProcess
{
    public static ProcessStartInfo CreateStartInfo(string cliPath, params string[] arguments)
    {
        if (!Path.IsPathFullyQualified(cliPath))
        {
            throw new ArgumentException("An absolute Aspire CLI executable path is required.", nameof(cliPath));
        }

        var startInfo = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    public static async Task DrainAsync(TextReader reader, CancellationToken cancellationToken)
    {
        // Drain concurrently without retaining output: CLI diagnostics can include dashboard credentials.
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
        }
    }

    public static async Task TerminateOwnedChildAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                // Only a CLI subprocess we launched, never an AppHost or an arbitrary process tree.
                process.Kill();
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            // The child exited between HasExited and Kill.
        }
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
    }
}
