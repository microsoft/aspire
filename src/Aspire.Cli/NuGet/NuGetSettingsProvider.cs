// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using NuGet.Configuration;

namespace Aspire.Cli.NuGet;

internal interface INuGetSettingsProvider
{
    Task<bool> IsPackageSourceMappingEnabledAsync(
        DirectoryInfo workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class NuGetSettingsProvider(
    BundleNuGetService bundleNuGetService) : INuGetSettingsProvider
{
    internal static Task<bool> HasPackageSourceMappingAsync(
        IReadOnlyList<string> configPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configPaths);
        cancellationToken.ThrowIfCancellationRequested();

        var settings = Settings.LoadSettingsGivenConfigPaths(configPaths.ToList());
        var enabled = new PackageSourceMappingProvider(settings)
            .GetPackageSourceMappingItems()
            .Count > 0;
        return Task.FromResult(enabled);
    }

    public async Task<bool> IsPackageSourceMappingEnabledAsync(
        DirectoryInfo workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var settings = await bundleNuGetService.GetNuGetSettingsAsync(
            workingDirectory.FullName,
            cancellationToken).ConfigureAwait(false);
        return settings.PackageSourceMappingEnabled;
    }
}
