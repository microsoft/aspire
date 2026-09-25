// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;

namespace Aspire.Cli.Packaging;

internal static class PackageSourceOverrideMappings
{
    /// <summary>
    /// Resolves a command-line package source against the invocation directory, returning relative local sources as absolute paths so persisted mappings remain valid elsewhere.
    /// </summary>
    public static string ResolveForWorkingDirectory(string source, DirectoryInfo workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var sourceKind = ClassifySource(source, out _);

        // On Unix, Uri treats DOS-shaped paths such as C:/feed as absolute file URIs.
        // Preserve a file URI only when the source explicitly includes the file: scheme.
        if (Path.IsPathFullyQualified(source) ||
            sourceKind is PackageSourceKind.Http or PackageSourceKind.FileUri)
        {
            return source;
        }

        return Path.GetFullPath(source, workingDirectory.FullName);
    }

    public static string? GetMissingLocalDirectory(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        var sourceKind = ClassifySource(source, out var localDirectory);
        if (sourceKind is PackageSourceKind.Http)
        {
            return null;
        }

        return Directory.Exists(localDirectory) ? null : localDirectory;
    }

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

    public static PackageMapping[] CreateForTemplateOperations(string packageSourceOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSourceOverride);

        // NuGet package search queries every configured source without applying package source
        // mapping. Keep the temporary config exclusive to --source so discovery and installation
        // cannot contact a channel feed or NuGet.org behind the user's approved proxy.
        return
        [
            new("Aspire*", packageSourceOverride),
            new(PackageMapping.AllPackages, packageSourceOverride)
        ];
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

    private static PackageSourceKind ClassifySource(string source, out string? localDirectory)
    {
        if (UrlHelper.IsHttpUrl(source))
        {
            localDirectory = null;
            return PackageSourceKind.Http;
        }

        if (source.StartsWith("file:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(source, UriKind.Absolute, out var uri) &&
            uri.IsFile)
        {
            localDirectory = uri.LocalPath;
            return PackageSourceKind.FileUri;
        }

        localDirectory = source;
        return PackageSourceKind.LocalPath;
    }

    private enum PackageSourceKind
    {
        Http,
        FileUri,
        LocalPath
    }
}
