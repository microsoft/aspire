// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.DotNet;

/// <summary>
/// Default implementation of <see cref="IDotNetRuntimeSelector"/>.
/// </summary>
internal sealed class DotNetRuntimeSelector(
    ILogger<DotNetRuntimeSelector> logger,
    IConfiguration configuration,
    IDotNetSdkInstaller sdkInstaller,
    IInteractionService interactionService,
    IAnsiConsole console) : IDotNetRuntimeSelector
{
    private string? _dotNetExecutablePath;
    private DotNetRuntimeMode _mode = DotNetRuntimeMode.System;
    private readonly Dictionary<string, string> _environmentVariables = new();
    private bool? _initializationResult;

    /// <inheritdoc />
    public string DotNetExecutablePath => _dotNetExecutablePath ?? "dotnet";

    /// <inheritdoc />
    public DotNetRuntimeMode Mode => _mode;

    /// <inheritdoc />
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Return cached result if already initialized
        if (_initializationResult.HasValue)
        {
            return _initializationResult.Value;
        }

        _initializationResult = await InitializeCoreAsync(cancellationToken);
        return _initializationResult.Value;
    }

    private async Task<bool> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // Check configuration first, then environment variables
        var disablePrivateSdk = configuration["ASPIRE_DISABLE_PRIVATE_SDK"] == "1" 
            || Environment.GetEnvironmentVariable("ASPIRE_DISABLE_PRIVATE_SDK") == "1";
        var overrideVersion = configuration["ASPIRE_DOTNET_SDK_VERSION"]
            ?? Environment.GetEnvironmentVariable("ASPIRE_DOTNET_SDK_VERSION") 
            ?? configuration["overrideMinimumSdkVersion"];
        
        // Load settings from ~/.aspire/settings.json
        var settings = await LoadSettingsAsync();
        
        // Determine the required SDK version
        var requiredVersion = overrideVersion 
            ?? settings?.DotNet?.SdkVersion 
            ?? DotNetSdkInstaller.MinimumSdkVersion;

        // Determine the preferred mode
        var preferredMode = DetermineModeFromSettings(settings, disablePrivateSdk);

        // Check if system SDK meets requirements
        var systemSdkAvailable = await sdkInstaller.CheckAsync(requiredVersion, cancellationToken);

        switch (preferredMode)
        {
            case DotNetRuntimeMode.System:
                if (systemSdkAvailable)
                {
                    _mode = DotNetRuntimeMode.System;
                    _dotNetExecutablePath = "dotnet";
                    return true;
                }
                else if (!disablePrivateSdk)
                {
                    // Fall back to private if system doesn't work
                    return await TryInitializePrivateAsync(requiredVersion, cancellationToken);
                }
                else
                {
                    logger.LogError("System .NET SDK {Version} not available and private SDK disabled", requiredVersion);
                    return false;
                }

            case DotNetRuntimeMode.Private:
                if (!disablePrivateSdk)
                {
                    return await TryInitializePrivateAsync(requiredVersion, cancellationToken);
                }
                else
                {
                    logger.LogWarning("Private SDK requested but disabled by environment variable");
                    if (systemSdkAvailable)
                    {
                        _mode = DotNetRuntimeMode.System;
                        _dotNetExecutablePath = "dotnet";
                        return true;
                    }
                    return false;
                }

            case DotNetRuntimeMode.Custom:
                var customPath = settings?.DotNet?.CustomPath;
                if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
                {
                    _mode = DotNetRuntimeMode.Custom;
                    _dotNetExecutablePath = customPath;
                    return true;
                }
                logger.LogError("Custom dotnet path not found: {Path}", customPath);
                return false;

            default:
                return false;
        }
    }

    /// <inheritdoc />
    public IDictionary<string, string> GetEnvironmentVariables()
    {
        return new Dictionary<string, string>(_environmentVariables);
    }

    private async Task<AspireSettings?> LoadSettingsAsync()
    {
        try
        {
            var aspireHome = configuration["ASPIRE_HOME"]
                ?? Environment.GetEnvironmentVariable("ASPIRE_HOME") 
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire");
            
            var settingsPath = Path.Combine(aspireHome, "settings.json");
            
            if (!File.Exists(settingsPath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(settingsPath);
            return JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.AspireSettings);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load settings.json");
            return null;
        }
    }

    private static DotNetRuntimeMode DetermineModeFromSettings(AspireSettings? settings, bool disablePrivateSdk)
    {
        if (settings?.DotNet == null)
        {
            return DotNetRuntimeMode.System;
        }

        // Check for legacy preferPrivate setting
        if (settings.DotNet.PreferPrivate == true && !disablePrivateSdk)
        {
            return DotNetRuntimeMode.Private;
        }

        return settings.DotNet.Mode?.ToLowerInvariant() switch
        {
            "private" when !disablePrivateSdk => DotNetRuntimeMode.Private,
            "system" => DotNetRuntimeMode.System,
            "custom" => DotNetRuntimeMode.Custom,
            _ => DotNetRuntimeMode.System
        };
    }

    private async Task<bool> TryInitializePrivateAsync(string requiredVersion, CancellationToken cancellationToken)
    {
        var aspireHome = configuration["ASPIRE_HOME"]
            ?? Environment.GetEnvironmentVariable("ASPIRE_HOME") 
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire");
        
        var privateSdkPath = Path.Combine(aspireHome, "sdk", requiredVersion);
        var privateDotNetPath = Path.Combine(privateSdkPath, "dotnet");
        
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            privateDotNetPath += ".exe";
        }

        // Check if private SDK already exists and works
        if (File.Exists(privateDotNetPath))
        {
            _mode = DotNetRuntimeMode.Private;
            _dotNetExecutablePath = privateDotNetPath;
            
            // Set environment variables to isolate the private SDK
            _environmentVariables["DOTNET_ROOT"] = privateSdkPath;
            _environmentVariables["DOTNET_HOST_PATH"] = privateDotNetPath;
            
            return true;
        }

        // Private SDK doesn't exist, offer to install it
        return await OfferToInstallPrivateSdkAsync(requiredVersion, privateSdkPath, privateDotNetPath, cancellationToken);
    }

    private async Task<bool> OfferToInstallPrivateSdkAsync(string requiredVersion, string privateSdkPath, string privateDotNetPath, CancellationToken cancellationToken)
    {
        var autoInstall = configuration["ASPIRE_AUTO_INSTALL"] == "1"
            || Environment.GetEnvironmentVariable("ASPIRE_AUTO_INSTALL") == "1";
        
        if (!autoInstall)
        {
            console.MarkupLine($"[yellow]Required .NET SDK version {requiredVersion} is not available on the system PATH.[/]");
            console.MarkupLine($"[green]Would you like to install a private copy under ~/.aspire/sdk?[/]");
            
            if (!await interactionService.ConfirmAsync("Install private .NET SDK?", defaultValue: false, cancellationToken))
            {
                return false;
            }
        }

        // Acquire lock for this SDK version to prevent concurrent installations
        using var sdkLock = await SdkLockHelper.AcquireSdkLockAsync(requiredVersion, cancellationToken);

        // Check again if SDK was installed by another process while we were waiting for the lock
        if (File.Exists(privateDotNetPath))
        {
            _mode = DotNetRuntimeMode.Private;
            _dotNetExecutablePath = privateDotNetPath;
            
            // Set environment variables to isolate the private SDK
            _environmentVariables["DOTNET_ROOT"] = privateSdkPath;
            _environmentVariables["DOTNET_HOST_PATH"] = privateDotNetPath;
            
            return true;
        }

        try
        {
            await console.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Installing .NET SDK {requiredVersion}...", async ctx =>
                {
                    ctx.Status($"Creating directory {privateSdkPath}...");
                    Directory.CreateDirectory(privateSdkPath);

                    ctx.Status($"Downloading and installing .NET SDK {requiredVersion}...");
                    await InstallPrivateSdkAsync(configuration, requiredVersion, privateSdkPath, cancellationToken);
                });

            if (File.Exists(privateDotNetPath))
            {
                console.MarkupLine($"[green]Successfully installed private .NET SDK to {privateSdkPath}[/]");
                
                _mode = DotNetRuntimeMode.Private;
                _dotNetExecutablePath = privateDotNetPath;
                
                // Set environment variables to isolate the private SDK
                _environmentVariables["DOTNET_ROOT"] = privateSdkPath;
                _environmentVariables["DOTNET_HOST_PATH"] = privateDotNetPath;
                
                return true;
            }
            else
            {
                console.MarkupLine("[red]Failed to install private .NET SDK[/]");
                return false;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to install private .NET SDK");
            console.MarkupLine($"[red]Installation failed: {ex.Message}[/]");
            return false;
        }
    }

    private static async Task InstallPrivateSdkAsync(IConfiguration configuration, string requiredVersion, string installPath, CancellationToken cancellationToken)
    {
        // Use dotnet-install script to install the SDK
        var scriptUrl = configuration["ASPIRE_DOTNET_INSTALL_SCRIPT_URL"]
            ?? Environment.GetEnvironmentVariable("ASPIRE_DOTNET_INSTALL_SCRIPT_URL");
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        
        if (string.IsNullOrEmpty(scriptUrl))
        {
            scriptUrl = isWindows 
                ? "https://dot.net/v1/dotnet-install.ps1"
                : "https://dot.net/v1/dotnet-install.sh";
        }

        var tempDir = Path.GetTempPath();
        var scriptName = isWindows ? "dotnet-install.ps1" : "dotnet-install.sh";
        var scriptPath = Path.Combine(tempDir, scriptName);

        // Download the install script
        using var httpClient = new HttpClient();
        var scriptContent = await httpClient.GetStringAsync(scriptUrl, cancellationToken);
        await File.WriteAllTextAsync(scriptPath, scriptContent, cancellationToken);

        if (!isWindows)
        {
            // Make script executable on Unix
            File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        // Run the install script
        var arguments = isWindows
            ? $"-ExecutionPolicy Bypass -File \"{scriptPath}\" -Version {requiredVersion} -InstallDir \"{installPath}\" -NoPath"
            : $"\"{scriptPath}\" --version {requiredVersion} --install-dir \"{installPath}\" --no-path";

        var executable = isWindows ? "powershell" : "bash";
        
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        
        await process.WaitForExitAsync(cancellationToken);
        
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"dotnet-install script failed with exit code {process.ExitCode}: {error}");
        }

        // Clean up the script
        try
        {
            File.Delete(scriptPath);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}