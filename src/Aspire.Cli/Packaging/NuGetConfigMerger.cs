// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using System.Xml;
using System.Diagnostics.CodeAnalysis;

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
        public required Dictionary<string, string> UrlToExistingKey { get; init; }
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

        if (!targetDirectory.Exists)
        {
            targetDirectory.Create();
        }

        if (!TryFindNuGetConfigInDirectory(targetDirectory, out var nugetConfigFile))
        {
            await CreateNewNuGetConfigAsync(targetDirectory, channel, confirmationCallback, cancellationToken);
        }
        else
        {
            await UpdateExistingNuGetConfigAsync(nugetConfigFile, channel, confirmationCallback, cancellationToken);
        }
    }

    private static async Task CreateNewNuGetConfigAsync(DirectoryInfo targetDirectory, PackageChannel channel, Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback, CancellationToken cancellationToken)
    {
        var mappings = channel.Mappings;
        if (mappings is null || mappings.Length == 0)
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
        
        if (channel.ConfigureGlobalPackagesFolder)
        {
            // Need to modify the temporary config to add globalPackagesFolder before copying
            await AddGlobalPackagesFolderToConfigAsync(tmpConfig.ConfigFile);
        }
        
        File.Copy(tmpConfig.ConfigFile.FullName, targetPath, overwrite: true);
    }

    private static async Task UpdateExistingNuGetConfigAsync(FileInfo nugetConfigFile, PackageChannel channel, Func<FileInfo, XmlDocument?, XmlDocument, CancellationToken, Task<bool>>? confirmationCallback, CancellationToken cancellationToken)
    {
        var mappings = channel.Mappings;
        if (mappings is null || mappings.Length == 0)
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
        
        if (channel.ConfigureGlobalPackagesFolder)
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
        var urlToExistingKey = BuildExistingSourceMappings(existingAdds);

        return new NuGetConfigContext
        {
            Document = doc,
            Configuration = configuration,
            PackageSources = packageSources,
            PackageSourceMapping = configuration.Element("packageSourceMapping"),
            Mappings = mappings,
            RequiredSources = requiredSources,
            ExistingAdds = existingAdds,
            UrlToExistingKey = urlToExistingKey
        };
    }

    private static Dictionary<string, string> BuildExistingSourceMappings(XElement[] existingAdds)
    {
        var urlToExistingKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var addElement in existingAdds)
        {
            var key = (string?)addElement.Attribute("key");
            var value = (string?)addElement.Attribute("value");
            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
            {
                urlToExistingKey[value] = key;
            }
        }
        return urlToExistingKey;
    }

    private static void AddMissingPackageSources(NuGetConfigContext context)
    {
        var existingValues = new HashSet<string>(context.ExistingAdds
            .Select(e => (string?)e.Attribute("value") ?? string.Empty), StringComparer.OrdinalIgnoreCase);
        var existingKeys = new HashSet<string>(context.ExistingAdds
            .Select(e => (string?)e.Attribute("key") ?? string.Empty), StringComparer.OrdinalIgnoreCase);

        var missingSources = context.RequiredSources
            .Where(s => !existingValues.Contains(s) && !existingKeys.Contains(s))
            .ToArray();

        // Add missing sources
        foreach (var source in missingSources)
        {
            // Use the source URL as both key and value for consistency with our temporary config
            var add = new XElement("add");
            add.SetAttributeValue("key", source);
            add.SetAttributeValue("value", source);
            context.PackageSources.Add(add);
        }
    }

    private static void UpdateExistingPackageSourceMapping(NuGetConfigContext context)
    {
        var packageSourceMapping = context.PackageSourceMapping!;
        
        // Create a lookup of patterns to new sources from the mappings
        var patternToNewSource = context.Mappings.ToDictionary(m => m.PackageFilter, m => m.Source, StringComparer.OrdinalIgnoreCase);
        
        // Track sources that still have packages after remapping
        var sourcesInUse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        var patternsToAdd = RemapExistingPatterns(packageSourceMapping, patternToNewSource, context.UrlToExistingKey, sourcesInUse);
        AddRemappedPatterns(packageSourceMapping, patternsToAdd, context.UrlToExistingKey, sourcesInUse);
        AddNewPatterns(packageSourceMapping, context, sourcesInUse);
        FixUrlBasedPackageSourceKeys(packageSourceMapping, context.UrlToExistingKey, sourcesInUse);
        HandleWildcardMappingForExistingSources(packageSourceMapping, context, sourcesInUse);
        RemoveEmptyPackageSourceElements(packageSourceMapping, context.PackageSources, context.UrlToExistingKey, sourcesInUse);
    }

    private static List<(string pattern, string newSource)> RemapExistingPatterns(
        XElement packageSourceMapping, 
        Dictionary<string, string> patternToNewSource, 
        Dictionary<string, string> urlToExistingKey,
        HashSet<string> sourcesInUse)
    {
        // First pass: Remove patterns that need to be remapped and track what needs to be added
        var patternsToAdd = new List<(string pattern, string newSource)>();
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

                // Check if this pattern needs to be remapped to a new source
                if (patternToNewSource.TryGetValue(pattern, out var newSource))
                {
                    // Determine the key that will be used for the new source
                    var expectedKey = urlToExistingKey.TryGetValue(newSource, out var existingKey) ? existingKey : newSource;
                    
                    if (!string.Equals(sourceKey, expectedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        // This pattern needs to be moved to the new source
                        elementsToRemove.Add(packageElement);
                        patternsToAdd.Add((pattern, newSource));
                    }
                }
                // If the pattern is not defined in the new mappings, only remove it in specific cases
                else if (!patternToNewSource.ContainsKey(pattern))
                {
                    // Get the source URL to check if this source should keep obsolete patterns
                    var sourceElement = urlToExistingKey.FirstOrDefault(kvp => string.Equals(kvp.Value, sourceKey, StringComparison.OrdinalIgnoreCase));
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

        return patternsToAdd;
    }

    private static void AddRemappedPatterns(
        XElement packageSourceMapping,
        List<(string pattern, string newSource)> patternsToAdd,
        Dictionary<string, string> urlToExistingKey,
        HashSet<string> sourcesInUse)
    {
        // Second pass: Group patterns by source and add them all to the same packageSource element
        var patternsBySource = patternsToAdd.GroupBy(x => x.newSource, StringComparer.OrdinalIgnoreCase);
        
        foreach (var sourceGroup in patternsBySource)
        {
            var newSource = sourceGroup.Key;
            
            // Use existing key if available, otherwise use the source URL as key
            var keyToUse = urlToExistingKey.TryGetValue(newSource, out var existingKey) ? existingKey : newSource;
            
            // Find or create the packageSource element for this source using the appropriate key
            var targetSourceElement = packageSourceMapping.Elements("packageSource")
                .FirstOrDefault(ps => string.Equals((string?)ps.Attribute("key"), keyToUse, StringComparison.OrdinalIgnoreCase));
            
            if (targetSourceElement is null)
            {
                // Create new packageSource element for this source using the appropriate key
                targetSourceElement = new XElement("packageSource");
                targetSourceElement.SetAttributeValue("key", keyToUse);
                packageSourceMapping.Add(targetSourceElement);
            }

            // Add all patterns for this source
            foreach (var (pattern, _) in sourceGroup)
            {
                // Check if this pattern already exists in the target source
                var existingPattern = targetSourceElement.Elements("package")
                    .FirstOrDefault(p => string.Equals((string?)p.Attribute("pattern"), pattern, StringComparison.OrdinalIgnoreCase));
                
                if (existingPattern is null)
                {
                    // Add the package pattern to the target source
                    var packageElement = new XElement("package");
                    packageElement.SetAttributeValue("pattern", pattern);
                    targetSourceElement.Add(packageElement);
                }
            }
            
            sourcesInUse.Add(keyToUse);
        }
    }

    private static void AddNewPatterns(
        XElement packageSourceMapping,
        NuGetConfigContext context,
        HashSet<string> sourcesInUse)
    {
        // Find patterns from mappings that don't exist anywhere in the current packageSourceMapping
        var existingPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var packageSourceElement in packageSourceMapping.Elements("packageSource"))
        {
            foreach (var packageElement in packageSourceElement.Elements("package"))
            {
                var pattern = (string?)packageElement.Attribute("pattern");
                if (!string.IsNullOrEmpty(pattern))
                {
                    existingPatterns.Add(pattern);
                }
            }
        }

        // Group new patterns by their target source
        var newPatternsBySource = context.Mappings
            .Where(m => !existingPatterns.Contains(m.PackageFilter))
            .GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase);

        foreach (var sourceGroup in newPatternsBySource)
        {
            var targetSource = sourceGroup.Key;
            
            // Use existing key if available, otherwise use the source URL as key
            var keyToUse = context.UrlToExistingKey.TryGetValue(targetSource, out var existingKey) ? existingKey : targetSource;
            
            // Find or create the packageSource element for this source
            var targetSourceElement = packageSourceMapping.Elements("packageSource")
                .FirstOrDefault(ps => string.Equals((string?)ps.Attribute("key"), keyToUse, StringComparison.OrdinalIgnoreCase));
            
            if (targetSourceElement is null)
            {
                // Create new packageSource element for this source
                targetSourceElement = new XElement("packageSource");
                targetSourceElement.SetAttributeValue("key", keyToUse);
                packageSourceMapping.Add(targetSourceElement);
            }

            // Add all new patterns for this source
            foreach (var mapping in sourceGroup)
            {
                // Check if this pattern already exists in the target source (just in case)
                var existingPattern = targetSourceElement.Elements("package")
                    .FirstOrDefault(p => string.Equals((string?)p.Attribute("pattern"), mapping.PackageFilter, StringComparison.OrdinalIgnoreCase));
                
                if (existingPattern is null)
                {
                    // Add the package pattern to the target source
                    var packageElement = new XElement("package");
                    packageElement.SetAttributeValue("pattern", mapping.PackageFilter);
                    targetSourceElement.Add(packageElement);
                }
            }
            
            sourcesInUse.Add(keyToUse);
        }
    }

    private static void FixUrlBasedPackageSourceKeys(
        XElement packageSourceMapping,
        Dictionary<string, string> urlToExistingKey,
        HashSet<string> sourcesInUse)
    {
        // Fourth pass: Fix packageSource elements that use URLs as keys when proper keys exist
        var packageSourceElementsToFix = packageSourceMapping.Elements("packageSource")
            .Where(ps => {
                var key = (string?)ps.Attribute("key");
                return !string.IsNullOrEmpty(key) && urlToExistingKey.TryGetValue(key, out var properKey) && 
                       !string.Equals(key, properKey, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        foreach (var elementToFix in packageSourceElementsToFix)
        {
            var urlKey = (string?)elementToFix.Attribute("key");
            if (urlToExistingKey.TryGetValue(urlKey!, out var properKey))
            {
                // Find if there's already a packageSource with the proper key
                var existingProperElement = packageSourceMapping.Elements("packageSource")
                    .FirstOrDefault(ps => string.Equals((string?)ps.Attribute("key"), properKey, StringComparison.OrdinalIgnoreCase));
                
                if (existingProperElement is not null)
                {
                    // Move all packages from URL-based element to proper key element
                    var packagesToMove = elementToFix.Elements("package").ToArray();
                    foreach (var packageToMove in packagesToMove)
                    {
                        // Check if the pattern already exists in the target element
                        var pattern = (string?)packageToMove.Attribute("pattern");
                        var existingPattern = existingProperElement.Elements("package")
                            .FirstOrDefault(p => string.Equals((string?)p.Attribute("pattern"), pattern, StringComparison.OrdinalIgnoreCase));
                        
                        if (existingPattern is null)
                        {
                            packageToMove.Remove();
                            existingProperElement.Add(packageToMove);
                        }
                    }
                    
                    // Remove the URL-based element if it's now empty
                    if (!elementToFix.Elements("package").Any())
                    {
                        elementToFix.Remove();
                    }
                }
                else
                {
                    // Just update the key to use the proper key
                    elementToFix.SetAttributeValue("key", properKey);
                    sourcesInUse.Add(properKey);
                }
            }
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
            
            // Only add wildcard patterns to sources that originally had NO patterns at all
            // Sources that had patterns but lost them due to remapping should be removed entirely
            
            // Check the original packageSourceMapping to see which sources had patterns originally
            var originalSourcesWithPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var originalPsm = context.PackageSourceMapping;
            if (originalPsm != null)
            {
                foreach (var ps in originalPsm.Elements("packageSource"))
                {
                    var originalSourceKey = (string?)ps.Attribute("key");
                    if (!string.IsNullOrEmpty(originalSourceKey) && ps.Elements("package").Any())
                    {
                        // Add the original key
                        originalSourcesWithPatterns.Add(originalSourceKey);
                        
                        // Also add the proper key if this was a URL-based key
                        if (context.UrlToExistingKey.TryGetValue(originalSourceKey, out var properKey))
                        {
                            originalSourcesWithPatterns.Add(properKey);
                        }
                    }
                }
            }
            
            // Only give wildcard patterns to sources that:
            // 1. Have no patterns now
            // 2. Originally had no patterns either (were unmapped before) OR still have some patterns left
            // 3. Are not safe to remove (user-defined sources)
            // 4. Are required by the current channel OR are not Microsoft-controlled sources
            foreach (var sourceKey in sourcesWithoutAnyPatterns)
            {
                // Get the source URL to check if it's safe to give it a wildcard pattern
                var sourceElement = context.ExistingAdds
                    .FirstOrDefault(add => string.Equals((string?)add.Attribute("key"), sourceKey, StringComparison.OrdinalIgnoreCase));
                var sourceValue = (string?)sourceElement?.Attribute("value");
                
                // Check if this source is required by the current channel
                var isRequiredByCurrentChannel = context.RequiredSources.Contains(sourceKey, StringComparer.OrdinalIgnoreCase) ||
                                               context.RequiredSources.Contains(sourceValue ?? "", StringComparer.OrdinalIgnoreCase);
                
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
            
            // Also give wildcard patterns to sources that still have some patterns left but should remain fully functional
            // when there's a wildcard mapping that could interfere with their ability to serve packages
            // But only for user-defined sources, not Microsoft-controlled feeds
            var sourcesWithPatternsLeft = packageSourceMapping.Elements("packageSource")
                .Where(ps => ps.Elements("package").Any() && !ps.Elements("package").Any(p => (string?)p.Attribute("pattern") == "*"))
                .Select(ps => (string?)ps.Attribute("key"))
                .Where(key => !string.IsNullOrEmpty(key))
                .Cast<string>()
                .ToArray();
                
            foreach (var sourceKey in sourcesWithPatternsLeft)
            {
                // Get the source URL to check if it's a user-defined source
                var sourceElement = context.ExistingAdds
                    .FirstOrDefault(add => string.Equals((string?)add.Attribute("key"), sourceKey, StringComparison.OrdinalIgnoreCase));
                var sourceValue = (string?)sourceElement?.Attribute("value");
                
                // For user-defined sources that still have patterns, also give them wildcard patterns
                // to ensure they can serve other packages too. But skip Microsoft-controlled sources
                // that have specific patterns as they are intended to serve specific packages only.
                if (!IsSourceSafeToRemove(sourceKey, sourceValue) && !IsMicrosoftControlledSource(sourceKey, sourceValue))
                {
                    var packageSourceElement = packageSourceMapping.Elements("packageSource")
                        .FirstOrDefault(ps => string.Equals((string?)ps.Attribute("key"), sourceKey, StringComparison.OrdinalIgnoreCase));
                    
                    if (packageSourceElement != null)
                    {
                        var wildcardPackage = new XElement("package");
                        wildcardPackage.SetAttributeValue("pattern", "*");
                        packageSourceElement.Add(wildcardPackage);
                        sourcesInUse.Add(sourceKey);
                    }
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
        // Only remove sources that we know are tied to Aspire channels or PR hives
        if (string.IsNullOrEmpty(sourceKey) && string.IsNullOrEmpty(sourceValue))
        {
            return false;
        }

        var urlToCheck = sourceValue ?? sourceKey;
        
        // Check if this is an Aspire PR hive
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
        Dictionary<string, string> urlToExistingKey,
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
                var isUsedByExistingKey = urlToExistingKey.Any(kvp => 
                    string.Equals(kvp.Key, sourceKey, StringComparison.OrdinalIgnoreCase) && 
                    sourcesInUse.Contains(kvp.Value));
                    
                if (!isUsedByExistingKey)
                {
                    var sourceToRemove = packageSources.Elements("add")
                        .FirstOrDefault(add => string.Equals((string?)add.Attribute("key"), sourceKey, StringComparison.OrdinalIgnoreCase) ||
                                              string.Equals((string?)add.Attribute("value"), sourceKey, StringComparison.OrdinalIgnoreCase));
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
        var patternsBySource = context.Mappings.GroupBy(m => m.Source, StringComparer.OrdinalIgnoreCase);
        
        foreach (var sourceGroup in patternsBySource)
        {
            var sourceUrl = sourceGroup.Key;
            // Use existing key if available, otherwise use the source URL as key
            var keyToUse = context.UrlToExistingKey.TryGetValue(sourceUrl, out var existingKey) ? existingKey : sourceUrl;
            
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
            .Distinct(StringComparer.OrdinalIgnoreCase)
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
                .Select(e => (string?)e.Attribute("value") ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            var existingKeys = new HashSet<string>(existingAdds
                .Select(e => (string?)e.Attribute("key") ?? string.Empty), StringComparer.OrdinalIgnoreCase);

            // Create a mapping from source URLs to their existing keys for reuse in package source mappings
            var urlToExistingKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var addElement in existingAdds)
            {
                var key = (string?)addElement.Attribute("key");
                var value = (string?)addElement.Attribute("value");
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    urlToExistingKey[value] = key;
                }
            }

            var missingSources = requiredSources
                .Where(s => !existingValues.Contains(s) && !existingKeys.Contains(s))
                .ToArray();

            // Check if any sources are missing
            if (missingSources.Length > 0)
            {
                return true;
            }

            // Check if package source mappings need to be updated
            var packageSourceMapping = doc.Root?.Element("packageSourceMapping");
            if (packageSourceMapping is not null)
            {
                // Create a lookup of patterns to required sources from the mappings
                var patternToRequiredSource = mappings.ToDictionary(m => m.PackageFilter, m => m.Source, StringComparer.OrdinalIgnoreCase);

                // Check if any patterns are mapped to the wrong source
                var packageSourceElements = packageSourceMapping.Elements("packageSource");
                foreach (var packageSourceElement in packageSourceElements)
                {
                    var sourceKey = (string?)packageSourceElement.Attribute("key");
                    if (string.IsNullOrEmpty(sourceKey))
                    {
                        continue;
                    }

                    var packageElements = packageSourceElement.Elements("package");
                    foreach (var packageElement in packageElements)
                    {
                        var pattern = (string?)packageElement.Attribute("pattern");
                        if (string.IsNullOrEmpty(pattern))
                        {
                            continue;
                        }

                        // Check if this pattern should be mapped to a different source
                        if (patternToRequiredSource.TryGetValue(pattern, out var requiredSourceUrl))
                        {
                            // Use existing key if available, otherwise use the source URL as key
                            var expectedKey = urlToExistingKey.TryGetValue(requiredSourceUrl, out var existingKey) ? existingKey : requiredSourceUrl;
                            
                            if (!string.Equals(sourceKey, expectedKey, StringComparison.OrdinalIgnoreCase))
                            {
                                return true; // This pattern needs to be remapped
                            }
                        }
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

    private static void AddGlobalPackagesFolderConfiguration(XElement configuration)
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
            // Add globalPackagesFolder configuration
            var globalPackagesFolderAdd = new XElement("add");
            globalPackagesFolderAdd.SetAttributeValue("key", "globalPackagesFolder");
            globalPackagesFolderAdd.SetAttributeValue("value", ".nugetpackages");
            config.Add(globalPackagesFolderAdd);
        }
    }
}
