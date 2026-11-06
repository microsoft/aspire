// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// Locates the <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> server that ships with this package.
/// </summary>
/// <remarks>
/// The tool is repacked into the Aspire.Hosting.Dotnet package under <c>tools/dotnet-watch/&lt;SDK major&gt;/</c>
/// (see <c>Aspire.Hosting.Dotnet.csproj</c>), and the package's buildTransitive targets stamp that directory on the
/// AppHost assembly as <see cref="ToolsDirectoryMetadataKey"/> metadata, the way DCP's location reaches the AppHost.
/// The watcher loads MSBuild from the selected SDK, so the payload's major version must match the SDK's. There is no
/// download and no fallback: a missing payload is an error that says how to fix it.
/// TODO(spike): package-backed polyglot AppHosts (prebuilt AppHost server) don't run the package's MSBuild targets and
/// so have no metadata; they need <see cref="ToolPathConfigurationKey"/> until the CLI bundle carries the tool.
/// </remarks>
internal static class DotnetWatchToolResolver
{
    public const string PackageId = "Microsoft.DotNet.HotReload.Watch.Aspire";
    public const string ToolDllName = PackageId + ".dll";

    /// <summary>AppHost assembly metadata key holding the directory that contains one payload per SDK major.</summary>
    public const string ToolsDirectoryMetadataKey = "aspiredotnetwatchtoolsdir";

    /// <summary>Explicitly selects a tool payload directory or DLL path instead of the shipped payload.</summary>
    public const string ToolPathConfigurationKey = "ASPIRE_DOTNET_WATCH_TOOL_PATH";

    /// <summary>Returns the major version of an SDK version string such as <c>11.0.100-rc.2.26504.105</c>.</summary>
    public static int GetSdkMajor(string sdkVersion)
    {
        var dot = sdkVersion.IndexOf('.');
        if (dot <= 0 || !int.TryParse(sdkVersion.AsSpan(0, dot), out var major))
        {
            throw new DistributedApplicationException($"Unrecognized .NET SDK version '{sdkVersion}'.");
        }

        return major;
    }

    /// <summary>Reads the shipped payload directory from the AppHost assembly's metadata.</summary>
    public static string? GetToolsDirectory(DistributedApplicationOptions options) =>
        GetToolsDirectory(string.IsNullOrEmpty(options.AssemblyName) ? Assembly.GetEntryAssembly() : Assembly.Load(options.AssemblyName));

    /// <summary>Reads the shipped payload directory from <paramref name="appHostAssembly"/>'s metadata.</summary>
    public static string? GetToolsDirectory(Assembly? appHostAssembly) =>
        appHostAssembly?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, ToolsDirectoryMetadataKey, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } value
            ? value
            : null;

    /// <summary>
    /// Returns the watch tool DLL for <paramref name="sdkVersion"/>: <paramref name="overridePath"/> when set, otherwise the
    /// payload for the SDK's major version under <paramref name="toolsDirectory"/>.
    /// </summary>
    /// <exception cref="DistributedApplicationException">The tool isn't there; the message says how to fix it.</exception>
    public static string ResolveToolPath(string? overridePath, string? toolsDirectory, string sdkVersion, ILogger logger)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            var dll = Directory.Exists(overridePath) ? Path.Combine(overridePath, ToolDllName) : overridePath;
            if (!File.Exists(dll))
            {
                throw new DistributedApplicationException($"{ToolPathConfigurationKey} points to '{overridePath}', but '{dll}' does not exist.");
            }

            logger.LogInformation("Using watch tool from {ToolPathConfigurationKey}: {Path}", ToolPathConfigurationKey, dll);
            return dll;
        }

        if (string.IsNullOrEmpty(toolsDirectory))
        {
            throw new DistributedApplicationException(
                $"The hot reload tool isn't available to this AppHost: it ships in the Aspire.Hosting.Dotnet package, but the AppHost " +
                $"assembly has no '{ToolsDirectoryMetadataKey}' metadata. Reference Aspire.Hosting.Dotnet from the AppHost project and " +
                $"rebuild it, set {ToolPathConfigurationKey} to the tool's folder, or run without --watch.");
        }

        var major = GetSdkMajor(sdkVersion);
        var toolDll = Path.Combine(toolsDirectory, major.ToString(System.Globalization.CultureInfo.InvariantCulture), ToolDllName);
        if (File.Exists(toolDll))
        {
            logger.LogInformation("Using watch tool for .NET {Major} SDKs: {Path}", major, toolDll);
            return toolDll;
        }

        var shipped = Directory.Exists(toolsDirectory)
            ? Directory.GetDirectories(toolsDirectory)
                .Where(d => File.Exists(Path.Combine(d, ToolDllName)))
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

        throw new DistributedApplicationException(shipped.Length == 0
            ? $"The hot reload tool is missing: '{toolDll}' does not exist. Restore and rebuild the AppHost so the Aspire.Hosting.Dotnet " +
              "package is in place, or run without --watch."
            : $"Hot reload needs the watch tool for .NET {major} SDKs (SDK {sdkVersion} is selected), but this Aspire.Hosting.Dotnet " +
              $"ships it only for .NET {string.Join(", ", shipped)} SDKs. Select one of those SDKs in global.json, use an " +
              "Aspire.Hosting.Dotnet version that supports this SDK, or run without --watch.");
    }

    /// <summary>
    /// Resolves the SDK directory selected by <c>global.json</c> for <paramref name="projectDirectory"/>.
    /// </summary>
    public static async Task<(string SdkDirectory, string Muxer, string SdkVersion)> ResolveSdkAsync(
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        // `dotnet --version` honors global.json from the working directory; `--list-sdks` gives each SDK's base path.
        var version = (await RunDotnetAsync(projectDirectory, "--version", cancellationToken).ConfigureAwait(false)).Trim();
        var sdks = await RunDotnetAsync(projectDirectory, "--list-sdks", cancellationToken).ConfigureAwait(false);
        foreach (var line in sdks.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Format: "10.0.403 [/usr/share/dotnet/sdk]"
            var space = line.IndexOf(' ');
            if (space > 0 && line[..space] == version)
            {
                var basePath = line[(space + 1)..].Trim('[', ']');
                var sdkDirectory = Path.Combine(basePath, version);
                var dotnetRoot = Path.GetDirectoryName(basePath)!;
                var muxer = Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                return (sdkDirectory, muxer, version);
            }
        }

        throw new DistributedApplicationException($"Could not locate SDK '{version}' selected for '{projectDirectory}'.");
    }

    /// <summary>
    /// The SDK version <paramref name="muxer"/> selects in <paramref name="workingDirectory"/> (honoring its
    /// <c>global.json</c> scope), or <see langword="null"/> if none can be selected there.
    /// </summary>
    public static async Task<string?> TryGetSdkVersionAsync(string muxer, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            return (await RunDotnetAsync(workingDirectory, "--version", cancellationToken, muxer).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is DistributedApplicationException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return null;
        }
    }

    private static async Task<string> RunDotnetAsync(string workingDirectory, string argument, CancellationToken cancellationToken, string muxer = "dotnet")
    {
        using var process = Process.Start(new ProcessStartInfo(muxer, argument)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start dotnet.");

        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(error) ? "" : $" {error.Trim()}";
            throw new DistributedApplicationException($"'dotnet {argument}' failed in '{workingDirectory}' with exit code {process.ExitCode}.{detail}");
        }

        return output;
    }
}
