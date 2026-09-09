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
    private static readonly TimeSpan s_serialWaitTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan s_bootWaitTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(2);

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

        var existingSerial = await GetReadyRunningEmulatorSerialForAvdAsync(
            avdName,
            token => WaitForPendingRunningEmulatorSerialForAvdAsync(
                avdName,
                probeToken => ProbeRunningEmulatorSerialForAvdAsync(adbPath, avdName, logger, probeToken),
                s_serialWaitTimeout,
                s_pollInterval,
                logger,
                token),
            (serial, token) => WaitForEmulatorBootAsync(adbPath, serial, logger, token),
            logger,
            cancellationToken).ConfigureAwait(false);
        if (existingSerial is not null)
        {
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
            var emulatorOutput = new ProcessOutputBuffer();
            _ = DrainProcessOutputAsync(emulatorProcess.StandardOutput, logger, LogLevel.Information, emulatorOutput, CancellationToken.None);
            _ = DrainProcessOutputAsync(emulatorProcess.StandardError, logger, LogLevel.Warning, emulatorOutput, CancellationToken.None);

            var serial = await WaitForEmulatorSerialAsync(
                avdName,
                token => GetRunningEmulatorSerialForAvdAsync(adbPath, avdName, logger, token),
                () => emulatorProcess.HasExited
                    ? FormatEmulatorExitMessage(avdName, emulatorProcess.ExitCode, emulatorOutput.GetOutput())
                    : null,
                s_serialWaitTimeout,
                s_pollInterval,
                cancellationToken).ConfigureAwait(false);
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

    internal static async Task<string?> GetReadyRunningEmulatorSerialForAvdAsync(
        string avdName,
        Func<CancellationToken, Task<string?>> getRunningEmulatorSerialAsync,
        Func<string, CancellationToken, Task> waitForEmulatorBootAsync,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var existingSerial = await getRunningEmulatorSerialAsync(cancellationToken).ConfigureAwait(false);
        if (existingSerial is null)
        {
            return null;
        }

        logger.LogInformation("Android emulator '{AvdName}' is already running as {Serial}.", avdName, existingSerial);
        await waitForEmulatorBootAsync(existingSerial, cancellationToken).ConfigureAwait(false);

        return existingSerial;
    }

    internal static async Task<string> WaitForEmulatorSerialAsync(
        string avdName,
        Func<CancellationToken, Task<string?>> getRunningEmulatorSerialAsync,
        Func<string?> getEmulatorProcessExitMessage,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        string? serial = null;
        await WaitForAndroidToolingAsync(
            async token =>
            {
                var processExitMessage = getEmulatorProcessExitMessage();
                if (processExitMessage is not null)
                {
                    throw new DistributedApplicationException(processExitMessage);
                }

                serial = await getRunningEmulatorSerialAsync(token).ConfigureAwait(false);
                return serial is not null;
            },
            timeout,
            pollInterval,
            $"Timed out waiting for Android emulator '{avdName}' to appear in adb. Try starting the emulator manually from Android Studio Device Manager and then start the Aspire resource again.",
            cancellationToken).ConfigureAwait(false);

        Debug.Assert(serial is not null, "The wait helper only completes after the serial is resolved.");
        return serial;
    }

    private static Task WaitForEmulatorBootAsync(string adbPath, string serial, ILogger logger, CancellationToken cancellationToken)
    {
        return WaitForEmulatorBootAsync(
            serial,
            async token =>
            {
                var result = await RunToolAsync(
                    adbPath,
                    ["-s", serial, "shell", "getprop", "sys.boot_completed"],
                    s_adbTimeout,
                    "adb shell getprop sys.boot_completed",
                    $"Unable to query Android emulator '{serial}' boot state.",
                    logger,
                    token).ConfigureAwait(false);

                var output = result.StandardOutput.Trim();
                return string.Equals(output, "1", StringComparison.Ordinal);
            },
            s_bootWaitTimeout,
            s_pollInterval,
            logger,
            cancellationToken);
    }

    internal static async Task WaitForEmulatorBootAsync(
        string serial,
        Func<CancellationToken, Task<bool>> isBootCompletedAsync,
        TimeSpan timeout,
        TimeSpan pollInterval,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Waiting for Android emulator {Serial} to finish booting.", serial);

        await WaitForAndroidToolingAsync(
            async token =>
            {
                try
                {
                    return await isBootCompletedAsync(token).ConfigureAwait(false);
                }
                catch (DistributedApplicationException ex)
                {
                    logger.LogDebug(ex, "Android emulator {Serial} boot state is not available yet.", serial);
                    return false;
                }
            },
            timeout,
            pollInterval,
            $"Timed out waiting for Android emulator '{serial}' to finish booting. Try starting the emulator manually and wait for the home screen before starting the Aspire resource again.",
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WaitForAndroidToolingAsync(
        Func<CancellationToken, Task<bool>> probeAsync,
        TimeSpan timeout,
        TimeSpan pollInterval,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (!await probeAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                await Task.Delay(pollInterval, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DistributedApplicationException(timeoutMessage);
        }
    }

    internal static async Task<string?> WaitForPendingRunningEmulatorSerialForAvdAsync(
        string avdName,
        Func<CancellationToken, Task<RunningEmulatorSerialProbeResult>> probeRunningEmulatorSerialAsync,
        TimeSpan timeout,
        TimeSpan pollInterval,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                var result = await probeRunningEmulatorSerialAsync(timeoutCts.Token).ConfigureAwait(false);
                switch (result.State)
                {
                    case RunningEmulatorSerialProbeState.Found:
                        Debug.Assert(result.Serial is not null);
                        return result.Serial;

                    case RunningEmulatorSerialProbeState.NotFound:
                        return null;

                    case RunningEmulatorSerialProbeState.Pending:
                        logger.LogDebug("Waiting for a starting Android emulator to report the AVD name for '{AvdName}'.", avdName);
                        break;
                }

                await Task.Delay(pollInterval, timeoutCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DistributedApplicationException(
                $"Timed out waiting for Android emulator '{avdName}' to report its AVD name. Try waiting for the emulator to finish starting from Android Studio Device Manager and then start the Aspire resource again.");
        }
    }

    private static async Task<string?> GetRunningEmulatorSerialForAvdAsync(string adbPath, string avdName, ILogger logger, CancellationToken cancellationToken)
    {
        var result = await ProbeRunningEmulatorSerialForAvdAsync(adbPath, avdName, logger, cancellationToken).ConfigureAwait(false);
        return result.State == RunningEmulatorSerialProbeState.Found ? result.Serial : null;
    }

    private static async Task<RunningEmulatorSerialProbeResult> ProbeRunningEmulatorSerialForAvdAsync(string adbPath, string avdName, ILogger logger, CancellationToken cancellationToken)
    {
        var devices = await RunToolAsync(
            adbPath,
            ["devices"],
            s_adbTimeout,
            "adb devices",
            "Unable to list Android devices. Install Android SDK platform-tools and ensure adb is on PATH or ANDROID_HOME or ANDROID_SDK_ROOT is set.",
            logger,
            cancellationToken).ConfigureAwait(false);

        var hasPendingEmulator = false;
        foreach (var serial in ParseRunningEmulatorSerials(devices.StandardOutput))
        {
            var runningAvdName = await TryGetRunningAvdNameAsync(adbPath, serial, logger, cancellationToken).ConfigureAwait(false);
            if (runningAvdName.IsPending)
            {
                hasPendingEmulator = true;
                continue;
            }

            if (string.Equals(runningAvdName.AvdName, avdName, StringComparison.Ordinal))
            {
                return RunningEmulatorSerialProbeResult.Found(serial);
            }
        }

        return hasPendingEmulator ? RunningEmulatorSerialProbeResult.Pending() : RunningEmulatorSerialProbeResult.NotFound();
    }

    internal static IReadOnlyList<string> ParseRunningEmulatorSerials(string output)
    {
        var serials = new List<string>();

        // `adb devices` reports an emulator as "offline" while it is still starting:
        //   List of devices attached
        //   emulator-5554	device
        //   emulator-5556	offline
        // Include both states so an already-starting AVD is not treated as absent and
        // launched a second time.
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts is [var serial, ("device" or "offline"), ..] && serial.StartsWith("emulator-", StringComparison.Ordinal))
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

    private static async Task<AvdNameProbeResult> TryGetRunningAvdNameAsync(string adbPath, string serial, ILogger logger, CancellationToken cancellationToken)
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

            return AvdNameProbeResult.Resolved(ParseAvdNameForRunningEmulator(result.StandardOutput));
        }
        catch (DistributedApplicationException ex)
        {
            logger.LogDebug(ex, "Unable to determine AVD name for emulator {Serial}; treating it as a starting emulator.", serial);
            return AvdNameProbeResult.Pending();
        }
    }

    private static bool IsAvdName(string line)
    {
        var pipeIndex = line.IndexOf('|');
        if (pipeIndex < 0)
        {
            return true;
        }

        var prefix = line[..pipeIndex].Trim();
        return !string.Equals(prefix, "INFO", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(prefix, "WARNING", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(prefix, "ERROR", StringComparison.OrdinalIgnoreCase);
    }

    internal static string FindAndroidToolPath(string executableName, string androidSdkRelativePath)
    {
        return FindAndroidToolPath(executableName, androidSdkRelativePath, GetAndroidSdkRoots());
    }

    private static string FindAndroidToolPath(string executableName, string androidSdkRelativePath, IEnumerable<string> androidSdkRoots)
    {
        var executable = OperatingSystem.IsWindows() ? $"{executableName}.exe" : executableName;
        foreach (var androidSdkRoot in androidSdkRoots)
        {
            if (string.IsNullOrWhiteSpace(androidSdkRoot))
            {
                continue;
            }

            var path = Path.Combine(androidSdkRoot, androidSdkRelativePath);
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

    private static IEnumerable<string> GetAndroidSdkRoots()
    {
        var androidHome = Environment.GetEnvironmentVariable("ANDROID_HOME");
        if (!string.IsNullOrWhiteSpace(androidHome))
        {
            yield return androidHome;
        }

        var androidSdkRoot = Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT");
        if (!string.IsNullOrWhiteSpace(androidSdkRoot) &&
            !string.Equals(androidSdkRoot, androidHome, StringComparison.Ordinal))
        {
            yield return androidSdkRoot;
        }
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

    private static async Task DrainProcessOutputAsync(StreamReader reader, ILogger logger, LogLevel logLevel, ProcessOutputBuffer? outputBuffer, CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                outputBuffer?.AddLine(line);
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

    private static string FormatEmulatorExitMessage(string avdName, int exitCode, string details)
    {
        return $"Android emulator '{avdName}' exited with code {exitCode} before it appeared in adb. " +
            "Verify the Android Virtual Device exists and can start from Android Studio Device Manager." +
            FormatDetails(details);
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

    internal readonly record struct RunningEmulatorSerialProbeResult(RunningEmulatorSerialProbeState State, string? Serial)
    {
        internal static RunningEmulatorSerialProbeResult Found(string serial)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(serial);
            return new(RunningEmulatorSerialProbeState.Found, serial);
        }

        internal static RunningEmulatorSerialProbeResult NotFound()
        {
            return new(RunningEmulatorSerialProbeState.NotFound, Serial: null);
        }

        internal static RunningEmulatorSerialProbeResult Pending()
        {
            return new(RunningEmulatorSerialProbeState.Pending, Serial: null);
        }
    }

    internal enum RunningEmulatorSerialProbeState
    {
        Found,
        NotFound,
        Pending
    }

    private readonly record struct AvdNameProbeResult(string? AvdName, bool IsPending)
    {
        public static AvdNameProbeResult Resolved(string? avdName)
        {
            return new(avdName, IsPending: false);
        }

        public static AvdNameProbeResult Pending()
        {
            return new(AvdName: null, IsPending: true);
        }
    }

    private sealed record ToolResult(string StandardOutput, string StandardError);

    private sealed class ProcessOutputBuffer
    {
        private const int MaxLines = 20;
        private readonly Queue<string> _lines = new();

        public void AddLine(string line)
        {
            lock (_lines)
            {
                if (_lines.Count == MaxLines)
                {
                    _lines.Dequeue();
                }

                _lines.Enqueue(line);
            }
        }

        public string GetOutput()
        {
            lock (_lines)
            {
                return string.Join(Environment.NewLine, _lines);
            }
        }
    }
}

internal sealed record EmulatorOption(string Id, string DisplayName);
