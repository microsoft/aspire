// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aspire.Shared;

namespace Aspire.Hosting.Dotnet.Watch;

internal sealed record DotnetWatchToolInstallation(string DllPath, string Version, int RuntimeMajor);

internal sealed class DotnetWatchToolException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal interface IDotnetWatchToolInstaller
{
    Task InstallAsync(string version, string directory, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class DotnetWatchToolManager(
    string cacheDirectory,
    string defaultVersion,
    IDotnetWatchToolInstaller installer,
    Action<string> log)
{
    internal const string PackageId = "Microsoft.DotNet.HotReload.Watch.Aspire";
    internal const string PathVariable = "ASPIRE_DOTNET_WATCH_TOOL_PATH";
    internal const string VersionVariable = "ASPIRE_DOTNET_WATCH_TOOL_VERSION";
    private const int CacheFormat = 1;
    private const string ManifestName = "installation.json";

    internal static string DefaultVersion =>
        typeof(DotnetWatchToolManager).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "DotnetWatchToolVersion").Value!;

    public static Task<DotnetWatchToolInstallation> EnsureFromEnvironmentAsync(
        string appHostDirectory,
        string dotnetPath,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        var manager = new DotnetWatchToolManager(
            Path.Combine(AspireHomeDirectory.GetDefault(), "tools", "dotnet-watch"),
            DefaultVersion,
            new DotnetWatchToolInstaller(dotnetPath),
            log);
        return manager.EnsureAsync(
            appHostDirectory,
            Environment.GetEnvironmentVariable(PathVariable),
            Environment.GetEnvironmentVariable(VersionVariable),
            cancellationToken);
    }

    public async Task<DotnetWatchToolInstallation> EnsureAsync(
        string appHostDirectory,
        string? explicitPath,
        string? explicitVersion,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrEmpty(explicitPath))
        {
            var path = Path.GetFullPath(explicitPath, appHostDirectory);
            return ValidatePayload(FindEntryPoint(path), "custom");
        }

        var version = string.IsNullOrEmpty(explicitVersion) ? defaultVersion : explicitVersion.Trim();
        // Accept exact NuGet versions, e.g. 10.0.403 or 11.0.100-rc.2.26504.105, never ranges or paths.
        if (!Regex.IsMatch(version, @"\A[0-9]+(?:\.[0-9]+){1,3}(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z", RegexOptions.CultureInvariant))
        {
            throw new DotnetWatchToolException($"{VersionVariable} must specify an exact package version, not a range or path.");
        }

        var installationDirectory = Path.Combine(
            Path.GetFullPath(cacheDirectory), $"v{CacheFormat}", RuntimeInformation.RuntimeIdentifier, version.ToLowerInvariant());

        // A complete hit never starts dotnet or consults NuGet. In particular, do not use
        // `dotnet tool update`, package search, or the upstream tool's broken --help as a probe.
        if (TryReadInstallation(installationDirectory, version) is { } cached)
        {
            return cached;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(installationDirectory)!);
        using var installationLock = await FileLock.AcquireAsync(
            installationDirectory + ".lock", cancellationToken).ConfigureAwait(false);
        if (TryReadInstallation(installationDirectory, version) is { } completedByAnotherCaller)
        {
            return completedByAnotherCaller;
        }

        if (Directory.Exists(installationDirectory))
        {
            // Do not repair in place: another AppHost can still be using files from this entry.
            throw new DotnetWatchToolException(
                $"The watch tool cache entry '{installationDirectory}' is incomplete or incompatible. " +
                "Stop runs using this installation and remove this entry before retrying.");
        }

        // Atomic publication requires staging on the cache filesystem, which may differ from
        // the OS temp filesystem. The private Aspire cache parent owns this unique directory.
        var staging = Directory.CreateDirectory(
            Path.Combine(Path.GetDirectoryName(installationDirectory)!, $".install-{Guid.NewGuid():N}"));
        try
        {
            log($"Acquiring experimental .NET watch tool {version}.");
            await installer.InstallAsync(version, staging.FullName, appHostDirectory, cancellationToken).ConfigureAwait(false);
            var installation = ValidatePayload(FindEntryPoint(staging.FullName), version);
            var entryPoint = Path.GetRelativePath(staging.FullName, installation.DllPath);
            var manifest = new DotnetWatchToolCacheManifest
            {
                Format = CacheFormat,
                Version = version,
                EntryPoint = entryPoint,
                Files = Directory.EnumerateFiles(staging.FullName, "*", SearchOption.AllDirectories)
                    .Select(path => new DotnetWatchToolCacheFile
                    {
                        Path = Path.GetRelativePath(staging.FullName, path),
                        Length = new FileInfo(path).Length
                    })
                    .ToArray()
            };
            await File.WriteAllTextAsync(
                Path.Combine(staging.FullName, ManifestName),
                JsonSerializer.Serialize(manifest, DotnetWatchToolCacheJsonContext.Default.DotnetWatchToolCacheManifest),
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging.FullName, installationDirectory);
            return installation with { DllPath = Path.Combine(installationDirectory, entryPoint) };
        }
        finally
        {
            if (Directory.Exists(staging.FullName))
            {
                try
                {
                    staging.Delete(recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    log($"Could not remove watch tool staging directory '{staging.FullName}': {exception.Message}");
                }
            }
        }
    }

    private DotnetWatchToolInstallation? TryReadInstallation(string directory, string version)
    {
        var manifestPath = Path.Combine(directory, ManifestName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var manifest = JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath),
                DotnetWatchToolCacheJsonContext.Default.DotnetWatchToolCacheManifest);
            if (manifest is null || manifest.Format != CacheFormat ||
                !string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(manifest.EntryPoint) ||
                manifest.Files is not { Length: > 0 })
            {
                log($"Ignoring incompatible watch tool cache metadata in '{directory}'.");
                return null;
            }

            foreach (var file in manifest.Files)
            {
                if (file is null || string.IsNullOrEmpty(file.Path) || file.Length < 0)
                {
                    log($"Ignoring invalid watch tool cache metadata in '{directory}'.");
                    return null;
                }

                var path = ResolveContainedPath(directory, file.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != file.Length)
                {
                    log($"Ignoring incomplete watch tool cache entry '{directory}'.");
                    return null;
                }
            }

            return ValidatePayload(ResolveContainedPath(directory, manifest.EntryPoint), version);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or DotnetWatchToolException or ArgumentException)
        {
            log($"Could not validate watch tool cache entry '{directory}': {exception.Message}");
            return null;
        }
    }

    internal static string FindEntryPoint(string path)
    {
        if (File.Exists(path))
        {
            return path;
        }

        if (!Directory.Exists(path))
        {
            throw new DotnetWatchToolException($"The watch tool installation '{path}' does not exist.");
        }

        var direct = Path.Combine(path, PackageId + ".dll");
        if (File.Exists(direct))
        {
            return direct;
        }

        var settings = Directory.GetFiles(path, "DotnetToolSettings.xml", SearchOption.AllDirectories);
        if (settings.Length != 1)
        {
            throw new DotnetWatchToolException(
                $"The watch tool directory '{path}' must contain exactly one tool payload. Specify its entry DLL directly.");
        }

        // Installed tools declare: <Command Name="..." EntryPoint="tool.dll" Runner="dotnet" />.
        // Resolve against the settings directory, never against the AppHost's current directory.
        var commands = XDocument.Load(settings[0]).Descendants("Command")
            .Where(command => string.Equals((string?)command.Attribute("Name"), PackageId, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (commands.Length != 1 || (string?)commands[0].Attribute("Runner") != "dotnet" ||
            (string?)commands[0].Attribute("EntryPoint") is not { Length: > 0 } entryPoint)
        {
            throw new DotnetWatchToolException($"The installation '{path}' does not declare the expected .NET watch tool.");
        }

        return ResolveContainedPath(Path.GetDirectoryName(settings[0])!, entryPoint);
    }

    internal static DotnetWatchToolInstallation ValidatePayload(string dllPath, string version)
    {
        if (!File.Exists(dllPath) ||
            !File.Exists(Path.ChangeExtension(dllPath, ".deps.json")) ||
            !File.Exists(Path.ChangeExtension(dllPath, ".runtimeconfig.json")))
        {
            throw new DotnetWatchToolException(
                $"The watch tool payload '{dllPath}' is incomplete. Keep its entry DLL, deps/runtime configuration, dependencies, and agents together.");
        }

        try
        {
            using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(dllPath, ".runtimeconfig.json")));
            // Framework-dependent tools declare either runtimeOptions.framework or
            // frameworks: [{"name":"Microsoft.NETCore.App","version":"10.0.14"}, ...].
            var options = runtimeConfig.RootElement.GetProperty("runtimeOptions");
            var frameworks = options.TryGetProperty("frameworks", out var frameworkArray)
                ? frameworkArray.EnumerateArray().ToArray()
                : [options.GetProperty("framework")];
            var runtime = frameworks.Single(
                framework => framework.GetProperty("name").GetString() == "Microsoft.NETCore.App");
            var runtimeVersion = runtime.GetProperty("version").GetString();
            if (runtimeVersion is null ||
                !Version.TryParse(runtimeVersion.Split('-')[0], out var parsedVersion) ||
                parsedVersion.Major < 1)
            {
                throw new DotnetWatchToolException($"The watch tool '{dllPath}' declares an invalid .NET runtime requirement.");
            }

            ValidateRuntimeAssets(dllPath);
            var directory = Path.GetDirectoryName(dllPath)!;
            // These agent TFMs are independent of the tool's own runtime (including .NET 11 tools).
            // https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch/AppModels/HotReloadAppModel.cs#L17-L20
            foreach (var agent in new[]
            {
                "hotreload/net6.0/Microsoft.Extensions.DotNetDeltaApplier.dll",
                "hotreload/net6.0/Microsoft.AspNetCore.Watch.BrowserRefresh.dll",
                "hotreload/net10.0/Microsoft.Extensions.DotNetDeltaApplier.dll"
            })
            {
                if (!File.Exists(Path.Combine(directory, agent)))
                {
                    throw new DotnetWatchToolException(
                        $"The watch tool payload '{dllPath}' is missing agent '{agent}'. Keep the complete installed payload together.");
                }
            }

            return new(Path.GetFullPath(dllPath), version, parsedVersion.Major);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new DotnetWatchToolException($"The watch tool payload '{dllPath}' contains invalid runtime or dependency metadata.", exception);
        }
    }

    private static void ValidateRuntimeAssets(string dllPath)
    {
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(dllPath, ".deps.json")));
        var target = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()
            ?? throw new DotnetWatchToolException("The watch tool has no runtime dependency target.");
        var libraries = dependencies.RootElement.GetProperty("targets").GetProperty(target);
        var directory = Path.GetDirectoryName(dllPath)!;
        foreach (var library in libraries.EnumerateObject())
        {
            foreach (var assetType in new[] { "runtime", "native", "resources" })
            {
                if (!library.Value.TryGetProperty(assetType, out var assets))
                {
                    continue;
                }

                foreach (var asset in assets.EnumerateObject())
                {
                    // NuGet's deps file retains package-relative asset names such as
                    // lib/net10.0/Foo.dll; tool installation flattens runtime DLLs and
                    // preserves satellite cultures as cs/Foo.resources.dll.
                    var name = asset.Name.Replace('\\', '/').Split('/')[^1];
                    var relativePath = assetType == "resources"
                        ? Path.Combine(asset.Value.GetProperty("locale").GetString()
                            ?? throw new DotnetWatchToolException("The watch tool declares a satellite assembly without a culture."), name)
                        : name;
                    if (!File.Exists(ResolveContainedPath(directory, relativePath)))
                    {
                        throw new DotnetWatchToolException(
                            $"The watch tool payload '{dllPath}' is missing dependency '{relativePath}'. Keep the complete installed payload together.");
                    }
                }
            }
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var path = Path.GetFullPath(relativePath, root);
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relativePath) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new DotnetWatchToolException("Watch tool metadata contains a path outside its installation.");
        }

        return path;
    }
}

