// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
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
    /// This is a defense against a real failure mode: when the bundled <c>Aspire.TypeSystem</c>
    /// (compiled into the apphost server's single-file executable) and the libs copy
    /// (restored alongside <c>Aspire.Hosting.*.dll</c>) report different assembly versions or MVIDs,
    /// integration assemblies that reference the libs copy will fail to bind their type
    /// references through the default context. The resulting <see cref="ReflectionTypeLoadException"/>
    /// would otherwise be swallowed silently and surface only as a downstream "no code generator
    /// found" / "no language support found" error with no actionable diagnostic.
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

            if (defaultName.Version != probedName.Version)
            {
                logger.LogWarning(
                    "Shared assembly '{AssemblyName}' version mismatch: bundled={BundledVersion}, libs={LibsVersion} ({LibsPath}). " +
                    "Integration assemblies referencing this assembly from the libs directory will fail to bind their type " +
                    "references through the default load context, which causes integrations to be silently skipped during type discovery. " +
                    "This typically indicates the apphost server bundle and the restored integration packages were produced by " +
                    "different build configurations.",
                    sharedName,
                    defaultName.Version,
                    probedName.Version,
                    libsPath);
                continue;
            }

            // Same version, but different MVID (compiled from different sources) is also a binary-incompatibility risk.
            // We can't read the probed MVID without loading the assembly, which we deliberately don't do here.
            // Logging the bundled MVID at Debug helps correlate with any subsequent ReflectionTypeLoadException.
            logger.LogDebug("Shared assembly '{AssemblyName}' identity matches: Version={Version}, BundledMvid={Mvid}",
                sharedName, defaultName.Version, defaultMvid);
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
