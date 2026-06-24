// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using NuGetPackage = Aspire.Shared.NuGetPackageCli;

namespace Aspire.Cli.Integrations;

internal sealed class IntegrationIndexSourceContext(
    DirectoryInfo workingDirectory,
    IReadOnlyList<PackageChannel> channels)
{
    private readonly Dictionary<PackageChannel, Task<IReadOnlyList<NuGetPackage>>> _nugetIntegrationPackagesByChannel = [];

    public DirectoryInfo WorkingDirectory { get; } = workingDirectory;

    public IReadOnlyList<PackageChannel> Channels { get; } = channels;

    public Task<IReadOnlyList<NuGetPackage>> GetNuGetIntegrationPackagesAsync(PackageChannel channel, CancellationToken cancellationToken)
    {
        lock (_nugetIntegrationPackagesByChannel)
        {
            if (!_nugetIntegrationPackagesByChannel.TryGetValue(channel, out var packagesTask))
            {
                packagesTask = GetNuGetIntegrationPackagesCoreAsync(channel, cancellationToken);
                _nugetIntegrationPackagesByChannel.Add(channel, packagesTask);
            }

            return packagesTask;
        }
    }

    private async Task<IReadOnlyList<NuGetPackage>> GetNuGetIntegrationPackagesCoreAsync(PackageChannel channel, CancellationToken cancellationToken)
    {
        var packages = await channel.GetIntegrationPackagesAsync(
            workingDirectory: WorkingDirectory,
            cancellationToken: cancellationToken);

        return packages.ToArray();
    }
}
