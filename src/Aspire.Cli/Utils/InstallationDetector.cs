// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils;

/// <summary>
/// Information about the CLI installation method and self-update availability.
/// </summary>
internal sealed record InstallationInfo(bool IsDotNetTool, bool SelfUpdateDisabled, string? UpdateInstructions);

/// <summary>
/// Detects how the CLI was installed and whether self-update is available.
/// </summary>
internal interface IInstallationDetector
{
    /// <summary>
    /// Gets information about the current CLI installation.
    /// </summary>
    InstallationInfo GetInstallationInfo();
}

/// <summary>
/// Model for the <c>.aspire-update.json</c> file that can disable self-update.
/// </summary>
internal sealed class AspireUpdateConfig
{
    [JsonPropertyName("selfUpdateDisabled")]
    public bool SelfUpdateDisabled { get; set; }

    [JsonPropertyName("updateInstructions")]
    public string? UpdateInstructions { get; set; }
}

/// <summary>
/// Detects CLI installation method by checking for <c>.aspire-update.json</c>, WinGet path heuristics, and dotnet tool indicators.
/// </summary>
internal sealed class InstallationDetector : IInstallationDetector
{
    private readonly ILogger<InstallationDetector> _logger;
    private readonly string? _processPath;
    private InstallationInfo? _cachedInfo;

    internal const string UpdateConfigFileName = ".aspire-update.json";

    /// <summary>
    /// Generic update message shown when the CLI appears to be managed by a package manager but the specific one cannot be determined.
    /// </summary>
    internal const string PackageManagerUpdateInstructions = "If you installed the Aspire CLI through a package manager, use that package manager to update.";

    /// <summary>
    /// Update instructions for WinGet installations.
    /// </summary>
    internal const string WinGetUpdateInstructions = "winget upgrade Microsoft.Aspire";

    /// <summary>
    /// Update instructions for .NET global tool installations.
    /// </summary>
    internal const string DotNetToolUpdateInstructions = "dotnet tool update -g Aspire.Cli";

    /// <summary>
    /// Update instructions for script/direct-binary installations.
    /// </summary>
    internal const string SelfUpdateInstructions = "aspire update --self";

    public InstallationDetector(ILogger<InstallationDetector> logger)
        : this(logger, Environment.ProcessPath)
    {
    }

    /// <summary>
    /// Constructor that accepts a process path for testability.
    /// </summary>
    internal InstallationDetector(ILogger<InstallationDetector> logger, string? processPath)
    {
        _logger = logger;
        _processPath = processPath;
    }

    public InstallationInfo GetInstallationInfo()
    {
        if (_cachedInfo is not null)
        {
            return _cachedInfo;
        }

        _cachedInfo = DetectInstallation();
        return _cachedInfo;
    }

    private InstallationInfo DetectInstallation()
    {
        // Check if running as a dotnet tool first
        if (IsDotNetToolProcess(_processPath))
        {
            _logger.LogDebug("CLI is running as a .NET tool.");
            return new InstallationInfo(IsDotNetTool: true, SelfUpdateDisabled: false, UpdateInstructions: DotNetToolUpdateInstructions);
        }

        // Resolve symlinks once (critical for Homebrew on macOS where the binary is symlinked)
        var resolvedPath = ResolveSymlinks(_processPath);

        // Check for .aspire-update.json next to the resolved process path
        var config = TryLoadUpdateConfig(resolvedPath);
        if (config is not null)
        {
            if (config.SelfUpdateDisabled)
            {
                _logger.LogDebug("Self-update is disabled via {FileName}.", UpdateConfigFileName);
                return new InstallationInfo(IsDotNetTool: false, SelfUpdateDisabled: true, UpdateInstructions: config.UpdateInstructions);
            }

            _logger.LogDebug("{FileName} found but selfUpdateDisabled is false.", UpdateConfigFileName);
        }

        // Check if installed via WinGet (process under WinGet packages directory)
        if (!string.IsNullOrEmpty(resolvedPath) && IsWinGetInstall(resolvedPath))
        {
            _logger.LogDebug("CLI appears to be installed via WinGet (process path is under a WinGet packages directory).");
            return new InstallationInfo(IsDotNetTool: false, SelfUpdateDisabled: true, UpdateInstructions: WinGetUpdateInstructions);
        }

        // Default: script install or direct binary, self-update is available
        return new InstallationInfo(IsDotNetTool: false, SelfUpdateDisabled: false, UpdateInstructions: SelfUpdateInstructions);
    }

