// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Utils;
using Aspire.Hosting.Utils;

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

    public static bool SourcesMatch(string left, string right, IEnvironment environment)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return true;
        }

        var pathComparer = environment.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        if (UrlHelper.IsHttpUrl(left) || UrlHelper.IsHttpUrl(right))
        {
            return Uri.TryCreate(left, UriKind.Absolute, out var leftUri) &&
                Uri.TryCreate(right, UriKind.Absolute, out var rightUri) &&
                Uri.Compare(
                    leftUri,
                    rightUri,
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.UriEscaped,
                    StringComparison.Ordinal) == 0;
        }

        if (Uri.TryCreate(left, UriKind.Absolute, out var leftFileUri) && leftFileUri.IsFile)
        {
            left = leftFileUri.LocalPath;
        }

        if (Uri.TryCreate(right, UriKind.Absolute, out var rightFileUri) && rightFileUri.IsFile)
        {
            right = rightFileUri.LocalPath;
        }

        var leftPath = ResolveLocalSourcePath(left, environment.IsMacOS());
        var rightPath = ResolveLocalSourcePath(right, environment.IsMacOS());
        return pathComparer.Equals(leftPath, rightPath);
    }

    public static bool IsSourceMappedForPackage(
        string source,
        string packageId,
        IEnumerable<string> configPaths,
        DirectoryInfo workingDirectory,
        bool configWillBeRelocated,
        IEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(configPaths);
        ArgumentNullException.ThrowIfNull(workingDirectory);

        var packageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var disabledPackageSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sourceMappings = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var configPathArray = configPaths.ToArray();

        // A generated guest server can preserve one AppHost-local config plus the normal global
        // hierarchy. If multiple local files contribute, retain the source override instead of
        // pretending the relocated generated project can reproduce that hierarchy.
        if (configWillBeRelocated &&
            configPathArray.Count(path => IsSameOrAncestor(new FileInfo(path).Directory!, workingDirectory)) > 1)
        {
            return false;
        }

        // `dotnet nuget config paths` returns nearest-to-global paths. Apply them in reverse so
        // closer files can clear, remove, or replace the lower-precedence source and mapping keys.
        foreach (var configPath in configPathArray.Reverse())
        {
            try
            {
                var configuration = XDocument.Load(configPath).Root;
                if (configuration is null)
                {
                    return false;
                }

                var configDirectory = new FileInfo(configPath).Directory!;
                if (configWillBeRelocated &&
                    IsSameOrAncestor(configDirectory, workingDirectory) &&
                    GetSection(configuration, "packageSources")?
                        .Elements()
                        .Where(element => HasName(element, "add"))
                        .Select(add => GetAttributeValue(add, "value"))
                        .OfType<string>()
                        .Any(value =>
                        {
                            var expandedValue = Environment.ExpandEnvironmentVariables(value);
                            var resolvedValue = ResolveForWorkingDirectory(expandedValue, configDirectory);
                            return !string.Equals(expandedValue, resolvedValue, StringComparison.Ordinal) &&
                                SourcesMatch(resolvedValue, source, environment);
                        }) == true)
                {
                    return false;
                }

                ApplyPackageSources(
                    GetSection(configuration, "packageSources"),
                    packageSources,
                    configDirectory);
                ApplyKeyValueSection(GetSection(configuration, "disabledPackageSources"), disabledPackageSources);

                if (GetSection(configuration, "packageSourceMapping") is { } mappingSection)
                {
                    foreach (var element in mappingSection.Elements())
                    {
                        if (HasName(element, "clear"))
                        {
                            sourceMappings.Clear();
                        }
                        else if (HasName(element, "remove") &&
                            GetAttributeValue(element, "key") is { Length: > 0 } removedSourceKey)
                        {
                            sourceMappings.Remove(removedSourceKey);
                        }
                        else if (HasName(element, "packageSource") &&
                            GetAttributeValue(element, "key") is { Length: > 0 } sourceKey)
                        {
                            sourceMappings[sourceKey] = element
                                .Elements()
                                .Where(package => HasName(package, "package"))
                                .Select(package => GetAttributeValue(package, "pattern"))
                                .OfType<string>()
                                .ToArray();
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return false;
            }
        }

        var matchingSourceKeys = packageSources
            .Where(pair =>
                !disabledPackageSources.TryGetValue(pair.Key, out var disabled) ||
                !bool.TryParse(disabled, out var isDisabled) ||
                !isDisabled)
            .Where(pair => SourcesMatch(pair.Value, source, environment))
            .Select(pair => pair.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (matchingSourceKeys.Count == 0)
        {
            return false;
        }

        if (sourceMappings.Count == 0)
        {
            return true;
        }

        var bestOverallMatch = -1;
        var bestSourceMatch = -1;
        foreach (var (sourceKey, patterns) in sourceMappings)
        {
            foreach (var pattern in patterns)
            {
                var match = GetPatternMatchLength(pattern, packageId);
                bestOverallMatch = Math.Max(bestOverallMatch, match);
                if (matchingSourceKeys.Contains(sourceKey))
                {
                    bestSourceMatch = Math.Max(bestSourceMatch, match);
                }
            }
        }

        return bestSourceMatch >= 0 && bestSourceMatch == bestOverallMatch;

        static XElement? GetSection(XElement configuration, string name)
            => configuration.Elements().FirstOrDefault(element => HasName(element, name));

        static bool HasName(XElement element, string name)
            => string.Equals(element.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

        static string? GetAttributeValue(XElement element, string name)
            => element.Attributes()
                .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                ?.Value;

        static bool IsSameOrAncestor(DirectoryInfo candidate, DirectoryInfo directory)
        {
            var relativePath = Path.GetRelativePath(candidate.FullName, directory.FullName);
            return relativePath == "." ||
                relativePath != ".." &&
                !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                !Path.IsPathRooted(relativePath);
        }

        static void ApplyPackageSources(
            XElement? section,
            Dictionary<string, string> values,
            DirectoryInfo configDirectory)
        {
            if (section is null)
            {
                return;
            }

            foreach (var element in section.Elements())
            {
                if (HasName(element, "clear"))
                {
                    values.Clear();
                }
                else if (HasName(element, "remove") &&
                    GetAttributeValue(element, "key") is { Length: > 0 } removedKey)
                {
                    values.Remove(removedKey);
                }
                else if (HasName(element, "add") &&
                    GetAttributeValue(element, "key") is { Length: > 0 } addKey &&
                    GetAttributeValue(element, "value") is { Length: > 0 } value)
                {
                    values[addKey] = ResolveConfiguredSource(value, configDirectory);
                }
            }
        }

        static string ResolveConfiguredSource(string source, DirectoryInfo configDirectory)
        {
            // NuGet expands environment variables in NuGet.Config values on every platform using
            // Environment.ExpandEnvironmentVariables. Keep source matching aligned with that behavior.
            // https://learn.microsoft.com/nuget/reference/nuget-config-file#using-environment-variables
            return ResolveForWorkingDirectory(Environment.ExpandEnvironmentVariables(source), configDirectory);
        }

        static void ApplyKeyValueSection(XElement? section, Dictionary<string, string> values)
        {
            if (section is null)
            {
                return;
            }

            foreach (var element in section.Elements())
            {
                if (HasName(element, "clear"))
                {
                    values.Clear();
                }
                else if (HasName(element, "remove") &&
                    GetAttributeValue(element, "key") is { Length: > 0 } removedKey)
                {
                    values.Remove(removedKey);
                }
                else if (HasName(element, "add") &&
                    GetAttributeValue(element, "key") is { Length: > 0 } addKey &&
                    GetAttributeValue(element, "value") is { Length: > 0 } value)
                {
                    values[addKey] = value;
                }
            }
        }

        static int GetPatternMatchLength(string pattern, string candidate)
        {
            // NuGet package source mapping selects exact IDs before the longest matching prefix,
            // with `*` as the lowest-priority default.
            // https://learn.microsoft.com/nuget/consume-packages/package-source-mapping#package-pattern-requirements
            if (string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return int.MaxValue;
            }

            if (pattern.EndsWith('*') &&
                candidate.StartsWith(pattern.AsSpan(0, pattern.Length - 1), StringComparison.OrdinalIgnoreCase))
            {
                return pattern.Length - 1;
            }

            return -1;
        }
    }

    private static string ResolveLocalSourcePath(string path, bool resolveStoredCasing)
    {
        var resolvedPath = PathNormalizer.ResolveSymlinks(path);
        if (!resolveStoredCasing)
        {
            return resolvedPath;
        }

        var root = Path.GetPathRoot(resolvedPath);
        if (string.IsNullOrEmpty(root))
        {
            return resolvedPath;
        }

        var segments = resolvedPath[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var current = root;

        foreach (var segment in segments)
        {
            var candidate = Path.Combine(current, segment);
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return resolvedPath;
            }

            try
            {
                string? exactMatch = null;
                string? caseInsensitiveMatch = null;
                foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                {
                    var entryName = Path.GetFileName(entry);
                    if (entryName.Equals(segment, StringComparison.Ordinal))
                    {
                        exactMatch = entry;
                        break;
                    }

                    if (caseInsensitiveMatch is null &&
                        entryName.Equals(segment, StringComparison.OrdinalIgnoreCase))
                    {
                        caseInsensitiveMatch = entry;
                    }
                }

                current = exactMatch ?? caseInsensitiveMatch ?? candidate;
            }
            catch (IOException)
            {
                return resolvedPath;
            }
            catch (UnauthorizedAccessException)
            {
                return resolvedPath;
            }
        }

        return current;
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
