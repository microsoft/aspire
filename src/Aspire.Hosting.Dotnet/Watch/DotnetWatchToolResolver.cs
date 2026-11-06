// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
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
/// Package-backed polyglot AppHosts (prebuilt AppHost server) don't run the package's MSBuild targets, so they have no
/// metadata; they load this assembly straight from the restored package, so the payload is found next to it instead.
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

    /// <summary>
    /// Reads the shipped payload directory from <paramref name="appHostAssembly"/>'s metadata, or else from the package this
    /// assembly was loaded from (prebuilt polyglot AppHost servers).
    /// </summary>
    public static string? GetToolsDirectory(Assembly? appHostAssembly) =>
        appHostAssembly?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, ToolsDirectoryMetadataKey, StringComparison.OrdinalIgnoreCase))?.Value is { Length: > 0 } value
            ? value
            : GetPackageToolsDirectory(typeof(DotnetWatchToolResolver).Assembly.Location);

    /// <summary>Maps <c>&lt;package&gt;/lib/&lt;tfm&gt;/Aspire.Hosting.Dotnet.dll</c> to <c>&lt;package&gt;/tools/dotnet-watch/</c> when that exists.</summary>
    public static string? GetPackageToolsDirectory(string? hostingDotnetAssemblyPath)
    {
        if (string.IsNullOrEmpty(hostingDotnetAssemblyPath) || Path.GetDirectoryName(hostingDotnetAssemblyPath) is not { Length: > 0 } libDirectory)
        {
            return null;
        }

        var candidate = Path.GetFullPath(Path.Combine(libDirectory, "..", "..", "tools", "dotnet-watch"));
        return Directory.Exists(candidate) ? candidate + Path.DirectorySeparatorChar : null;
    }

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
        var path = Path.ChangeExtension(toolDll, ".runtimeconfig.json");
        if (!File.Exists(path))
        {
            throw new DistributedApplicationException(
                $"The hot reload tool '{toolDll}' has no '{Path.GetFileName(path)}', so it can't be started with 'dotnet exec'. " +
                "Restore and rebuild the AppHost so the Aspire.Hosting.Dotnet package is in place, or run without --watch.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object)
        {
            return new([], "Minor");
        }

        var frameworks = new List<RuntimeFramework>();
        if (options.TryGetProperty("framework", out var framework))
        {
            AddFramework(framework);
        }

        if (options.TryGetProperty("frameworks", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                AddFramework(item);
            }
        }

        var rollForward = options.TryGetProperty("rollForward", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : options.TryGetProperty("rollForwardOnNoCandidateFx", out var legacy) && legacy.ValueKind == JsonValueKind.Number && legacy.GetInt32() == 2
                ? "Major"
                : options.TryGetProperty("rollForwardOnNoCandidateFx", out legacy) && legacy.ValueKind == JsonValueKind.Number && legacy.GetInt32() == 0
                    ? "LatestPatch"
                    : "Minor";
        if (!string.IsNullOrEmpty(rollForwardOverride) && GetRollForwardRank(rollForwardOverride) > GetRollForwardRank(rollForward))
        {
            rollForward = rollForwardOverride;
        }

        return new(frameworks, rollForward);

        void AddFramework(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object &&
                element.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } frameworkName &&
                element.TryGetProperty("version", out var version) && version.GetString() is { Length: > 0 } frameworkVersion)
            {
                frameworks.Add(new(frameworkName, frameworkVersion));
            }
        }
    }

    /// <summary>Parses <c>dotnet --list-runtimes</c> lines such as <c>Microsoft.NETCore.App 10.0.14 [/usr/share/dotnet/shared/Microsoft.NETCore.App]</c>.</summary>
    internal static IReadOnlyList<RuntimeFramework> ParseInstalledFrameworks(string listRuntimesOutput)
    {
        var installed = new List<RuntimeFramework>();
        foreach (var line in listRuntimesOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                installed.Add(new(parts[0], parts[1]));
            }
        }

        return installed;
    }

    /// <summary>The required frameworks that no installed framework satisfies under the roll-forward policy.</summary>
    internal static IReadOnlyList<RuntimeFramework> FindMissingFrameworks(RuntimeRequirements requirements, IReadOnlyList<RuntimeFramework> installed)
    {
        var rank = GetRollForwardRank(requirements.RollForward);
        return requirements.Frameworks
            .Where(required => !TryParseVersion(required.Version, out var requiredVersion, out var requiredIsPrerelease) || !installed.Any(candidate =>
                string.Equals(candidate.Name, required.Name, StringComparison.OrdinalIgnoreCase) &&
                TryParseVersion(candidate.Version, out var version, out var prerelease) &&
                Satisfies(requiredVersion, requiredIsPrerelease, version, prerelease, rank)))
            .ToList();

        static bool Satisfies(Version required, bool requiredIsPrerelease, Version candidate, bool candidateIsPrerelease, int rank)
        {
            if (candidate < required)
            {
                return false;
            }

            if (candidate == required)
            {
                // A prerelease doesn't meet the release of the same version.
                return requiredIsPrerelease || !candidateIsPrerelease;
            }

            return rank switch
            {
                0 => false,
                1 => candidate.Major == required.Major && candidate.Minor == required.Minor,
                2 => candidate.Major == required.Major,
                _ => true,
            };
        }
    }

    // Disable < LatestPatch < Minor/LatestMinor < Major/LatestMajor, by what they accept. Unknown values are treated as
    // the most permissive so the check doesn't reject what the host might accept.
    private static int GetRollForwardRank(string rollForward) => rollForward.ToUpperInvariant() switch
    {
        "DISABLE" => 0,
        "LATESTPATCH" => 1,
        "MINOR" or "LATESTMINOR" => 2,
        _ => 3,
    };

    private static bool TryParseVersion(string value, out Version version, out bool prerelease)
    {
        var dash = value.IndexOfAny(['-', '+']);
        prerelease = dash >= 0 && value[dash] == '-';
        return Version.TryParse(dash >= 0 ? value[..dash] : value, out version!);
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
