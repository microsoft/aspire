// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.NuGet;

internal interface INuGetSettingsProvider
{
    bool IsPackageSourceMappingEnabled(
        DirectoryInfo workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class NuGetSettingsProvider(
    BundleNuGetService bundleNuGetService) : INuGetSettingsProvider
{
    public bool IsPackageSourceMappingEnabled(
        DirectoryInfo workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var settings = bundleNuGetService.GetNuGetSettings(
            workingDirectory.FullName,
            cancellationToken);
        return settings.PackageSourceMappings.Count > 0;
    }
}
