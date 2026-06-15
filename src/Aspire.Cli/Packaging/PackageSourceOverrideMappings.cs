// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Packaging;

internal static class PackageSourceOverrideMappings
{
    public static PackageMapping[] Create(string packageSourceOverride, PackageChannel? requestedChannel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSourceOverride);
        if (HasCredentialMaterial(packageSourceOverride))
        {
            throw new ArgumentException("Credential-bearing HTTP sources cannot be persisted.", nameof(packageSourceOverride));
        }

        var mappings = new List<PackageMapping>
        {
            new("Aspire*", packageSourceOverride)
        };

        if (requestedChannel?.Mappings is not null)
        {
            foreach (var mapping in requestedChannel.Mappings)
            {
                if (mapping.PackageFilter.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                mappings.Add(mapping);
            }
        }

        if (!mappings.Any(static mapping => mapping.PackageFilter == PackageMapping.AllPackages))
        {
            mappings.Add(new PackageMapping(PackageMapping.AllPackages, PackageSources.NuGetOrg));
        }

        return [.. mappings.DistinctBy(static mapping => $"{mapping.PackageFilter}\0{mapping.Source}")];
    }

    public static bool HasCredentialMaterial(string source)
    {
        return Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) &&
            (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) &&
            (!string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment));
    }
}
