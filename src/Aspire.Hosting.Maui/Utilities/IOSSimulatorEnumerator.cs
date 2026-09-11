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
            var simctlJson = GetSimctlJsonDocument(json);
            if (simctlJson is null)
            {
                throw new DistributedApplicationException(
                    "Unable to parse xcrun simctl list devices available -j output. " +
                    "The command completed successfully but did not return a parseable JSON object with a 'devices' property. " +
                    "Open Xcode and verify the simulator runtimes are installed, or run xcrun simctl list devices available -j to inspect the output.");
            }

            using var doc = JsonDocument.Parse(simctlJson);

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

    private static string? GetSimctlJsonDocument(string output)
    {
        // `xcrun simctl list devices available -j` should emit pure JSON, but some toolchain
        // setups prepend or append diagnostics, including JSON-like payloads:
        //   diagnostic {not JSON}
        //   { "devices": { "com.apple.CoreSimulator.SimRuntime.iOS-18-2": [...] } }
        //   trailing diagnostic {"ignored": true}
        // Use the first balanced, parseable JSON object that has the simctl "devices" shape.
        foreach (var jsonObject in EnumerateJsonObjects(output))
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonObject);
                if (doc.RootElement.TryGetProperty("devices", out _))
                {
                    return jsonObject;
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateJsonObjects(string output)
    {
        for (var start = output.IndexOf('{', StringComparison.Ordinal); start >= 0; start = output.IndexOf('{', start + 1))
        {
            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = start; i < output.Length; i++)
            {
                var ch = output[i];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (ch == '\\')
                    {
                        escaped = true;
                    }
                    else if (ch == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                }
                else if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        yield return output[start..(i + 1)];
                        start = i;
                        break;
                    }
                }
            }
        }
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
