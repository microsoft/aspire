// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using NuGet.Frameworks;
using NuGet.ProjectModel;

namespace Aspire.Managed.NuGet.Commands;

internal sealed class NuGetPackageAssetResolution
{
    public required string PackagesPath { get; init; }

    public required string TargetFramework { get; init; }

    public required string RuntimeIdentifier { get; init; }

    public required int LibraryCount { get; init; }

    public required int SkippedPackageCount { get; init; }

    public required IReadOnlyList<NuGetPackageAsset> Assets { get; init; }
}

internal sealed class NuGetPackageAsset
{
    public required string SourcePath { get; init; }

    public required string RelativePath { get; init; }

    public required bool IsManagedAssembly { get; init; }

    public required bool IsNativeLibrary { get; init; }

    public string? Culture { get; init; }
}

internal static class NuGetPackageAssetResolver
{
    public static NuGetPackageAssetResolution Resolve(
        string assetsPath,
        string framework,
        string? runtimeIdentifier,
        Action<string>? verboseLog = null)
    {
        if (!File.Exists(assetsPath))
        {
            throw new FileNotFoundException($"Assets file not found: {assetsPath}", assetsPath);
        }

        var lockFileFormat = new LockFileFormat();
        var lockFile = lockFileFormat.Read(assetsPath);
        if (lockFile is null)
        {
            throw new InvalidOperationException("Failed to parse project.assets.json");
        }

        var effectiveRuntimeIdentifier = string.IsNullOrWhiteSpace(runtimeIdentifier)
            ? RuntimeInformation.RuntimeIdentifier
            : runtimeIdentifier;
        var target = ResolveTarget(lockFile, framework, effectiveRuntimeIdentifier);
        if (target is null)
        {
            throw new InvalidOperationException(
                $"Target framework '{framework}' not found in assets file. Available targets: {string.Join(", ", lockFile.Targets.Select(t => t.TargetFramework.GetShortFolderName()))}");
        }

        var packagesPath = GetPackagesPath(lockFile);
        var assets = new List<NuGetPackageAsset>();
        var skippedCount = 0;

        foreach (var library in target.Libraries)
        {
            var (libraryAssets, librarySkippedCount) = ResolveLibrary(library, packagesPath, verboseLog);
            assets.AddRange(libraryAssets);
            skippedCount += librarySkippedCount;
        }

        return new NuGetPackageAssetResolution
        {
            PackagesPath = packagesPath,
            TargetFramework = target.TargetFramework.GetShortFolderName(),
            RuntimeIdentifier = effectiveRuntimeIdentifier,
            LibraryCount = target.Libraries.Count,
            SkippedPackageCount = skippedCount,
            Assets = assets
        };
    }

    private static LockFileTarget? ResolveTarget(LockFile lockFile, string framework, string runtimeIdentifier)
    {
        var nugetFramework = NuGetFramework.ParseFolder(framework);
        return lockFile.GetTarget(nugetFramework, runtimeIdentifier)
            ?? lockFile.GetTarget(nugetFramework, runtimeIdentifier: null);
    }

    private static string GetPackagesPath(LockFile lockFile)
    {
        var packagesPath = lockFile.PackageFolders.FirstOrDefault()?.Path;
        if (!string.IsNullOrEmpty(packagesPath))
        {
            return packagesPath;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".nuget",
            "packages");
    }

    private static (IReadOnlyList<NuGetPackageAsset> Assets, int SkippedCount) ResolveLibrary(
        LockFileTargetLibrary library,
        string packagesPath,
        Action<string>? verboseLog)
    {
        if (library.Type != "package")
        {
            return ([], 0);
        }

        var libraryName = library.Name ?? string.Empty;
        var libraryVersion = library.Version?.ToString() ?? string.Empty;
        var packagePath = Path.Combine(packagesPath, libraryName.ToLowerInvariant(), libraryVersion);

        if (!Directory.Exists(packagePath))
        {
            verboseLog?.Invoke($"  Skip (not found): {libraryName}/{libraryVersion} at {packagePath}");
            return ([], 1);
        }

        var assets = new List<NuGetPackageAsset>();
        AddRuntimeAssemblies(assets, library.RuntimeAssemblies, packagePath);
        AddRuntimeTargets(assets, library.RuntimeTargets, packagePath);
        AddResourceAssemblies(assets, library.ResourceAssemblies, packagePath);
        AddNativeLibraries(assets, library.NativeLibraries, packagePath);

        return (assets, 0);
    }

