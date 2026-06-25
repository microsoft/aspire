// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Service that loads assemblies on demand with proper logging.
/// </summary>
internal sealed class AssemblyLoader
{
    private readonly Lazy<IReadOnlyList<Assembly>> _assemblies;
    private readonly string _applicationBasePath;
    private readonly Lazy<IReadOnlyList<string>> _assemblyNamesToLoad;
    private readonly IntegrationLoadContext _loadContext;
    private readonly IntegrationPackageProbeManifest _packageProbeManifest;
    private readonly RemoteHostProfilingTelemetry _profilingTelemetry;

    public AssemblyLoader(
        IConfiguration configuration,
        ILogger<AssemblyLoader> logger,
        RemoteHostProfilingTelemetry profilingTelemetry)
    {
        _profilingTelemetry = profilingTelemetry;
        _applicationBasePath = AppContext.BaseDirectory;
        var libsPath = configuration[KnownConfigNames.IntegrationLibsPath];
        var probeManifestPath = configuration[KnownConfigNames.IntegrationProbeManifestPath];
        _packageProbeManifest = IntegrationPackageProbeManifest.Load(probeManifestPath);
        _loadContext = CreateLoadContext(libsPath, _applicationBasePath, _packageProbeManifest, logger);
        _assemblyNamesToLoad = new Lazy<IReadOnlyList<string>>(
            () => GetAssemblyNamesToLoad(configuration, libsPath, _applicationBasePath, _packageProbeManifest));

        // ASPIRE_INTEGRATION_LIBS_PATH is set by the CLI when running guest (polyglot) apphosts
        // that require additional hosting integration packages. See docs/specs/bundle.md for details.
        logger.LogDebug(
            "Using load context {LoadContext} for integration assemblies. Integration libs path: {Path}. Probe manifest path: {ProbeManifestPath}",
            _loadContext.Name ?? "<unknown>",
            string.IsNullOrWhiteSpace(libsPath) ? "<none>" : libsPath,
            string.IsNullOrWhiteSpace(probeManifestPath) ? "<none>" : probeManifestPath);

        WarnIfSharedAssemblyMismatch(libsPath, logger);

        _assemblies = new Lazy<IReadOnlyList<Assembly>>(
            () => LoadAssemblies(_assemblyNamesToLoad.Value, _loadContext, logger));
    }

    public IReadOnlyList<Assembly> GetAssemblies()
    {
        var cacheHit = _assemblies.IsValueCreated;
        using var activity = _profilingTelemetry.StartAssemblyLoad(cacheHit);
        activity.SetAssemblyRequestedNames(_assemblyNamesToLoad.Value);
        try
        {
            var assemblies = _assemblies.Value;
            activity.SetAssemblyCount(assemblies.Count);
            activity.SetAssemblyLoadedNames(assemblies);
            return assemblies;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    /// <summary>
    /// Snapshots the currently loaded ATS integration assemblies as
    /// <see cref="CodeGenerationLoadedAssemblyInfo"/> records suitable for inclusion in a
    /// diagnostic payload. Returns an empty list when no assemblies have been loaded yet so the
    /// caller can include the result unconditionally.
    /// </summary>
    /// <remarks>
    /// This intentionally avoids forcing <see cref="GetAssemblies"/> to run if it hasn't already,
    /// because we want to capture the actual state at the moment a failure occurred rather than
    /// triggering the load (which may itself throw).
    /// </remarks>
    public IReadOnlyList<CodeGenerationLoadedAssemblyInfo> GetLoadedAssemblyDiagnostics()
    {
        var infos = new List<CodeGenerationLoadedAssemblyInfo>();
        if (!_assemblies.IsValueCreated)
        {
            return infos;
        }

        foreach (var assembly in _assemblies.Value)
        {
            infos.Add(CreateAssemblyInfo(assembly));
        }

        return infos;
    }

    private static CodeGenerationLoadedAssemblyInfo CreateAssemblyInfo(Assembly assembly)
    {
        var name = assembly.GetName();
        string? location;
        try
        {
            location = string.IsNullOrEmpty(assembly.Location) ? null : assembly.Location;
        }
        catch
        {
            location = null;
        }

        return new CodeGenerationLoadedAssemblyInfo
        {
            Name = name.Name ?? assembly.FullName ?? "<unknown>",
            InformationalVersion = CodeGenerationDiagnosticBuilder.GetInformationalVersion(assembly),
            Location = location
        };
    }

    internal static IReadOnlyList<string> GetAssemblyNamesToLoad(
        IConfiguration configuration,
        string? integrationLibsPath,
        string applicationBasePath,
        IntegrationPackageProbeManifest? packageProbeManifest = null)
    {
        var assemblyNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in configuration.GetSection("AtsAssemblies").Get<string[]>() ?? [])
        {
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                assemblyNames.Add(name);
            }
        }

        foreach (var name in DiscoverAspireHostingAssemblies([integrationLibsPath, applicationBasePath], packageProbeManifest?.RuntimeAssemblyNames))
        {
            if (seen.Add(name))
            {
                assemblyNames.Add(name);
            }
        }

        return assemblyNames;
    }

