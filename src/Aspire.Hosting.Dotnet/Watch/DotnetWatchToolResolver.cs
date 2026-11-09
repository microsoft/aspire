// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json;
using Aspire.Hosting.Dcp.Process;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// Locates the <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> server that ships with this package.
/// </summary>
/// <remarks>
/// The tool is repacked into the Aspire.Hosting.Dotnet package under <c>tools/dotnet-watch/</c> (see
/// <c>Aspire.Hosting.Dotnet.csproj</c>), and the package's buildTransitive targets stamp that directory on the AppHost
/// assembly as <see cref="ToolsDirectoryMetadataKey"/> metadata, the way DCP's location reaches the AppHost.
/// The watcher loads MSBuild from the selected SDK, so it only works with SDKs of its own major version: watch mode
/// supports .NET 10 SDKs only. There is no download and no fallback: an unsupported SDK or a missing tool is an error
/// that says how to fix it.
/// The location is chosen by how this assembly was loaded, never by probing one place and then another: when
/// Aspire.Hosting.Dotnet is loaded straight from its restored NuGet package (package-backed polyglot AppHosts on the
/// prebuilt AppHost server, which never run the package's MSBuild targets), the payload is that package's
/// <c>tools/</c> folder; otherwise (C# AppHosts, repo-mode AppHost servers) the AppHost metadata is required.
/// The watch host (<c>Aspire.Hosting.Dotnet.WatchHost.dll</c>, which hosts the watcher and proxies each service
/// replica) ships the same way under <c>tools/dotnet-watch-host/</c>, stamped as
/// <see cref="WatchHostDirectoryMetadataKey"/>.
/// </remarks>
internal static class DotnetWatchToolResolver
{
    public const string PackageId = "Microsoft.DotNet.HotReload.Watch.Aspire";
    public const string ToolDllName = PackageId + ".dll";

    /// <summary>The only SDK major version the shipped tool supports.</summary>
    public const int SupportedSdkMajor = 10;

    /// <summary>AppHost assembly metadata key holding the directory that contains the shipped tool.</summary>
    public const string ToolsDirectoryMetadataKey = "aspiredotnetwatchtoolsdir";

    /// <summary>Explicitly selects a tool directory or DLL path instead of the shipped tool.</summary>
    public const string ToolPathConfigurationKey = "ASPIRE_DOTNET_WATCH_TOOL_PATH";

    /// <summary>Returns the major version of an SDK version string such as <c>10.0.403</c>.</summary>
    public static int GetSdkMajor(string sdkVersion)
    {
        var dot = sdkVersion.IndexOf('.');
        if (dot <= 0 || !int.TryParse(sdkVersion.AsSpan(0, dot), out var major))
        {
            throw new DistributedApplicationException($"Unrecognized .NET SDK version '{sdkVersion}'.");
        }

        return major;
    }

    /// <summary>Returns the shipped tool directory for <paramref name="appHostAssembly"/> (see <see cref="GetPayloadDirectory"/>).</summary>
    public static string? GetToolsDirectory(Assembly? appHostAssembly) =>
        GetPayloadDirectory(appHostAssembly, ToolsDirectoryMetadataKey, ToolsPackageFolder, typeof(DotnetWatchToolResolver).Assembly.Location);

    /// <summary>Package folder (under <c>tools/</c>) of the watch tool.</summary>
    public const string ToolsPackageFolder = "dotnet-watch";

