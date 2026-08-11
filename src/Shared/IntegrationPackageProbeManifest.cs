// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aspire.Hosting;

/// <summary>
/// Represents the NuGet package-backed integration asset probe manifest emitted by the CLI.
/// </summary>
internal sealed class IntegrationPackageProbeManifest
{
    public const string FileName = "integration-package-probe-manifest.json";

    public static IntegrationPackageProbeManifest Empty { get; } = Create([], []);

    private readonly IReadOnlyDictionary<string, string> _managedAssemblyPaths;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _nativeLibraryPaths;

    private IntegrationPackageProbeManifest(
        IReadOnlyList<IntegrationPackageManagedAssembly> managedAssemblies,
        IReadOnlyList<IntegrationPackageNativeLibrary> nativeLibraries)
    {
        ManagedAssemblies = managedAssemblies;
        NativeLibraries = nativeLibraries;
        _managedAssemblyPaths = CreateManagedAssemblyLookup(managedAssemblies);
        _nativeLibraryPaths = CreateNativeLibraryLookup(nativeLibraries);
        RuntimeAssemblyNames = GetRuntimeAssemblyNames(managedAssemblies);
    }

    public IReadOnlyList<IntegrationPackageManagedAssembly> ManagedAssemblies { get; }

    public IReadOnlyList<IntegrationPackageNativeLibrary> NativeLibraries { get; }

    public IReadOnlyList<string> RuntimeAssemblyNames { get; }

    public static IntegrationPackageProbeManifest Create(
        IEnumerable<IntegrationPackageManagedAssembly> managedAssemblies,
        IEnumerable<IntegrationPackageNativeLibrary> nativeLibraries)
    {
        ArgumentNullException.ThrowIfNull(managedAssemblies);
        ArgumentNullException.ThrowIfNull(nativeLibraries);

        var managedLookup = new Dictionary<string, IntegrationPackageManagedAssembly>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in managedAssemblies)
        {
            var normalizedAssembly = new IntegrationPackageManagedAssembly
            {
                Name = NormalizeRequiredValue(assembly.Name, "managedAssemblies[].name"),
                Culture = NormalizeCulture(assembly.Culture),
                Path = NormalizeRequiredValue(assembly.Path, "managedAssemblies[].path")
            };

            managedLookup.TryAdd(
                CreateManagedAssemblyLookupKey(normalizedAssembly.Name, normalizedAssembly.Culture),
                normalizedAssembly);
        }

        var nativeLookup = new Dictionary<string, IntegrationPackageNativeLibrary>(StringComparer.OrdinalIgnoreCase);
        foreach (var nativeLibrary in nativeLibraries)
        {
            var normalizedNativeLibrary = new IntegrationPackageNativeLibrary
            {
                FileName = NormalizeRequiredValue(nativeLibrary.FileName, "nativeLibraries[].fileName"),
                Path = NormalizeRequiredValue(nativeLibrary.Path, "nativeLibraries[].path")
            };

            nativeLookup.TryAdd(
                $"{normalizedNativeLibrary.FileName}|{normalizedNativeLibrary.Path}",
                normalizedNativeLibrary);
        }

        var managedAssemblyList = managedLookup.Values.ToList();
        managedAssemblyList.Sort(static (left, right) =>
        {
            var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
            if (nameComparison != 0)
            {
                return nameComparison;
            }

            var cultureComparison = StringComparer.OrdinalIgnoreCase.Compare(left.Culture, right.Culture);
            return cultureComparison != 0
                ? cultureComparison
                : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });

