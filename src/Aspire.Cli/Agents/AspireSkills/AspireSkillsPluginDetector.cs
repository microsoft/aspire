// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Detects whether an agent-native plugin manager already provides the Aspire skills.
/// </summary>
internal interface IAspireSkillsPluginDetector
{
    Task<AspireSkillsPluginDetectionResult> DetectInstalledAsync(CancellationToken cancellationToken);
}

internal sealed record AspireSkillsPluginDetectionResult(bool IsInstalled, string? HostName)
{
    public static AspireSkillsPluginDetectionResult NotInstalled { get; } = new(false, HostName: null);

    public static AspireSkillsPluginDetectionResult Installed(string hostName) => new(true, hostName);
}

internal sealed class AspireSkillsPluginDetector(ILogger<AspireSkillsPluginDetector> logger) : IAspireSkillsPluginDetector
{
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(10);

    private static readonly PluginProbe[] s_pluginProbes =
    [
        new("GitHub Copilot CLI", "copilot", ["plugin", "list"]),
        new("Claude Code", "claude", ["plugin", "list"])
    ];

    public async Task<AspireSkillsPluginDetectionResult> DetectInstalledAsync(CancellationToken cancellationToken)
    {
        foreach (var probe in s_pluginProbes)
        {
            var executablePath = PathLookupHelper.FindFullPathFromPath(probe.Command);
            if (executablePath is null)
            {
                logger.LogDebug("{HostName} plugin manager was not found on PATH.", probe.HostName);
                continue;
            }

            if (await IsAspireSkillsPluginInstalledAsync(probe, executablePath, cancellationToken).ConfigureAwait(false))
            {
                return AspireSkillsPluginDetectionResult.Installed(probe.HostName);
            }
        }

        return AspireSkillsPluginDetectionResult.NotInstalled;
    }

    private async Task<bool> IsAspireSkillsPluginInstalledAsync(PluginProbe probe, string executablePath, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_probeTimeout);

        var startInfo = new ProcessStartInfo(executablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in probe.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var started = false;

        try
        {
            process.Start();
            started = true;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                logger.LogDebug("{HostName} plugin list failed with exit code {ExitCode}: {Error}", probe.HostName, process.ExitCode, stderr.Trim());
                return false;
            }

            var output = $"{stdout}\n{stderr}";
            return output.Contains("microsoft/aspire-skills", StringComparison.OrdinalIgnoreCase) ||
                   output.Contains("aspire-skills", StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("{HostName} plugin list probe timed out after {Timeout}.", probe.HostName, s_probeTimeout);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or IOException)
        {
            logger.LogDebug(ex, "{HostName} plugin list probe failed.", probe.HostName);
            return false;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                TryKillProcess(process, probe.HostName);
            }
        }
    }

    private void TryKillProcess(Process process, string hostName)
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            logger.LogDebug(ex, "Failed to terminate timed-out {HostName} plugin list probe.", hostName);
        }
    }

    private sealed record PluginProbe(string HostName, string Command, string[] Arguments);
}
