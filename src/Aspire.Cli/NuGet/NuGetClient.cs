// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Globalization;
using System.IO.Hashing;
using System.Text;
using Aspire.Cli.Configuration;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.Extensions.Logging;
using NuGet.Commands;
using NuGet.Configuration;
using NuGet.Credentials;
using NuGet.Frameworks;
using NuGet.LibraryModel;
using NuGet.Packaging.Signing;
using NuGet.ProjectModel;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.RuntimeModel;
using NuGet.Versioning;
using INuGetLogger = NuGet.Common.ILogger;
using NuGetLogLevel = NuGet.Common.LogLevel;
using NuGetLogMessage = NuGet.Common.ILogMessage;

namespace Aspire.Cli.NuGet;

/// <summary>
/// Runs the NuGet operations that the <c>aspire-managed nuget</c> helper used to run out of process.
/// </summary>
/// <remarks>
/// Each operation mirrors one helper subcommand (<c>restore</c>, <c>manifest</c>, and <c>search</c>) so bundled CLIs
/// keep the same restore results, search results, and failure text. Failures are reported as
/// <see cref="NuGetOperationException"/>, whose <see cref="NuGetOperationException.Output"/> is the text the helper
/// wrote to stderr.
/// </remarks>
internal interface INuGetClient
{
    Task RestoreAsync(
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> nugetConfigPaths,
        string workingDirectory,
        string? globalPackagesFolderOverride,
        IReadOnlyList<string> sensitiveSources,
        CancellationToken cancellationToken);

    Task WriteManifestAsync(
        string assetsFilePath,
        string outputPath,
        string framework,
        string? runtimeIdentifier,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        bool prerelease,
        int take,
        IReadOnlyList<string> explicitSources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken);

    NuGetSettingsInfo GetSettings(string workingDirectory, byte[] sourceIdentityKey);

    void WriteConfigOverlay(NuGetConfigOverlayRequest request, string outputPath);
}

internal sealed record NuGetSearchResult(
    string Id,
    string Version,
    string Source,
    IReadOnlyList<string> AllVersions);

/// <summary>
/// Reports a failed in-process NuGet operation.
/// </summary>
/// <param name="output">The diagnostic text the <c>aspire-managed nuget</c> helper would have written to stderr.</param>
internal sealed class NuGetOperationException(string output)
    : Exception("NuGet operation failed.")
{
    /// <summary>
    /// Gets the text the helper would have written to stderr. Callers surface it exactly as they surfaced the
    /// helper's stderr, so user-visible failure messages are unchanged.
    /// </summary>
    public string Output { get; } = output;
}

