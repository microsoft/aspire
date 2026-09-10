// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Aspire.Cli.Packaging;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.NuGet;

internal sealed record NuGetSettingsInfo(
    IReadOnlyList<string> ConfigPaths,
    IReadOnlyList<NuGetSourceInfo> Sources,
    bool PackageSourceMappingEnabled,
    IReadOnlyList<NuGetPackageSourceMappingInfo> PackageSourceMappings,
    IReadOnlyList<string> DisabledPackageSourceKeys,
    IReadOnlyList<string> ReservedPackageSourceKeys,
    byte[] SourceIdentityKey);

internal sealed record NuGetSourceInfo(
    string Name,
    string Identity,
    bool IsEnabled,
    bool HasCredentialMaterial,
    bool RequiresFullOutputSuppression);

internal sealed record NuGetPackageSourceMappingInfo(string SourceKey, string[] Patterns);

internal sealed record NuGetConfigOverlayInfo(
    NuGetConfigSourceDefinition[] Sources,
    NuGetPackageSourceMappingInfo[] PackageSourceMappings,
    bool ClearDisabledPackageSources,
    string[] DisabledPackageSourceKeys,
    string? GlobalPackagesFolder);

internal sealed record NuGetConfigSourceDefinition(string Key, string Source);

/// <summary>
/// Service for NuGet operations that works in bundle mode.
/// Uses the NuGetHelper tool via the layout runtime.
/// </summary>
internal interface INuGetService
{
    /// <summary>
    /// Restores packages to the cache and creates a package probe manifest.
    /// </summary>
    /// <param name="packages">The packages to restore.</param>
    /// <param name="targetFramework">The target framework.</param>
    /// <param name="runtimeIdentifier">The runtime identifier used to prefer runtime-specific assets in the generated layout.</param>
    /// <param name="sources">Additional NuGet sources.</param>
    /// <param name="workingDirectory">Working directory for nuget.config discovery and for resolving the workspace-local restore cache. Required.</param>
    /// <param name="nugetConfigPaths">NuGet.config paths ordered from highest to lowest precedence.</param>
    /// <param name="nugetConfigOverlayCacheIdentity">A stable cache identity for the first config path when it is an invocation-scoped overlay.</param>
    /// <param name="additionalSensitiveSources">Additional source values that must be redacted from restore output.</param>
    /// <param name="globalPackagesFolderOverride">An optional global packages folder override for the restore process.</param>
    /// <param name="suppressFailureOutput">Whether restore and manifest failure output must be discarded because it can contain credential material that cannot be safely recognized without the original value.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The path to the package probe manifest.</returns>
    Task<string> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        IReadOnlyList<string>? nugetConfigPaths = null,
        string? nugetConfigOverlayCacheIdentity = null,
        IEnumerable<string>? additionalSensitiveSources = null,
        string? globalPackagesFolderOverride = null,
        bool suppressFailureOutput = false,
        CancellationToken ct = default);
}

/// <summary>
/// NuGet service implementation that uses the bundle's NuGetHelper tool.
/// </summary>
internal sealed class BundleNuGetService : INuGetService
{
    internal const string ManagedComponentNotFoundMessage = "aspire-managed not found in layout.";
    private readonly ILayoutDiscovery _layoutDiscovery;
    private readonly LayoutProcessRunner _layoutProcessRunner;
    private readonly IFeatures _features;
    private readonly IEnvironment _environment;
    private readonly ILogger<BundleNuGetService> _logger;
    private readonly IBundleService? _bundleService;

    internal Func<byte[]> SourceIdentityKeyFactory { get; init; }
        = static () => RandomNumberGenerator.GetBytes(NuGetSourceIdentity.KeySizeInBytes);

    public BundleNuGetService(
        ILayoutDiscovery layoutDiscovery,
        LayoutProcessRunner layoutProcessRunner,
        IFeatures features,
        IEnvironment environment,
        ILogger<BundleNuGetService> logger,
        IBundleService? bundleService = null)
    {
        _layoutDiscovery = layoutDiscovery;
        _layoutProcessRunner = layoutProcessRunner;
        _features = features;
        _environment = environment;
        _logger = logger;
        _bundleService = bundleService;
    }

