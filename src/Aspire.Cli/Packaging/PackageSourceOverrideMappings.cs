// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Utils;
namespace Aspire.Cli.Packaging;

internal static class PackageSourceOverrideMappings
{
    internal const string DefaultPackagePattern = "Aspire*";

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

    public static PackageMapping[] Create(
        string packageSourceOverride,
        PackageChannel? requestedChannel,
        string? nugetServiceIndexOverride,
        string? packagePattern = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSourceOverride);
        ThrowIfCredentialBearingSourceOverride(packageSourceOverride);

        packagePattern = GetEffectivePackagePattern(packagePattern);

        var mappings = new List<PackageMapping>
        {
            new(packagePattern, packageSourceOverride)
        };

        if (IsExactPackagePattern(packagePattern))
        {
            // The exact pattern guarantees that the selected integration comes from --source.
            // Keep that source eligible for the remaining Aspire closure alongside the resolved
            // channel or ambient feed. NuGet has no source priority, so matching packages can come
            // from either feed; the exact selected-package pattern remains authoritative.
            mappings.Add(new PackageMapping(DefaultPackagePattern, packageSourceOverride));
            mappings.Add(new PackageMapping(PackageMapping.AllPackages, packageSourceOverride));
        }

        if (requestedChannel?.Mappings is not null)
        {
            foreach (var mapping in requestedChannel.Mappings)
            {
                if (CompetesWithAuthoritativePattern(mapping.PackageFilter, packagePattern))
                {
                    continue;
                }

                mappings.Add(mapping);
            }
        }

        if (!mappings.Any(mapping =>
            mapping.PackageFilter == PackageMapping.AllPackages &&
            !PackageSourceIdentity.Comparer.Equals(mapping.Source, packageSourceOverride)))
        {
            // Honor the runtime service-index override (env / sidecar) when the
            // CLI emits a fresh fallback mapping. Reads from existing user
            // configs are not rewritten — see docs/specs/cli-identity-sidecar.md.
            var fallbackSource = string.IsNullOrEmpty(nugetServiceIndexOverride)
                ? PackageSources.NuGetOrg
                : nugetServiceIndexOverride;
            mappings.Add(new PackageMapping(PackageMapping.AllPackages, fallbackSource));
        }

        // Equivalent mappings must not select different isolated restore caches. Preserve the
        // first source spelling, but deduplicate using the same identity as alias resolution.
        return
        [
            .. mappings.DistinctBy(static mapping =>
                (mapping.PackageFilter.ToUpperInvariant(), PackageSourceIdentity.Normalize(mapping.Source)))
        ];
    }

    internal static string GetEffectivePackagePattern(string? packagePattern)
        => string.IsNullOrWhiteSpace(packagePattern) ? DefaultPackagePattern : packagePattern;

    internal static bool IsExactPackagePattern(string packagePattern)
        => !packagePattern.EndsWith('*');

    internal static bool MatchesPackage(string packagePattern, string packageName)
        => packagePattern.EndsWith('*')
            ? packageName.StartsWith(packagePattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(packageName, packagePattern, StringComparison.OrdinalIgnoreCase);

    internal static bool CompetesWithAuthoritativePattern(
        string candidatePattern,
        string authoritativePattern)
    {
        if (authoritativePattern == PackageMapping.AllPackages)
        {
            return false;
        }

        if (!authoritativePattern.EndsWith('*'))
        {
            return string.Equals(candidatePattern, authoritativePattern, StringComparison.OrdinalIgnoreCase);
        }

        var authoritativePrefix = authoritativePattern[..^1];
        if (candidatePattern == PackageMapping.AllPackages)
        {
            return false;
        }

        var candidatePrefix = candidatePattern.EndsWith('*')
            ? candidatePattern[..^1]
            : candidatePattern;
        return candidatePrefix.Length >= authoritativePrefix.Length &&
            candidatePrefix.StartsWith(authoritativePrefix, StringComparison.OrdinalIgnoreCase);
    }

    public static PackageMapping[] CreateForSourceOnlyOperations(string packageSourceOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageSourceOverride);
        ThrowIfCredentialBearingSourceOverride(packageSourceOverride);

        // NuGet package search queries every configured source without applying package source
        // mapping. Keep the temporary config exclusive to --source so discovery and installation
        // cannot contact a channel feed or NuGet.org behind the user's approved proxy.
        return
        [
            new(DefaultPackagePattern, packageSourceOverride),
            new(PackageMapping.AllPackages, packageSourceOverride)
        ];
    }

    public static bool HasCredentialMaterial(string source)
        => NuGetSourceIdentity.HasCredentialMaterial(source);

    private static void ThrowIfCredentialBearingSourceOverride(string source)
    {
        if (HasCredentialMaterial(source))
        {
            throw new ArgumentException(
                "Credential-bearing HTTP sources cannot be supplied through --source. Configure credentials through NuGet instead.",
                nameof(source));
        }
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
        var trimmedSource = source.Trim();
        if (trimmedSource.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmedSource.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
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