internal sealed class NuGetClient(
    IFeatures features,
    IEnvironment environment,
    ILogger<NuGetClient> logger) : INuGetClient
{
    private const string NuGetOrgUrl = "https://api.nuget.org/v3/index.json";
    private const string RuntimeIdentifierGraphResourceName = "Aspire.Cli.RuntimeIdentifierGraph.json";
    private static readonly Lock s_operationLock = new();
    private static int s_activeOperationCount;

    // Output the helper never produced -- credential provider and trust store diagnostics -- only goes to the debug
    // log. Keeping it out of each operation's captured output keeps failure messages identical to the helper's.
    private readonly DiagnosticNuGetLogger _diagnosticLogger = new(logger);

    public async Task RestoreAsync(
        IReadOnlyList<(string Id, string Version)> packages,
        string framework,
        string? runtimeIdentifier,
        string outputPath,
        IReadOnlyList<string> sources,
        IReadOnlyList<string> nugetConfigPaths,
        string workingDirectory,
        string? globalPackagesFolderOverride,
        IReadOnlyList<string> sensitiveSources,
        CancellationToken cancellationToken)
    {
        using var operation = BeginOperation(sensitiveSources);
        var output = new NuGetOperationOutput(logger, sensitiveSources);

        // The helper received DOTNET_NUGET_SIGNATURE_VERIFICATION only in its own environment. NuGet reads it from the
        // process environment, so it has to be set here, but only for the duration of the restore.
        using var signatureVerification = NuGetSignatureVerificationEnabler.ApplyToCurrentProcess(features, environment);
        try
        {
            Directory.CreateDirectory(outputPath);

            // Restore is delegated to NuGet's RestoreRunner so the CLI resolves packages exactly the way
            // the aspire-managed helper did. Reimplementing the graph walk here previously diverged from
            // NuGet on RID-specific dependencies, placeholder assets, and version selection.
            var machineWideSettings = new XPlatMachineWideSetting();
            var settings = nugetConfigPaths.Count > 0
                ? Settings.LoadSettingsGivenConfigPaths(nugetConfigPaths.ToList())
                : Settings.LoadDefaultSettings(workingDirectory, configFileName: null, machineWideSettings);

            var packageSources = ResolvePackageSources(settings, sources, addNuGetOrgFallback: false);
            var targetFramework = NuGetFramework.Parse(framework);
            var packageSpec = BuildPackageSpec(
                packages,
                targetFramework,
                runtimeIdentifier,
                outputPath,
                packageSources,
                settings,
                globalPackagesFolderOverride);

            var dgSpec = new DependencyGraphSpec();
            dgSpec.AddProject(packageSpec);
            dgSpec.AddRestore(packageSpec.RestoreMetadata.ProjectUniqueName);

            var providerCache = new RestoreCommandProvidersCache();
            var dgProvider = new DependencyGraphSpecRequestProvider(providerCache, dgSpec, settings);

            using var cacheContext = new SourceCacheContext();
            var restoreArgs = new RestoreArgs
            {
                CacheContext = cacheContext,
                Log = output,
                PreLoadedRequestProviders = [dgProvider],
                DisableParallel = Environment.ProcessorCount == 1,
                AllowNoOp = false,
                MachineWideSettings = machineWideSettings,
            };

            NativeAotNuGetTrustStore.Initialize(output, _diagnosticLogger, environment);

            var results = await RestoreRunner.RunAsync(restoreArgs, cancellationToken).ConfigureAwait(false);
            var summary = results.Count > 0 ? results[0] : null;

            if (summary is null)
            {
                output.WriteLine("Error: Restore returned no results");
                throw new NuGetOperationException(output.Text);
            }

            if (!summary.Success)
            {
                var errors = string.Join(
                    Environment.NewLine,
                    summary.Errors?.Select(error => error.Message) ?? ["Unknown error"]);
                output.WriteLine($"Error: Restore failed: {errors}");
                throw new NuGetOperationException(output.Text);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not NuGetOperationException)
        {
            output.WriteLine($"Error: {ex.Message}");
            if (output.Verbose)
            {
                output.WriteLine(ex.ToString());
            }

            // The original exception can contain credential-bearing source URLs. Diagnostics,
            // including the full exception when debug logging is enabled, are captured through the
            // redacting operation output, so do not retain the unsafe object in the outer log chain.
            throw new NuGetOperationException(output.Text);
        }
    }

    private static PackageSpec BuildPackageSpec(
        IReadOnlyList<(string Id, string Version)> packages,
        NuGetFramework framework,
        string? runtimeIdentifier,
        string outputPath,
        List<PackageSource> sources,
        ISettings settings,
        string? globalPackagesFolderOverride)
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
            PackagesPath = globalPackagesFolderOverride ?? SettingsUtility.GetGlobalPackagesFolder(settings),
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
        using var operation = BeginOperation();
        var output = new NuGetOperationOutput(logger);
        try
        {
            // Asset selection is delegated to NuGet's own restore output. The assets file already
            // records which assemblies, resources, and native libraries apply to this target, so the
            // manifest reflects NuGet's RID fallback, `_._` placeholder, and locale semantics instead
            // of a reimplementation of them.
            var resolution = NuGetPackageAssetResolver.Resolve(
                assetsFilePath,
                framework,
                runtimeIdentifier,
                output.Verbose ? output.WriteDiagnostic : null);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine($"Error: {ex.Message}");
            if (output.Verbose)
            {
                output.WriteLine(ex.ToString());
            }

            throw new NuGetOperationException(output.Text);
        }
    }

    public async Task<IReadOnlyList<NuGetSearchResult>> SearchAsync(
        string query,
        bool prerelease,
        int take,
        IReadOnlyList<string> explicitSources,
        string? nugetConfigPath,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var operation = BeginOperation();
        var output = new NuGetOperationOutput(logger);
        try
        {
            var settings = LoadSettings(nugetConfigPath, workingDirectory);
            var packageSources = LoadPackageSources(settings, explicitSources, output);
            var searchFilter = new global::NuGet.Protocol.Core.Types.SearchFilter(prerelease);

            var searchResults = await Task.WhenAll(packageSources.Select(source => SearchSourceSafelyAsync(
                source,
                query,
                searchFilter,
                take,
                output,
                cancellationToken))).ConfigureAwait(false);

            // Shape the results exactly as the helper did, including its comparers. Versions are compared as strings
            // rather than as NuGet versions because that is what decided which source's entry survived deduplication.
            return searchResults
                .SelectMany(packages => packages)
                .GroupBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(package => package.Version).First())
                .OrderBy(package => package.Id)
                .Take(take)
                .ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine($"Error: {ex.Message}");
            if (output.Verbose)
            {
                output.WriteLine(ex.ToString());
            }

            throw new NuGetOperationException(output.Text);
        }
    }

    private static async Task<IReadOnlyList<NuGetSearchResult>> SearchSourceSafelyAsync(
        PackageSource source,
        string query,
        global::NuGet.Protocol.Core.Types.SearchFilter filter,
        int take,
        NuGetOperationOutput output,
        CancellationToken cancellationToken)
    {
        try
        {
            return await SearchSourceAsync(source, query, filter, take, output, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Like the helper, report the failed source and keep the results from the others. The helper wrote the
            // exception message, but NuGet protocol failures format the feed URL into it -- often a derived resource
            // URL rather than the configured source -- which would leak UserInfo/SAS credentials into ~/.aspire/logs.
            output.WriteLine($"Warning: Failed to search {PackageSourceRedactor.RedactForDisplay(source.Name)}: {ex.GetType().Name}");
            return [];
        }
    }

    /// <summary>
    /// Starts a NuGet operation and returns the scope that ends it.
    /// </summary>
    /// <remarks>
    /// NuGet keeps process-wide state between operations: the credential service with its cached credentials,
    /// credential provider plugin processes, the HTTP throttle, and other caches. The helper discarded all of it by
    /// exiting after every operation. The CLI can live much longer -- for example for an entire <c>aspire run</c> --
    /// so when the last overlapping operation ends, NuGet's own end-of-build reset is raised to discard that state the
    /// same way. Operations are counted so one ending cannot reset state another is still using.
    /// </remarks>
    internal IDisposable BeginOperation(IReadOnlyList<string>? sensitiveSources = null)
    {
        lock (s_operationLock)
        {
            var diagnosticScope = _diagnosticLogger.RegisterSensitiveSources(sensitiveSources ?? []);

            try
            {
                s_activeOperationCount++;

                // Credential providers are a deliberate addition over the aspire-managed helper, which never set up NuGet's
                // credential service and so could only authenticate with credentials stored in nuget.config. The service
                // is set up per operation because the reset at the end of the previous one discards it; this is a no-op
                // while an overlapping operation still has it set up.
                DefaultCredentialServiceUtility.SetupDefaultCredentialService(_diagnosticLogger, nonInteractive: true);
            }
            catch
            {
                s_activeOperationCount--;
                diagnosticScope.Dispose();
                throw;
            }

            return new OperationScope(_diagnosticLogger, diagnosticScope);
        }
    }

    private sealed class OperationScope(
        INuGetLogger diagnosticLogger,
        IDisposable diagnosticScope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (s_operationLock)
            {
                try
                {
                    if (--s_activeOperationCount != 0)
                    {
                        return;
                    }

                    // Raised under the lock so an operation starting concurrently cannot set up state that this reset
                    // then discards.
                    try
                    {
                        global::NuGet.Common.StaticState.RaiseBuildEnded();
                    }
                    catch (Exception ex)
                    {
                        // Reset handlers tear down plugin processes. A failure there must not turn a completed operation
                        // into a failed one.
                        diagnosticLogger.LogDebug($"Failed to reset NuGet process state: {ex}");
                    }
                }
                finally
                {
                    diagnosticScope.Dispose();
                }
            }
        }
    }

    private static async Task<IReadOnlyList<NuGetSearchResult>> SearchSourceAsync(
        PackageSource source,
        string query,
        global::NuGet.Protocol.Core.Types.SearchFilter filter,
        int take,
        INuGetLogger nuGetLogger,
        CancellationToken cancellationToken)
    {
        var repository = Repository.Factory.GetCoreV3(source);
        var searchResource = await repository.GetResourceAsync<PackageSearchResource>(cancellationToken).ConfigureAwait(false);
        if (searchResource is null)
        {
            return [];
        }

        // The helper requested a single page starting at the first result; it never paged further.
        var results = await searchResource.SearchAsync(
            query,
            filter,
            skip: 0,
            take,
            nuGetLogger,
            cancellationToken).ConfigureAwait(false);

        var packages = new List<NuGetSearchResult>();
        foreach (var result in results)
        {
            var versions = await result.GetVersionsAsync().ConfigureAwait(false);
            packages.Add(new NuGetSearchResult(
                result.Identity.Id,
                result.Identity.Version.ToString(),
                source.Source,
                versions?.Select(version => version.Version.ToString()).ToArray() ?? []));
        }

        return packages;
    }

    private static ISettings LoadSettings(string? nugetConfigPath, string workingDirectory)
    {
        // A config path that does not exist falls back to normal discovery, as it did in the helper.
        if (!string.IsNullOrEmpty(nugetConfigPath) && File.Exists(nugetConfigPath))
        {
            return Settings.LoadSpecificSettings(
                Path.GetDirectoryName(nugetConfigPath)!,
                Path.GetFileName(nugetConfigPath));
        }

        return Settings.LoadDefaultSettings(workingDirectory);
    }

    private static List<PackageSource> LoadPackageSources(
        ISettings settings,
        IReadOnlyList<string> explicitSources,
        NuGetOperationOutput output)
    {
        var sources = explicitSources.Select(source => new PackageSource(source)).ToList();

        if (sources.Count == 0)
        {
            sources.AddRange(new PackageSourceProvider(settings)
                .LoadPackageSources()
                .Where(source => source.IsEnabled));
        }

        if (sources.Count == 0)
        {
            sources.Add(new PackageSource(NuGetOrgUrl, "nuget.org"));
            output.WriteLine("Note: No package sources configured, using nuget.org as fallback.");
        }

        return sources;
    }

    private static List<PackageSource> ResolvePackageSources(
        ISettings settings,
        IReadOnlyList<string> cliSources,
        bool addNuGetOrgFallback)
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

        if (addNuGetOrgFallback &&
            !sources.Any(source => source.Source.Equals(NuGetOrgUrl, StringComparison.OrdinalIgnoreCase)))
        {
            sources.Add(new PackageSource(NuGetOrgUrl, "nuget.org"));
        }

        return sources;
    }

    public NuGetSettingsInfo GetSettings(string workingDirectory, byte[] sourceIdentityKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(sourceIdentityKey);

        var settings = Settings.LoadDefaultSettings(
            workingDirectory,
            configFileName: null,
            new XPlatMachineWideSetting());
        var packageSourceProvider = new PackageSourceProvider(settings);
        var packageSources = packageSourceProvider.LoadPackageSources().ToArray();
        var auditSources = packageSourceProvider.LoadAuditSources().ToArray();
        var sources = packageSources
            .Select(source => CreateSourceInfo(source, sourceIdentityKey))
            .ToArray();
        var sensitiveSourceValues = packageSources
            .Concat(auditSources)
            .Select(static source => source.Source)
            .Where(NuGetSourceIdentity.HasCredentialMaterial)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var packageSourceMappings = new PackageSourceMappingProvider(settings)
            .GetPackageSourceMappingItems()
            .Select(static mapping => new NuGetPackageSourceMapping(
                mapping.Key,
                mapping.Patterns.Select(static pattern => pattern.Pattern).ToArray()))
            .ToArray();
        var disabledPackageSourceKeys = settings
            .GetSection(ConfigurationConstants.DisabledPackageSources)?
            .Items
            .OfType<AddItem>()
            .Select(static item => item.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        var credentialSourceKeys = settings
            .GetSection(ConfigurationConstants.CredentialsSectionName)?
            .Items
            .OfType<CredentialsItem>()
            .Select(static item => item.ElementName) ?? [];
        var clientCertificateSourceKeys = settings
            .GetSection(ConfigurationConstants.ClientCertificates)?
            .Items
            .OfType<ClientCertItem>()
            .Select(static item => item.PackageSource) ?? [];
        var reservedPackageSourceKeys = sources
            .Select(static source => source.Name)
            .Concat(packageSourceMappings.Select(static mapping => mapping.SourceKey))
            .Concat(disabledPackageSourceKeys)
            .Concat(credentialSourceKeys)
            .Concat(clientCertificateSourceKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new NuGetSettingsInfo(
            settings.GetConfigFilePaths().ToArray(),
            ComputeSettingsCacheIdentity(settings, packageSources, auditSources, packageSourceMappings),
            sources,
            sensitiveSourceValues,
            packageSourceMappings.Length > 0,
            packageSourceMappings,
            disabledPackageSourceKeys,
            reservedPackageSourceKeys,
            sourceIdentityKey);
    }

    public void WriteConfigOverlay(NuGetConfigOverlayRequest request, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var outputFile = new FileInfo(outputPath);
        outputFile.Directory?.Create();
        outputFile.Delete();

        var settings = new Settings(
            outputFile.DirectoryName!,
            outputFile.Name,
            isMachineWide: false);

        // A new Settings file starts with NuGet.org. An Aspire policy overlay contains only
        // entries selected by the caller so ambient sources continue to come from the hierarchy.
        var defaultSources = settings
            .GetSection(ConfigurationConstants.PackageSources)?
            .Items
            .OfType<SourceItem>()
            .ToArray() ?? [];
        foreach (var defaultSource in defaultSources)
        {
            settings.Remove(ConfigurationConstants.PackageSources, defaultSource);
        }

        foreach (var source in request.Sources)
        {
            settings.AddOrUpdate(
                ConfigurationConstants.PackageSources,
                new SourceItem(source.Key, source.Source));
        }

        if (request.ClearDisabledPackageSources)
        {
            settings.AddOrUpdate(
                ConfigurationConstants.DisabledPackageSources,
                new ClearItem());
            foreach (var sourceKey in request.DisabledPackageSourceKeys)
            {
                settings.AddOrUpdate(
                    ConfigurationConstants.DisabledPackageSources,
                    new AddItem(sourceKey, "true"));
            }
        }

        if (request.PackageSourceMappings.Length > 0)
        {
            settings.AddOrUpdate(
                ConfigurationConstants.PackageSourceMapping,
                new ClearItem());
            var mappings = request.PackageSourceMappings
                .Select(static mapping => new PackageSourceMappingSourceItem(
                    mapping.SourceKey,
                    mapping.Patterns.Select(static pattern => new PackagePatternItem(pattern))))
                .ToArray();
            new PackageSourceMappingProvider(settings, shouldSkipSave: true)
                .SavePackageSourceMappings(mappings);
        }

        if (!string.IsNullOrEmpty(request.GlobalPackagesFolder))
        {
            settings.AddOrUpdate(
                ConfigurationConstants.Config,
                new AddItem(
                    ConfigurationConstants.GlobalPackagesFolder,
                    request.GlobalPackagesFolder));
        }

        settings.SaveToDisk();
    }

    private static string ComputeSettingsCacheIdentity(
        ISettings settings,
        IReadOnlyList<PackageSource> packageSources,
        IReadOnlyList<PackageSource> auditSources,
        IReadOnlyList<NuGetPackageSourceMapping> packageSourceMappings)
    {
        var hash = new XxHash3();

        foreach (var configPath in settings.GetConfigFilePaths())
        {
            AppendCacheIdentityValue(hash, "config-path");
            AppendCacheIdentityValue(hash, configPath);
        }

        AppendPackageSources(hash, "package-source", packageSources);
        AppendPackageSources(hash, "audit-source", auditSources);

        var signatureValidationMode = settings
            .GetSection(ConfigurationConstants.Config)?
            .Items
            .OfType<AddItem>()
            .LastOrDefault(static item => string.Equals(
                item.Key,
                "signatureValidationMode",
                StringComparison.OrdinalIgnoreCase))?
            .Value;
        if (signatureValidationMode is not null)
        {
            AppendCacheIdentityValue(hash, "signature-validation-mode");
            AppendCacheIdentityValue(hash, signatureValidationMode);
        }

        var trustedSigners = settings
            .GetSection(ConfigurationConstants.TrustedSigners)?
            .Items
            .OfType<TrustedSignerItem>()
            .OrderBy(static signer => signer.GetType().Name, StringComparer.Ordinal)
            .ThenBy(static signer => signer.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        foreach (var trustedSigner in trustedSigners)
        {
            AppendCacheIdentityValue(hash, "trusted-signer");
            AppendCacheIdentityValue(hash, trustedSigner.GetType().Name);
            AppendSettingItemAttributes(hash, trustedSigner);
            foreach (var certificate in trustedSigner.Certificates
                .OrderBy(static certificate => certificate.Fingerprint, StringComparer.OrdinalIgnoreCase))
            {
                AppendCacheIdentityValue(hash, "trusted-signer-certificate");
                AppendSettingItemAttributes(hash, certificate);
            }
        }

        foreach (var mapping in packageSourceMappings)
        {
            AppendCacheIdentityValue(hash, "package-source-mapping");
            AppendCacheIdentityValue(hash, mapping.SourceKey);
            foreach (var pattern in mapping.Patterns)
            {
                AppendCacheIdentityValue(hash, pattern);
            }
        }

        var pathContext = NuGetPathContext.Create(settings);
        AppendCacheIdentityValue(hash, "global-packages");
        AppendCacheIdentityValue(hash, pathContext.UserPackageFolder);
        foreach (var fallbackPackageFolder in pathContext.FallbackPackageFolders)
        {
            AppendCacheIdentityValue(hash, "fallback-packages");
            AppendCacheIdentityValue(hash, fallbackPackageFolder);
        }

        return Convert.ToHexString(hash.GetCurrentHash());
    }

    private static void AppendSettingItemAttributes(XxHash3 hash, SettingItem item)
    {
        foreach (var attribute in item.GetAttributes().OrderBy(static attribute => attribute.Key, StringComparer.Ordinal))
        {
            AppendCacheIdentityValue(hash, attribute.Key);
            AppendCacheIdentityValue(hash, attribute.Value);
        }
    }

    private static void AppendPackageSources(
        XxHash3 hash,
        string kind,
        IReadOnlyList<PackageSource> sources)
    {
        foreach (var source in sources)
        {
            AppendCacheIdentityValue(hash, kind);
            AppendCacheIdentityValue(hash, source.Name);
            AppendCacheIdentityValue(hash, source.Source);
            AppendCacheIdentityValue(hash, source.IsEnabled.ToString(CultureInfo.InvariantCulture));
            AppendCacheIdentityValue(hash, source.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
            AppendCacheIdentityValue(hash, source.AllowInsecureConnections.ToString(CultureInfo.InvariantCulture));
            AppendCacheIdentityValue(hash, source.DisableTLSCertificateValidation.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void AppendCacheIdentityValue(XxHash3 hash, string value)
    {
        hash.Append(Encoding.UTF8.GetBytes(value.Length.ToString(CultureInfo.InvariantCulture)));
        hash.Append(":"u8);
        hash.Append(Encoding.UTF8.GetBytes(value));
        hash.Append("\n"u8);
    }

    private static NuGetSourceInfo CreateSourceInfo(PackageSource source, byte[] identityKey)
        => new(
            source.Name,
            NuGetSourceIdentity.Compute(source.Source, identityKey),
            source.IsEnabled,
            source.Credentials is not null,
            source.ClientCertificates is { Count: > 0 });

    /// <summary>
    /// Records one operation's output the way the aspire-managed helper wrote it to stderr.
    /// </summary>
    /// <remarks>
    /// The helper's stderr was the only diagnostic channel back to the CLI: the CLI logged it and put it into the
    /// exception message when the operation failed. This buffers the same text for <see cref="NuGetOperationException"/>
    /// and also streams each entry to the debug log, where the CLI logged successful operations' stderr.
    /// </remarks>
    private sealed class NuGetOperationOutput(
        ILogger logger,
        IReadOnlyList<string>? sensitiveSources = null) : INuGetLogger
    {
        private readonly Lock _lock = new();
        private readonly StringBuilder _text = new();

        /// <summary>
        /// Gets whether the helper would have run with <c>--verbose</c>. The CLI passed that flag exactly when its
        /// logger had debug logging enabled.
        /// </summary>
        public bool Verbose { get; } = logger.IsEnabled(LogLevel.Debug);

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return _text.ToString();
                }
            }
        }

        public void WriteLine(string text)
        {
            text = PackageSourceRedactor.RedactOccurrences(text, sensitiveSources ?? []);

            // The CLI read the helper's stderr line by line and re-joined it with AppendLine, which normalized any
            // line breaks embedded in a message to Environment.NewLine. Split the same way so the text matches.
            lock (_lock)
            {
                foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
                {
                    _text.AppendLine(line);
                }
            }

            logger.LogDebug("{Message}", text);
        }

        /// <summary>
        /// Logs text the helper wrote to stdout. The CLI only surfaced stdout when an operation failed, so it goes to
        /// the debug log without becoming part of <see cref="Text"/>.
        /// </summary>
        public void WriteDiagnostic(string text) =>
            logger.LogDebug("{Message}", PackageSourceRedactor.RedactOccurrences(text, sensitiveSources ?? []));

        public void Log(NuGetLogLevel level, string data)
        {
            // Same filtering and prefixes as the helper's NuGet logger.
            if (!Verbose && level < NuGetLogLevel.Warning)
            {
                return;
            }

            var prefix = level switch
            {
                NuGetLogLevel.Error => "ERROR: ",
                NuGetLogLevel.Warning => "WARNING: ",
                _ => ""
            };

            WriteLine($"{prefix}{data}");
        }

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
    }

    /// <summary>
    /// Sends NuGet output that has no helper equivalent to the debug log only.
    /// </summary>
    internal sealed class DiagnosticNuGetLogger(ILogger logger) : INuGetLogger
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, int> _sourceRegistrations = new(StringComparer.Ordinal);
        private string[] _sensitiveSources = [];

        internal IDisposable RegisterSensitiveSources(IReadOnlyList<string> sensitiveSources)
        {
            var registeredSources = sensitiveSources
                .Where(static source => !string.IsNullOrEmpty(source))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            lock (_lock)
            {
                foreach (var source in registeredSources)
                {
                    _sourceRegistrations[source] = _sourceRegistrations.GetValueOrDefault(source) + 1;
                }

                _sensitiveSources = [.. _sourceRegistrations.Keys];
            }

            return new SensitiveSourceScope(this, registeredSources);
        }

        public void Log(NuGetLogLevel level, string data)
        {
            string[] sensitiveSources;
            lock (_lock)
            {
                sensitiveSources = _sensitiveSources;
            }

            logger.LogDebug(
                "{Message}",
                PackageSourceRedactor.RedactOccurrences(data, sensitiveSources));
        }

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

        private void UnregisterSensitiveSources(IReadOnlyList<string> sensitiveSources)
        {
            lock (_lock)
            {
                foreach (var source in sensitiveSources)
                {
                    var registrationCount = _sourceRegistrations[source];
                    if (registrationCount == 1)
                    {
                        _sourceRegistrations.Remove(source);
                    }
                    else
                    {
                        _sourceRegistrations[source] = registrationCount - 1;
                    }
                }

                _sensitiveSources = [.. _sourceRegistrations.Keys];
            }
        }

        private sealed class SensitiveSourceScope(
            DiagnosticNuGetLogger logger,
            IReadOnlyList<string> sensitiveSources) : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    logger.UnregisterSensitiveSources(sensitiveSources);
                }
            }
        }
    }

    private static class NativeAotNuGetTrustStore
    {
        private static readonly object s_lock = new();
        private static bool s_initialized;

        public static void Initialize(NuGetOperationOutput output, INuGetLogger diagnosticLogger, IEnvironment environment)
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

                try
                {
                    InitializeFromEmbeddedResources(diagnosticLogger);
                    s_initialized = true;
                }
                catch (Exception ex)
                {
                    // Like the helper, report the failure but let the restore continue. NuGet can still succeed when
                    // these packages do not need verification or the system provides its own certificate bundles.
                    output.WriteLine($"WARNING: Failed to initialize NuGet trust store from embedded certificates: {ex}");
                }
            }
        }

        private static void InitializeFromEmbeddedResources(INuGetLogger diagnosticLogger)
        {
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
                X509TrustStore.InitializeForDotNetSdk(diagnosticLogger);
            }
            finally
            {
                AppContext.SetData("Microsoft.DotNet.Sdk.Root", previousSdkRoot);

                // The factories hold the certificates they loaded, so a directory that cannot be removed is only a
                // leftover temp file and must not undo a successful initialization.
                try
                {
                    rootDirectory.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diagnosticLogger.LogDebug($"Failed to delete temporary trust store directory '{rootDirectory.FullName}': {ex.Message}");
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
