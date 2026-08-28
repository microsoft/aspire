// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

internal sealed class IntegrationRestoreSourceResolver(
    IPackagingService packagingService,
    ILogger logger,
    string? nugetServiceIndexOverride = null)
{
    public async Task<IntegrationRestoreSources> ResolveAsync(
        string? requestedChannel,
        string? packageSourceOverride,
        CancellationToken cancellationToken)
    {
        ThrowIfStagingUnavailable(requestedChannel);

        var additionalSources = new List<string>();
        var safePackageSourceOverride = !string.IsNullOrWhiteSpace(packageSourceOverride) &&
            !PackageSourceOverrideMappings.HasCredentialMaterial(packageSourceOverride)
                ? packageSourceOverride
                : null;
        var hasOverride = !string.IsNullOrWhiteSpace(safePackageSourceOverride);

        if (safePackageSourceOverride is not null)
        {
            additionalSources.Add(safePackageSourceOverride);
        }

        PackageChannel? matchedChannel = null;
        IReadOnlyList<PackageChannel> matchedChannels = [];

        try
        {
            if (hasOverride && string.IsNullOrEmpty(requestedChannel))
            {
                // A source override without an explicit channel should not also add every
                // built-in Aspire feed; doing so would make those feeds co-eligible and defeat
                // the override for Aspire packages.
                matchedChannels = [];
            }
            else
            {
                matchedChannels = await GetExplicitRestoreChannelsAsync(requestedChannel, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(requestedChannel))
                {
                    matchedChannel = matchedChannels.FirstOrDefault(c =>
                        string.Equals(c.Name, requestedChannel, StringComparisons.ChannelName));
                }
            }

            foreach (var channel in matchedChannels)
            {
                if (channel.Mappings is null)
                {
                    continue;
                }

                foreach (var mapping in channel.Mappings)
                {
                    if (hasOverride && IsAspireSpecificMapping(mapping))
                    {
                        continue;
                    }

                    if (!additionalSources.Contains(mapping.Source, StringComparer.OrdinalIgnoreCase))
                    {
                        additionalSources.Add(mapping.Source);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve integration restore package channels, relying on configured NuGet sources.");
        }

        PackageMapping[]? packageSourceMappings = null;
        var configureGlobalPackagesFolder = false;
        string? globalPackagesFolderSource = null;

        if (hasOverride)
        {
            packageSourceMappings = PackageSourceOverrideMappings.Create(safePackageSourceOverride!, matchedChannel, nugetServiceIndexOverride);
            configureGlobalPackagesFolder = matchedChannel?.ConfigureGlobalPackagesFolder == true;
            globalPackagesFolderSource = configureGlobalPackagesFolder ? safePackageSourceOverride : null;

            foreach (var mapping in packageSourceMappings.Where(static mapping => mapping.PackageFilter == PackageMapping.AllPackages))
            {
                if (!additionalSources.Contains(mapping.Source, StringComparer.OrdinalIgnoreCase))
                {
                    additionalSources.Add(mapping.Source);
                }
            }
        }
        else if (matchedChannel?.Mappings is { Length: > 0 } &&
            !string.Equals(matchedChannel.Name, PackageChannelNames.Local, StringComparisons.ChannelName))
        {
            packageSourceMappings = matchedChannel.Mappings;
            configureGlobalPackagesFolder = matchedChannel.ConfigureGlobalPackagesFolder;
            globalPackagesFolderSource = configureGlobalPackagesFolder ? GetPrimaryFeedUrl(matchedChannel.Mappings) : null;
        }

        return new IntegrationRestoreSources(
            additionalSources,
            packageSourceMappings,
            configureGlobalPackagesFolder,
            globalPackagesFolderSource);
    }

    private void ThrowIfStagingUnavailable(string? requestedChannel)
    {
        if (!string.Equals(requestedChannel, PackageChannelNames.Staging, StringComparisons.ChannelName))
        {
            return;
        }

        var reason = packagingService.GetStagingChannelUnavailableReason();
        if (reason is not null)
        {
            throw new InvalidOperationException(reason);
        }
    }

    private async Task<IReadOnlyList<PackageChannel>> GetExplicitRestoreChannelsAsync(string? requestedChannel, CancellationToken cancellationToken)
    {
        var channels = await packagingService.GetChannelsAsync(cancellationToken, requestedChannel).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(requestedChannel))
        {
            var matchingChannel = channels.FirstOrDefault(c => string.Equals(c.Name, requestedChannel, StringComparisons.ChannelName));
            if (matchingChannel is not null)
            {
                return [matchingChannel];
            }
        }

        return channels.Where(c => c.Type == PackageChannelType.Explicit).ToArray();
    }

    private static string GetPrimaryFeedUrl(PackageMapping[] mappings)
    {
        var aspire = mappings.FirstOrDefault(m =>
            string.Equals(m.PackageFilter, "Aspire*", StringComparison.OrdinalIgnoreCase));
        return aspire?.Source ?? mappings[0].Source;
    }

    private static bool IsAspireSpecificMapping(PackageMapping mapping) =>
        mapping.PackageFilter != PackageMapping.AllPackages &&
        mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase);
}

internal sealed record IntegrationRestoreSources(
    IReadOnlyList<string> AdditionalSources,
    PackageMapping[]? PackageSourceMappings,
    bool ConfigureGlobalPackagesFolder,
    string? GlobalPackagesFolderSource);
