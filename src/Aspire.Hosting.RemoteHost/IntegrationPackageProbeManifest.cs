// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Represents the package-backed integration asset probe manifest emitted by the CLI.
/// </summary>
internal sealed class IntegrationPackageProbeManifest
{
    public static IntegrationPackageProbeManifest Empty { get; } = new(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
        []);

    private readonly IReadOnlyDictionary<string, string> _managedAssemblyPaths;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _nativeLibraryPaths;

    private IntegrationPackageProbeManifest(
        IReadOnlyDictionary<string, string> managedAssemblyPaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> nativeLibraryPaths,
        IReadOnlyList<string> runtimeAssemblyNames)
    {
        _managedAssemblyPaths = managedAssemblyPaths;
        _nativeLibraryPaths = nativeLibraryPaths;
        RuntimeAssemblyNames = runtimeAssemblyNames;
    }

    public IReadOnlyList<string> RuntimeAssemblyNames { get; }

    public static IntegrationPackageProbeManifest Load(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return Empty;
        }

        var normalizedManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(normalizedManifestPath))
        {
            throw new InvalidOperationException($"Integration package probe manifest '{normalizedManifestPath}' does not exist.");
        }

        using var stream = File.OpenRead(normalizedManifestPath);
        using var document = JsonDocument.Parse(stream);
        var managedAssemblies = ReadManagedAssemblies(document.RootElement);
        var nativeLibraries = ReadNativeLibraries(document.RootElement);

        return new IntegrationPackageProbeManifest(
            CreateManagedAssemblyLookup(managedAssemblies),
            CreateNativeLibraryLookup(nativeLibraries),
            GetRuntimeAssemblyNames(managedAssemblies));
    }

    public string? TryGetManagedAssemblyPath(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null)
        {
            return null;
        }

        return _managedAssemblyPaths.TryGetValue(
            CreateManagedAssemblyLookupKey(assemblyName.Name, NormalizeCulture(assemblyName.CultureName)),
            out var path)
            ? path
            : null;
    }

    public IReadOnlyList<string> GetNativeLibraryPaths(string unmanagedDllName)
    {
        var candidatePaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in GetNativeLibraryLookupKeys(unmanagedDllName))
        {
            if (!_nativeLibraryPaths.TryGetValue(key, out var paths))
            {
                continue;
            }

            foreach (var path in paths.OrderBy(GetNativePathPriority).ThenBy(static path => path, StringComparer.Ordinal))
            {
                if (seenPaths.Add(path))
                {
                    candidatePaths.Add(path);
                }
            }
        }

        return candidatePaths;
    }

    internal static IReadOnlyList<string> GetNativeLibraryLookupKeys(string libraryName)
    {
        ArgumentException.ThrowIfNullOrEmpty(libraryName);

        var keys = new List<string>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedFileName = Path.GetFileName(libraryName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(normalizedFileName);

        AddKey(normalizedFileName);
        AddKey(fileNameWithoutExtension);

        if (!normalizedFileName.Contains('.'))
        {
            AddKey($"{normalizedFileName}.dll");
            AddKey($"lib{normalizedFileName}.so");
            AddKey($"lib{normalizedFileName}.dylib");
        }

        if (fileNameWithoutExtension.StartsWith("lib", StringComparison.OrdinalIgnoreCase) &&
            fileNameWithoutExtension.Length > 3)
        {
            var withoutLibPrefix = fileNameWithoutExtension[3..];
            AddKey(withoutLibPrefix);

            if (!normalizedFileName.Contains('.'))
            {
                AddKey($"{withoutLibPrefix}.dll");
                AddKey($"lib{withoutLibPrefix}.so");
                AddKey($"lib{withoutLibPrefix}.dylib");
            }
        }
        else
        {
            AddKey($"lib{fileNameWithoutExtension}");

            if (!normalizedFileName.Contains('.'))
            {
                AddKey($"lib{fileNameWithoutExtension}.so");
                AddKey($"lib{fileNameWithoutExtension}.dylib");
            }
        }

        return keys;

        void AddKey(string key)
        {
            if (!string.IsNullOrWhiteSpace(key) && seenKeys.Add(key))
            {
                keys.Add(key);
            }
        }
    }

    private static IReadOnlyDictionary<string, string> CreateManagedAssemblyLookup(IEnumerable<ManagedAssemblyEntry> managedAssemblies)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in managedAssemblies)
        {
            var lookupKey = CreateManagedAssemblyLookupKey(assembly.Name, assembly.Culture);

            if (!lookup.TryAdd(lookupKey, assembly.Path))
            {
                throw new InvalidOperationException(
                    $"Integration package probe manifest contains duplicate managed assembly entry '{assembly.Name}'{(assembly.Culture is null ? string.Empty : $" ({assembly.Culture})")}.");
            }
        }

        return lookup;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CreateNativeLibraryLookup(IEnumerable<NativeLibraryEntry> nativeLibraries)
    {
        var lookup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var nativeLibrary in nativeLibraries)
        {
            foreach (var key in GetNativeLibraryLookupKeys(nativeLibrary.FileName))
            {
                if (!lookup.TryGetValue(key, out var paths))
                {
                    paths = [];
                    lookup[key] = paths;
                }

                if (!paths.Contains(nativeLibrary.Path, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(nativeLibrary.Path);
                }
            }
        }

        return lookup.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> GetRuntimeAssemblyNames(IEnumerable<ManagedAssemblyEntry> managedAssemblies)
    {
        var assemblyNames = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in managedAssemblies)
        {
            if (assembly.Culture is not null)
            {
                continue;
            }

            assemblyNames.Add(assembly.Name);
        }

        return assemblyNames.ToList();
    }

    private static string CreateManagedAssemblyLookupKey(string assemblyName, string? culture)
    {
        return $"{culture ?? "<neutral>"}|{assemblyName}";
    }

    private static int GetNativePathPriority(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        var ridMarker = $"/runtimes/{RuntimeInformation.RuntimeIdentifier}/";
        return normalizedPath.Contains(ridMarker, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private static string NormalizeAndValidatePath(string? path, string propertyName)
    {
        var normalizedPath = Path.GetFullPath(NormalizeRequiredValue(path, propertyName));
        if (!File.Exists(normalizedPath))
        {
            throw new InvalidOperationException($"Integration package probe manifest path '{normalizedPath}' does not exist.");
        }

        return normalizedPath;
    }

    private static string? NormalizeCulture(string? culture)
    {
        return string.IsNullOrWhiteSpace(culture) || string.Equals(culture, "neutral", StringComparison.OrdinalIgnoreCase)
            ? null
            : culture.Trim();
    }

    private static string NormalizeRequiredValue(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Integration package probe manifest entry is missing required property '{propertyName}'.");
        }

        return value.Trim();
    }

    private static IReadOnlyList<ManagedAssemblyEntry> ReadManagedAssemblies(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("managedAssemblies", out var managedAssembliesElement) ||
            managedAssembliesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var managedAssemblies = new List<ManagedAssemblyEntry>();
        foreach (var element in managedAssembliesElement.EnumerateArray())
        {
            managedAssemblies.Add(new ManagedAssemblyEntry(
                NormalizeRequiredValue(ReadStringProperty(element, "name"), "managedAssemblies[].name"),
                NormalizeCulture(ReadStringProperty(element, "culture", required: false)),
                NormalizeAndValidatePath(ReadStringProperty(element, "path"), "managedAssemblies[].path")));
        }

        return managedAssemblies;
    }

    private static IReadOnlyList<NativeLibraryEntry> ReadNativeLibraries(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("nativeLibraries", out var nativeLibrariesElement) ||
            nativeLibrariesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var nativeLibraries = new List<NativeLibraryEntry>();
        foreach (var element in nativeLibrariesElement.EnumerateArray())
        {
            nativeLibraries.Add(new NativeLibraryEntry(
                NormalizeRequiredValue(ReadStringProperty(element, "fileName"), "nativeLibraries[].fileName"),
                NormalizeAndValidatePath(ReadStringProperty(element, "path"), "nativeLibraries[].path")));
        }

        return nativeLibraries;
    }

    private static string? ReadStringProperty(JsonElement element, string propertyName, bool required = true)
    {
        if (!element.TryGetProperty(propertyName, out var propertyElement) ||
            propertyElement.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                throw new InvalidOperationException($"Integration package probe manifest entry is missing required property '{propertyName}'.");
            }

            return null;
        }

        if (propertyElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException($"Integration package probe manifest property '{propertyName}' must be a string.");
        }

        return propertyElement.GetString();
    }

    private readonly record struct ManagedAssemblyEntry(string Name, string? Culture, string Path);

    private readonly record struct NativeLibraryEntry(string FileName, string Path);
}