    /// <summary>
    /// Returns the directory of a payload shipped in the Aspire.Hosting.Dotnet package. Exactly one source applies:
    /// when <paramref name="hostingDotnetAssemblyPath"/> is inside its restored NuGet package it's that package's
    /// <c>tools/&lt;packageFolder&gt;/</c>; otherwise it's <paramref name="appHostAssembly"/>'s
    /// <paramref name="metadataKey"/> metadata, or <see langword="null"/> when the AppHost has none. Existence isn't
    /// checked here; the resolvers report a missing payload with the path they expected.
    /// </summary>
    public static string? GetPayloadDirectory(Assembly? appHostAssembly, string metadataKey, string packageFolder, string? hostingDotnetAssemblyPath)
    {
        if (GetPackageRoot(hostingDotnetAssemblyPath) is { } packageRoot)
        {
            return Path.Combine(packageRoot, "tools", packageFolder) + Path.DirectorySeparatorChar;
        }

        return appHostAssembly?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, metadataKey, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } value
            ? value
            : null;
    }

    /// <summary>
    /// Returns the package root when <paramref name="hostingDotnetAssemblyPath"/> is
    /// <c>&lt;package&gt;/lib/&lt;tfm&gt;/Aspire.Hosting.Dotnet.dll</c> of a restored Aspire.Hosting.Dotnet package (its
    /// root holds <c>aspire.hosting.dotnet.nuspec</c>), otherwise <see langword="null"/>.
    /// </summary>
    internal static string? GetPackageRoot(string? hostingDotnetAssemblyPath)
    {
        if (string.IsNullOrEmpty(hostingDotnetAssemblyPath) ||
            Path.GetDirectoryName(Path.GetFullPath(hostingDotnetAssemblyPath)) is not { Length: > 0 } tfmDirectory ||
            Path.GetDirectoryName(tfmDirectory) is not { Length: > 0 } libDirectory ||
            !string.Equals(Path.GetFileName(libDirectory), "lib", StringComparison.OrdinalIgnoreCase) ||
            Path.GetDirectoryName(libDirectory) is not { Length: > 0 } packageRoot)
        {
            return null;
        }

        return File.Exists(Path.Combine(packageRoot, PackageNuspecFileName)) ? packageRoot : null;
    }

    /// <summary>The nuspec NuGet writes into the root of a restored Aspire.Hosting.Dotnet package.</summary>
    internal const string PackageNuspecFileName = "aspire.hosting.dotnet.nuspec";

    /// <summary>AppHost assembly metadata key holding the directory that contains the watch host.</summary>
    public const string WatchHostDirectoryMetadataKey = "aspiredotnetwatchhostdir";

    /// <summary>Package folder (under <c>tools/</c>) of the watch host.</summary>
    public const string WatchHostPackageFolder = "dotnet-watch-host";

    /// <summary>Returns the watch host directory for <paramref name="appHostAssembly"/> (see <see cref="GetPayloadDirectory"/>).</summary>
    public static string? GetWatchHostDirectory(Assembly? appHostAssembly) =>
        GetPayloadDirectory(appHostAssembly, WatchHostDirectoryMetadataKey, WatchHostPackageFolder, typeof(DotnetWatchToolResolver).Assembly.Location);

    /// <summary>Returns the watch host (<c>Aspire.Hosting.Dotnet.WatchHost.dll</c>) under <paramref name="watchHostDirectory"/>.</summary>
    /// <exception cref="DistributedApplicationException">The watch host isn't there; the message says how to fix it.</exception>
    public static string ResolveWatchHostPath(string? watchHostDirectory)
    {
        if (string.IsNullOrEmpty(watchHostDirectory))
        {
            throw new DistributedApplicationException(
                $"The hot reload host isn't available to this AppHost: it ships in the Aspire.Hosting.Dotnet package, but the AppHost " +
                $"assembly has no '{WatchHostDirectoryMetadataKey}' metadata. Reference Aspire.Hosting.Dotnet from the AppHost project and " +
                "rebuild it, or run without --watch.");
        }

        var path = Path.Combine(watchHostDirectory, Aspire.Shared.DotnetWatch.DotnetWatchHostProtocol.AssemblyFileName);
        if (!File.Exists(path))
        {
            throw new DistributedApplicationException(
                $"The hot reload host is missing: '{path}' does not exist. Restore and rebuild the AppHost so the Aspire.Hosting.Dotnet " +
                "package is in place, or run without --watch.");
        }

        return path;
    }

    /// <summary>
    /// Returns the watch tool DLL for <paramref name="sdkVersion"/>: <paramref name="overridePath"/> when set, otherwise the
    /// shipped tool under <paramref name="toolsDirectory"/>.
    /// </summary>
    /// <exception cref="DistributedApplicationException">The SDK isn't supported or the tool isn't there; the message says how to fix it.</exception>
    public static string ResolveToolPath(string? overridePath, string? toolsDirectory, string sdkVersion, ILogger logger)
    {
        if (!string.IsNullOrEmpty(overridePath))
        {
            var dll = Directory.Exists(overridePath) ? Path.Combine(overridePath, ToolDllName) : overridePath;
            if (!File.Exists(dll))
            {
                throw new DistributedApplicationException($"{ToolPathConfigurationKey} points to '{overridePath}', but '{dll}' does not exist.");
            }

            logger.LogDebug("Using watch tool from {ToolPathConfigurationKey}: {Path}", ToolPathConfigurationKey, dll);
            return dll;
        }

        if (GetSdkMajor(sdkVersion) != SupportedSdkMajor)
        {
            throw new DistributedApplicationException(
                $"Hot reload supports .NET {SupportedSdkMajor} SDKs only, but the watched projects select SDK {sdkVersion}. " +
                $"Select a .NET {SupportedSdkMajor} SDK in their global.json, or run without --watch.");
        }

        if (string.IsNullOrEmpty(toolsDirectory))
        {
            throw new DistributedApplicationException(
                $"The hot reload tool isn't available to this AppHost: it ships in the Aspire.Hosting.Dotnet package, but the AppHost " +
                $"assembly has no '{ToolsDirectoryMetadataKey}' metadata. Reference Aspire.Hosting.Dotnet from the AppHost project and " +
                $"rebuild it, set {ToolPathConfigurationKey} to the tool's folder, or run without --watch.");
        }

        var toolDll = Path.Combine(toolsDirectory, ToolDllName);
        if (!File.Exists(toolDll))
        {
            throw new DistributedApplicationException(
                $"The hot reload tool is missing: '{toolDll}' does not exist. Restore and rebuild the AppHost so the Aspire.Hosting.Dotnet " +
                "package is in place, or run without --watch.");
        }

        logger.LogDebug("Using watch tool: {Path}", toolDll);
        return toolDll;
    }

    /// <summary>The roll-forward environment variable, which the watcher (started with <c>dotnet exec</c>) inherits.</summary>
    public const string RollForwardEnvironmentVariable = "DOTNET_ROLL_FORWARD";

    /// <summary>
    /// Checks that <paramref name="muxer"/> has the shared frameworks <paramref name="toolDll"/> runs on. The payload is
    /// chosen by SDK major, but each payload is built against a specific runtime (for example a 10.0.4xx SDK tool needs
    /// that band's 10.0 runtime patch), which an older install of the same major doesn't have. Without this check the
    /// watcher fails to start with a host error buried in the server log.
    /// </summary>
    /// <exception cref="DistributedApplicationException">A framework is missing; the message names it and how to fix it.</exception>
    public static async Task EnsureRuntimeAvailableAsync(string toolDll, string muxer, string sdkVersion, CancellationToken cancellationToken)
    {
        var requirements = ReadRuntimeRequirements(toolDll, Environment.GetEnvironmentVariable(RollForwardEnvironmentVariable));
        if (requirements.Frameworks.Count == 0)
        {
            return;
        }

        var runtimes = await RunDotnetAsync(Path.GetDirectoryName(toolDll)!, "--list-runtimes", cancellationToken, muxer).ConfigureAwait(false);
        var missing = FindMissingFrameworks(requirements, ParseInstalledFrameworks(runtimes));
        if (missing.Count == 0)
        {
            return;
        }

        var needed = string.Join(", ", missing.Select(f => $"{f.Name} {f.Version}"));
        throw new DistributedApplicationException(
            $"The hot reload tool for .NET {GetSdkMajor(sdkVersion)} SDKs ('{toolDll}') needs {needed} " +
            $"(roll forward: {requirements.RollForward}), which '{muxer}' doesn't have (SDK {sdkVersion} is selected). " +
            "Install that .NET runtime (or a newer SDK of the same major, which includes it) into the same .NET installation, " +
            "or run without --watch.");
    }

    internal sealed record RuntimeFramework(string Name, string Version);

    internal sealed record RuntimeRequirements(IReadOnlyList<RuntimeFramework> Frameworks, string RollForward);

    /// <summary>
    /// Reads the shared frameworks from <c>&lt;tool&gt;.runtimeconfig.json</c>. The effective roll-forward policy is the
    /// most permissive of the file's and <paramref name="rollForwardOverride"/>, so the check never rejects a runtime
    /// the host would accept.
    /// </summary>
    internal static RuntimeRequirements ReadRuntimeRequirements(string toolDll, string? rollForwardOverride)
    {
        try
        {
            var requirements = DotnetWatchPayload.ReadRuntimeRequirements(toolDll, rollForwardOverride);
            return new([.. requirements.Frameworks.Select(framework => new RuntimeFramework(framework.Name, framework.Version))],
                requirements.RollForward);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
            ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DistributedApplicationException(
                $"{exception.Message} Restore and rebuild the AppHost or run without --watch.", exception);
        }
    }

    /// <summary>Parses <c>dotnet --list-runtimes</c> lines such as <c>Microsoft.NETCore.App 10.0.14 [/usr/share/dotnet/shared/Microsoft.NETCore.App]</c>.</summary>
    internal static IReadOnlyList<RuntimeFramework> ParseInstalledFrameworks(string listRuntimesOutput)
    {
        return [.. DotnetWatchPayload.ParseInstalledFrameworks(listRuntimesOutput)
            .Select(framework => new RuntimeFramework(framework.Name, framework.Version))];
    }

    /// <summary>The required frameworks that no installed framework satisfies under the roll-forward policy.</summary>
    internal static IReadOnlyList<RuntimeFramework> FindMissingFrameworks(RuntimeRequirements requirements, IReadOnlyList<RuntimeFramework> installed)
    {
        var shared = new DotnetWatchPayload.RuntimeRequirements(
            [.. requirements.Frameworks.Select(framework => new DotnetWatchPayload.Framework(framework.Name, framework.Version))],
            requirements.RollForward);
        return [.. DotnetWatchPayload.FindMissingFrameworks(shared,
            [.. installed.Select(framework => new DotnetWatchPayload.Framework(framework.Name, framework.Version))])
            .Select(framework => new RuntimeFramework(framework.Name, framework.Version))];
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

    private static async Task<string> RunDotnetAsync(string workingDirectory, string argument, CancellationToken cancellationToken, string muxer = "dotnet")
    {
        var result = await ProcessUtil.RunAndCaptureTextAsync(muxer, [argument], workingDirectory, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.StandardError) ? "" : $" {result.StandardError.Trim()}";
            throw new DistributedApplicationException($"'dotnet {argument}' failed in '{workingDirectory}' with exit code {result.ExitCode}.{detail}");
        }

        return result.StandardOutput;
    }
}