    private static void AddRuntimeAssemblies(
        List<NuGetPackageAsset> assets,
        IEnumerable<LockFileItem> runtimeAssemblies,
        string packagePath)
    {
        foreach (var runtimeAssembly in runtimeAssemblies)
        {
            if (IsPlaceholderPath(runtimeAssembly.Path))
            {
                continue;
            }

            var sourcePath = Path.Combine(packagePath, runtimeAssembly.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var fileName = Path.GetFileName(sourcePath);
            AddAsset(assets, sourcePath, fileName, isManagedAssembly: IsManagedAssembly(sourcePath), isNativeLibrary: false);

            if (runtimeAssembly.Path.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase))
            {
                AddAsset(assets, sourcePath, runtimeAssembly.Path, isManagedAssembly: IsManagedAssembly(sourcePath), isNativeLibrary: false);
            }

            var xmlSourcePath = Path.ChangeExtension(sourcePath, ".xml");
            if (File.Exists(xmlSourcePath))
            {
                AddAsset(assets, xmlSourcePath, Path.ChangeExtension(fileName, ".xml"), isManagedAssembly: false, isNativeLibrary: false);
            }
        }
    }

    private static void AddRuntimeTargets(
        List<NuGetPackageAsset> assets,
        IEnumerable<LockFileRuntimeTarget> runtimeTargets,
        string packagePath)
    {
        foreach (var runtimeTarget in runtimeTargets)
        {
            if (IsPlaceholderPath(runtimeTarget.Path))
            {
                continue;
            }

            var sourcePath = Path.Combine(packagePath, runtimeTarget.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            AddAsset(
                assets,
                sourcePath,
                runtimeTarget.Path,
                isManagedAssembly: string.Equals(runtimeTarget.AssetType, "runtime", StringComparison.OrdinalIgnoreCase) && IsManagedAssembly(sourcePath),
                isNativeLibrary: string.Equals(runtimeTarget.AssetType, "native", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void AddResourceAssemblies(
        List<NuGetPackageAsset> assets,
        IEnumerable<LockFileItem> resourceAssemblies,
        string packagePath)
    {
        foreach (var resourceAssembly in resourceAssemblies)
        {
            if (IsPlaceholderPath(resourceAssembly.Path))
            {
                continue;
            }

            var sourcePath = Path.Combine(packagePath, resourceAssembly.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var locale = resourceAssembly.Properties.TryGetValue("locale", out var value)
                ? value
                : Path.GetFileName(Path.GetDirectoryName(resourceAssembly.Path));

            if (string.IsNullOrEmpty(locale))
            {
                continue;
            }

            AddAsset(
                assets,
                sourcePath,
                Path.Combine(locale, Path.GetFileName(sourcePath)),
                isManagedAssembly: IsManagedAssembly(sourcePath),
                isNativeLibrary: false,
                culture: locale);
        }
    }

    private static void AddNativeLibraries(
        List<NuGetPackageAsset> assets,
        IEnumerable<LockFileItem> nativeLibraries,
        string packagePath)
    {
        foreach (var nativeLib in nativeLibraries)
        {
            if (IsPlaceholderPath(nativeLib.Path))
            {
                continue;
            }

            var sourcePath = Path.Combine(packagePath, nativeLib.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            AddAsset(assets, sourcePath, Path.GetFileName(sourcePath), isManagedAssembly: false, isNativeLibrary: true);
            AddAsset(assets, sourcePath, nativeLib.Path, isManagedAssembly: false, isNativeLibrary: true);
        }
    }

    private static void AddAsset(
        List<NuGetPackageAsset> assets,
        string sourcePath,
        string relativePath,
        bool isManagedAssembly,
        bool isNativeLibrary,
        string? culture = null)
    {
        assets.Add(new NuGetPackageAsset
        {
            SourcePath = sourcePath,
            RelativePath = NormalizeRelativePath(relativePath),
            IsManagedAssembly = isManagedAssembly,
            IsNativeLibrary = isNativeLibrary,
            Culture = culture
        });
    }

    private static bool IsManagedAssembly(string path)
    {
        return path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static bool IsPlaceholderPath(string path)
    {
        return string.Equals(Path.GetFileName(path), "_._", StringComparison.OrdinalIgnoreCase);
    }
}