internal sealed class DotnetWatchToolInstaller(string dotnetPath) : IDotnetWatchToolInstaller
{
    public async Task InstallAsync(string version, string directory, string workingDirectory, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(dotnetPath)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "tool", "install", DotnetWatchToolManager.PackageId, "--version", version, "--tool-path", directory })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new DotnetWatchToolException("Could not start the .NET SDK to acquire the experimental watch tool.", exception);
        }

        // NuGet can write to both streams. Drain concurrently and do not echo credential-bearing feed diagnostics.
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                // Preserve caller cancellation if the process exits between the check and termination.
            }

            await ((Task)Task.WhenAll(output, error)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        await Task.WhenAll(output, error).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new DotnetWatchToolException(
                $"Acquiring {DotnetWatchToolManager.PackageId} {version} failed with exit code {process.ExitCode}. " +
                "Check the SDK and the NuGet sources, source mappings, and credentials configured for the AppHost directory.");
        }
    }
}

internal sealed class DotnetWatchToolCacheManifest
{
    public required int Format { get; init; }
    public required string Version { get; init; }
    public required string? EntryPoint { get; init; }
    public required DotnetWatchToolCacheFile?[]? Files { get; init; }
}

internal sealed class DotnetWatchToolCacheFile
{
    public required string? Path { get; init; }
    public required long Length { get; init; }
}

[JsonSerializable(typeof(DotnetWatchToolCacheManifest))]
internal sealed partial class DotnetWatchToolCacheJsonContext : JsonSerializerContext;
