// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
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
    private readonly IntegrationLoadContext _loadContext;
    private readonly IntegrationPackageProbeManifest _packageProbeManifest;

    public AssemblyLoader(IConfiguration configuration, ILogger<AssemblyLoader> logger)
    {
        _applicationBasePath = AppContext.BaseDirectory;
        var libsPath = configuration[KnownConfigNames.IntegrationLibsPath];
        var probeManifestPath = configuration[KnownConfigNames.IntegrationProbeManifestPath];
        _packageProbeManifest = IntegrationPackageProbeManifest.Load(probeManifestPath);
        _loadContext = CreateLoadContext(libsPath, _applicationBasePath, _packageProbeManifest, logger);

        // ASPIRE_INTEGRATION_LIBS_PATH is set by the CLI when running guest (polyglot) apphosts
        // that require additional hosting integration packages. See docs/specs/bundle.md for details.
        logger.LogDebug(
            "Using load context {LoadContext} for integration assemblies. Integration libs path: {Path}. Probe manifest path: {ProbeManifestPath}",
            _loadContext.Name ?? "<unknown>",
            string.IsNullOrWhiteSpace(libsPath) ? "<none>" : libsPath,
            string.IsNullOrWhiteSpace(probeManifestPath) ? "<none>" : probeManifestPath);

        _assemblies = new Lazy<IReadOnlyList<Assembly>>(
            () => LoadAssemblies(configuration, _loadContext, logger, libsPath, _applicationBasePath, _packageProbeManifest));
    }

    public IReadOnlyList<Assembly> GetAssemblies() => _assemblies.Value;

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
        IConfiguration configuration,
        IntegrationLoadContext loadContext,
        ILogger logger,
        string? integrationLibsPath,
        string applicationBasePath,
        IntegrationPackageProbeManifest packageProbeManifest)
    {
        var assemblyNames = GetAssemblyNamesToLoad(configuration, integrationLibsPath, applicationBasePath, packageProbeManifest);
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
