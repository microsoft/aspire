// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.DotNet;

namespace Aspire.Cli.NuGet;

internal interface INuGetSettingsProvider
{
    Task<bool> IsPackageSourceMappingEnabledAsync(
        DirectoryInfo workingDirectory,
        CancellationToken cancellationToken);
}

internal sealed class NuGetSettingsProvider(
    BundleNuGetService bundleNuGetService,
    IDotNetCliRunner dotNetCliRunner) : INuGetSettingsProvider
{
    public async Task<bool> IsPackageSourceMappingEnabledAsync(
        DirectoryInfo workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);

        try
        {
            var settings = await bundleNuGetService.GetNuGetSettingsAsync(
                workingDirectory.FullName,
                cancellationToken).ConfigureAwait(false);
            return settings.PackageSourceMappingEnabled;
        }
        catch (BundledNuGetComponentNotFoundException)
        {
            // Source builds and unit tests do not always have an extracted bundle layout.
            var (exitCode, configPaths) = await dotNetCliRunner.GetNuGetConfigPathsAsync(
                workingDirectory,
                new ProcessInvocationOptions { SuppressLogging = true },
                cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Unable to discover the NuGet configuration hierarchy for '{workingDirectory.FullName}'.");
            }

            return await HasPackageSourceMappingAsync(configPaths, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static async Task<bool> HasPackageSourceMappingAsync(
        IReadOnlyList<string> configPaths,
        CancellationToken cancellationToken)
    {
        // NuGet returns paths from highest to lowest precedence. A clear in a higher-precedence
        // config prevents lower mapping entries from contributing to the effective settings.
        foreach (var configPath in configPaths)
        {
            await using var stream = File.OpenRead(configPath);
            var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken).ConfigureAwait(false);
            var section = document.Root?
                .Elements()
                .FirstOrDefault(static element => string.Equals(
                    element.Name.LocalName,
                    "packageSourceMapping",
                    StringComparison.Ordinal));
            if (section is null)
            {
                continue;
            }

            bool? hasPackageSourceMapping = null;
            foreach (var element in section.Elements())
            {
                if (string.Equals(element.Name.LocalName, "clear", StringComparison.OrdinalIgnoreCase))
                {
                    hasPackageSourceMapping = false;
                }
                else if (string.Equals(element.Name.LocalName, "packageSource", StringComparison.OrdinalIgnoreCase))
                {
                    hasPackageSourceMapping = true;
                }
            }

            if (hasPackageSourceMapping is not null)
            {
                return hasPackageSourceMapping.Value;
            }
        }

        return false;
    }
}
