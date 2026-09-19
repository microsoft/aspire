// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;
using System.Collections.Immutable;
using NuGet.Commands;
using NuGet.Credentials;
using NuGet.LibraryModel;
using NuGet.ProjectModel;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.RuntimeModel;
using NuGet.Versioning;
using INuGetLogger = NuGet.Common.ILogger;
using NuGetLogLevel = NuGet.Common.LogLevel;
using NuGetLogMessage = NuGet.Common.ILogMessage;

namespace Aspire.Cli.NuGet;

internal interface INuGetClient
{
    Task RestoreAsync(
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken);

    Task WriteManifestAsync(
        string assetsFilePath,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        bool exactMatch,
        bool prerelease,
        int take,
        bool useCache,
        IReadOnlyList<string> explicitSources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed record NuGetSearchResult(
    string Id,
    string Version,
    string Source,
    IReadOnlyList<string> AllVersions);

internal sealed class NuGetClient(
    IFeatures features,
    IEnvironment environment,
    ILogger<NuGetClient> logger) : INuGetClient
{
    private const string NuGetOrgUrl = "https://api.nuget.org/v3/index.json";
    private const string RuntimeIdentifierGraphResourceName = "Aspire.Cli.RuntimeIdentifierGraph.json";
    private readonly NuGetLogger _nuGetLogger = new(logger);
    private static readonly Lock s_credentialServiceLock = new();
    private static bool s_credentialServiceInitialized;

    public async Task RestoreAsync(
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        InitializeCredentialService();
        Directory.CreateDirectory(outputPath);

        // Restore is delegated to NuGet's RestoreRunner so the CLI resolves packages exactly the way
        // the aspire-managed helper did. Reimplementing the graph walk here previously diverged from
        // NuGet on RID-specific dependencies, placeholder assets, and version selection.
        var machineWideSettings = new XPlatMachineWideSetting();
        var settings = Settings.LoadDefaultSettings(workingDirectory, nugetConfigPath, machineWideSettings);

        var packageSources = ResolvePackageSources(settings, sources);
        var targetFramework = NuGetFramework.Parse(framework);
        var packageSpec = BuildPackageSpec(
            packages,
            targetFramework,
            runtimeIdentifier,
            outputPath,
            packageSources,
            settings);

        var dgSpec = new DependencyGraphSpec();
        dgSpec.AddProject(packageSpec);
        dgSpec.AddRestore(packageSpec.RestoreMetadata.ProjectUniqueName);

        var providerCache = new RestoreCommandProvidersCache();
        var dgProvider = new DependencyGraphSpecRequestProvider(providerCache, dgSpec, settings);

        NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(features, environment);
        NativeAotNuGetTrustStore.Initialize(_nuGetLogger, environment);

        using var cacheContext = new SourceCacheContext();
        var restoreArgs = new RestoreArgs
        {
            CacheContext = cacheContext,
            Log = _nuGetLogger,
            PreLoadedRequestProviders = [dgProvider],
            DisableParallel = Environment.ProcessorCount == 1,
            AllowNoOp = false,
            MachineWideSettings = machineWideSettings,
        };

        var results = await RestoreRunner.RunAsync(restoreArgs, cancellationToken).ConfigureAwait(false);
        var summary = results.Count > 0 ? results[0] : null;

        if (summary is null)
        {
            throw new InvalidOperationException("NuGet restore returned no results.");
        }

        if (!summary.Success)
        {
            var errors = string.Join(
                Environment.NewLine,
                summary.Errors?.Select(error => error.Message) ?? ["Unknown error"]);
            throw new InvalidOperationException($"NuGet restore failed: {errors}");
        }
    }

    private static PackageSpec BuildPackageSpec(
        IReadOnlyList<(string Id, string Version)> packages,
        NuGetFramework framework,
        string? runtimeIdentifier,
        string outputPath,
        List<PackageSource> sources,
        ISettings settings)
    {
        var projectName = "AspireRestore";
        var projectPath = Path.Combine(outputPath, "project.json");
        var tfmShort = framework.GetShortFolderName();
        var runtimeIdentifierGraphPath = !string.IsNullOrWhiteSpace(runtimeIdentifier)
            ? EnsureRuntimeIdentifierGraphPath(outputPath)
            : null;

        var dependencies = packages.Select(package => new LibraryDependency
        {
            LibraryRange = new LibraryRange(
                package.Id,
                VersionRange.Parse(package.Version),
                LibraryDependencyTarget.Package)
        }).ToImmutableArray();

        var tfInfo = new TargetFrameworkInformation
        {
            FrameworkName = framework,
            TargetAlias = tfmShort,
            Dependencies = dependencies,
            RuntimeIdentifierGraphPath = runtimeIdentifierGraphPath
        };

        var restoreMetadata = new ProjectRestoreMetadata
        {
            ProjectUniqueName = projectName,
            ProjectName = projectName,
            ProjectPath = projectPath,
            ProjectStyle = ProjectStyle.PackageReference,
            OutputPath = outputPath,
            PackagesPath = SettingsUtility.GetGlobalPackagesFolder(settings),
            OriginalTargetFrameworks = [tfmShort],
            ConfigFilePaths = settings.GetConfigFilePaths().ToList(),
        };

        foreach (var source in sources)
        {
            restoreMetadata.Sources.Add(source);
        }

        restoreMetadata.TargetFrameworks.Add(new ProjectRestoreMetadataFrameworkInfo(framework)
        {
            TargetAlias = tfmShort
        });

        var packageSpec = new PackageSpec([tfInfo])
        {
            Name = projectName,
            FilePath = projectPath,
            RestoreMetadata = restoreMetadata,
        };

        if (!string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            packageSpec.RuntimeGraph = new RuntimeGraph([new RuntimeDescription(runtimeIdentifier)]);
        }

        return packageSpec;
    }

    /// <summary>
    /// Writes the SDK runtime identifier graph next to the restore output. NuGet reads it from disk
    /// to expand RID-specific dependencies, and the Native AOT CLI has no SDK layout to point at.
    /// </summary>
    private static string EnsureRuntimeIdentifierGraphPath(string outputPath)
    {
        var graphPath = Path.Combine(outputPath, "RuntimeIdentifierGraph.json");
        if (File.Exists(graphPath))
        {
            return graphPath;
        }

        using var resourceStream = typeof(NuGetClient).Assembly.GetManifestResourceStream(RuntimeIdentifierGraphResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded runtime identifier graph '{RuntimeIdentifierGraphResourceName}' was not found.");
        using var fileStream = File.Create(graphPath);
        resourceStream.CopyTo(fileStream);

        return graphPath;
    }

    public async Task WriteManifestAsync(
        string assetsFilePath,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        CancellationToken cancellationToken)
    {
        // Asset selection is delegated to NuGet's own restore output. The assets file already
        // records which assemblies, resources, and native libraries apply to this target, so the
        // manifest reflects NuGet's RID fallback, `_._` placeholder, and locale semantics instead
        // of a reimplementation of them.
        var resolution = NuGetPackageAssetResolver.Resolve(assetsFilePath, framework, runtimeIdentifier);

        var managedAssemblies = new List<IntegrationPackageManagedAssembly>();
        var nativeLibraries = new List<IntegrationPackageNativeLibrary>();

        foreach (var asset in resolution.Assets)
        {
            if (asset.IsManagedAssembly)
            {
                managedAssemblies.Add(new IntegrationPackageManagedAssembly
                {
                    PackageId = asset.PackageId,
                    PackageVersion = asset.PackageVersion,
                    Name = Path.GetFileNameWithoutExtension(asset.RelativePath),
                    Culture = asset.Culture,
                    Path = asset.SourcePath
                });
            }

            if (asset.IsNativeLibrary)
            {
                nativeLibraries.Add(new IntegrationPackageNativeLibrary
                {
                    FileName = Path.GetFileName(asset.RelativePath),
                    Path = asset.SourcePath
                });
            }
        }

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        var manifest = IntegrationPackageProbeManifest.Create(managedAssemblies, nativeLibraries);
        await IntegrationPackageProbeManifest.WriteAsync(outputPath, manifest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        bool exactMatch,
        bool prerelease,
        int take,
        bool useCache,
        IReadOnlyList<string> explicitSources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        InitializeCredentialService();
        var settings = LoadSettings(nugetConfigPath, workingDirectory);
        var packageSources = LoadPackageSources(settings, explicitSources);
        var sourceSearches = packageSources.Select(source => SearchSourceSafelyAsync(
            source,
            query,
            exactMatch,
            prerelease,
            take,
            useCache,
            cancellationToken));

        var sourceResults = await Task.WhenAll(sourceSearches).ConfigureAwait(false);
        var results = sourceResults.SelectMany(result => result.Packages).ToArray();
        var failures = sourceResults.Where(result => result.Exception is not null).ToArray();
        if (results.Length == 0 && failures.Length > 0)
        {
            throw new InvalidOperationException(
                $"Failed to search NuGet package source(s): {string.Join(", ", failures.Select(result => result.Source))}.",
                new AggregateException(failures.Select(result => result.Exception!)));
        }

        if (exactMatch)
        {
            return results
                .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(package => NuGetVersion.Parse(package.Version))
                .ToArray();
        }

        return results
            .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(package => NuGetVersion.Parse(package.Version)).First())
            .OrderBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<NuGetSourceSearchResult> SearchSourceSafelyAsync(
        PackageSource source,
        string query,
        bool exactMatch,
        bool prerelease,
        int take,
        bool useCache,
        CancellationToken cancellationToken)
    {
        try
        {
            var packages = exactMatch
                ? await GetPackageMetadataAsync(source, query, prerelease, useCache, cancellationToken).ConfigureAwait(false)
                : await SearchSourceAsync(
                    source,
                    query,
                    new global::NuGet.Protocol.Core.Types.SearchFilter(prerelease),
                    take,
                    cancellationToken).ConfigureAwait(false);
            return new(packages, PackageSourceRedactor.RedactForDisplay(source.Source), Exception: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var displaySource = PackageSourceRedactor.RedactForDisplay(source.Source);

            // Only the exception type is logged. NuGet protocol failures format the feed URL into
            // their own message -- often a derived resource URL rather than the configured source
            // string -- so logging the exception would leak UserInfo/SAS credentials into
            // ~/.aspire/logs, which users routinely attach to bug reports. A total search failure
            // still surfaces through the error thrown by SearchAsync, so nothing fails silently.
            logger.LogWarning(
                "Failed to search NuGet package source '{PackageSource}': {ExceptionType}",
                displaySource,
                ex.GetType().Name);
            return new([], displaySource, ex);
        }
    }

    private void InitializeCredentialService()
    {
        if (s_credentialServiceInitialized)
        {
            DefaultCredentialServiceUtility.UpdateCredentialServiceDelegatingLogger(_nuGetLogger);
            return;
        }

        lock (s_credentialServiceLock)
        {
            if (!s_credentialServiceInitialized)
            {
                DefaultCredentialServiceUtility.SetupDefaultCredentialService(_nuGetLogger, nonInteractive: true);
                s_credentialServiceInitialized = true;
            }
            else
            {
                DefaultCredentialServiceUtility.UpdateCredentialServiceDelegatingLogger(_nuGetLogger);
            }
        }
    }

    private async Task<IReadOnlyList<NuGetSearchResult>> SearchSourceAsync(
        PackageSource source,
        string query,
        global::NuGet.Protocol.Core.Types.SearchFilter filter,
        int take,
        CancellationToken cancellationToken)
    {
        var repository = Repository.Factory.GetCoreV3(source);
        var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
        if (searchResource is null)
        {
            return [];
        }

        var packages = new List<NuGetSearchResult>();
        var skip = 0;
        while (true)
        {
            var results = (await searchResource.SearchAsync(
                query,
                filter,
                skip,
                take,
                _nuGetLogger,
                cancellationToken).ConfigureAwait(false)).ToArray();

            foreach (var result in results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var versions = await result.GetVersionsAsync().ConfigureAwait(false);
                packages.Add(new NuGetSearchResult(
                    result.Identity.Id,
                    result.Identity.Version.ToString(),
                    source.Source,
                    versions?.Select(version => version.Version.ToString()).ToArray() ?? []));
            }

            if (results.Length < take)
            {
                break;
            }

            skip += take;
        }

        return packages;
    }

    private async Task<IReadOnlyList<NuGetSearchResult>> GetPackageMetadataAsync(
        PackageSource source,
        string packageId,
        bool prerelease,
        bool useCache,
        CancellationToken cancellationToken)
    {
        var repository = Repository.Factory.GetCoreV3(source);
        var metadataResource = await repository.GetResourceAsync<PackageMetadataResource>(cancellationToken).ConfigureAwait(false);
        if (metadataResource is null)
        {
            return [];
        }

        using var cacheContext = new SourceCacheContext
        {
            NoCache = !useCache,
            DirectDownload = !useCache
        };
        var metadata = (await metadataResource.GetMetadataAsync(
            packageId,
            prerelease,
            includeUnlisted: false,
            cacheContext,
            _nuGetLogger,
            cancellationToken).ConfigureAwait(false)).ToArray();
        if (metadata.Length == 0)
        {
            return [];
        }

        var latest = metadata
            .OrderByDescending(package => package.Identity.Version)
            .First();
        return
        [
            new NuGetSearchResult(
                latest.Identity.Id,
                latest.Identity.Version.ToString(),
                source.Source,
                metadata.Select(package => package.Identity.Version.ToString()).ToArray())
        ];
    }

    private static ISettings LoadSettings(string? nugetConfigPath, string workingDirectory)
    {
        if (!string.IsNullOrEmpty(nugetConfigPath))
        {
            return Settings.LoadSpecificSettings(
                Path.GetDirectoryName(nugetConfigPath)!,
                Path.GetFileName(nugetConfigPath));
        }

        return Settings.LoadDefaultSettings(workingDirectory);
    }

    private static List<PackageSource> LoadPackageSources(
        ISettings settings,
        IReadOnlyList<string> explicitSources)
    {
        if (explicitSources.Count > 0)
        {
            return explicitSources.Select(source => new PackageSource(source)).ToList();
        }

        var sources = new PackageSourceProvider(settings)
            .LoadPackageSources()
            .Where(source => source.IsEnabled)
            .ToList();

        if (sources.Count == 0)
        {
            sources.Add(new PackageSource(NuGetOrgUrl, "nuget.org"));
        }

        return sources;
    }

    private static List<PackageSource> ResolvePackageSources(
        ISettings settings,
        IReadOnlyList<string> cliSources)
    {
        var sources = new PackageSourceProvider(settings)
            .LoadPackageSources()
            .Where(source => source.IsEnabled)
            .ToList();

        foreach (var cliSource in cliSources)
        {
            if (!sources.Any(source => source.Source.Equals(cliSource, StringComparison.OrdinalIgnoreCase)))
            {
                sources.Add(new PackageSource(cliSource));
            }
        }

        if (!sources.Any(source => source.Source.Equals(NuGetOrgUrl, StringComparison.OrdinalIgnoreCase)))
        {
            sources.Add(new PackageSource(NuGetOrgUrl, "nuget.org"));
        }

        return sources;
    }
    private sealed record NuGetSourceSearchResult(
        IReadOnlyList<NuGetSearchResult> Packages,
        string Source,
        Exception? Exception);

    private sealed class NuGetLogger(ILogger logger) : INuGetLogger
    {
        public void Log(NuGetLogLevel level, string data) => logger.Log(MapLogLevel(level), "{Message}", data);
        public void Log(NuGetLogMessage message) => Log(message.Level, message.Message);

        public Task LogAsync(NuGetLogLevel level, string data)
        {
            Log(level, data);
            return Task.CompletedTask;
        }

        public Task LogAsync(NuGetLogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }

        public void LogDebug(string data) => Log(NuGetLogLevel.Debug, data);
        public void LogError(string data) => Log(NuGetLogLevel.Error, data);
        public void LogInformation(string data) => Log(NuGetLogLevel.Information, data);
        public void LogInformationSummary(string data) => Log(NuGetLogLevel.Information, data);
        public void LogMinimal(string data) => Log(NuGetLogLevel.Minimal, data);
        public void LogVerbose(string data) => Log(NuGetLogLevel.Verbose, data);
        public void LogWarning(string data) => Log(NuGetLogLevel.Warning, data);

        private static LogLevel MapLogLevel(NuGetLogLevel level) => level switch
        {
            NuGetLogLevel.Debug or NuGetLogLevel.Verbose => LogLevel.Debug,
            NuGetLogLevel.Information or NuGetLogLevel.Minimal => LogLevel.Information,
            NuGetLogLevel.Warning => LogLevel.Warning,
            NuGetLogLevel.Error => LogLevel.Error,
            _ => LogLevel.None
        };
    }

    private static class NativeAotNuGetTrustStore
    {
        private static readonly object s_lock = new();
        private static bool s_initialized;

        public static void Initialize(INuGetLogger logger, IEnvironment environment)
        {
            if (s_initialized ||
                !environment.IsLinux() ||
                !bool.TryParse(environment.GetEnvironmentVariable(
                    NuGetSignatureVerificationEnabler.DotNetNuGetSignatureVerification), out var enabled) ||
                !enabled)
            {
                return;
            }

            lock (s_lock)
            {
                if (s_initialized)
                {
                    return;
                }

                var previousSdkRoot = AppContext.GetData("Microsoft.DotNet.Sdk.Root");
                var rootDirectory = Directory.CreateTempSubdirectory("aspire-nuget-trust-");
                try
                {
                    var trustedRootsDirectory = Directory.CreateDirectory(
                        Path.Combine(rootDirectory.FullName, "trustedroots"));
                    WriteResource("codesignctl.pem", trustedRootsDirectory.FullName);
                    WriteResource("timestampctl.pem", trustedRootsDirectory.FullName);

                    // NuGet resolves its fallback trust bundles under Microsoft.DotNet.Sdk.Root.
                    // Point it at the securely extracted embedded SDK bundles only while the factories initialize.
                    AppContext.SetData("Microsoft.DotNet.Sdk.Root", rootDirectory.FullName);
                    X509TrustStore.InitializeForDotNetSdk(logger);
                    s_initialized = true;
                }
                finally
                {
                    AppContext.SetData("Microsoft.DotNet.Sdk.Root", previousSdkRoot);
                    rootDirectory.Delete(recursive: true);
                }
            }
        }

        private static void WriteResource(string resourceName, string destinationDirectory)
        {
            using var resourceStream = typeof(NuGetClient).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded NuGet trust root resource '{resourceName}' was not found.");
            using var fileStream = File.Create(Path.Combine(destinationDirectory, resourceName));
            resourceStream.CopyTo(fileStream);
        }
    }
}
