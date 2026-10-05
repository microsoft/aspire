// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Xml;
using System.Xml.Linq;

namespace Aspire.Cli.Packaging;

internal class NuGetConfigMerger
{
    private sealed record NuGetConfigContext
    {
        public required XDocument Document { get; init; }
        public required XElement Configuration { get; init; }
        public required XElement PackageSources { get; init; }
        public XElement? PackageSourceMapping { get; init; }
        public required PackageMapping[] Mappings { get; init; }
        public required string[] RequiredSources { get; init; }
        public required XElement[] ExistingAdds { get; init; }
        public required Dictionary<string, List<string>> SourceToExistingKeys { get; init; }
    }
    /// <summary>
    /// Creates or updates a NuGet.config file in the specified directory based on the provided <see cref="PackageChannel"/>.
    /// For implicit channels (no explicit mappings) this method is a no-op.
    /// </summary>
    /// <param name="targetDirectory">The directory where the NuGet.config should be created or updated.</param>
    /// <param name="channel">The package channel providing mapping information.</param>
    /// <param name="confirmationCallback">Optional callback invoked before creating or updating the NuGet.config file. 
    /// The callback receives the target file info, original content (null for new files), proposed new content, and a cancellation token.
    /// Return true to proceed with the update, false to skip it.</param>
    /// <param name="cancellationToken">A cancellation token to observe while waiting for the task to complete.</param>
    public static async Task CreateOrUpdateAsync(DirectoryInfo targetDirectory, PackageChannel channel, Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ArgumentNullException.ThrowIfNull(channel);

        // Only explicit channels (with mappings) require a NuGet.config merge/write.
        var mappings = channel.Mappings;
        if (channel.Type is not PackageChannelType.Explicit || mappings is null || mappings.Length == 0)
        {
            return;
        }

        await CreateOrUpdateAsync(targetDirectory, mappings, channel.ConfigureGlobalPackagesFolder, confirmationCallback, cancellationToken);
    }

    /// <summary>
    /// Creates or updates a NuGet.config file in the specified directory based on the provided package source mappings.
    /// </summary>
    public static async Task CreateOrUpdateAsync(
        DirectoryInfo targetDirectory,
        PackageMapping[] mappings,
        bool configureGlobalPackagesFolder = false,
        Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ArgumentNullException.ThrowIfNull(mappings);

        if (mappings.Length == 0)
        {
            return;
        }

        if (!targetDirectory.Exists)
        {
            targetDirectory.Create();
        }

        if (!TryFindNuGetConfigInDirectory(targetDirectory, out var nugetConfigFile))
        {
            await CreateNewNuGetConfigAsync(targetDirectory, mappings, configureGlobalPackagesFolder, confirmationCallback, cancellationToken);
        }
        else
        {
            await UpdateExistingNuGetConfigAsync(nugetConfigFile, mappings, configureGlobalPackagesFolder, confirmationCallback, cancellationToken);
        }
    }

    private static async Task CreateNewNuGetConfigAsync(DirectoryInfo targetDirectory, PackageMapping[] mappings, bool configureGlobalPackagesFolder, Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback, CancellationToken cancellationToken)
    {
        if (mappings.Length == 0)
        {
            return;
        }

        var targetPath = Path.Combine(targetDirectory.FullName, "nuget.config");
        var targetFile = new FileInfo(targetPath);

        using var tmpConfig = await TemporaryNuGetConfig.CreateAsync(mappings);

        if (confirmationCallback is not null)
        {
            // Load the proposed content as XmlDocument for the callback
            var proposedDocument = new XmlDocument();
            proposedDocument.Load(tmpConfig.ConfigFile.FullName);

            var shouldProceed = await confirmationCallback(targetFile, null, proposedDocument, cancellationToken);
            if (!shouldProceed)
            {
                return;
            }
        }

        if (configureGlobalPackagesFolder)
        {
            // Need to modify the temporary config to add globalPackagesFolder before copying
            await AddGlobalPackagesFolderToConfigAsync(tmpConfig.ConfigFile);
        }

        File.Copy(tmpConfig.ConfigFile.FullName, targetPath, overwrite: true);
    }

