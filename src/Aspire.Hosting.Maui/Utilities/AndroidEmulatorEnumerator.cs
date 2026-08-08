// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Utilities;

/// <summary>
/// Enumerates and starts Android Virtual Devices for MAUI Android emulator resources.
/// </summary>
internal static class AndroidEmulatorEnumerator
{
    private static readonly TimeSpan s_listTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_adbTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<IReadOnlyList<EmulatorOption>> GetAvailableEmulatorsAsync(ILogger logger, CancellationToken cancellationToken)
    {
        var emulatorPath = FindAndroidToolPath("emulator", Path.Combine("emulator", "emulator"));
        var result = await RunToolAsync(
            emulatorPath,
            ["-list-avds"],
            s_listTimeout,
            "emulator -list-avds",
            "Unable to list Android emulators. Install the Android SDK emulator tools and set ANDROID_HOME or ANDROID_SDK_ROOT if they are not on PATH.",
            logger,
            cancellationToken).ConfigureAwait(false);

        return ParseAvdList(result.StandardOutput);
    }

    internal static IReadOnlyList<EmulatorOption> ParseAvdList(string output)
    {
        var results = new List<EmulatorOption>();

        // `emulator -list-avds` usually prints one AVD name per line, but recent emulator
        // builds can also write diagnostic lines such as:
        //   INFO    | Storing crashdata in: /tmp/android-user/emu-crash-35.1.20.db
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsAvdName(line))
            {
                results.Add(new EmulatorOption(line, line.Replace('_', ' ')));
            }
        }

        return results;
    }

    internal static async Task<string> EnsureEmulatorRunningAsync(string avdName, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(avdName);

        var adbPath = FindAndroidToolPath("adb", Path.Combine("platform-tools", "adb"));
        var emulatorPath = FindAndroidToolPath("emulator", Path.Combine("emulator", "emulator"));

        var existingSerial = await GetRunningEmulatorSerialForAvdAsync(adbPath, avdName, logger, cancellationToken).ConfigureAwait(false);
        if (existingSerial is not null)
        {
            logger.LogInformation("Android emulator '{AvdName}' is already running as {Serial}.", avdName, existingSerial);
            return existingSerial;
        }

        logger.LogInformation("Starting Android emulator '{AvdName}'.", avdName);

        var startInfo = new ProcessStartInfo(emulatorPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-avd");
        startInfo.ArgumentList.Add(avdName);
        startInfo.ArgumentList.Add("-no-snapshot-load");

        Process? emulatorProcess = null;
        try
        {
            emulatorProcess = StartProcess(startInfo, "Unable to start Android emulator. Install the Android SDK emulator tools and ensure the selected AVD exists.");

            // The emulator process stays alive for the lifetime of the virtual device. Drain both
            // redirected streams so a full pipe cannot block emulator startup.
            _ = DrainProcessOutputAsync(emulatorProcess.StandardOutput, logger, LogLevel.Information, CancellationToken.None);
            _ = DrainProcessOutputAsync(emulatorProcess.StandardError, logger, LogLevel.Warning, CancellationToken.None);

            var serial = await WaitForEmulatorSerialAsync(adbPath, avdName, logger, cancellationToken).ConfigureAwait(false);
            await WaitForEmulatorBootAsync(adbPath, serial, logger, cancellationToken).ConfigureAwait(false);

            logger.LogInformation("Android emulator '{AvdName}' is ready as {Serial}.", avdName, serial);
            return serial;
        }
        catch
        {
            TryKillProcess(emulatorProcess, logger);
            throw;
        }
    }

    private static async Task<string> WaitForEmulatorSerialAsync(string adbPath, string avdName, ILogger logger, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(90));

        while (true)
        {
            try
            {
                var serial = await GetRunningEmulatorSerialForAvdAsync(adbPath, avdName, logger, timeoutCts.Token).ConfigureAwait(false);
                if (serial is not null)
                {
                    return serial;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DistributedApplicationException(
                    $"Timed out waiting for Android emulator '{avdName}' to appear in adb. " +
                    "Try starting the emulator manually from Android Studio Device Manager and then start the Aspire resource again.");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeoutCts.Token).ConfigureAwait(false);
        }
    }

    private static async Task WaitForEmulatorBootAsync(string adbPath, string serial, ILogger logger, CancellationToken cancellationToken)
    {
        logger.LogInformation("Waiting for Android emulator {Serial} to finish booting.", serial);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(2));

        while (true)
        {
            string output;
            try
            {
                var result = await RunToolAsync(
                    adbPath,
                    ["-s", serial, "shell", "getprop", "sys.boot_completed"],
                    s_adbTimeout,
                    "adb shell getprop sys.boot_completed",
                    $"Unable to query Android emulator '{serial}' boot state.",
                    logger,
                    timeoutCts.Token).ConfigureAwait(false);
                output = result.StandardOutput.Trim();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DistributedApplicationException(
                    $"Timed out waiting for Android emulator '{serial}' to finish booting. " +
                    "Try starting the emulator manually and wait for the home screen before starting the Aspire resource again.");
            }

            if (string.Equals(output, "1", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeoutCts.Token).ConfigureAwait(false);
        }
    }

    private static async Task<string?> GetRunningEmulatorSerialForAvdAsync(string adbPath, string avdName, ILogger logger, CancellationToken cancellationToken)
    {
        var devices = await RunToolAsync(
            adbPath,
            ["devices"],
            s_adbTimeout,
            "adb devices",
            "Unable to list Android devices. Install Android SDK platform-tools and ensure adb is on PATH or ANDROID_HOME is set.",
            logger,
            cancellationToken).ConfigureAwait(false);

        foreach (var serial in ParseRunningEmulatorSerials(devices.StandardOutput))
        {
            var runningAvdName = await TryGetRunningAvdNameAsync(adbPath, serial, logger, cancellationToken).ConfigureAwait(false);
            if (string.Equals(runningAvdName, avdName, StringComparison.Ordinal))
            {
                return serial;
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> ParseRunningEmulatorSerials(string output)
    {
        var serials = new List<string>();

        // `adb devices` output:
        //   List of devices attached
        //   emulator-5554	device
        //   emulator-5556	offline
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is [var serial, "device", ..] && serial.StartsWith("emulator-", StringComparison.Ordinal))
            {
                serials.Add(serial);
            }
        }

        return serials;
    }

    internal static string? ParseAvdNameForRunningEmulator(string output)
    {
        // `adb -s emulator-5554 emu avd name` output:
        //   Pixel_5_API_35
        //   OK
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => !string.Equals(line, "OK", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> TryGetRunningAvdNameAsync(string adbPath, string serial, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            var result = await RunToolAsync(
                adbPath,
                ["-s", serial, "emu", "avd", "name"],
                s_adbTimeout,
                "adb emu avd name",
                $"Unable to query Android emulator '{serial}' AVD name.",
                logger,
                cancellationToken).ConfigureAwait(false);

            return ParseAvdNameForRunningEmulator(result.StandardOutput);
        }
        catch (DistributedApplicationException ex)
        {
            logger.LogDebug(ex, "Unable to determine AVD name for emulator {Serial}.", serial);
            return null;
        }
    }

    private static bool IsAvdName(string line)
    {
        if (line.Contains('|') ||
            line.StartsWith("INFO", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !line.Contains(' ');
    }

    private static string FindAndroidToolPath(string executableName, string androidSdkRelativePath)
    {
        var executable = OperatingSystem.IsWindows() ? $"{executableName}.exe" : executableName;
        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME")
            ?? Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");

        if (!string.IsNullOrEmpty(androidHome))
        {
            var path = Path.Combine(androidHome, androidSdkRelativePath);
            if (!Path.HasExtension(path))
            {
                path = OperatingSystem.IsWindows() ? $"{path}.exe" : path;
            }

            if (File.Exists(path))
            {
                return path;
            }
        }

        return executable;
    }

    private static async Task<ToolResult> RunToolAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string commandDisplay,
        string failureMessage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = StartProcess(startInfo, failureMessage);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

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
                    $"{commandDisplay} exited with code {process.ExitCode}. {failureMessage}{FormatDetails(details)}");
            }

            return new ToolResult(stdout, stderr);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process, logger);
            throw new DistributedApplicationException($"{commandDisplay} timed out after {timeout.TotalSeconds:N0} seconds. {failureMessage}");
        }
        catch
        {
            TryKillProcess(process, logger);
            throw;
        }
    }

    private static Process StartProcess(ProcessStartInfo startInfo, string failureMessage)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new DistributedApplicationException(failureMessage);
        }
        catch (Win32Exception ex)
        {
            throw new DistributedApplicationException(failureMessage, ex);
        }
    }

    private static async Task DrainProcessOutputAsync(StreamReader reader, ILogger logger, LogLevel logLevel, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                logger.Log(logLevel, "{Line}", line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Stopped draining Android emulator output.");
        }
    }

    private static string FormatDetails(string details)
    {
        return string.IsNullOrWhiteSpace(details) ? string.Empty : $" Details: {details}";
    }

    private static void TryKillProcess(Process? process, ILogger logger)
    {
        try
        {
            if (process is not null && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to kill Android emulator tooling process.");
        }
    }

    private sealed record ToolResult(string StandardOutput, string StandardError);
}

internal sealed record EmulatorOption(string Id, string DisplayName);
