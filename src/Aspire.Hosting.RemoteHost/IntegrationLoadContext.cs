// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Loads Aspire integration assemblies from probe directories with version unification.
/// If the default load context already provides an assembly at a higher or equal version,
/// the default context wins. Only <c>Aspire.TypeSystem</c> is always shared.
/// </summary>
internal sealed class IntegrationLoadContext : AssemblyLoadContext
{
    private const string SharedAssemblyName = "Aspire.TypeSystem";

    private readonly string[] _probeDirectories;
    private readonly IntegrationPackageProbeManifest _packageProbeManifest;
    private readonly ILogger _logger;

    internal IntegrationLoadContext(string[] probeDirectories, ILogger logger)
        : this(probeDirectories, IntegrationPackageProbeManifest.Empty, logger)
    {
    }

    internal IntegrationLoadContext(string[] probeDirectories, IntegrationPackageProbeManifest packageProbeManifest, ILogger logger)
        : base("Aspire.Integrations")
    {
        _probeDirectories = probeDirectories;
        _packageProbeManifest = packageProbeManifest;
        _logger = logger;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null ||
            string.Equals(assemblyName.Name, SharedAssemblyName, StringComparison.OrdinalIgnoreCase))
        {
            // Returning null tells the runtime to fall back to the default ALC.
            // This ensures Aspire.TypeSystem has the same type identity in both
            // contexts, so shared contracts (ICodeGenerator, ILanguageSupport,
            // AtsContext, etc.) work across the ALC boundary without requiring
            // reflection or marshalling.
            return null;
        }

        var probedPath = _packageProbeManifest.TryGetManagedAssemblyPath(assemblyName)
            ?? TryResolveFromProbeDirectories(assemblyName.Name);

        if (probedPath is null)
        {
            return null;
        }

        // Version unification: if the default context already has this assembly
        // at a higher or equal version (e.g., framework-provided), defer to it.
        if (TryGetDefaultContextVersion(assemblyName, out var defaultVersion))
        {
            var probedVersion = AssemblyName.GetAssemblyName(probedPath).Version;
            if (defaultVersion >= probedVersion)
            {
                _logger.LogDebug("[IntegrationALC] Deferring to default ({DefaultVersion} >= {ProbedVersion}): {AssemblyName}",
                    defaultVersion, probedVersion, assemblyName.Name);
                return null;
            }
        }

        _logger.LogDebug("[IntegrationALC] Loading: {AssemblyName} from {Path}", assemblyName.Name, probedPath);
        return LoadFromAssemblyPath(probedPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        foreach (var path in _packageProbeManifest.GetNativeLibraryPaths(unmanagedDllName))
        {
            try
            {
                _logger.LogDebug("[IntegrationALC] Loading native library: {NativeLibraryName} from {Path}", unmanagedDllName, path);
                return LoadUnmanagedDllFromPath(path);
            }
            catch (DllNotFoundException ex)
            {
                _logger.LogDebug(ex, "[IntegrationALC] Failed to load native library {NativeLibraryName} from {Path}", unmanagedDllName, path);
            }
            catch (BadImageFormatException ex)
            {
                _logger.LogDebug(ex, "[IntegrationALC] Failed to load native library {NativeLibraryName} from {Path}", unmanagedDllName, path);
            }
            catch (FileLoadException ex)
            {
                _logger.LogDebug(ex, "[IntegrationALC] Failed to load native library {NativeLibraryName} from {Path}", unmanagedDllName, path);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "[IntegrationALC] Failed to load native library {NativeLibraryName} from {Path}", unmanagedDllName, path);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogDebug(ex, "[IntegrationALC] Failed to load native library {NativeLibraryName} from {Path}", unmanagedDllName, path);
            }
        }

        return base.LoadUnmanagedDll(unmanagedDllName);
    }

    private static bool TryGetDefaultContextVersion(AssemblyName assemblyName, out Version? version)
    {
        version = null;
        try
        {
            var defaultAsm = Default.LoadFromAssemblyName(assemblyName);
            version = defaultAsm.GetName().Version;
            return version is not null;
        }
        catch
        {
            return false;
        }
    }

    private string? TryResolveFromProbeDirectories(string assemblyName)
    {
        foreach (var dir in _probeDirectories)
        {
            var path = Path.Combine(dir, $"{assemblyName}.dll");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
