// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.DotNet;

internal sealed class DotNetCliExecutionFactory(
    ILogger<DotNetCliExecutionFactory> logger,
    IConfiguration configuration,
    CliExecutionContext executionContext) : IDotNetCliExecutionFactory
{
    internal static int GetCurrentProcessId() => Environment.ProcessId;

    internal static long GetCurrentProcessStartTimeUnixSeconds()
    {
        var startTime = Process.GetCurrentProcess().StartTime;
        return ((DateTimeOffset)startTime).ToUnixTimeSeconds();
    }

    public IDotNetCliExecution CreateExecution(string[] args, IDictionary<string, string>? env, DirectoryInfo workingDirectory, DotNetCliRunnerInvocationOptions options)
    {
        var suppressLogging = options.SuppressLogging;

        if (!suppressLogging)
        {
            logger.LogDebug("Running {FullName} with args: {Args}", workingDirectory.FullName, string.Join(" ", args));

            if (env is not null)
            {
                foreach (var envKvp in env)
                {
                    logger.LogDebug("Running {FullName} with env: {EnvKey}={EnvValue}", workingDirectory.FullName, envKvp.Key, envKvp.Value);
                }
            }
        }

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory.FullName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (env is not null)
        {
            foreach (var envKvp in env)
            {
                startInfo.EnvironmentVariables[envKvp.Key] = envKvp.Value;
            }
        }

        foreach (var a in args)
        {
            startInfo.ArgumentList.Add(a);
        }

        // The AppHost uses this environment variable to signal to the CliOrphanDetector which process
        // it should monitor in order to know when to stop the CLI. As long as the process still exists
        // the orphan detector will allow the CLI to keep running. If the environment variable does
        // not exist the orphan detector will exit.
        startInfo.EnvironmentVariables[KnownConfigNames.CliProcessId] = GetCurrentProcessId().ToString(CultureInfo.InvariantCulture);

        // Set the CLI process start time for robust orphan detection to prevent PID reuse issues.
        // The AppHost will verify both PID and start time to ensure it's monitoring the correct process.
        startInfo.EnvironmentVariables[KnownConfigNames.CliProcessStarted] = GetCurrentProcessStartTimeUnixSeconds().ToString(CultureInfo.InvariantCulture);

        // Always set MSBUILDTERMINALLOGGER=false for all dotnet command executions to ensure consistent terminal logger behavior
        startInfo.EnvironmentVariables[KnownConfigNames.MsBuildTerminalLogger] = "false";

        // Suppress the .NET welcome message that appears on first run
        startInfo.EnvironmentVariables["DOTNET_NOLOGO"] = "1";

        // Configure DOTNET_ROOT to point to the private SDK installation if it exists
        ConfigurePrivateSdkEnvironment(startInfo);

        // Set debug session info if available
        var debugSessionInfo = configuration[KnownConfigNames.DebugSessionInfo];
        if (!string.IsNullOrEmpty(debugSessionInfo))
        {
            startInfo.EnvironmentVariables[KnownConfigNames.DebugSessionInfo] = debugSessionInfo;
        }

        var process = new Process { StartInfo = startInfo };
        return new DotNetCliExecution(process, logger, options);
    }

    /// <summary>
    /// Configures environment variables to use the private SDK installation if it exists.
    /// </summary>
    /// <param name="startInfo">The process start info to configure.</param>
    private void ConfigurePrivateSdkEnvironment(ProcessStartInfo startInfo)
    {
        // Get the effective minimum SDK version to determine which private SDK to use
        var sdkVersion = DotNetSdkInstaller.GetEffectiveMinimumSdkVersion(configuration);
        var sdksDirectory = executionContext.SdksDirectory.FullName;
        var sdkInstallPath = Path.Combine(sdksDirectory, "dotnet", sdkVersion);
        var dotnetExecutablePath = Path.Combine(
            sdkInstallPath,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet"
        );

        // Check if the private SDK exists
        if (Directory.Exists(sdkInstallPath))
        {
            // Set the executable path to be the private SDK.
            startInfo.FileName = dotnetExecutablePath;

            // Set DOTNET_ROOT to point to the private SDK installation
            startInfo.EnvironmentVariables["DOTNET_ROOT"] = sdkInstallPath;

            // Also set DOTNET_MULTILEVEL_LOOKUP to 0 to prevent fallback to system SDKs
            startInfo.EnvironmentVariables["DOTNET_MULTILEVEL_LOOKUP"] = "0";

            // Prepend the private SDK path to PATH so the dotnet executable from the private installation is found first
            var currentPath = startInfo.EnvironmentVariables["PATH"] ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            startInfo.EnvironmentVariables["PATH"] = $"{sdkInstallPath}{Path.PathSeparator}{currentPath}";

            logger.LogDebug("Using private SDK installation at {SdkPath}", sdkInstallPath);
        }
    }
}