    internal static IReadOnlyList<string> DiscoverAspireHostingAssemblies(IEnumerable<string?> directories, IEnumerable<string>? manifestAssemblyNames = null)
    {
        var assemblyNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "Aspire*.dll", SearchOption.TopDirectoryOnly))
            {
                var assemblyName = Path.GetFileNameWithoutExtension(file);
                if (IsAutoDiscoveredAssembly(assemblyName))
                {
                    assemblyNames.Add(assemblyName);
                }
            }
        }

        foreach (var assemblyName in manifestAssemblyNames ?? [])
        {
            if (IsAutoDiscoveredAssembly(assemblyName))
            {
                assemblyNames.Add(assemblyName);
            }
        }

        return assemblyNames.ToList();
    }

    private static bool IsAutoDiscoveredAssembly(string? assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return false;
        }

        if (assemblyName.Equals("Aspire.Hosting.AppHost", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith("Aspire.AppHost.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return assemblyName.Equals("Aspire.Hosting", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.StartsWith("Aspire.Hosting.", StringComparison.OrdinalIgnoreCase);
    }

    private static List<Assembly> LoadAssemblies(
        IReadOnlyList<string> assemblyNames,
        IntegrationLoadContext loadContext,
        ILogger logger)
    {
        var assemblies = new List<Assembly>();

        foreach (var name in assemblyNames)
        {
            try
            {
                var assembly = LoadAssembly(loadContext, name);
                assemblies.Add(assembly);
                logger.LogDebug("Loaded assembly: {AssemblyName} in {LoadContext}",
                    assembly.FullName,
                    AssemblyLoadContext.GetLoadContext(assembly)?.Name ?? "<unknown>");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load assembly '{AssemblyName}'", name);
            }
        }

        return assemblies;
    }

    /// <summary>
    /// Warns when a shared assembly (one that <see cref="IntegrationLoadContext"/> intentionally
    /// resolves through the default <see cref="AssemblyLoadContext"/>) exists in the integration
    /// libs directory at a different identity than what the default context provides.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a defense against a real failure mode: when the bundled <c>Aspire.TypeSystem</c>
    /// (compiled into the apphost server's single-file executable) and the libs copy (restored
    /// alongside <c>Aspire.Hosting.*.dll</c>) report different assembly versions or MVIDs,
    /// integration assemblies that reference the libs copy will fail to bind their type references
    /// through the default context. The resulting <see cref="ReflectionTypeLoadException"/> would
    /// otherwise be swallowed silently and surface only as a downstream "no code generator found"
    /// or "no language support found" error with no actionable diagnostic.
    /// </para>
    /// <para>
    /// <c>Aspire.TypeSystem</c> freezes its strong-name <c>AssemblyVersion</c> at a fixed constant
    /// (see <c>src/Aspire.TypeSystem/Aspire.TypeSystem.csproj</c> for the full rationale). The CLR
    /// satisfies a strong-named reference when the loaded (bundled) copy's version is <c>&gt;=</c>
    /// the requested version, so the only failing configuration is a bundled copy STRICTLY LOWER
    /// than the libs copy -- an already-shipped old CLI paired with post-freeze libs. The warning
    /// below trips only on that case; the supported "new CLI + older SDK" direction (bundled
    /// <c>&gt;=</c> libs, including a same-version/differing-MVID daily-SDK pairing) binds and is
    /// logged at Debug only.
    /// </para>
    /// </remarks>
    private static void WarnIfSharedAssemblyMismatch(string? integrationLibsPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(integrationLibsPath) || !Directory.Exists(integrationLibsPath))
        {
            return;
        }

        foreach (var sharedName in IntegrationLoadContext.GetSharedAssemblyNames())
        {
            var libsPath = Path.Combine(integrationLibsPath, sharedName + ".dll");
            if (!File.Exists(libsPath))
            {
                continue;
            }

            AssemblyName? probedName;
            try
            {
                probedName = AssemblyName.GetAssemblyName(libsPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read assembly identity from {Path}", libsPath);
                continue;
            }

            var defaultAsm = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(
                assembly => string.Equals(assembly.GetName().Name, sharedName, StringComparison.OrdinalIgnoreCase));
            if (defaultAsm is null)
            {
                logger.LogDebug("Default context does not currently provide '{AssemblyName}'", sharedName);
                continue;
            }

            var defaultName = defaultAsm.GetName();
            var defaultMvid = defaultAsm.ManifestModule.ModuleVersionId;

            if (defaultName.Version < probedName.Version)
            {
                // Bundled copy strictly lower: it cannot satisfy the libs' strong-named reference,
                // so integrations are silently skipped during type discovery -- surface it.
                logger.LogWarning(
                    "Shared assembly '{AssemblyName}' version too low: bundled={BundledVersion}, libs={LibsVersion} ({LibsPath}). " +
                    "The bundled copy is older than the integration packages and cannot satisfy their strong-named reference, " +
                    "so integration assemblies will fail to bind their type references through the default load context and be " +
                    "silently skipped during type discovery. The apphost server (CLI) is older than the restored Aspire packages; " +
                    "update the Aspire CLI to a version at least as new as the packages.",
                    sharedName,
                    defaultName.Version,
                    probedName.Version,
                    libsPath);
                continue;
            }

            // Bundled version >= libs version: the bundled copy satisfies the libs' strong-named
            // reference (strong-name binding keys on version + public key token, not MVID), so it
            // binds. Logged at Debug to correlate with any unrelated ReflectionTypeLoadException;
            // we can't read the probed MVID without loading the assembly, which we avoid here.
            logger.LogDebug("Shared assembly '{AssemblyName}' satisfies libs reference: bundled={BundledVersion}, libs={LibsVersion}, BundledMvid={Mvid}",
                sharedName, defaultName.Version, probedName.Version, defaultMvid);
        }
    }

    private static Assembly LoadAssembly(IntegrationLoadContext loadContext, string name)
    {
        var assemblyName = new AssemblyName(name);
        return loadContext.LoadFromAssemblyName(assemblyName);
    }

    private static IntegrationLoadContext CreateLoadContext(
        string? integrationLibsPath,
        string applicationBasePath,
        IntegrationPackageProbeManifest packageProbeManifest,
        ILogger logger)
    {
        var probeDirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(integrationLibsPath) && Directory.Exists(integrationLibsPath))
        {
            probeDirs.Add(Path.GetFullPath(integrationLibsPath));
        }
        if (Directory.Exists(applicationBasePath))
        {
            probeDirs.Add(Path.GetFullPath(applicationBasePath));
        }

        return new IntegrationLoadContext([.. probeDirs], packageProbeManifest, logger);
    }
}