    private static async Task UpdateExistingNuGetConfigAsync(FileInfo nugetConfigFile, PackageMapping[] mappings, bool configureGlobalPackagesFolder, Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback, CancellationToken cancellationToken)
    {
        if (mappings.Length == 0)
        {
            return;
        }

        // Load original content for callback
        XmlDocument? originalDocument = null;
        if (confirmationCallback is not null)
        {
            originalDocument = new XmlDocument();
            using var stream = nugetConfigFile.OpenRead();
            originalDocument.Load(stream);
        }

        var configContext = await LoadAndValidateConfigAsync(nugetConfigFile, mappings);
        AddMissingPackageSources(configContext);

        if (configContext.PackageSourceMapping is not null)
        {
            UpdateExistingPackageSourceMapping(configContext);
        }
        else
        {
            CreateNewPackageSourceMapping(configContext);
        }

        if (confirmationCallback is not null)
        {
            // Convert XDocument to XmlDocument for the callback
            var proposedDocument = new XmlDocument();
            using var stringWriter = new StringWriter();
            configContext.Document.Save(stringWriter);
            proposedDocument.LoadXml(stringWriter.ToString());

            var shouldProceed = await confirmationCallback(nugetConfigFile, originalDocument, proposedDocument, cancellationToken);
            if (!shouldProceed)
            {
                return;
            }
        }

        if (configureGlobalPackagesFolder)
        {
            AddGlobalPackagesFolderConfiguration(configContext);
        }

        await SaveConfigAsync(nugetConfigFile, configContext.Document);
    }

    private static async Task<NuGetConfigContext> LoadAndValidateConfigAsync(FileInfo nugetConfigFile, PackageMapping[] mappings)
    {
        // Get the required sources from mappings
        var requiredSources = mappings
            .Select(m => m.Source)
            .Distinct(PackageSourceIdentity.Comparer)
            .ToArray();

        // Load the existing NuGet.config
        XDocument doc;
        await using (var stream = nugetConfigFile.OpenRead())
        {
            doc = XDocument.Load(stream);
        }

        var configuration = doc.Root ?? new XElement("configuration");
        if (doc.Root is null)
        {
            doc.Add(configuration);
        }

        var packageSources = configuration.Element("packageSources");
        if (packageSources is null)
        {
            packageSources = new XElement("packageSources");
            configuration.Add(packageSources);
        }

        var existingAdds = packageSources.Elements("add").ToArray();
        var sourceToExistingKeys = BuildExistingSourceMappings(existingAdds);

        return new NuGetConfigContext
        {
            Document = doc,
            Configuration = configuration,
            PackageSources = packageSources,
            PackageSourceMapping = configuration.Element("packageSourceMapping"),
            Mappings = mappings,
            RequiredSources = requiredSources,
            ExistingAdds = existingAdds,
            SourceToExistingKeys = sourceToExistingKeys
        };
    }

    private static Dictionary<string, List<string>> BuildExistingSourceMappings(XElement[] existingAdds)
    {
        // NuGet permits multiple source keys for the same value, while mappings, credentials, and
        // client certificates are associated with those keys. Retain every alias so URL-targeted
        // mappings cannot disconnect a key from its key-scoped configuration.
        var sourceToExistingKeys = new Dictionary<string, List<string>>(PackageSourceIdentity.Comparer);
        foreach (var addElement in existingAdds)
        {
            var key = (string?)addElement.Attribute("key");
            var value = (string?)addElement.Attribute("value");
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
            {
                AddSourceKey(sourceToExistingKeys, value, key);
            }
        }

        return sourceToExistingKeys;
    }

