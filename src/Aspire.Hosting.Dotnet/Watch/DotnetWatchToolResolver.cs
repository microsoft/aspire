// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dotnet.Watch;

/// <summary>
/// SPIKE: runtime-only acquisition of the <c>Microsoft.DotNet.HotReload.Watch.Aspire</c> tool (plan Session 4).
/// </summary>
/// <remarks>
/// The watcher loads MSBuild from the selected SDK, so its target runtime must be able to load that SDK. The spike
/// therefore selects the tool version from the resolved SDK: SDK 10.x uses the plan's pinned 10.0.403, newer SDKs use
/// the package published with exactly that SDK version (e.g. <c>11.0.100-rc.2.26504.105</c>).
/// Shortcuts that a real implementation must replace:
/// <list type="bullet">
/// <item>TODO(spike): the package URL is a hardcoded dotnet-public flat-container URL; honor configured/approved NuGet sources.</item>
/// <item>TODO(spike): no signature/hash verification of the downloaded package.</item>
/// <item>TODO(spike): no CLI-callable prelaunch "ensure" entry point (needed for Session 8 AppHost watch).</item>
/// <item>TODO(spike): in-process lock only; concurrent AppHosts acquiring the same version can race.</item>
/// </list>
/// </remarks>
internal static class DotnetWatchToolResolver
{
    public const string PackageId = "Microsoft.DotNet.HotReload.Watch.Aspire";
    public const string PinnedVersion = "10.0.403";
    public const string ToolDllName = PackageId + ".dll";

    /// <summary>Overrides acquisition with an existing tool payload directory or DLL path.</summary>
    public const string ToolPathConfigurationKey = "ASPIRE_DOTNET_WATCH_TOOL_PATH";

    /// <summary>Overrides the package version that is acquired.</summary>
    public const string ToolVersionConfigurationKey = "ASPIRE_DOTNET_WATCH_TOOL_VERSION";

    private const string FeedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/flat2/";

    private static readonly SemaphoreSlim s_lock = new(1, 1);

    /// <summary>Selects the watcher package version that can load <paramref name="sdkVersion"/>.</summary>
    public static string SelectToolVersion(string sdkVersion, string? overrideVersion)
    {
        if (!string.IsNullOrEmpty(overrideVersion))
        {
            return overrideVersion;
        }

        var dash = sdkVersion.IndexOf('-');
        var numeric = Version.Parse(dash < 0 ? sdkVersion : sdkVersion[..dash]);

        // The watcher package ships with every SDK build under the SDK's own version. 10.x keeps the plan's pin.
        return numeric.Major <= 10 ? PinnedVersion : sdkVersion;
    }

    public static async Task<string> EnsureToolAsync(string? overridePath, string version, ILogger logger, CancellationToken cancellationToken)
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

        var cacheRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspire", "dotnet-watch", version);
        var toolDll = Path.Combine(cacheRoot, ToolDllName);
        var marker = Path.Combine(cacheRoot, ".complete");

        await s_lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(marker) && File.Exists(toolDll))
            {
                logger.LogInformation("Using cached watch tool {PackageId} {Version} from {Path}", PackageId, version, cacheRoot);
                return toolDll;
            }

            var lower = version.ToLowerInvariant();
            var packageUrl = $"{FeedUrl}{PackageId.ToLowerInvariant()}/{lower}/{PackageId.ToLowerInvariant()}.{lower}.nupkg";
            logger.LogInformation("Acquiring watch tool {PackageId} {Version} from {Url}", PackageId, version, packageUrl);
            var stopwatch = Stopwatch.StartNew();

            var staging = cacheRoot + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
                using (var buffer = new MemoryStream())
                {
                    using var response = await http.GetAsync(packageUrl, cancellationToken).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new DistributedApplicationException(
                            $"Could not download {PackageId} {version} ({(int)response.StatusCode}) from {packageUrl}. " +
                            $"Set {ToolVersionConfigurationKey} to a published version or {ToolPathConfigurationKey} to a local copy.");
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                    buffer.Write(bytes);
                    buffer.Position = 0;
                    using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

                    // The payload lives under tools/<tfm>/any/ (net10.0 for 10.x, net11.0 for 11.x).
                    var payloadPrefix = archive.Entries
                        .Select(e => e.FullName)
                        .FirstOrDefault(n => n.StartsWith("tools/", StringComparison.Ordinal) && n.EndsWith("/any/" + ToolDllName, StringComparison.Ordinal))
                        ?[..^ToolDllName.Length]
                        ?? throw new DistributedApplicationException($"Package {PackageId} {version} does not contain tools/<tfm>/any/{ToolDllName}.");

                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.FullName.StartsWith(payloadPrefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/'))
                        {
                            continue;
                        }

                        var relative = entry.FullName[payloadPrefix.Length..];
                        var destination = Path.GetFullPath(Path.Combine(staging, relative));
                        if (!destination.StartsWith(Path.GetFullPath(staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        {
                            throw new InvalidDataException($"Unexpected package entry '{entry.FullName}'.");
                        }

                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        entry.ExtractToFile(destination, overwrite: true);
                    }
                }

                File.WriteAllText(Path.Combine(staging, ".complete"), version);
                if (Directory.Exists(cacheRoot))
                {
                    Directory.Delete(cacheRoot, recursive: true);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(cacheRoot)!);
                Directory.Move(staging, cacheRoot);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }

            logger.LogInformation("Acquired watch tool in {Elapsed:0.0}s: {Path}", stopwatch.Elapsed.TotalSeconds, toolDll);
            return toolDll;
        }
        finally
        {
            s_lock.Release();
        }
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

    private static async Task<string> RunDotnetAsync(string workingDirectory, string argument, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo("dotnet", argument)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start dotnet.");

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new DistributedApplicationException($"'dotnet {argument}' failed in '{workingDirectory}' with exit code {process.ExitCode}.");
        }

        return output;
    }
}
