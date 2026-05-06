// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Caching;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.DotNet;

internal interface IDotNetCliRunner
{
    Task<(int ExitCode, bool IsAspireHost, string? AspireHostingVersion)> GetAppHostInformationAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<(int ExitCode, JsonDocument? Output)> GetProjectItemsAndPropertiesAsync(FileInfo projectFile, string[] items, string[] properties, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<int> RunAsync(FileInfo projectFile, bool watch, bool noBuild, bool noRestore, string[] args, IDictionary<string, string>? env, TaskCompletionSource<IAppHostCliBackchannel>? backchannelCompletionSource, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<(int ExitCode, string? TemplateVersion)> InstallTemplateAsync(string packageName, string version, FileInfo? nugetConfigFile, string? nugetSource, bool force, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<int> NewProjectAsync(string templateName, string name, string outputPath, string[] extraArgs, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<int> RestoreAsync(FileInfo projectFilePath, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<int> BuildAsync(FileInfo projectFilePath, bool noRestore, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<int> AddPackageAsync(FileInfo projectFilePath, string packageName, string packageVersion, string? nugetSource, bool noRestore, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<int> AddProjectToSolutionAsync(FileInfo solutionFile, FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<(int ExitCode, NuGetPackage[]? Packages)> SearchPackagesAsync(DirectoryInfo workingDirectory, string query, bool exactMatch, bool prerelease, int take, int skip, FileInfo? nugetConfigFile, bool useCache, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<(int ExitCode, string[] ConfigPaths)> GetNuGetConfigPathsAsync(DirectoryInfo workingDirectory, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<(int ExitCode, IReadOnlyList<FileInfo> Projects)> GetSolutionProjectsAsync(FileInfo solutionFile, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<int> AddProjectReferenceAsync(FileInfo projectFile, FileInfo referencedProject, ProcessInvocationOptions options, CancellationToken cancellationToken);
    Task<int> InitUserSecretsAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken);
}

internal sealed class ProcessInvocationOptions
{
    public Action<string>? StandardOutputCallback { get; set; }
    public Action<string>? StandardErrorCallback { get; set; }

    public bool NoLaunchProfile { get; set; }
    public bool StartDebugSession { get; set; }
    public bool NoExtensionLaunch { get; set; }
    public bool Debug { get; set; }

    /// <summary>
    /// When true, suppresses logging of process output to the logger.
    /// Useful for background operations like NuGet package cache refreshes.
    /// </summary>
    public bool SuppressLogging { get; set; }
}

internal sealed class DotNetCliRunner(
    ILogger<DotNetCliRunner> logger,
    IServiceProvider serviceProvider,
    AspireCliTelemetry telemetry,
    IConfiguration configuration,
    IDiskCache diskCache,
    IFeatures features,
    IInteractionService interactionService,
    CliExecutionContext executionContext,
    IProcessExecutionFactory executionFactory) : IDotNetCliRunner
{
    private readonly IDiskCache _diskCache = diskCache;

    // Retry configuration for NuGet package search operations
    private const int MaxSearchRetries = 3;
    private static readonly TimeSpan[] s_searchRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)];

    private string GetMsBuildServerValue()
    {
        return configuration["DOTNET_CLI_USE_MSBUILD_SERVER"] ?? "true";
    }

    internal static string GetBackchannelSocketPath()
    {
        return CliPathHelper.CreateUnixDomainSocketPath("cli.sock");
    }

    private async Task<int> ExecuteAsync(
        string[] args,
        IDictionary<string, string>? env,
        FileInfo? projectFile,
        DirectoryInfo workingDirectory,
        TaskCompletionSource<IAppHostCliBackchannel>? backchannelCompletionSource,
        ProcessInvocationOptions options,
        CancellationToken cancellationToken)
    {
        // Build the final environment variables by merging caller-provided env with dotnet-specific settings.
        var finalEnv = env?.ToDictionary() ?? new Dictionary<string, string>();
        ConfigureDotNetEnvironment(finalEnv);

        // Resolve the dotnet executable path, preferring the private SDK installation if available.
        var dotnetPath = ResolveDotNetPath(finalEnv);

        // Do not use 'using' here: StartBackchannelAsync runs fire-and-forget and
        // accesses execution.HasExited / ExitCode after this method returns. Disposing
        // the underlying Process while the backchannel task is still polling would
        // cause ObjectDisposedException. Let the GC handle cleanup instead.
        var execution = executionFactory.CreateExecution(dotnetPath, args, finalEnv, workingDirectory, options);

        // Get socket path from env if present
        string? socketPath = null;
        env?.TryGetValue(KnownConfigNames.UnixSocketPath, out socketPath);

        // Handle extension-based launch for app hosts with backchannel
        if (backchannelCompletionSource is not null)
        {
            if (ExtensionHelper.IsExtensionHost(interactionService, out var extensionInteractionService, out var extensionBackchannel)
                && projectFile is not null
                && !options.NoExtensionLaunch
                && await extensionBackchannel.HasCapabilityAsync(KnownCapabilities.Project, cancellationToken))
            {
                await extensionInteractionService.LaunchAppHostAsync(
                    projectFile.FullName,
                    execution.Arguments.ToList(),
                    execution.EnvironmentVariables.Select(kvp => new EnvVar { Name = kvp.Key, Value = kvp.Value }).ToList(),
                    options.StartDebugSession);

                _ = StartBackchannelAsync(null, socketPath!, backchannelCompletionSource, cancellationToken);

                return ExitCodeConstants.Success;
            }
        }

        var started = execution.Start();

        if (!started)
        {
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }

        if (backchannelCompletionSource is not null && socketPath is not null)
        {
            _ = StartBackchannelAsync(execution, socketPath, backchannelCompletionSource, cancellationToken);
        }

        return await execution.WaitForExitAsync(cancellationToken);
    }

    internal static int GetCurrentProcessId() => Environment.ProcessId;

    internal static long GetCurrentProcessStartTimeUnixSeconds()
    {
        var startTime = Process.GetCurrentProcess().StartTime;
        return ((DateTimeOffset)startTime).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Configures dotnet-specific environment variables for CLI process executions.
    /// </summary>
    private void ConfigureDotNetEnvironment(IDictionary<string, string> env)
    {
        // The AppHost uses this environment variable to signal to the CliOrphanDetector which process
        // it should monitor in order to know when to stop the CLI. As long as the process still exists
        // the orphan detector will allow the CLI to keep running. If the environment variable does
        // not exist the orphan detector will exit.
        env[KnownConfigNames.CliProcessId] = GetCurrentProcessId().ToString(CultureInfo.InvariantCulture);

        // Set the CLI process start time for robust orphan detection to prevent PID reuse issues.
        // The AppHost will verify both PID and start time to ensure it's monitoring the correct process.
        env[KnownConfigNames.CliProcessStarted] = GetCurrentProcessStartTimeUnixSeconds().ToString(CultureInfo.InvariantCulture);

        // Always set MSBUILDTERMINALLOGGER=false for all dotnet command executions to ensure consistent terminal logger behavior
        env[KnownConfigNames.MsBuildTerminalLogger] = "false";

        // Suppress the .NET welcome message that appears on first run
        env["DOTNET_NOLOGO"] = "1";

        // Set debug session info if available
        var debugSessionInfo = configuration[KnownConfigNames.DebugSessionInfo];
        if (!string.IsNullOrEmpty(debugSessionInfo))
        {
            env[KnownConfigNames.DebugSessionInfo] = debugSessionInfo;
        }
    }

    /// <summary>
    /// Resolves the dotnet executable path, preferring a private SDK installation if available.
    /// When a private SDK is found, the appropriate environment variables (DOTNET_ROOT, PATH, etc.)
    /// are set on the provided dictionary.
    /// </summary>
    /// <returns>The path to the dotnet executable.</returns>
    private string ResolveDotNetPath(IDictionary<string, string> env)
    {
        var sdkVersion = DotNetSdkInstaller.GetEffectiveMinimumSdkVersion(configuration);
        var sdksDirectory = executionContext.SdksDirectory.FullName;
        var sdkInstallPath = Path.Combine(sdksDirectory, "dotnet", sdkVersion);
        var dotnetExecutablePath = Path.Combine(
            sdkInstallPath,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet"
        );

        if (Directory.Exists(sdkInstallPath))
        {
            env["DOTNET_ROOT"] = sdkInstallPath;
            env["DOTNET_MULTILEVEL_LOOKUP"] = "0";

            // Prepend the private SDK path to PATH. Check if the caller already provided a PATH override.
            var currentPath = env.TryGetValue("PATH", out var userPath)
                ? userPath
                : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            env["PATH"] = $"{sdkInstallPath}{Path.PathSeparator}{currentPath}";

            logger.LogDebug("Using private SDK installation at {SdkPath}", sdkInstallPath);
            return dotnetExecutablePath;
        }

        return "dotnet";
    }

    private async Task StartBackchannelAsync(IProcessExecution? execution, string socketPath, TaskCompletionSource<IAppHostCliBackchannel> backchannelCompletionSource, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));

        var backchannel = serviceProvider.GetRequiredService<IAppHostCliBackchannel>();
        var connectionAttempts = 0;

        logger.LogDebug("Starting backchannel connection to AppHost at {SocketPath}", socketPath);

        var startTime = DateTimeOffset.UtcNow;

        do
        {
            try
            {
                logger.LogTrace("Attempting to connect to AppHost backchannel at {SocketPath} (attempt {Attempt})", socketPath, connectionAttempts);
                await backchannel.ConnectAsync(socketPath, connectionAttempts, cancellationToken).ConfigureAwait(false);
                backchannelCompletionSource.SetResult(backchannel);
                // Note: We intentionally do not call Environment.Exit when the backchannel disconnects.
                // The CLI should complete normally and return the appropriate exit code based on the
                // deployment result. Calling Environment.Exit here would bypass the normal exit code
                // logic and always return success (0), even when the deployment failed.

                logger.LogDebug("Connected to AppHost backchannel at {SocketPath}", socketPath);
                return;
            }
            catch (Exception ex) when (ex is SocketException or RemoteRpcException && execution is { HasExited: true, ExitCode: not 0 })
            {
                // Log at Debug level - this is expected when AppHost crashes, the real error is in AppHost output
                logger.LogDebug(ex, "AppHost process has exited. Unable to connect to backchannel at {SocketPath}", socketPath);
                var backchannelException = new FailedToConnectBackchannelConnection("AppHost process has exited unexpectedly.", ex);
                backchannelCompletionSource.SetException(backchannelException);
                return;
            }
            catch (Exception ex) when (ex is SocketException or RemoteRpcException)
            {
                // If the process is taking a long time to open a back channel but
                // it has not exited then it probably means that its a larger build
                // (remember it has to build the apphost and its dependencies).
                // In that case, after 30 seconds we just slow down the polling to
                // once per second.
                var waitingFor = DateTimeOffset.UtcNow - startTime;
                if (waitingFor > TimeSpan.FromSeconds(10))
                {
                    logger.LogTrace(ex, "Slow polling for backchannel connection (attempt {Attempt})", connectionAttempts);
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // We don't want to spam the logs with our early connection attempts.
                }
            }
            catch (AppHostIncompatibleException ex)
            {
                logger.LogError(
                    ex,
                    "The app host is incompatible with the CLI and must be updated to a version that supports the {RequiredCapability} capability.",
                    ex.RequiredCapability
                    );

                // If the app host is incompatible then there is no point
                // trying to reconnect, we should propagate the exception
                // up to the code that needs to back channel so it can display
                // and error message to the user.
                backchannelCompletionSource.SetException(ex);

                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An unexpected error occurred while trying to connect to the backchannel.");
                backchannelCompletionSource.SetException(ex);
                throw;
            }
            finally
            {
                connectionAttempts++;
            }

        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    // Cache expiry/max age handled inside DiskCache implementation.

    public async Task<(int ExitCode, bool IsAspireHost, string? AspireHostingVersion)> GetAppHostInformationAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        // Get both properties and PackageReference items to determine Aspire.Hosting version
        var (exitCode, jsonDocument) = await GetProjectItemsAndPropertiesAsync(
            projectFile,
            ["PackageReference", "AspireProjectOrPackageReference", "PackageVersion"],
            ["IsAspireHost", "AspireHostingSDKVersion"],
            options,
            cancellationToken);

        if (exitCode == 0 && jsonDocument != null)
        {
            var rootElement = jsonDocument.RootElement;

            if (!rootElement.TryGetProperty("Properties", out var properties))
            {
                return (exitCode, false, null);
            }

            if (!properties.TryGetProperty("IsAspireHost", out var isAspireHostElement))
            {
                return (exitCode, false, null);
            }

            if (isAspireHostElement.GetString() == "true")
            {
                // Try to get Aspire.Hosting version from PackageReference items
                string? aspireHostingVersion = null;

                if (rootElement.TryGetProperty("Items", out var items))
                {
                    // Check PackageReference items first
                    if (items.TryGetProperty("PackageReference", out var packageReferences))
                    {
                        aspireHostingVersion = GetPackageVersion(packageReferences, "Aspire.Hosting") ??
                            GetPackageVersion(packageReferences, "Aspire.Hosting.AppHost");
                    }

                    // Fallback to AspireProjectOrPackageReference items if not found
                    if (aspireHostingVersion == null && items.TryGetProperty("AspireProjectOrPackageReference", out var aspireProjectOrPackageReferences))
                    {
                        aspireHostingVersion = GetPackageVersion(aspireProjectOrPackageReferences, "Aspire.Hosting") ??
                            GetPackageVersion(aspireProjectOrPackageReferences, "Aspire.Hosting.AppHost");
                    }

                    // Fallback to PackageVersion items for Central Package Management if not found
                    if (aspireHostingVersion == null && items.TryGetProperty("PackageVersion", out var packageVersions))
                    {
                        aspireHostingVersion = GetPackageVersion(packageVersions, "Aspire.Hosting") ??
                            GetPackageVersion(packageVersions, "Aspire.Hosting.AppHost");
                    }
                }

                // If no package version found, fallback to SDK version
                if (aspireHostingVersion == null && properties.TryGetProperty("AspireHostingSDKVersion", out var aspireHostingSdkVersionElement))
                {
                    aspireHostingVersion = aspireHostingSdkVersionElement.GetString();
                }

                return (exitCode, true, aspireHostingVersion);
            }
            else
            {
                return (exitCode, false, null);
            }
        }
        else
        {
            return (exitCode, false, null);
        }
    }

    private static string? GetPackageVersion(JsonElement items, string packageId)
    {
        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("Identity", out var identity) &&
                identity.GetString() == packageId &&
                item.TryGetProperty("Version", out var version))
            {
                return version.GetString();
            }
        }

        return null;
    }

    public async Task<(int ExitCode, JsonDocument? Output)> GetProjectItemsAndPropertiesAsync(FileInfo projectFile, string[] items, string[] properties, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        var isSingleFileAppHost = projectFile.Name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase);

        // If we are a single file app host then we use the build command instead of msbuild command.
        var cliArgsList = new List<string> { isSingleFileAppHost ? "build" : "msbuild" };

        if (properties.Length > 0)
        {
            // HACK: MSBuildVersion here because if you ever invoke `dotnet msbuild -getproperty with just a single
            //       property it will not be returned as JSON. I've reported this as a problem to MSBuild but obviously
            //       we need to work around it:
            //
            //       https://github.com/dotnet/msbuild/issues/12490
            //
            cliArgsList.Add($"-getProperty:MSBuildVersion,{string.Join(",", properties)}");
        }

        if (items.Length > 0)
        {
            cliArgsList.Add($"-getItem:{string.Join(",", items)}");
        }

        cliArgsList.Add(projectFile.FullName);

        string[] cliArgs = [.. cliArgsList];

        var existingStandardOutputCallback = options.StandardOutputCallback;
        var existingStandardErrorCallback = options.StandardErrorCallback;

        // Retry when MSBuild returns success but produces no output, which can happen
        // due to MSBuild server contention (e.g. when another AppHost build is running).
        const int maxRetries = 3;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            var stdoutBuilder = new StringBuilder();
            options.StandardOutputCallback = (line) => {
                stdoutBuilder.AppendLine(line);
                existingStandardOutputCallback?.Invoke(line);
            };

            var stderrBuilder = new StringBuilder();
            options.StandardErrorCallback = (line) => {
                stderrBuilder.AppendLine(line);
                existingStandardErrorCallback?.Invoke(line);
            };

            var exitCode = await ExecuteAsync(
                args: cliArgs,
                env: null,
                projectFile: projectFile,
                workingDirectory: projectFile.Directory!,
                backchannelCompletionSource: null,
                options: options,
                cancellationToken: cancellationToken);

            var stdout = stdoutBuilder.ToString();
            var stderr = stderrBuilder.ToString();

            if (exitCode != 0)
            {
                logger.LogError(
                    "Failed to get items and properties from project. Exit code was: {ExitCode}. See debug logs for more details. Stderr: {Stderr}, Stdout: {Stdout}",
                    exitCode,
                    stderr,
                    stdout
                );

                return (exitCode, null);
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                if (attempt < maxRetries - 1)
                {
                    logger.LogWarning(
                        "dotnet msbuild returned exit code 0 but produced no output (attempt {Attempt}/{MaxRetries}). Retrying after delay. Stderr: {Stderr}",
                        attempt + 1,
                        maxRetries,
                        stderr);
                    await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                logger.LogWarning(
                    "dotnet msbuild returned exit code 0 but produced no output after {MaxRetries} attempts. Stderr: {Stderr}",
                    maxRetries,
                    stderr);
                return (exitCode, null);
            }

            var json = JsonDocument.Parse(stdout);
            return (exitCode, json);
        }

        // Should not be reached, but return failure as a safety net
        return (1, null);
    }

    public async Task<int> RunAsync(FileInfo projectFile, bool watch, bool noBuild, bool noRestore, string[] args, IDictionary<string, string>? env, TaskCompletionSource<IAppHostCliBackchannel>? backchannelCompletionSource, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        if (watch && noBuild)
        {
            var ex = new InvalidOperationException(ErrorStrings.CantUseBothWatchAndNoBuild);
            backchannelCompletionSource?.SetException(ex);
            throw ex;
        }

        var isSingleFile = projectFile.Extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
        var watchOrRunCommand = watch ? "watch" : "run";
        var noBuildSwitch = noBuild ? "--no-build" : string.Empty;
        var noRestoreSwitch = noRestore && !noBuild ? "--no-restore" : string.Empty; // --no-build implies --no-restore
        var noProfileSwitch = options.NoLaunchProfile ? "--no-launch-profile" : string.Empty;
        // Add --non-interactive flag when using watch to prevent interactive prompts during automation
        var nonInteractiveSwitch = watch ? "--non-interactive" : string.Empty;
        // Add --verbose flag when using watch and debug is enabled
        var verboseSwitch = watch && options.Debug ? "--verbose" : string.Empty;

        string[] cliArgs = isSingleFile switch
        {
            false => [watchOrRunCommand, nonInteractiveSwitch, verboseSwitch, noBuildSwitch, noRestoreSwitch, noProfileSwitch, "--project", projectFile.FullName, "--", .. args],
            true => ["run", noProfileSwitch, "--file", projectFile.FullName, "--", .. args]
        };

        cliArgs = [.. cliArgs.Where(arg => !string.IsNullOrWhiteSpace(arg))];

        // We copy the dictionary here because we don't want to mutate the input.
        var finalEnv = env?.ToDictionary() ?? new Dictionary<string, string>();

        // Inject DOTNET_CLI_USE_MSBUILD_SERVER when noBuild == false
        if (!noBuild)
        {
            finalEnv["DOTNET_CLI_USE_MSBUILD_SERVER"] = GetMsBuildServerValue();
        }

        // Check if update notifications are disabled and set version check environment variable
        if (!features.IsFeatureEnabled(KnownFeatures.UpdateNotificationsEnabled, defaultValue: true))
        {
            // Only set the environment variable if it's not already set by the user
            if (!finalEnv.ContainsKey(KnownConfigNames.VersionCheckDisabled))
            {
                finalEnv[KnownConfigNames.VersionCheckDisabled] = "true";
            }
        }

        // Set DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER when watch is enabled to prevent launching browser
        if (watch)
        {
            // Only set the environment variable if it's not already set by the user
            if (!finalEnv.ContainsKey("DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"))
            {
                finalEnv["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "true";
            }
        }

        // Set the backchannel socket path when backchannel is configured
        if (backchannelCompletionSource is not null)
        {
            var socketPath = GetBackchannelSocketPath();
            finalEnv[KnownConfigNames.UnixSocketPath] = socketPath;
        }

        return await ExecuteAsync(
            args: cliArgs,
            env: finalEnv,
            projectFile: projectFile,
            workingDirectory: projectFile.Directory!,
            backchannelCompletionSource: backchannelCompletionSource,
            options: options,
            cancellationToken: cancellationToken);
    }

    public async Task<(int ExitCode, string? TemplateVersion)> InstallTemplateAsync(string packageName, string version, FileInfo? nugetConfigFile, string? nugetSource, bool force, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity(kind: ActivityKind.Client);

        // NOTE: The change to @ over :: for template version separator (now enforced in .NET 10.0 SDK).
        var workingDirectory = nugetConfigFile?.Directory ?? executionContext.WorkingDirectory;
        var localPackagePath = ResolveLocalTemplatePackagePath(packageName, version, nugetSource, workingDirectory);

        // dotnet new install <path>.nupkg --force can register duplicate template packages for the same
        // local file. Refresh local packages by uninstalling first, then reinstalling without --force.
        if (localPackagePath is not null && force)
        {
            await UninstallTemplateAsync(packageName, workingDirectory, cancellationToken);
            force = false;
        }

        List<string> cliArgs = ["new", "install", localPackagePath?.FullName ?? $"{packageName}@{version}"];

        if (force)
        {
            cliArgs.Add("--force");
        }

        if (localPackagePath is null && nugetSource is not null)
        {
            cliArgs.Add("--nuget-source");
            cliArgs.Add(nugetSource);
        }

        var stdoutBuilder = new StringBuilder();
        var existingStandardOutputCallback = options.StandardOutputCallback; // Preserve the existing callback if it exists.
        options.StandardOutputCallback = (line) => {
            stdoutBuilder.AppendLine(line);
            existingStandardOutputCallback?.Invoke(line);
        };

        var stderrBuilder = new StringBuilder();
        var existingStandardErrorCallback = options.StandardErrorCallback; // Preserve the existing callback if it exists.
        options.StandardErrorCallback = (line) => {
            stderrBuilder.AppendLine(line);
            existingStandardErrorCallback?.Invoke(line);
        };

        // The dotnet new install command does not support the --configfile option so if we
        // are installing packages based on a channel config we'll be passing in a nuget config
        // file which is dynamically generated in a temporary folder. We'll use that temporary
        // folder as the working directory for the command. If we are using an implicit channel
        // then we just use the current execution context for the CLI and inherit whatever
        // NuGet.configs that may or may not be laying around.

        var exitCode = await ExecuteAsync(
            args: [.. cliArgs],
            env: new Dictionary<string, string>
            {
                // Force English output for consistent parsing.
                // See NOTE: below
                [KnownConfigNames.DotnetCliUiLanguage] = "en-US"
            },
            projectFile: null,
            workingDirectory: workingDirectory,
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);

        var stdout = stdoutBuilder.ToString();
        var stderr = stderrBuilder.ToString();

        if (exitCode != 0)
        {
            logger.LogError(
                "Failed to install template {PackageName} with version {Version}. See debug logs for more details. Stderr: {Stderr}, Stdout: {Stdout}",
                packageName,
                version,
                stderr,
                stdout
            );
            return (exitCode, null);
        }
        else
        {
            if (localPackagePath is not null)
            {
                return (exitCode, version);
            }

            if (stdout is null)
            {
                logger.LogError("Failed to read stdout from the process. This should never happen.");
                return (ExitCodeConstants.FailedToInstallTemplates, null);
            }

            // NOTE: This parsing logic is hopefully temporary and in the future we'll
            //       have structured output:
            //
            //       See: https://github.com/dotnet/sdk/issues/46345
            //
            if (!TryParsePackageVersionFromStdout(stdout, out var parsedVersion))
            {
                logger.LogError("Failed to parse template version from stdout.");

                // Throwing here because this should never happen - we don't want to return
                // the zero exit code if we can't parse the version because its possibly a
                // signal that the .NET SDK has changed.
                throw new InvalidOperationException(ErrorStrings.FailedToParseTemplateVersionFromStdout);
            }

            return (exitCode, parsedVersion);
        }
    }

    private async Task UninstallTemplateAsync(string packageName, DirectoryInfo workingDirectory, CancellationToken cancellationToken)
    {
        var exitCode = await ExecuteAsync(
            args: ["new", "uninstall", packageName],
            env: new Dictionary<string, string>
            {
                [KnownConfigNames.DotnetCliUiLanguage] = "en-US"
            },
            projectFile: null,
            workingDirectory: workingDirectory,
            backchannelCompletionSource: null,
            options: new ProcessInvocationOptions
            {
                SuppressLogging = true
            },
            cancellationToken: cancellationToken);

        if (exitCode != 0)
        {
            logger.LogDebug("dotnet new uninstall {PackageName} returned {ExitCode} before local reinstall.", packageName, exitCode);
        }
    }

    private static FileInfo? ResolveLocalTemplatePackagePath(string packageName, string version, string? nugetSource, DirectoryInfo workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(nugetSource))
        {
            return null;
        }

        string sourcePath;
        if (Uri.TryCreate(nugetSource, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile)
            {
                return null;
            }

            sourcePath = uri.LocalPath;
        }
        else
        {
            sourcePath = Path.GetFullPath(nugetSource, workingDirectory.FullName);
        }

        if (File.Exists(sourcePath) && string.Equals(Path.GetExtension(sourcePath), ".nupkg", StringComparison.OrdinalIgnoreCase))
        {
            return new FileInfo(sourcePath);
        }

        if (!Directory.Exists(sourcePath))
        {
            return null;
        }

        var expectedFileName = $"{packageName}.{version}.nupkg";
        var packagePath = Directory.EnumerateFiles(sourcePath, "*.nupkg", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), expectedFileName, StringComparison.OrdinalIgnoreCase));

        return packagePath is null ? null : new FileInfo(packagePath);
    }

    internal static bool TryParsePackageVersionFromStdout(string stdout, [NotNullWhen(true)] out string? version)
    {
        var lines = stdout.Split(Environment.NewLine);
        var successLine = lines.SingleOrDefault(x => x.StartsWith("Success: Aspire.ProjectTemplates"));

        if (successLine is null)
        {
            version = null;
            return false;
        }

        var templateVersion = successLine.Split(" ") switch { // Break up the success line.
            { Length: > 2 } chunks => chunks[1].Split("@") switch { // Break up the template+version string (@ separator for .NET 10.0+)
                { Length: 2 } versionChunks => versionChunks[1], // The version in the second chunk
                _ => chunks[1].Split("::") switch { // Fallback to :: separator for older SDK versions
                    { Length: 2 } versionChunks => versionChunks[1],
                    _ => null
                }
            },
            _ => null
        };

        if (templateVersion is not null)
        {
            version = templateVersion;
            return true;
        }
        else
        {
            version = null;
            return false;
        }
    }

    public async Task<int> NewProjectAsync(string templateName, string name, string outputPath, string[] extraArgs, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        string[] cliArgs = ["new", templateName, "--name", name, "--output", outputPath, ..extraArgs];
        return await ExecuteAsync(
            args: cliArgs,
            env: null,
            projectFile: null,
            workingDirectory: new DirectoryInfo(Environment.CurrentDirectory),
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);
    }

    public async Task<int> RestoreAsync(FileInfo projectFilePath, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        string[] cliArgs = ["restore", projectFilePath.FullName];

        return await ExecuteAsync(
            args: cliArgs,
            env: null,
            projectFile: projectFilePath,
            workingDirectory: projectFilePath.Directory!,
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);
    }

    public async Task<int> BuildAsync(FileInfo projectFilePath, bool noRestore, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        var noRestoreSwitch = noRestore ? "--no-restore" : string.Empty;
        string[] cliArgs = ["build", noRestoreSwitch, projectFilePath.FullName];
        cliArgs = [.. cliArgs.Where(arg => !string.IsNullOrWhiteSpace(arg))];

        // Always inject DOTNET_CLI_USE_MSBUILD_SERVER for apphost builds
        var env = new Dictionary<string, string>
        {
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = GetMsBuildServerValue()
        };

        return await ExecuteAsync(
            args: cliArgs,
            env: env,
            projectFile: projectFilePath,
            workingDirectory: projectFilePath.Directory!,
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);
    }
    public async Task<int> AddPackageAsync(FileInfo projectFilePath, string packageName, string packageVersion, string? nugetSource, bool noRestore, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        var cliArgsList = new List<string>
        {
            "package"
        };

        // For single-file AppHost (apphost.cs), use --file switch instead of positional argument
        var isSingleFileAppHost = projectFilePath.Name.Equals("apphost.cs", StringComparison.OrdinalIgnoreCase);
        if (isSingleFileAppHost)
        {
            cliArgsList.Add("add");
            cliArgsList.AddRange(["--file", projectFilePath.FullName]);
            // For single-file AppHost, use packageName@version format
            cliArgsList.Add($"{packageName}@{packageVersion}");
        }
        else
        {
            cliArgsList.Add("add");
            // For non single-file scenarios, use separate --version flag
            cliArgsList.Add(packageName);
            cliArgsList.Add("--version");
            cliArgsList.Add(packageVersion);
            cliArgsList.Add("--project");
            cliArgsList.Add(projectFilePath.FullName);
        }

        if (noRestore)
        {
            cliArgsList.Add("--no-restore");
        }
        if (!string.IsNullOrEmpty(nugetSource))
        {
            cliArgsList.Add("--source");
            cliArgsList.Add(nugetSource);
        }

        string[] cliArgs = [.. cliArgsList];

        logger.LogInformation("Adding package {PackageName} with version {PackageVersion} to project {ProjectFilePath}", packageName, packageVersion, projectFilePath.FullName);

        var result = await ExecuteAsync(
            args: cliArgs,
            env: null,
            projectFile: projectFilePath,
            workingDirectory: projectFilePath.Directory!,
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);

        if (result != 0)
        {
            logger.LogError("Failed to add package {PackageName} with version {PackageVersion} to project {ProjectFilePath}. See debug logs for more details.", packageName, packageVersion, projectFilePath.FullName);
        }
        else
        {
            logger.LogInformation("Package {PackageName} with version {PackageVersion} added to project {ProjectFilePath}", packageName, packageVersion, projectFilePath.FullName);
        }

        return result;
    }

    public async Task<int> AddProjectToSolutionAsync(FileInfo solutionFile, FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        string[] cliArgs = ["sln", solutionFile.FullName, "add", projectFile.FullName];

        logger.LogInformation("Adding project {ProjectFilePath} to solution {SolutionFilePath}", projectFile.FullName, solutionFile.FullName);

        var result = await ExecuteAsync(
            args: cliArgs,
            env: null,
            projectFile: null,
            workingDirectory: solutionFile.Directory!,
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);

        if (result != 0)
        {
            logger.LogError("Failed to add project {ProjectFilePath} to solution {SolutionFilePath}. See debug logs for more details.", projectFile.FullName, solutionFile.FullName);
        }
        else
        {
            logger.LogInformation("Project {ProjectFilePath} added to solution {SolutionFilePath}", projectFile.FullName, solutionFile.FullName);
        }

        return result;
    }

    public async Task<string> ComputeNuGetConfigHierarchySha256Async(DirectoryInfo workingDirectory, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        // The purpose of this method is to compute a hash that can be used as a substitute for an explicitly passed
        // in NuGet.config file hash. This is useful for when `aspire add` is invoked and we present options from the
        // implicit feed where we effectively are presenting cached options based on the NuGet.config config in the
        // current working directory. If any NuGet.config in the hierarchy of NuGet.config files is touched then the
        // cache will be invalidated and we'll do a live search instead of using the cache. This is necessary for
        // implicit channel searches which generally provide the best choice to users in the case of `aspire add`.

        ArgumentNullException.ThrowIfNull(workingDirectory);

        using var activity = telemetry.StartDiagnosticActivity();

        var (exitCode, configPaths) = await GetNuGetConfigPathsAsync(workingDirectory, options, cancellationToken);

        if (exitCode != 0)
        {
            logger.LogError("Failed to get NuGet config paths. Exit code was: {ExitCode}.", exitCode);
            return string.Empty;
        }

        if (configPaths.Length == 0)
        {
            return string.Empty;
        }

        var hashes = new List<string>();

        foreach (var configPath in configPaths)
        {
            if (string.IsNullOrWhiteSpace(configPath))
            {
                continue;
            }

            var filePath = Path.IsPathRooted(configPath)
                ? configPath
                : Path.Combine(workingDirectory.FullName, configPath);

            if (!File.Exists(filePath))
            {
                logger.LogDebug("NuGet config file not found at path: {Path}", filePath);
                continue;
            }

            try
            {
                using var stream = File.OpenRead(filePath);
                var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
                var hex = Convert.ToHexString(bytes);
                hashes.Add(hex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                logger.LogDebug(ex, "Failed to read or hash NuGet config file at path: {Path}", filePath);
                continue;
            }
        }

        var result = string.Join("|", hashes);
        return result;
    }

    public async Task<(int ExitCode, NuGetPackage[]? Packages)> SearchPackagesAsync(DirectoryInfo workingDirectory, string query, bool exactMatch, bool prerelease, int take, int skip, FileInfo? nugetConfigFile, bool useCache, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        string? rawKey = null;
        var cacheEnabled = useCache;
        if (useCache)
        {
            try
            {
                // Compute optional hash of the nuget.config file contents (if any)
                string nugetConfigHash = string.Empty;
                if (nugetConfigFile is not null && nugetConfigFile.Exists)
                {
                    using var stream = nugetConfigFile.OpenRead();
                    var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
                    nugetConfigHash = Convert.ToHexString(bytes);
                }
                else
                {
                    nugetConfigHash = await ComputeNuGetConfigHierarchySha256Async(workingDirectory, options, cancellationToken);
                }

                // Build a cache key using the main discriminators, including CLI version.
                var cliVersion = VersionHelper.GetDefaultTemplateVersion();
                rawKey = $"query={query}|exactMatch={exactMatch}|prerelease={prerelease}|take={take}|skip={skip}|nugetConfigHash={nugetConfigHash}|cliVersion={cliVersion}";
                var cached = await _diskCache.GetAsync(rawKey, cancellationToken).ConfigureAwait(false);
                if (cached is not null)
                {
                    try
                    {
                        var foundPackages = PackageUpdateHelpers.ParsePackageSearchResults(cached);
                        return (0, foundPackages.ToArray());
                    }
                    catch (JsonException ex)
                    {
                        logger.LogDebug(ex, "Failed to parse cached package search JSON; performing live search.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Fail open – treat as cache miss.
                logger.LogDebug(ex, "Failed to probe package search disk cache; proceeding without cache.");
                cacheEnabled = false; // disable write attempt as well
            }
        }

        List<string> cliArgs = [
            "package",
            "search",
            query,
            "--format",
            "json"
        ];

        if (exactMatch) // search for all versions that match the query exactly
        {
            cliArgs.Add("--exact-match");
        }
        else // 'exact-match' flag causes the take and skip arguments to be ignored
        {
            cliArgs.AddRange([
                "--take",
                take.ToString(CultureInfo.InvariantCulture),
                "--skip",
                skip.ToString(CultureInfo.InvariantCulture),
            ]);
        }

        if (nugetConfigFile is not null)
        {
            cliArgs.Add("--configfile");
            cliArgs.Add(nugetConfigFile.FullName);
        }

        if (prerelease)
        {
            cliArgs.Add("--prerelease");
        }

        int result = 0;
        string stdout = string.Empty;
        string stderr = string.Empty;

        // Capture original callbacks before the retry loop to avoid mutation/chaining
        var originalStdoutCallback = options.StandardOutputCallback;
        var originalStderrCallback = options.StandardErrorCallback;

        for (int attempt = 1; attempt <= MaxSearchRetries; attempt++)
        {
            var stdoutBuilder = new StringBuilder();
            options.StandardOutputCallback = (line) =>
            {
                stdoutBuilder.AppendLine(line);
                originalStdoutCallback?.Invoke(line);
            };

            var stderrBuilder = new StringBuilder();
            options.StandardErrorCallback = (line) =>
            {
                stderrBuilder.AppendLine(line);
                originalStderrCallback?.Invoke(line);
            };

            result = await ExecuteAsync(
                args: cliArgs.ToArray(),
                env: null,
                projectFile: null,
                workingDirectory: workingDirectory!,
                backchannelCompletionSource: null,
                options: options,
                cancellationToken: cancellationToken);

            stdout = stdoutBuilder.ToString();
            stderr = stderrBuilder.ToString();

            if (result == 0)
            {
                // Success - exit retry loop
                break;
            }

            if (attempt < MaxSearchRetries)
            {
                // Use defensive bounds check in case MaxSearchRetries is changed without updating delay array
                var delayIndex = Math.Min(attempt - 1, s_searchRetryDelays.Length - 1);
                var delay = s_searchRetryDelays[delayIndex];
                logger.LogDebug(
                    "NuGet package search failed (attempt {Attempt}/{MaxRetries}), exit code {ExitCode}. Retrying in {DelaySeconds}s...",
                    attempt,
                    MaxSearchRetries,
                    result,
                    delay.TotalSeconds);
                await Task.Delay(delay, cancellationToken);
            }
        }

        if (result != 0)
        {
            logger.LogError(
                "Failed to search for packages after {MaxRetries} attempts. Query: {Query}, ConfigFile: {ConfigFile}, Stderr: {Stderr}, Stdout: {Stdout}",
                MaxSearchRetries,
                query,
                nugetConfigFile?.FullName ?? "(default)",
                stderr,
                stdout);

            return (result, null);
        }
        else
        {
            if (stdout is null)
            {
                logger.LogError("Failed to read stdout from the process. This should never happen.");
                return (ExitCodeConstants.FailedToAddPackage, null);
            }

            try
            {
                var foundPackages = PackageUpdateHelpers.ParsePackageSearchResults(stdout);

                // Attempt to persist the raw stdout JSON for future lookups when cache enabled
                if (cacheEnabled && rawKey is not null)
                {
                    await _diskCache.SetAsync(rawKey, stdout, cancellationToken).ConfigureAwait(false);
                }

                return (result, foundPackages.ToArray());
            }
            catch (JsonException ex)
            {
                logger.LogError($"Failed to read JSON returned by the package search. {ex.Message}");
                return (ExitCodeConstants.FailedToAddPackage, null);
            }

        }
    }

    public async Task<(int ExitCode, string[] ConfigPaths)> GetNuGetConfigPathsAsync(DirectoryInfo workingDirectory, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        string[] cliArgs = ["nuget", "config", "paths"];

        var stdoutLines = new List<string>();
        var existingStandardOutputCallback = options.StandardOutputCallback; // Preserve the existing callback if it exists.
        options.StandardOutputCallback = (line) => {
            stdoutLines.Add(line);
            existingStandardOutputCallback?.Invoke(line);
        };

        var stderrLines = new List<string>();
        var existingStandardErrorCallback = options.StandardErrorCallback; // Preserve the existing callback if it exists.
        options.StandardErrorCallback = (line) => {
            stderrLines.Add(line);
            existingStandardErrorCallback?.Invoke(line);
        };

        var exitCode = await ExecuteAsync(
            args: cliArgs,
            env: null,
            projectFile: null,
            workingDirectory: workingDirectory,
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);

        if (exitCode != 0)
        {
            logger.LogError("Failed to get NuGet config paths. Exit code was: {ExitCode}.", exitCode);
            return (exitCode, Array.Empty<string>());
        }
        else
        {
            return (exitCode, stdoutLines.ToArray());
        }
    }

    public async Task<(int ExitCode, IReadOnlyList<FileInfo> Projects)> GetSolutionProjectsAsync(FileInfo solutionFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        string[] cliArgs = ["sln", solutionFile.FullName, "list"];

        var stdoutLines = new List<string>();
        var existingStandardOutputCallback = options.StandardOutputCallback;
        options.StandardOutputCallback = (line) => {
            stdoutLines.Add(line);
            existingStandardOutputCallback?.Invoke(line);
        };

        var stderrLines = new List<string>();
        var existingStandardErrorCallback = options.StandardErrorCallback;
        options.StandardErrorCallback = (line) => {
            stderrLines.Add(line);
            existingStandardErrorCallback?.Invoke(line);
        };

        var exitCode = await ExecuteAsync(
            args: cliArgs,
            env: null,
            projectFile: null,
            workingDirectory: solutionFile.Directory!,
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);

        if (exitCode != 0)
        {
            logger.LogError("Failed to list solution projects. Exit code was: {ExitCode}.", exitCode);
            return (exitCode, Array.Empty<FileInfo>());
        }

        // Parse output - skip header lines (Project(s) and ----------)
        var projects = new List<FileInfo>();
        var startParsing = false;

        foreach (var line in stdoutLines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Skip header lines
            if (line.StartsWith("Project(s)", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("----------", StringComparison.Ordinal))
            {
                startParsing = true;
                continue;
            }

            if (startParsing && line.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var projectPath = Path.IsPathRooted(line)
                    ? line
                    : Path.Combine(solutionFile.Directory!.FullName, line);
                projects.Add(new FileInfo(projectPath));
            }
        }

        return (exitCode, projects);
    }

    public async Task<int> AddProjectReferenceAsync(FileInfo projectFile, FileInfo referencedProject, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        using var activity = telemetry.StartDiagnosticActivity();

        string[] cliArgs = ["add", projectFile.FullName, "reference", referencedProject.FullName];

        logger.LogInformation("Adding project reference from {ProjectFile} to {ReferencedProject}", projectFile.FullName, referencedProject.FullName);

        var result = await ExecuteAsync(
            args: cliArgs,
            env: null,
            projectFile: projectFile,
            workingDirectory: projectFile.Directory!,
            backchannelCompletionSource: null,
            options: options,
            cancellationToken: cancellationToken);

        if (result != 0)
        {
            logger.LogError("Failed to add project reference from {ProjectFile} to {ReferencedProject}. See debug logs for more details.", projectFile.FullName, referencedProject.FullName);
        }
        else
        {
            logger.LogInformation("Project reference added from {ProjectFile} to {ReferencedProject}", projectFile.FullName, referencedProject.FullName);
        }

        return result;
    }

    public Task<int> InitUserSecretsAsync(FileInfo projectFile, ProcessInvocationOptions options, CancellationToken cancellationToken)
    {
        return ExecuteAsync(["user-secrets", "init", "--project", projectFile.FullName], env: null, projectFile: null, projectFile.Directory!, backchannelCompletionSource: null, options, cancellationToken);
    }
}