    private static void AddMissingPackageSources(NuGetConfigContext context)
    {
        var existingValues = new HashSet<string>(context.ExistingAdds
            .Select(e => (string?)e.Attribute("value") ?? string.Empty), PackageSourceIdentity.Comparer);
        var existingKeys = new HashSet<string>(context.ExistingAdds
            .Select(e => (string?)e.Attribute("key") ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        var missingSources = context.RequiredSources
            .Where(source =>
                !existingValues.Contains(source) &&
                (!PackageSourceIdentity.IsNamedSourceReference(source) || !existingKeys.Contains(source)))
            .ToArray();

        // Add missing sources
        foreach (var source in missingSources)
        {
            var key = source;
            for (var suffix = 0; !existingKeys.Add(key); suffix++)
            {
                key = $"aspire-{suffix}";
            }

            var add = new XElement("add");
            add.SetAttributeValue("key", key);
            add.SetAttributeValue("value", source);
            context.PackageSources.Add(add);
            AddSourceKey(context.SourceToExistingKeys, source, key);
        }
    }

    private static void UpdateExistingPackageSourceMapping(NuGetConfigContext context)
    {
        var packageSourceMapping = context.PackageSourceMapping!;

        var mappingsByPattern = context.Mappings.ToLookup(
            static mapping => mapping.PackageFilter,
            StringComparer.OrdinalIgnoreCase);

        // Track sources that still have packages after remapping
        var sourcesInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        RemapExistingPatterns(packageSourceMapping, mappingsByPattern, context.SourceToExistingKeys, sourcesInUse);
        AddRequiredPatterns(packageSourceMapping, context, sourcesInUse);
        FixUrlBasedPackageSourceKeys(packageSourceMapping, context.SourceToExistingKeys, sourcesInUse);
        HandleWildcardMappingForExistingSources(packageSourceMapping, context, sourcesInUse);
        RemoveEmptyPackageSourceElements(packageSourceMapping, context.PackageSources, context.SourceToExistingKeys, sourcesInUse);
        RemoveOrphanedSafeToRemoveSources(context, sourcesInUse);
    }

    // Strip safe-to-remove sources (e.g. ~/.aspire/hives/*/packages) from <packageSources>
    // when they have no corresponding <packageSourceMapping> entry after the merge and are not
    // required by the new channel. Without this, CLI-managed dogfood feeds that were listed in
    // <packageSources> but never mapped (or whose mapping was rewritten by an earlier merge)
    // would linger forever and break `dotnet restore` with NU1301 once the hive directory is
    // cleaned up on disk.
    private static void RemoveOrphanedSafeToRemoveSources(NuGetConfigContext context, HashSet<string> sourcesInUse)
    {
        var orphanedSources = context.PackageSources.Elements("add")
            .Where(add =>
            {
                var key = (string?)add.Attribute("key");
                var value = (string?)add.Attribute("value");

                if (string.IsNullOrEmpty(key))
                {
                    return false;
                }

                if (sourcesInUse.Contains(key))
                {
                    return false;
                }

                if (context.RequiredSources.Contains(key, StringComparer.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(value) && context.RequiredSources.Contains(value, PackageSourceIdentity.Comparer))
                {
                    return false;
                }

                return IsSourceSafeToRemove(key, value);
            })
            .ToArray();

        foreach (var orphan in orphanedSources)
        {
            orphan.Remove();
        }
    }

    private static void RemapExistingPatterns(
        XElement packageSourceMapping,
        ILookup<string, PackageMapping> mappingsByPattern,
        IReadOnlyDictionary<string, List<string>> sourceToExistingKeys,
        HashSet<string> sourcesInUse)
    {
        var packageSourceElements = packageSourceMapping.Elements("packageSource").ToArray();

        foreach (var packageSourceElement in packageSourceElements)
        {
            var sourceKey = (string?)packageSourceElement.Attribute("key");
            if (string.IsNullOrEmpty(sourceKey))
            {
                continue;
            }

            var packageElements = packageSourceElement.Elements("package").ToArray();
            var elementsToRemove = new List<XElement>();

            foreach (var packageElement in packageElements)
            {
                var pattern = (string?)packageElement.Attribute("pattern");
                if (string.IsNullOrEmpty(pattern))
                {
                    continue;
                }

                if (mappingsByPattern.Contains(pattern))
                {
                    var isRequiredForSource = mappingsByPattern[pattern].Any(mapping =>
                        GetPackageSourceKeys(mapping.Source, sourceToExistingKeys)
                            .Contains(sourceKey, StringComparer.OrdinalIgnoreCase));
                    if (!isRequiredForSource)
                    {
                        elementsToRemove.Add(packageElement);
                    }
                }
                // If the pattern is not defined in the new mappings, only remove it in specific cases
                else
                {
                    // Get the source URL to check if this source should keep obsolete patterns
                    var sourceElement = sourceToExistingKeys.FirstOrDefault(kvp =>
                        kvp.Value.Contains(sourceKey, StringComparer.OrdinalIgnoreCase));
                    var sourceValue = sourceElement.Key ?? sourceKey;

                    // Only remove patterns that are not in the new mappings if:
                    // 1. The source is safe to remove (like a PR hive) AND the pattern is Aspire-related, OR
                    // 2. The source is Microsoft-controlled AND the pattern is Aspire-related AND not a wildcard
                    // This preserves user-defined patterns like "Microsoft.Extensions.SpecialPackage*"
                    var isAspireRelatedPattern = IsAspireRelatedPattern(pattern);

                    if ((IsSourceSafeToRemove(sourceKey, sourceValue) && isAspireRelatedPattern) ||
                        (IsMicrosoftControlledSource(sourceKey, sourceValue) && isAspireRelatedPattern && pattern != "*"))
                    {
                        elementsToRemove.Add(packageElement);
                    }
                }
            }

            // Remove patterns that need to be moved
            foreach (var element in elementsToRemove)
            {
                element.Remove();
            }

            // If this source still has packages after removal, mark it as in use
            if (packageSourceElement.Elements("package").Any())
            {
                sourcesInUse.Add(sourceKey);
            }
        }
    }

    private static void AddRequiredPatterns(
        XElement packageSourceMapping,
        NuGetConfigContext context,
        HashSet<string> sourcesInUse)
    {
        foreach (var sourceGroup in context.Mappings.GroupBy(
            static mapping => mapping.Source,
            PackageSourceIdentity.Comparer))
        {
            var targetSource = sourceGroup.Key;

            foreach (var keyToUse in GetPackageSourceKeys(targetSource, context.SourceToExistingKeys))
            {
                // Find or create the packageSource element for this source
                var targetSourceElement = packageSourceMapping.Elements("packageSource")
                    .FirstOrDefault(ps => string.Equals((string?)ps.Attribute("key"), keyToUse, StringComparison.OrdinalIgnoreCase));

                if (targetSourceElement is null)
                {
                    targetSourceElement = new XElement("packageSource");
                    targetSourceElement.SetAttributeValue("key", keyToUse);
                    packageSourceMapping.Add(targetSourceElement);
                }

                foreach (var pattern in sourceGroup
                    .Select(static mapping => mapping.PackageFilter)
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var existingPattern = targetSourceElement.Elements("package")
                        .FirstOrDefault(package => string.Equals(
                            (string?)package.Attribute("pattern"),
                            pattern,
                            StringComparison.OrdinalIgnoreCase));

                    if (existingPattern is null)
                    {
                        var packageElement = new XElement("package");
                        packageElement.SetAttributeValue("pattern", pattern);
                        targetSourceElement.Add(packageElement);
                    }
                }

                sourcesInUse.Add(keyToUse);
            }
        }
    }

    private static IReadOnlyList<string> GetPackageSourceKeys(
        string source,
        IReadOnlyDictionary<string, List<string>> sourceToExistingKeys)
        => sourceToExistingKeys.TryGetValue(source, out var existingKeys) ? existingKeys : [source];

    private static void AddSourceKey(
        Dictionary<string, List<string>> sourceToExistingKeys,
        string source,
        string key)
    {
        if (!sourceToExistingKeys.TryGetValue(source, out var existingKeys))
        {
            existingKeys = [];
            sourceToExistingKeys.Add(source, existingKeys);
        }

        if (!existingKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            existingKeys.Add(key);
        }
    }

    private static void FixUrlBasedPackageSourceKeys(
        XElement packageSourceMapping,
        IReadOnlyDictionary<string, List<string>> sourceToExistingKeys,
        HashSet<string> sourcesInUse)
    {
        // Fourth pass: Fix packageSource elements that use URLs as keys when proper keys exist
        var packageSourceElementsToFix = packageSourceMapping.Elements("packageSource")
            .Where(ps =>
            {
                var key = (string?)ps.Attribute("key");
                return !string.IsNullOrEmpty(key) &&
                    sourceToExistingKeys.TryGetValue(key, out var properKeys) &&
                    !properKeys.Contains(key, StringComparer.OrdinalIgnoreCase);
            })
            .ToArray();

        foreach (var elementToFix in packageSourceElementsToFix)
        {
            var urlKey = (string?)elementToFix.Attribute("key");
            if (!sourceToExistingKeys.TryGetValue(urlKey!, out var properKeys))
            {
                continue;
            }

            foreach (var properKey in properKeys)
            {
                var existingProperElement = packageSourceMapping.Elements("packageSource")
                    .FirstOrDefault(ps => string.Equals((string?)ps.Attribute("key"), properKey, StringComparison.OrdinalIgnoreCase));
                if (existingProperElement is null)
                {
                    existingProperElement = new XElement("packageSource", new XAttribute("key", properKey));
                    packageSourceMapping.Add(existingProperElement);
                }

                foreach (var packageToCopy in elementToFix.Elements("package"))
                {
                    var pattern = (string?)packageToCopy.Attribute("pattern");
                    if (!existingProperElement.Elements("package").Any(package =>
                        string.Equals((string?)package.Attribute("pattern"), pattern, StringComparison.OrdinalIgnoreCase)))
                    {
                        existingProperElement.Add(new XElement(packageToCopy));
                    }
                }

                sourcesInUse.Add(properKey);
            }

            elementToFix.Remove();
        }
    }

    private static void HandleWildcardMappingForExistingSources(
        XElement packageSourceMapping,
        NuGetConfigContext context,
        HashSet<string> sourcesInUse)
    {
        // Check if we have a wildcard pattern being added - if so, add it to unmapped existing sources
        var hasWildcardMapping = context.Mappings.Any(m => m.PackageFilter == "*");
        if (hasWildcardMapping)
        {
            // Find all existing sources
            var existingSourceKeys = context.ExistingAdds
                .Select(add => (string?)add.Attribute("key"))
                .Where(key => !string.IsNullOrEmpty(key))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Find sources that have any package patterns (after all the processing above)
            var sourcesWithPatterns = packageSourceMapping.Elements("packageSource")
                .Where(ps => ps.Elements("package").Any())
                .Select(ps => (string?)ps.Attribute("key"))
                .Where(key => !string.IsNullOrEmpty(key))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var sourcesWithoutAnyPatterns = existingSourceKeys.Except(sourcesWithPatterns, StringComparer.OrdinalIgnoreCase).ToArray();

            // Only give wildcard patterns to sources that:
            // 1. Have no patterns now
            // 2. Are not safe to remove (user-defined sources)
            // 3. Are required by the current channel OR are not Microsoft-controlled sources
            foreach (var sourceKey in sourcesWithoutAnyPatterns)
            {
                // Get the source URL to check if it's safe to give it a wildcard pattern
                var sourceElement = context.ExistingAdds
                    .FirstOrDefault(add => string.Equals((string?)add.Attribute("key"), sourceKey, StringComparison.OrdinalIgnoreCase));
                var sourceValue = (string?)sourceElement?.Attribute("value");

                // Check if this source is required by the current channel
                var isRequiredByCurrentChannel =
                    context.RequiredSources.Contains(sourceKey, StringComparer.OrdinalIgnoreCase) ||
                    sourceValue is not null && context.RequiredSources.Contains(sourceValue, PackageSourceIdentity.Comparer);

                // For user-defined sources, give them wildcard patterns to remain functional
                // Only skip this for sources that we would remove anyway (like PR hives) OR
                // Microsoft-controlled sources that are not required by the current channel
                if (!IsSourceSafeToRemove(sourceKey, sourceValue) &&
                    (isRequiredByCurrentChannel || !IsMicrosoftControlledSource(sourceKey, sourceValue)))
                {
                    var packageSourceElement = new XElement("packageSource");
                    packageSourceElement.SetAttributeValue("key", sourceKey);

                    var wildcardPackage = new XElement("package");
                    wildcardPackage.SetAttributeValue("pattern", "*");
                    packageSourceElement.Add(wildcardPackage);

                    packageSourceMapping.Add(packageSourceElement);
                    sourcesInUse.Add(sourceKey);
                }
            }
        }
    }

    private static bool IsMicrosoftControlledSource(string sourceKey, string? sourceValue)
    {
        var urlToCheck = sourceValue ?? sourceKey;

        if (string.IsNullOrEmpty(urlToCheck))
        {
            return false;
        }

        // Check if this is a Microsoft/Azure DevOps feed
        if (urlToCheck.Contains("pkgs.dev.azure.com"))
        {
            return true;
        }

        // Check if this is an official NuGet.org feed
        if (urlToCheck.Contains("api.nuget.org"))
        {
            return true;
        }

        return false;
    }

    private static bool IsAspireRelatedPattern(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        // Patterns that start with "Aspire" are Aspire-related
        // Wildcard patterns are not Aspire-specific
        // Other Microsoft.Extensions.* patterns (like "Microsoft.Extensions.SpecialPackage*") are NOT Aspire-related
        return pattern.StartsWith("Aspire", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceSafeToRemove(string sourceKey, string? sourceValue)
    {
        // Only remove sources that we know are tied to Aspire channels or CLI-managed hive feeds
        if (string.IsNullOrEmpty(sourceKey) && string.IsNullOrEmpty(sourceValue))
        {
            return false;
        }

        var urlToCheck = sourceValue ?? sourceKey;

        // Check if this is an Aspire hive feed
        if (!string.IsNullOrEmpty(urlToCheck) && urlToCheck.Contains(".aspire") && urlToCheck.Contains("hives"))
        {
            return true;
        }

        // Only remove very specific Azure DevOps feeds that we know are temporary (like aspire PR feeds)
        // Don't remove official .NET feeds or other potentially permanent feeds
        if (!string.IsNullOrEmpty(urlToCheck) && urlToCheck.Contains("pkgs.dev.azure.com"))
        {
            // Only remove if it's specifically an Aspire-related feed
            if (urlToCheck.Contains("aspire", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Be conservative - don't remove other Azure DevOps feeds as they might be official
            return false;
        }

        // Don't remove other sources - they may be user-defined
        return false;
    }

    private static void RemoveEmptyPackageSourceElements(
        XElement packageSourceMapping,
        XElement packageSources,
        IReadOnlyDictionary<string, List<string>> sourceToExistingKeys,
        HashSet<string> sourcesInUse)
    {
        // Fifth pass: Remove empty packageSource elements and their corresponding sources from packageSources
        var emptyPackageSourceElements = packageSourceMapping.Elements("packageSource")
            .Where(ps => !ps.Elements("package").Any())
            .ToArray();

        foreach (var emptyElement in emptyPackageSourceElements)
        {
            var sourceKey = (string?)emptyElement.Attribute("key");
            emptyElement.Remove();

            // Remove the corresponding source from packageSources if it's not in use elsewhere
            // For empty package source elements, we remove the source regardless of whether it's "safe to remove"
            // because an empty package source element means the source is no longer serving any patterns
            if (!string.IsNullOrEmpty(sourceKey) && !sourcesInUse.Contains(sourceKey))
            {
                // Also check if any existing source key maps to this URL (for URL->key mapping scenario)
                var isUsedByExistingKey = sourceToExistingKeys.Any(kvp =>
                    PackageSourceIdentity.Comparer.Equals(kvp.Key, sourceKey) &&
                    kvp.Value.Any(sourcesInUse.Contains));

                if (!isUsedByExistingKey)
                {
                    var sourceToRemove = packageSources.Elements("add")
                        .FirstOrDefault(add => string.Equals((string?)add.Attribute("key"), sourceKey, StringComparison.OrdinalIgnoreCase) ||
                                              PackageSourceIdentity.Comparer.Equals((string?)add.Attribute("value"), sourceKey));
                    sourceToRemove?.Remove();
                }
            }
        }
    }

    private static void CreateNewPackageSourceMapping(NuGetConfigContext context)
    {
        // Create package source mapping section if it doesn't exist
        var packageSourceMapping = new XElement("packageSourceMapping");
        context.Configuration.Add(packageSourceMapping);

        // Group patterns by their target source and add them
        var patternsBySource = context.Mappings.GroupBy(m => m.Source, PackageSourceIdentity.Comparer);

        foreach (var sourceGroup in patternsBySource)
        {
            var sourceUrl = sourceGroup.Key;
            foreach (var keyToUse in GetPackageSourceKeys(sourceUrl, context.SourceToExistingKeys))
            {
                var packageSource = new XElement("packageSource");
                packageSource.SetAttributeValue("key", keyToUse);

                foreach (var mapping in sourceGroup)
                {
                    var packageElement = new XElement("package");
                    packageElement.SetAttributeValue("pattern", mapping.PackageFilter);
                    packageSource.Add(packageElement);
                }

                packageSourceMapping.Add(packageSource);
            }
        }

        PreserveOriginalSourceFunctionality(packageSourceMapping, context);
    }

    private static void PreserveOriginalSourceFunctionality(XElement packageSourceMapping, NuGetConfigContext context)
    {
        // Since we're creating packageSourceMapping for the first time, we need to preserve the original behavior
        // where all existing sources could serve all packages. Any existing source that doesn't get specific
        // patterns from our mappings should get a wildcard pattern to remain functional.
        var existingSourceKeys = context.ExistingAdds
            .Select(add => (string?)add.Attribute("key"))
            .Where(key => !string.IsNullOrEmpty(key))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Find sources that have mappings from our new packageSourceMapping entries
        var sourcesWithNewMappings = packageSourceMapping.Elements("packageSource")
            .Select(ps => (string?)ps.Attribute("key"))
            .Where(key => !string.IsNullOrEmpty(key))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sourcesWithoutAnyPatterns = existingSourceKeys.Except(sourcesWithNewMappings, StringComparer.OrdinalIgnoreCase).ToArray();

        // Add wildcard pattern to existing sources that don't have any patterns to preserve their original functionality
        // Only exclude PR hives that are not the current target
        foreach (var sourceKey in sourcesWithoutAnyPatterns)
        {
            // Get the source URL to check if it should get a wildcard pattern
            var sourceElement = context.ExistingAdds
                .FirstOrDefault(add => string.Equals((string?)add.Attribute("key"), sourceKey, StringComparison.OrdinalIgnoreCase));
            var sourceValue = (string?)sourceElement?.Attribute("value");

            // Only exclude PR hives and Aspire-specific feeds that are not the current target
            if (!IsSourceSafeToRemove(sourceKey, sourceValue))
            {
                var packageSourceElement = new XElement("packageSource");
                packageSourceElement.SetAttributeValue("key", sourceKey);

                var wildcardPackage = new XElement("package");
                wildcardPackage.SetAttributeValue("pattern", "*");
                packageSourceElement.Add(wildcardPackage);

                packageSourceMapping.Add(packageSourceElement);
            }
        }
    }

    private static async Task SaveConfigAsync(FileInfo nugetConfigFile, XDocument document)
    {
        await using (var writeStream = nugetConfigFile.Open(FileMode.Create, FileAccess.Write, FileShare.None))
        {
            document.Save(writeStream);
        }
    }

    /// <summary>
    /// Checks if any sources from the mappings are missing from the existing NuGet.config
    /// or if package source mappings need to be updated.
    /// </summary>
    /// <param name="targetDirectory">The directory to check for NuGet.config.</param>
    /// <param name="channel">The package channel whose mappings are checked.</param>
    /// <returns>True if sources are missing or mappings need updates, false if all sources and mappings are correctly configured.</returns>
    public static bool HasMissingSources(DirectoryInfo targetDirectory, PackageChannel channel)
    {
        ArgumentNullException.ThrowIfNull(targetDirectory);
        ArgumentNullException.ThrowIfNull(channel);

        var mappings = channel.Mappings;
        if (channel.Type is not PackageChannelType.Explicit || mappings is null || mappings.Length == 0)
        {
            return false; // Implicit channels or empty mappings never require config changes.
        }

        if (!TryFindNuGetConfigInDirectory(targetDirectory, out var nugetConfigFile))
        {
            return true; // No config exists, so sources are "missing"
        }

        var requiredSources = mappings
            .Select(m => m.Source)
            .Distinct(PackageSourceIdentity.Comparer)
            .ToArray();

        try
        {
            using var stream = nugetConfigFile.OpenRead();
            var doc = XDocument.Load(stream);

            var packageSources = doc.Root?.Element("packageSources");
            if (packageSources is null)
            {
                return true;
            }

            var existingAdds = packageSources.Elements("add").ToArray();
            var existingValues = new HashSet<string>(existingAdds
                .Select(e => (string?)e.Attribute("value") ?? string.Empty), PackageSourceIdentity.Comparer);
            var existingKeys = new HashSet<string>(existingAdds
                .Select(e => (string?)e.Attribute("key") ?? string.Empty), StringComparer.OrdinalIgnoreCase);

            var sourceToExistingKeys = BuildExistingSourceMappings(existingAdds);

            var missingSources = requiredSources
                .Where(source =>
                    !existingValues.Contains(source) &&
                    (!PackageSourceIdentity.IsNamedSourceReference(source) || !existingKeys.Contains(source)))
                .ToArray();

            // Check if any sources are missing
            if (missingSources.Length > 0)
            {
                return true;
            }

            // Check if package source mappings need to be updated
            var packageSourceMapping = doc.Root?.Element("packageSourceMapping");
            if (packageSourceMapping is null)
            {
                return true;
            }

            var mappingsByPattern = mappings.ToLookup(
                static mapping => mapping.PackageFilter,
                StringComparer.OrdinalIgnoreCase);
            var packageSourceElements = packageSourceMapping.Elements("packageSource").ToArray();

            foreach (var packageSourceElement in packageSourceElements)
            {
                var sourceKey = (string?)packageSourceElement.Attribute("key");
                if (string.IsNullOrEmpty(sourceKey))
                {
                    continue;
                }

                foreach (var packageElement in packageSourceElement.Elements("package"))
                {
                    var pattern = (string?)packageElement.Attribute("pattern");
                    if (string.IsNullOrEmpty(pattern) || !mappingsByPattern.Contains(pattern))
                    {
                        continue;
                    }

                    if (!mappingsByPattern[pattern].Any(mapping =>
                        GetPackageSourceKeys(mapping.Source, sourceToExistingKeys)
                            .Contains(sourceKey, StringComparer.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            foreach (var mapping in mappings)
            {
                foreach (var expectedKey in GetPackageSourceKeys(mapping.Source, sourceToExistingKeys))
                {
                    var expectedSourceElement = packageSourceElements.FirstOrDefault(element =>
                        string.Equals((string?)element.Attribute("key"), expectedKey, StringComparison.OrdinalIgnoreCase));
                    if (expectedSourceElement is null ||
                        !expectedSourceElement.Elements("package").Any(package =>
                            string.Equals((string?)package.Attribute("pattern"), mapping.PackageFilter, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            return false; // All sources and mappings are correctly configured
        }
        catch
        {
            // If we can't read the file, assume sources are missing
            return true;
        }
    }

    internal static bool TryFindNuGetConfigInDirectory(DirectoryInfo directory, [NotNullWhen(true)] out FileInfo? nugetConfigFile)
    {
        ArgumentNullException.ThrowIfNull(directory);
        // Find all files whose name matches "nuget.config" ignoring case in the top-level directory only
        var matches = directory
            .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
            .Where(f => string.Equals(f.Name, "nuget.config", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length > 1)
        {
            throw new InvalidOperationException($"Multiple NuGet.config files found in '{directory.FullName}' differing only by case.");
        }

        nugetConfigFile = matches.SingleOrDefault();
        return matches.Length == 1;
    }

    private static async Task AddGlobalPackagesFolderToConfigAsync(FileInfo configFile)
    {
        XDocument doc;
        await using (var stream = configFile.OpenRead())
        {
            doc = XDocument.Load(stream);
        }

        var configuration = doc.Root ?? throw new InvalidOperationException("Invalid NuGet config structure");
        AddGlobalPackagesFolderConfiguration(configuration);

        await using (var writeStream = configFile.Open(FileMode.Create, FileAccess.Write, FileShare.None))
        {
            doc.Save(writeStream);
        }
    }

    private static void AddGlobalPackagesFolderConfiguration(NuGetConfigContext configContext)
    {
        AddGlobalPackagesFolderConfiguration(configContext.Configuration);
    }

    // Default workspace-relative cache used when no explicit path is supplied. Matches the
    // long-standing 'aspire init / aspire new' convention of putting a per-workspace
    // .nugetpackages folder next to the merged nuget.config so staging-vs-stable cache
    // poisoning doesn't bleed into the user's global ~/.nuget/packages folder.
    internal const string DefaultGlobalPackagesFolderValue = ".nugetpackages";

    internal static void AddGlobalPackagesFolderConfiguration(XElement configuration, string? globalPackagesFolderValue = null)
    {
        // Check if config section already exists
        var config = configuration.Element("config");
        if (config is null)
        {
            config = new XElement("config");
            configuration.Add(config);
        }

        // Check if globalPackagesFolder already exists
        var existingGlobalPackagesFolder = config.Elements("add")
            .FirstOrDefault(add => string.Equals((string?)add.Attribute("key"), "globalPackagesFolder", StringComparison.OrdinalIgnoreCase));

        if (existingGlobalPackagesFolder is null)
        {
            // Add globalPackagesFolder configuration. Callers (e.g. PrebuiltAppHostServer's
            // temporary nuget.config) supply an absolute path when the config file itself is
            // ephemeral so the cached packages outlive the config — otherwise NuGet would
            // resolve the relative ".nugetpackages" under the about-to-be-deleted temp dir.
            var globalPackagesFolderAdd = new XElement("add");
            globalPackagesFolderAdd.SetAttributeValue("key", "globalPackagesFolder");
            globalPackagesFolderAdd.SetAttributeValue("value", globalPackagesFolderValue ?? DefaultGlobalPackagesFolderValue);
            config.Add(globalPackagesFolderAdd);
        }
        else if (globalPackagesFolderValue is not null)
        {
            // Generated staging configs supply a feed-keyed absolute cache path. It must replace an
            // inherited value or same-version packages from different staging feeds can collide.
            existingGlobalPackagesFolder.SetAttributeValue("value", globalPackagesFolderValue);
        }
    }
}
