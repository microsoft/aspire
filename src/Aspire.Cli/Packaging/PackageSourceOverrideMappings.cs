// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Packaging;

internal static class PackageSourceOverrideMappings
{
    public static PackageMapping[] Create(string packageSourceOverride, PackageChannel? requestedChannel, string? nugetServiceIndexOverride)
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
            // Honor the runtime service-index override (env / sidecar) when the
            // CLI emits a fresh fallback mapping. Reads from existing user
            // configs are not rewritten — see docs/specs/cli-identity-sidecar.md.
            var fallbackSource = string.IsNullOrEmpty(nugetServiceIndexOverride)
                ? PackageSources.NuGetOrg
                : nugetServiceIndexOverride;
            mappings.Add(new PackageMapping(PackageMapping.AllPackages, fallbackSource));
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

    public static string? GetNormalizedLocalDirectory(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var trimmedSource = source.Trim();
        if (UrlHelper.IsHttpUrl(trimmedSource))
        {
            return null;
        }

        try
        {
            if (Uri.TryCreate(trimmedSource, UriKind.Absolute, out var uri))
            {
                return uri.IsFile ? Path.GetFullPath(uri.LocalPath) : null;
            }

            return Path.GetFullPath(trimmedSource);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }
}