        var nativeLibraryList = nativeLookup.Values.ToList();
        nativeLibraryList.Sort(static (left, right) =>
        {
            var nameComparison = StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName);
            return nameComparison != 0
                ? nameComparison
                : StringComparer.Ordinal.Compare(left.Path, right.Path);
        });

        return new IntegrationPackageProbeManifest(managedAssemblyList, nativeLibraryList);
    }

    public static IntegrationPackageProbeManifest Load(string? manifestPath)
    {
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return Empty;
        }

        var normalizedManifestPath = NormalizeManifestPath(manifestPath);
        if (!File.Exists(normalizedManifestPath))
        {
            throw new InvalidOperationException($"Integration package probe manifest '{normalizedManifestPath}' does not exist.");
        }

        using var stream = File.OpenRead(normalizedManifestPath);
        using var document = JsonDocument.Parse(stream);
        var managedAssemblies = ReadManagedAssemblies(document.RootElement);
        var nativeLibraries = ReadNativeLibraries(document.RootElement);

        return Create(managedAssemblies, nativeLibraries);
    }

    public static Task WriteAsync(
        string path,
        IntegrationPackageProbeManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(manifest);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("managedAssemblies");
            writer.WriteStartArray();
            foreach (var managedAssembly in manifest.ManagedAssemblies)
            {
                writer.WriteStartObject();
                writer.WriteString("name", managedAssembly.Name);
                if (managedAssembly.Culture is not null)
                {
                    writer.WriteString("culture", managedAssembly.Culture);
                }
                writer.WriteString("path", managedAssembly.Path);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WritePropertyName("nativeLibraries");
            writer.WriteStartArray();
            foreach (var nativeLibrary in manifest.NativeLibraries)
            {
                writer.WriteStartObject();
                writer.WriteString("fileName", nativeLibrary.FileName);
                writer.WriteString("path", nativeLibrary.Path);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return File.WriteAllBytesAsync(path, stream.ToArray(), cancellationToken);
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

    private static IReadOnlyDictionary<string, string> CreateManagedAssemblyLookup(IEnumerable<IntegrationPackageManagedAssembly> managedAssemblies)
    {
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assembly in managedAssemblies)
        {
            lookup[CreateManagedAssemblyLookupKey(assembly.Name, assembly.Culture)] = assembly.Path;
        }

        return lookup;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CreateNativeLibraryLookup(IEnumerable<IntegrationPackageNativeLibrary> nativeLibraries)
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

    private static IReadOnlyList<string> GetRuntimeAssemblyNames(IEnumerable<IntegrationPackageManagedAssembly> managedAssemblies)
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
        var normalizedPath = NormalizePath(
            NormalizeRequiredValue(path, propertyName),
            $"Integration package probe manifest entry path '{propertyName}' is invalid.");
        if (!File.Exists(normalizedPath))
        {
            throw new InvalidOperationException($"Integration package probe manifest path '{normalizedPath}' does not exist.");
        }

        return normalizedPath;
    }

    private static string NormalizeManifestPath(string manifestPath)
    {
        return NormalizePath(manifestPath, "Integration package probe manifest path is invalid.");
    }

    private static string NormalizePath(string path, string invalidPathMessage)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException(invalidPathMessage, ex);
        }
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

    private static IReadOnlyList<IntegrationPackageManagedAssembly> ReadManagedAssemblies(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("managedAssemblies", out var managedAssembliesElement) ||
            managedAssembliesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var managedAssemblies = new List<IntegrationPackageManagedAssembly>();
        foreach (var element in managedAssembliesElement.EnumerateArray())
        {
            managedAssemblies.Add(new IntegrationPackageManagedAssembly
            {
                Name = NormalizeRequiredValue(ReadStringProperty(element, "name"), "managedAssemblies[].name"),
                Culture = NormalizeCulture(ReadStringProperty(element, "culture", required: false)),
                Path = NormalizeAndValidatePath(ReadStringProperty(element, "path"), "managedAssemblies[].path")
            });
        }

        return managedAssemblies;
    }

    private static IReadOnlyList<IntegrationPackageNativeLibrary> ReadNativeLibraries(JsonElement rootElement)
    {
        if (!rootElement.TryGetProperty("nativeLibraries", out var nativeLibrariesElement) ||
            nativeLibrariesElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var nativeLibraries = new List<IntegrationPackageNativeLibrary>();
        foreach (var element in nativeLibrariesElement.EnumerateArray())
        {
            nativeLibraries.Add(new IntegrationPackageNativeLibrary
            {
                FileName = NormalizeRequiredValue(ReadStringProperty(element, "fileName"), "nativeLibraries[].fileName"),
                Path = NormalizeAndValidatePath(ReadStringProperty(element, "path"), "nativeLibraries[].path")
            });
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
}

/// <summary>
/// Represents a package-backed managed assembly that should be loaded from the package cache.
/// </summary>
internal sealed class IntegrationPackageManagedAssembly
{
    public required string Name { get; init; }

    public string? Culture { get; init; }

    public required string Path { get; init; }
}

/// <summary>
/// Represents a package-backed native library that should be loaded from the package cache.
/// </summary>
internal sealed class IntegrationPackageNativeLibrary
{
    public required string FileName { get; init; }

    public required string Path { get; init; }
}
