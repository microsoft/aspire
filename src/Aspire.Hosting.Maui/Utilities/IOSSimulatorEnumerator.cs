// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Enumerates available iOS simulators using Xcode's simctl tool.
/// </summary>
internal static class IOSSimulatorEnumerator
{
    internal static async Task<IReadOnlyList<EmulatorOption>> GetAvailableSimulatorsAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new DistributedApplicationException(
                "iOS Simulator selection requires macOS with Xcode installed. " +
                "Run this resource on macOS or provide an explicit simulator UDID only in environments where iOS Simulator is available.");
        }

        var startInfo = new ProcessStartInfo("xcrun")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("simctl");
        startInfo.ArgumentList.Add("list");
        startInfo.ArgumentList.Add("devices");
        startInfo.ArgumentList.Add("available");
        startInfo.ArgumentList.Add("-j");

        using var process = StartProcess(startInfo);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var details = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
                throw new DistributedApplicationException(
                    $"xcrun simctl list devices available -j exited with code {process.ExitCode}. " +
                    "Install Xcode and at least one iOS Simulator runtime, then start the Aspire resource again." +
                    FormatDetails(details));
            }

            return ParseSimctlOutput(stdout, logger);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process, logger);
            throw new DistributedApplicationException(
                "xcrun simctl list devices available -j timed out after 15 seconds. " +
                "Open Xcode and verify the simulator runtimes are installed.");
        }
        catch
        {
            TryKillProcess(process, logger);
            throw;
        }
    }

    internal static IReadOnlyList<EmulatorOption> ParseSimctlOutput(string json, ILogger logger)
    {
        var results = new List<EmulatorOption>();

        try
        {
            // `xcrun simctl list devices available -j` should emit pure JSON, but some
            // toolchain setups prepend diagnostics. Keep parsing resilient by extracting
            // the first JSON object instead of treating surrounding noise as no devices.
            var start = json.IndexOf('{', StringComparison.Ordinal);
            var end = json.LastIndexOf('}');
            if (start < 0 || end < start)
            {
                return results;
            }

            using var doc = JsonDocument.Parse(json[start..(end + 1)]);

            if (!doc.RootElement.TryGetProperty("devices", out var devicesElement))
            {
                return results;
            }

            foreach (var runtimeProp in devicesElement.EnumerateObject())
            {
                if (!runtimeProp.Name.Contains(".iOS-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var runtimeDisplayName = FormatRuntimeName(runtimeProp.Name);

                foreach (var device in runtimeProp.Value.EnumerateArray())
                {
                    var isAvailable = !device.TryGetProperty("isAvailable", out var isAvailableElement) ||
                        isAvailableElement.ValueKind != JsonValueKind.False;
                    var udid = device.TryGetProperty("udid", out var udidElement) ? udidElement.GetString() : null;
                    var name = device.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;

                    if (isAvailable && !string.IsNullOrEmpty(udid) && !string.IsNullOrEmpty(name))
                    {
                        results.Add(new EmulatorOption(udid, $"{name} - {runtimeDisplayName}"));
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse simctl JSON output.");
        }

        return results;
    }

    internal static string FormatRuntimeName(string runtimeId)
    {
        var lastDot = runtimeId.LastIndexOf('.');
        if (lastDot < 0 || lastDot >= runtimeId.Length - 1)
        {
            return runtimeId;
        }

        var suffix = runtimeId[(lastDot + 1)..];
        var parts = suffix.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return $"{parts[0]} {string.Join('.', parts[1..])}";
        }

        return suffix;
    }

    private static Process StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new DistributedApplicationException("Unable to start xcrun. Install Xcode and ensure xcrun is available on PATH.");
        }
        catch (Win32Exception ex)
        {
            throw new DistributedApplicationException("Unable to start xcrun. Install Xcode and ensure xcrun is available on PATH.", ex);
        }
    }

    private static void TryKillProcess(Process process, ILogger logger)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to kill simctl process.");
        }
    }

    private static string FormatDetails(string details)
    {
        return string.IsNullOrWhiteSpace(details) ? string.Empty : $" Details: {details}";
    }
}
