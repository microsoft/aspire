// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Cli.Bundles;

internal readonly record struct BundleLayoutState(
    string? LayoutPath,
    bool HasManagedDirectory,
    bool HasManagedExecutable,
    bool HasDcpDirectory,
    bool HasVersionMarker,
    bool HasExtractionInProgressMarker)
{
    public bool HasKnownLayoutPath => !string.IsNullOrEmpty(LayoutPath);

    public bool HasRequiredLayoutStructure => HasManagedDirectory && HasManagedExecutable && HasDcpDirectory;

    public bool IsExtractionComplete => !HasExtractionInProgressMarker && HasVersionMarker;

    public bool IsUsableExtractedLayout => !HasExtractionInProgressMarker && (HasVersionMarker || HasRequiredLayoutStructure);

    public bool IsIncompleteLayout => HasKnownLayoutPath && (!HasRequiredLayoutStructure || !IsUsableExtractedLayout);

    public string Describe()
    {
        return $"ProcessPath={Environment.ProcessPath ?? "<null>"}, " +
               $"BundleRoot={LayoutPath ?? "<unavailable>"}, " +
               $"ManagedDir={HasManagedDirectory}, " +
               $"ManagedExe={HasManagedExecutable}, " +
               $"DcpDir={HasDcpDirectory}, " +
               $"VersionMarker={HasVersionMarker}, " +
               $"ExtractionInProgressMarker={HasExtractionInProgressMarker}";
    }

    public static BundleLayoutState Inspect(string? layoutPath)
    {
        if (string.IsNullOrEmpty(layoutPath))
        {
            return default;
        }

        var managedDirectory = Path.Combine(layoutPath, BundleDiscovery.ManagedDirectoryName);
        var managedPath = Path.Combine(managedDirectory, BundleDiscovery.GetExecutableFileName(BundleDiscovery.ManagedExecutableName));
        var dcpDirectory = Path.Combine(layoutPath, BundleDiscovery.DcpDirectoryName);
        var versionMarkerPath = Path.Combine(layoutPath, BundleService.VersionMarkerFileName);
        var extractionInProgressMarkerPath = Path.Combine(layoutPath, BundleService.ExtractionInProgressMarkerFileName);

        return new BundleLayoutState(
            LayoutPath: layoutPath,
            HasManagedDirectory: Directory.Exists(managedDirectory),
            HasManagedExecutable: File.Exists(managedPath),
            HasDcpDirectory: Directory.Exists(dcpDirectory),
            HasVersionMarker: File.Exists(versionMarkerPath),
            HasExtractionInProgressMarker: File.Exists(extractionInProgressMarkerPath));
    }
}
