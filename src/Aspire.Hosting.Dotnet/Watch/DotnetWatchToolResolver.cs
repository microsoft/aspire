// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
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
/// <item>Integrity: the package comes from nuget.org and its SHA-512 is checked against a hash pinned in this file
/// (<see cref="PinnedVersion"/>) or, for SDK-matched versions, the hash nuget.org's catalog publishes for that version.
/// Versions that aren't on nuget.org (daily SDK builds) fall back to the dotnet-public feed without a hash check and
/// log a warning.</item>
/// <item>TODO(spike): honor configured/approved NuGet sources (and their credentials) instead of fixed URLs, and verify
/// the package's repository signature rather than relying on the HTTPS source's hash.</item>
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

    private const string NuGetOrgFlatContainerUrl = "https://api.nuget.org/v3-flatcontainer/";
    private const string NuGetOrgRegistrationUrl = "https://api.nuget.org/v3/registration5-gz-semver2/";
    private const string DotnetPublicFeedUrl = "https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/flat2/";

    /// <summary>SHA-512 (hex) of known package versions, checked without a catalog lookup.</summary>
    internal static readonly IReadOnlyDictionary<string, string> s_pinnedSha512 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [PinnedVersion] = "6b6d34443b0d02cec987a197945d63b8cbed5077840a5dffbe460a2cd46e4ae531b9028ad6791f847601762698bb589ea8f19c2e8e5156d6ea3c94bb886df1df",
    };

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
            var id = PackageId.ToLowerInvariant();
            var stopwatch = Stopwatch.StartNew();

            var staging = cacheRoot + ".staging-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                using (var http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromMinutes(5) })
                using (var buffer = new MemoryStream())
                {
                    byte[] bytes;
                    try
                    {
                        var expectedSha512 = s_pinnedSha512.GetValueOrDefault(version) ?? await GetNuGetOrgSha512Async(http, id, lower, cancellationToken).ConfigureAwait(false);
                        var packageUrl = expectedSha512 is not null
                            ? $"{NuGetOrgFlatContainerUrl}{id}/{lower}/{id}.{lower}.nupkg"
                            : $"{DotnetPublicFeedUrl}{id}/{lower}/{id}.{lower}.nupkg";
                        logger.LogInformation("Acquiring watch tool {PackageId} {Version} from {Url}", PackageId, version, packageUrl);

                        using var response = await http.GetAsync(packageUrl, cancellationToken).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                        {
                            throw new DistributedApplicationException(
                                $"Couldn't download the hot reload tool {PackageId} {version} (HTTP {(int)response.StatusCode} from {packageUrl}). " +
                                $"Set {ToolVersionConfigurationKey} to a published version or {ToolPathConfigurationKey} to a local copy, or run without --watch.");
                        }

                        bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                        if (expectedSha512 is null)
                        {
                            logger.LogWarning(
                                "{PackageId} {Version} isn't on nuget.org, so its hash couldn't be verified; downloaded it from the dotnet-public feed.",
                                PackageId, version);
                        }
                        else if (!string.Equals(Convert.ToHexStringLower(SHA512.HashData(bytes)), expectedSha512, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new DistributedApplicationException(
                                $"The downloaded hot reload tool {PackageId} {version} doesn't match its published SHA-512 hash, so it wasn't used. " +
                                "Try again; if it keeps failing, check for a proxy that rewrites downloads, or set " +
                                $"{ToolPathConfigurationKey} to a trusted local copy.");
                        }
                    }
                    catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                    {
                        throw new DistributedApplicationException(
                            $"Couldn't download the hot reload tool {PackageId} {version} ({ex.Message.TrimEnd('.')}). " +
                            "It's downloaded once per SDK version and cached under ~/.aspire/dotnet-watch. Check your network connection and " +
                            $"try again, set {ToolPathConfigurationKey} to a local copy of the package's tools/<tfm>/any folder, or run without --watch.",
                            ex);
                    }

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
    /// Returns the SHA-512 (hex) that nuget.org's catalog publishes for the package version, or <see langword="null"/>
    /// when the version isn't on nuget.org.
    /// </summary>
    private static async Task<string?> GetNuGetOrgSha512Async(HttpClient http, string id, string version, CancellationToken cancellationToken)
    {
        using var leafResponse = await http.GetAsync($"{NuGetOrgRegistrationUrl}{id}/{version}.json", cancellationToken).ConfigureAwait(false);
        if (leafResponse.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        leafResponse.EnsureSuccessStatusCode();
        using var leaf = JsonDocument.Parse(await leafResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var catalogUrl = leaf.RootElement.GetProperty("catalogEntry").GetString()
            ?? throw new InvalidDataException($"nuget.org has no catalog entry for {id} {version}.");

        using var catalogResponse = await http.GetAsync(catalogUrl, cancellationToken).ConfigureAwait(false);
        catalogResponse.EnsureSuccessStatusCode();
        using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var root = catalog.RootElement;
        if (!string.Equals(root.GetProperty("packageHashAlgorithm").GetString(), "SHA512", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unexpected package hash algorithm for {id} {version}.");
        }

        return Convert.ToHexStringLower(Convert.FromBase64String(root.GetProperty("packageHash").GetString()!));
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

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new DistributedApplicationException($"'dotnet {argument}' failed in '{workingDirectory}' with exit code {process.ExitCode}.");
        }

        return output;
    }
}