    private static bool IsDotNetToolProcess(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(processPath);
        return string.Equals(fileName, "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves symlinks for the given path. Returns the resolved path, or the original if not a symlink.
    /// </summary>
    private string? ResolveSymlinks(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        try
        {
            var linkTarget = File.ResolveLinkTarget(processPath, returnFinalTarget: true);
            if (linkTarget is not null)
            {
                _logger.LogDebug("Resolved symlink {ProcessPath} -> {ResolvedPath}", processPath, linkTarget.FullName);
                return linkTarget.FullName;
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Failed to resolve symlink for {ProcessPath}, using original path.", processPath);
        }

        return processPath;
    }

    /// <summary>
    /// Checks whether the resolved process path is under a known WinGet packages directory.
    /// WinGet installs portable packages to well-known directories:
    /// - User scope: %LOCALAPPDATA%\Microsoft\WinGet\Packages\
    /// - Machine scope: %PROGRAMFILES%\WinGet\Packages\
    /// Note: custom install paths (via --location or PortablePackageUserRoot) are not detected.
    /// </summary>
    internal static bool IsWinGetInstall(string resolvedPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(resolvedPath);

        var candidateDirs = new List<string>();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            candidateDirs.Add(Path.GetFullPath(Path.Combine(localAppData, "Microsoft", "WinGet", "Packages")));
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            candidateDirs.Add(Path.GetFullPath(Path.Combine(programFiles, "WinGet", "Packages")));
        }

        return candidateDirs.Any(dir => IsUnderDirectory(normalizedPath, dir));
    }

    /// <summary>
    /// Checks whether <paramref name="filePath"/> is under <paramref name="directoryPath"/> using a boundary-safe comparison.
    /// </summary>
    private static bool IsUnderDirectory(string filePath, string directoryPath)
    {
        // Ensure the directory path ends with a separator for boundary-safe comparison
        // (prevents "C:\...\Packages2\foo" from matching "C:\...\Packages")
        if (!directoryPath.EndsWith(Path.DirectorySeparatorChar))
        {
            directoryPath += Path.DirectorySeparatorChar;
        }

        return filePath.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase);
    }

    private AspireUpdateConfig? TryLoadUpdateConfig(string? resolvedPath)
    {
        if (string.IsNullOrEmpty(resolvedPath))
        {
            return null;
        }

        try
        {
            var directory = Path.GetDirectoryName(resolvedPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            var configPath = Path.Combine(directory, UpdateConfigFileName);
            if (!File.Exists(configPath))
            {
                _logger.LogDebug("{FileName} not found at {ConfigPath}.", UpdateConfigFileName, configPath);
                return null;
            }

            _logger.LogDebug("Found {FileName} at {ConfigPath}.", UpdateConfigFileName, configPath);

            var json = File.ReadAllText(configPath);
            var config = JsonSerializer.Deserialize(json, JsonSourceGenerationContext.Default.AspireUpdateConfig);

            if (config is null)
            {
                // Null deserialization result (e.g., "null" literal in JSON) — fail closed
                _logger.LogWarning("Failed to parse {FileName}: deserialized to null. Treating as self-update disabled.", UpdateConfigFileName);
                return new AspireUpdateConfig { SelfUpdateDisabled = true };
            }

            return config;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Malformed JSON, file read error, or permission error — fail closed (safer for package managers)
            _logger.LogWarning(ex, "Failed to read {FileName}. Treating as self-update disabled.", UpdateConfigFileName);
            return new AspireUpdateConfig { SelfUpdateDisabled = true };
        }
    }
}