    public async Task<string> RestorePackagesAsync(
        IEnumerable<(string Id, string Version)> packages,
        string workingDirectory,
        string targetFramework = "net10.0",
        string? runtimeIdentifier = null,
        IEnumerable<string>? sources = null,
        IReadOnlyList<string>? nugetConfigPaths = null,
        string? nugetConfigOverlayCacheIdentity = null,
        IEnumerable<string>? additionalSensitiveSources = null,
        string? globalPackagesFolderOverride = null,
        bool suppressFailureOutput = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var layoutLease = _bundleService is null
            ? null
            : await _bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "nuget-restore", ct).ConfigureAwait(false);
        var layout = layoutLease?.Layout ?? _layoutDiscovery.DiscoverLayout();
        if (layout is null)
        {
            throw new InvalidOperationException("Bundle layout not found. Cannot perform NuGet restore in bundle mode.");
        }

        var managedPath = layout.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            throw new InvalidOperationException("aspire-managed not found in layout.");
        }

        var packageList = packages.ToList();
        if (packageList.Count == 0)
        {
            throw new ArgumentException("At least one package is required", nameof(packages));
        }

        var sourceList = sources?.ToArray();
        var nugetConfigCacheIdentity = await ComputeNuGetConfigCacheIdentityAsync(
            nugetConfigPaths,
            nugetConfigOverlayCacheIdentity,
            ct).ConfigureAwait(false);
        var sensitiveSources = (sourceList ?? [])
            .Concat(additionalSensitiveSources ?? [])
            .Where(PackageSourceOverrideMappings.HasCredentialMaterial)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var nugetFallbackPackagesPaths = CliPathHelper.GetNuGetFallbackPackagesEnvironmentPaths(_environment);

        var restoreDir = Path.Combine(
            GetPackageRestoreCacheDirectory(workingDirectory),
            ComputePackageHash(
                packageList,
                targetFramework,
                runtimeIdentifier,
                managedPath,
                sourceList,
                nugetConfigCacheIdentity,
                globalPackagesFolderOverride ?? CliPathHelper.GetNuGetPackagesEnvironmentPath(_environment),
                nugetFallbackPackagesPaths));
        var objDir = Path.Combine(restoreDir, "obj");
        var manifestPath = Path.Combine(restoreDir, IntegrationPackageProbeManifest.FileName);
        var assetsPath = Path.Combine(objDir, "project.assets.json");
        var lockPath = Path.Combine(restoreDir, "restore.lock");

        // Reusable package caches are shared by every AppHost in the workspace and must remain
        // serialized while their manifest or project.assets.json file is being written.
        using var fileLock = await FileLock.AcquireAsync(lockPath, ct).ConfigureAwait(false);

        // Check if already restored after acquiring the lock because another process may
        // have populated the shared cache while this process was waiting.
        if (File.Exists(manifestPath) && TryValidatePackageManifest(manifestPath, _logger))
        {
            _logger.LogDebug("Using cached package manifest at {Path}", manifestPath);
            return manifestPath;
        }

        Directory.CreateDirectory(objDir);

        // Step 1: Restore packages
        // Prepend "nuget" subcommand for aspire-managed dispatch
        var restoreArgs = new List<string>
        {
            "nuget",
            "restore",
            "--no-nuget-org",
            "--output", objDir,
            "--framework", targetFramework
        };

        if (!string.IsNullOrEmpty(runtimeIdentifier))
        {
            restoreArgs.Add("--runtime-identifier");
            restoreArgs.Add(runtimeIdentifier);
        }

        foreach (var (id, version) in packageList)
        {
            restoreArgs.Add("--package");
            restoreArgs.Add($"{id},{version}");
        }

        if (sourceList is not null)
        {
            foreach (var source in sourceList)
            {
                restoreArgs.Add("--source");
                restoreArgs.Add(source);
            }
        }

        // Pass working directory for nuget.config discovery.
        restoreArgs.Add("--working-dir");
        restoreArgs.Add(workingDirectory);

        if (nugetConfigPaths is not null)
        {
            foreach (var nugetConfigPath in nugetConfigPaths)
            {
                restoreArgs.Add("--nuget-config");
                restoreArgs.Add(nugetConfigPath);
            }
        }

        // Enable verbose output for debugging
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            restoreArgs.Add("--verbose");
        }

        _logger.LogDebug("Restoring {Count} packages", packageList.Count);
        _logger.LogDebug("aspire-managed path: {ManagedPath}", managedPath);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            // Build a redacted copy of the args specifically for the log line so user-supplied
            // credentialed feeds (e.g., `https://user:pat@host/v3/index.json`, SAS-token URLs) do
            // not flow to the debug log alongside the rest of the restore invocation. The
            // original `restoreArgs` list is still passed verbatim to the process below.
            _logger.LogDebug("NuGet restore args: {Args}", string.Join(" ", BuildRedactedArgsForLog(restoreArgs)));
        }

        var environmentVariables = new Dictionary<string, string>();
        if (globalPackagesFolderOverride is not null)
        {
            environmentVariables[CliPathHelper.NuGetPackagesEnvironmentVariable] = globalPackagesFolderOverride;
        }
        NuGetSignatureVerificationEnabler.Apply(environmentVariables, _features, _environment);
        layoutLease?.AddEnvironment(environmentVariables);

        var (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
        managedPath,
        restoreArgs,
        environmentVariables: environmentVariables,
        // A restore against a slow/unresponsive NuGet source can hang. LayoutProcessRunner uses this
        // to bind the helper to the CLI's Windows kill-on-close job (and, on non-Windows, to instead
        // arm the cooperative parent-liveness watchdog) so a hard-killed CLI cannot leak it.
        killOnParentExit: true,
        ct: ct);

        var redactedError = suppressFailureOutput
            ? string.Empty
            : PackageSourceRedactor.RedactOccurrences(error, sensitiveSources);
        var redactedOutput = suppressFailureOutput
            ? string.Empty
            : PackageSourceRedactor.RedactOccurrences(output, sensitiveSources);

        // NuGet errors often repeat the feed URL. Redact helper output separately from the
        // invocation arguments so SAS tokens and URL user-info cannot reach logs or exceptions.
        if (!string.IsNullOrWhiteSpace(redactedError))
        {
            _logger.LogDebug("NuGetHelper restore stderr: {Error}", redactedError);
        }

        if (exitCode != 0)
        {
            _logger.LogError("Package restore failed with exit code {ExitCode}", exitCode);
            _logger.LogError("Package restore stderr: {Error}", redactedError);
            _logger.LogError("Package restore stdout: {Output}", redactedOutput);
            throw new InvalidOperationException($"Package restore failed: {redactedError}");
        }

        // Step 2: Create package probe manifest
        // Prepend "nuget" subcommand for aspire-managed dispatch
        var manifestArgs = new List<string>
        {
            "nuget",
            "manifest",
            "--assets", assetsPath,
            "--output", manifestPath,
            "--framework", targetFramework
        };

        if (!string.IsNullOrEmpty(runtimeIdentifier))
        {
            manifestArgs.Add("--runtime-identifier");
            manifestArgs.Add(runtimeIdentifier);
        }

        // Enable verbose output for debugging
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            manifestArgs.Add("--verbose");
        }

        _logger.LogDebug("Creating package manifest from {AssetsPath}", assetsPath);
        _logger.LogDebug("NuGet manifest args: {Args}", string.Join(" ", manifestArgs));

        (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
        managedPath,
        manifestArgs,
        environmentVariables: environmentVariables,
        // Same rationale as the restore step above: keep this aspire-managed helper from outliving a
        // hard-killed CLI (Windows kill-on-close job, or the cooperative watchdog on other hosts).
        killOnParentExit: true,
        ct: ct);

        redactedError = suppressFailureOutput
            ? string.Empty
            : PackageSourceRedactor.RedactOccurrences(error, sensitiveSources);
        redactedOutput = suppressFailureOutput
            ? string.Empty
            : PackageSourceRedactor.RedactOccurrences(output, sensitiveSources);
        if (!string.IsNullOrWhiteSpace(redactedError))
        {
            _logger.LogDebug("NuGetHelper manifest stderr: {Error}", redactedError);
        }

        if (exitCode != 0)
        {
            _logger.LogError("Manifest creation failed with exit code {ExitCode}", exitCode);
            _logger.LogError("Manifest creation stderr: {Error}", redactedError);
            _logger.LogError("Manifest creation stdout: {Output}", redactedOutput);
            throw new InvalidOperationException($"Manifest creation failed: {redactedError}");
        }

        _logger.LogDebug("Package manifest created at {Path}", manifestPath);
        return manifestPath;
    }

    internal async Task<NuGetSettingsInfo> GetNuGetSettingsAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        using var layoutLease = _bundleService is null
            ? null
            : await _bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "nuget-settings", cancellationToken).ConfigureAwait(false);
        var layout = layoutLease?.Layout ?? _layoutDiscovery.DiscoverLayout();
        var managedPath = layout?.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            throw new InvalidOperationException(ManagedComponentNotFoundMessage);
        }

        var sourceIdentityKey = SourceIdentityKeyFactory();
        if (sourceIdentityKey.Length != NuGetSourceIdentity.KeySizeInBytes)
        {
            throw new InvalidOperationException(
                $"The NuGet source identity key must be {NuGetSourceIdentity.KeySizeInBytes} bytes.");
        }

        var (exitCode, output, error) = await _layoutProcessRunner.RunAsync(
            managedPath,
            ["nuget", "settings", "--working-dir", workingDirectory],
            environmentVariables: new Dictionary<string, string>
            {
                [NuGetSourceIdentity.KeyEnvironmentVariable] = Convert.ToBase64String(sourceIdentityKey)
            },
            killOnParentExit: true,
            ct: cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Unable to discover the NuGet configuration hierarchy for '{workingDirectory}': {error}");
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException("The NuGet settings response was not an object.");
            }

            var configPaths = document.RootElement
                .GetProperty("ConfigPaths")
                .EnumerateArray()
                .Select(static element => element.GetString()
                    ?? throw new InvalidDataException("The NuGet configuration hierarchy contained a null path."))
                .ToArray();
            var sources = document.RootElement
                .GetProperty("Sources")
                .EnumerateArray()
                .Select(static element => new NuGetSourceInfo(
                    element.GetProperty("Name").GetString()
                        ?? throw new InvalidDataException("The NuGet settings response contained a source without a name."),
                    element.GetProperty("Identity").GetString()
                        ?? throw new InvalidDataException("The NuGet settings response contained a source without an identity."),
                    element.GetProperty("IsEnabled").GetBoolean(),
                    element.GetProperty("HasCredentialMaterial").GetBoolean(),
                    element.GetProperty("RequiresFullOutputSuppression").GetBoolean()))
                .ToArray();
            var packageSourceMappingEnabled = document.RootElement
                .GetProperty("PackageSourceMappingEnabled")
                .GetBoolean();
            var packageSourceMappings = document.RootElement
                .GetProperty("PackageSourceMappings")
                .EnumerateArray()
                .Select(static mapping => new NuGetPackageSourceMappingInfo(
                    mapping.GetProperty("SourceKey").GetString()
                        ?? throw new InvalidDataException("The NuGet settings response contained a mapping without a source key."),
                    mapping.GetProperty("Patterns")
                        .EnumerateArray()
                        .Select(static pattern => pattern.GetString()
                            ?? throw new InvalidDataException("The NuGet settings response contained a null package pattern."))
                        .ToArray()))
                .ToArray();
            var disabledPackageSourceKeys = ReadStringArray(
                document.RootElement,
                "DisabledPackageSourceKeys",
                "The NuGet settings response contained a null disabled package source key.");
            var reservedPackageSourceKeys = ReadStringArray(
                document.RootElement,
                "ReservedPackageSourceKeys",
                "The NuGet settings response contained a null reserved package source key.");

            return new NuGetSettingsInfo(
                configPaths,
                sources,
                packageSourceMappingEnabled,
                packageSourceMappings,
                disabledPackageSourceKeys,
                reservedPackageSourceKeys,
                sourceIdentityKey);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The NuGet settings response was invalid.", ex);
        }
    }

    internal async Task WriteNuGetConfigOverlayAsync(
        NuGetConfigOverlayInfo overlay,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        using var layoutLease = _bundleService is null
            ? null
            : await _bundleService.EnsureExtractedAndAcquireLayoutAsync("cli", "nuget-write-config", cancellationToken).ConfigureAwait(false);
        var layout = layoutLease?.Layout ?? _layoutDiscovery.DiscoverLayout();
        var managedPath = layout?.GetManagedPath();
        if (managedPath is null || !File.Exists(managedPath))
        {
            throw new InvalidOperationException(ManagedComponentNotFoundMessage);
        }

        var requestDirectory = Directory.CreateTempSubdirectory("aspire-nuget-config-request");
        try
        {
            var requestPath = Path.Combine(requestDirectory.FullName, "request.json");
            await using (var requestStream = File.Create(requestPath))
            {
                await JsonSerializer.SerializeAsync(
                    requestStream,
                    overlay,
                    BundleNuGetJsonContext.Default.NuGetConfigOverlayInfo,
                    cancellationToken).ConfigureAwait(false);
            }

            var (exitCode, _, error) = await _layoutProcessRunner.RunAsync(
                managedPath,
                ["nuget", "write-config", "--request", requestPath, "--output", outputPath],
                killOnParentExit: true,
                ct: cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                var sensitiveSources = overlay.Sources
                    .Select(static source => source.Source)
                    .Where(PackageSourceOverrideMappings.HasCredentialMaterial)
                    .ToArray();
                throw new InvalidOperationException(
                    $"Unable to generate the NuGet configuration overlay: {PackageSourceRedactor.RedactOccurrences(error, sensitiveSources)}");
            }
        }
        finally
        {
            requestDirectory.Delete(recursive: true);
        }
    }

    private static string[] ReadStringArray(
        JsonElement parent,
        string propertyName,
        string nullElementMessage)
        => parent
            .GetProperty(propertyName)
            .EnumerateArray()
            .Select(element => element.GetString()
                ?? throw new InvalidDataException(nullElementMessage))
            .ToArray();

    private static bool TryValidatePackageManifest(string manifestPath, ILogger logger)
    {
        try
        {
            _ = IntegrationPackageProbeManifest.Load(manifestPath);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Cached package manifest {ManifestPath} is invalid and will be regenerated.", manifestPath);
            return false;
        }
    }

    // Returns a redacted copy of the restore args suitable for debug logging. Replaces the value
    // immediately following each `--source` token with the credential-safe form from
    // PackageSourceRedactor. Built defensively to handle repeated `--source` flags and a missing
    // trailing value at the end of the args list.
    private static IReadOnlyList<string> BuildRedactedArgsForLog(IReadOnlyList<string> args)
    {
        var redacted = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            redacted.Add(args[i]);
            if (string.Equals(args[i], "--source", StringComparison.Ordinal) && i + 1 < args.Count)
            {
                redacted.Add(PackageSourceRedactor.RedactForDisplay(args[++i]));
            }
        }

        return redacted;
    }

    internal static string ComputePackageHash(
        List<(string Id, string Version)> packages,
        string tfm,
        string? runtimeIdentifier,
        string? managedPath = null,
        IEnumerable<string>? sources = null,
        string? nugetConfigCacheIdentity = null,
        string? nugetPackagesPath = null,
        IReadOnlyList<string>? nugetFallbackPackagesPaths = null)
    {
        var content = string.Join(";", packages.OrderBy(p => p.Id).Select(p => $"{p.Id}:{p.Version}"));
        content += $";tfm:{tfm}";
        content += $";rid:{runtimeIdentifier ?? "<none>"}";
        content += $";managed:{GetManagedToolFingerprint(managedPath)}";
        if (sources is not null)
        {
            foreach (var source in sources.OrderBy(static source => source, StringComparer.OrdinalIgnoreCase))
            {
                content += $";source:{source.Length}:{source}";
            }
        }
        if (nugetConfigCacheIdentity is not null)
        {
            content += $";config:{nugetConfigCacheIdentity}";
        }
        if (nugetPackagesPath is not null)
        {
            content += $";global-packages:{nugetPackagesPath.Length}:{nugetPackagesPath}";
        }
        if (nugetFallbackPackagesPaths is not null)
        {
            foreach (var path in nugetFallbackPackagesPaths)
            {
                content += $";fallback-packages:{path.Length}:{path}";
            }
        }

        return XxHash3.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(content)).ToString("X16", CultureInfo.InvariantCulture);
    }

    private async Task<string?> ComputeNuGetConfigCacheIdentityAsync(
        IReadOnlyList<string>? nugetConfigPaths,
        string? nugetConfigOverlayCacheIdentity,
        CancellationToken cancellationToken)
    {
        if (nugetConfigPaths is not { Count: > 0 })
        {
            return null;
        }

        var hash = new XxHash3();
        for (var index = 0; index < nugetConfigPaths.Count; index++)
        {
            var nugetConfigPath = nugetConfigPaths[index];
            if (index == 0 && nugetConfigOverlayCacheIdentity is not null)
            {
                hash.Append("\0NUGET_CONFIG_OVERLAY\0"u8);
                hash.Append(System.Text.Encoding.UTF8.GetBytes(nugetConfigOverlayCacheIdentity));
            }
            else
            {
                hash.Append(System.Text.Encoding.UTF8.GetBytes(nugetConfigPath));
                hash.Append(await File.ReadAllBytesAsync(nugetConfigPath, cancellationToken).ConfigureAwait(false));
            }

            var configContent = await File.ReadAllTextAsync(nugetConfigPath, cancellationToken).ConfigureAwait(false);
            foreach (var environmentVariableName in NuGetConfigEnvironmentVariables.FindReferencedNames(configContent))
            {
                hash.Append("\0NUGET_CONFIG_ENVIRONMENT\0"u8);
                hash.Append(System.Text.Encoding.UTF8.GetBytes(environmentVariableName));
                hash.Append(System.Text.Encoding.UTF8.GetBytes(
                    _environment.GetEnvironmentVariable(environmentVariableName) ?? "\0UNSET\0"));
            }
        }

        return Convert.ToHexString(hash.GetCurrentHash());
    }

    private static string GetManagedToolFingerprint(string? managedPath)
    {
        if (string.IsNullOrEmpty(managedPath))
        {
            return "<none>";
        }

        try
        {
            var fileInfo = new FileInfo(managedPath);
            if (!fileInfo.Exists)
            {
                return "<missing>";
            }

            return $"{fileInfo.Length}|{fileInfo.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return "<error>";
        }
        catch (UnauthorizedAccessException)
        {
            return "<error>";
        }
        catch (NotSupportedException)
        {
            return "<error>";
        }
    }

    private static string GetPackageRestoreCacheDirectory(string workingDirectory)
    {
        var integrationCacheDirectory = ConfigurationHelper.GetIntegrationCacheDirectory(
            new DirectoryInfo(Path.GetFullPath(workingDirectory)));
        return Path.Combine(integrationCacheDirectory.FullName, "package-restore");
    }
}

[JsonSerializable(typeof(NuGetConfigOverlayInfo))]
internal sealed partial class BundleNuGetJsonContext : JsonSerializerContext;
