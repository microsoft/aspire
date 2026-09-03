// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
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
        var hasOverride = !string.IsNullOrWhiteSpace(packageSourceOverride);

        if (hasOverride)
        {
            additionalSources.Add(packageSourceOverride!);
        }

        PackageChannel? matchedChannel = null;
        IReadOnlyList<PackageChannel> matchedChannels = [];
        var channelLookupSucceeded = false;

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
                channelLookupSucceeded = true;
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

                    if (!additionalSources.Contains(mapping.Source, PackageSourceIdentity.Comparer))
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

        if (channelLookupSucceeded &&
            !string.IsNullOrEmpty(requestedChannel) &&
            matchedChannel is null)
        {
            throw new InvalidOperationException($"Package channel '{requestedChannel}' was not found.");
        }

        PackageMapping[]? packageSourceMappings = null;
        var configureGlobalPackagesFolder = false;

        if (hasOverride)
        {
            packageSourceMappings = PackageSourceOverrideMappings.Create(packageSourceOverride!, matchedChannel, nugetServiceIndexOverride);
            configureGlobalPackagesFolder = matchedChannel?.ConfigureGlobalPackagesFolder == true;

            foreach (var mapping in packageSourceMappings.Where(static mapping => mapping.PackageFilter == PackageMapping.AllPackages))
            {
                if (!additionalSources.Contains(mapping.Source, PackageSourceIdentity.Comparer))
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
        }

        return new IntegrationRestoreSources(
            additionalSources,
            packageSourceMappings,
            configureGlobalPackagesFolder,
            configureGlobalPackagesFolder
                ? CreateGlobalPackagesFolderIdentity(additionalSources, packageSourceMappings)
                : null);
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

            return [];
        }

        return channels.Where(c => c.Type == PackageChannelType.Explicit).ToArray();
    }

    private static string CreateGlobalPackagesFolderIdentity(
        IReadOnlyList<string> additionalSources,
        IReadOnlyList<PackageMapping>? mappings)
    {
        var builder = new StringBuilder();
        IEnumerable<PackageMapping> orderedMappings = mappings is null
            ? []
            : mappings
                .OrderBy(static mapping => mapping.PackageFilter, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static mapping => PackageSourceIdentity.Normalize(mapping.Source), StringComparer.Ordinal);
        foreach (var mapping in orderedMappings)
        {
            AppendIdentityPart(builder, mapping.PackageFilter.ToUpperInvariant());
            AppendIdentityPart(builder, PackageSourceIdentity.Normalize(mapping.Source));
        }

        foreach (var source in additionalSources
            .Distinct(PackageSourceIdentity.Comparer)
            .OrderBy(PackageSourceIdentity.Normalize, StringComparer.Ordinal))
        {
            AppendIdentityPart(builder, PackageSourceIdentity.Normalize(source));
        }

        return builder.ToString();
    }

    private static void AppendIdentityPart(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
    }

    private static bool IsAspireSpecificMapping(PackageMapping mapping) =>
        mapping.PackageFilter != PackageMapping.AllPackages &&
        mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase);
}

internal sealed record IntegrationRestoreSources(
    IReadOnlyList<string> AdditionalSources,
    PackageMapping[]? PackageSourceMappings,
    bool ConfigureGlobalPackagesFolder,
    string? GlobalPackagesFolderIdentity);
