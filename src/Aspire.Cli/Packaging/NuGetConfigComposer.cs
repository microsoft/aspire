// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using System.Xml.Linq;

namespace Aspire.Cli.Packaging;

internal static class NuGetConfigComposer
{
    private static readonly Dictionary<string, string> s_mergerSectionNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["config"] = "config",
        ["packageSourceMapping"] = "packageSourceMapping",
        ["packageSources"] = "packageSources"
    };

    private static readonly HashSet<string> s_knownItemNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "add",
        "author",
        "certificate",
        "owners",
        "package",
        "repository",
        "fileCert",
        "storeCert"
    };

    /// <summary>
    /// Composes NuGet configuration files ordered from highest to lowest precedence.
    /// </summary>
    public static async Task<XDocument> ComposeAsync(
        IReadOnlyList<string> configPaths,
        CancellationToken cancellationToken)
    {
        var result = new XDocument(new XElement("configuration"));

        foreach (var configPath in configPaths.Reverse())
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
            {
                continue;
            }

            var fullConfigPath = Path.GetFullPath(configPath);
            var document = await LoadAsync(fullConfigPath, cancellationToken).ConfigureAwait(false);
            if (document.Root is not { } configuration ||
                !string.Equals(configuration.Name.LocalName, "configuration", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"NuGet configuration '{fullConfigPath}' does not have a <configuration> root element.");
            }

            foreach (var section in configuration.Elements())
            {
                MergeSection(result.Root!, section, Path.GetDirectoryName(fullConfigPath)!);
            }
        }

        return result;
    }

    private static async Task<XDocument> LoadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });

        return await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken).ConfigureAwait(false);
    }

    private static void MergeSection(XElement configuration, XElement incomingSection, string originDirectory)
    {
        // NuGet applies configuration files from lowest to highest precedence. A <clear /> removes
        // inherited items and keyed items replace the inherited item with the same key.
        // https://learn.microsoft.com/nuget/consume-packages/configuring-nuget-behavior#how-settings-are-applied
        var shouldCanonicalizeSectionName = s_mergerSectionNames.TryGetValue(incomingSection.Name.LocalName, out var canonicalSectionName);
        var sectionName = canonicalSectionName ?? incomingSection.Name.LocalName;
        var section = configuration.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, sectionName, StringComparison.OrdinalIgnoreCase));
        if (section is null)
        {
            section = new XElement(incomingSection.Name.Namespace + sectionName);
            configuration.Add(section);
        }
        else if (shouldCanonicalizeSectionName)
        {
            section.Name = section.Name.Namespace + sectionName;
        }

        foreach (var attribute in incomingSection.Attributes())
        {
            section.SetAttributeValue(attribute.Name, attribute.Value);
        }

        if (string.Equals(sectionName, "fallbackPackageFolders", StringComparison.OrdinalIgnoreCase))
        {
            MergeFallbackPackageFolders(section, incomingSection, originDirectory);
            return;
        }

        if (string.Equals(incomingSection.Name.LocalName, "minPublishAgeExceptions", StringComparison.OrdinalIgnoreCase) &&
            !incomingSection.Elements().Any(element => string.Equals(element.Name.LocalName, "clear", StringComparison.OrdinalIgnoreCase)))
        {
            section.RemoveNodes();
        }

        foreach (var incomingItem in incomingSection.Elements())
        {
            if (string.Equals(incomingItem.Name.LocalName, "clear", StringComparison.OrdinalIgnoreCase))
            {
                section.RemoveNodes();
                section.Add(new XElement(incomingItem.Name));
                continue;
            }

            var item = new XElement(incomingItem);
            CanonicalizeMergerItemNames(sectionName, item);
            ApplyEnvironmentTransforms(item);
            ResolveRelativePaths(sectionName, item, originDirectory);

            var existingItem = section.Elements()
                .FirstOrDefault(candidate => AreEquivalentItems(sectionName, candidate, item));
            if (existingItem is null)
            {
                section.Add(item);
            }
            else if (IsUnknownItem(sectionName, item))
            {
                MergeUnknownItem(existingItem, item);
            }
            else
            {
                existingItem.ReplaceWith(item);
            }
        }
    }

    private static void MergeFallbackPackageFolders(
        XElement section,
        XElement incomingSection,
        string originDirectory)
    {
        // NuGet searches fallback folders from the highest-precedence config to the lowest. Once the
        // hierarchy is flattened every item has the same origin, so physical order must encode those
        // precedence groups. Accumulate this config's items and insert them ahead of inherited items.
        // https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Configuration/Utility/SettingsUtility.cs
        var higherPrecedenceItems = new List<XElement>();
        foreach (var incomingItem in incomingSection.Elements())
        {
            if (string.Equals(incomingItem.Name.LocalName, "clear", StringComparison.OrdinalIgnoreCase))
            {
                section.RemoveNodes();
                section.Add(new XElement(incomingItem.Name));
                higherPrecedenceItems.Clear();
                continue;
            }

            var item = new XElement(incomingItem);
            ApplyEnvironmentTransforms(item);
            ResolveRelativePaths(incomingSection.Name.LocalName, item, originDirectory);

            section.Elements()
                .FirstOrDefault(candidate => AreEquivalentItems(incomingSection.Name.LocalName, candidate, item))
                ?.Remove();

            var duplicate = higherPrecedenceItems
                .FirstOrDefault(candidate => AreEquivalentItems(incomingSection.Name.LocalName, candidate, item));
            if (duplicate is not null)
            {
                higherPrecedenceItems.Remove(duplicate);
            }

            higherPrecedenceItems.Add(item);
        }

        if (higherPrecedenceItems.Count == 0)
        {
            return;
        }

        var firstInheritedItem = section.Elements()
            .FirstOrDefault(element => !string.Equals(element.Name.LocalName, "clear", StringComparison.OrdinalIgnoreCase));
        if (firstInheritedItem is null)
        {
            section.Add(higherPrecedenceItems);
        }
        else
        {
            firstInheritedItem.AddBeforeSelf(higherPrecedenceItems);
        }
    }

    private static void CanonicalizeMergerItemNames(string sectionName, XElement item)
    {
        if (string.Equals(sectionName, "config", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sectionName, "packageSources", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(item.Name.LocalName, "add", StringComparison.OrdinalIgnoreCase))
            {
                item.Name = item.Name.Namespace + "add";
            }

            return;
        }

        if (!string.Equals(sectionName, "packageSourceMapping", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(item.Name.LocalName, "packageSource", StringComparison.OrdinalIgnoreCase))
        {
            item.Name = item.Name.Namespace + "packageSource";
            foreach (var package in item.Elements().Where(element =>
                string.Equals(element.Name.LocalName, "package", StringComparison.OrdinalIgnoreCase)))
            {
                package.Name = package.Name.Namespace + "package";
            }
        }
    }

    private static bool AreEquivalentItems(string sectionName, XElement first, XElement second)
    {
        if (!string.Equals(first.Name.LocalName, second.Name.LocalName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var itemName = first.Name.LocalName;
        if (string.Equals(itemName, "add", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(GetAttributeValue(first, "key"), GetAttributeValue(second, "key"), StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(sectionName, "packageSourceCredentials", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(itemName, second.Name.LocalName, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(sectionName, "packageSourceMapping", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(itemName, "packageSource", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(GetAttributeValue(first, "key"), GetAttributeValue(second, "key"), StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(sectionName, "minPublishAgeExceptions", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(itemName, "package", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(GetAttributeValue(first, "pattern"), GetAttributeValue(second, "pattern"), StringComparison.OrdinalIgnoreCase);
        }

        // Keep item identities aligned with NuGet.Configuration's SettingItem.Equals implementations.
        // In particular, repository identity is its service index rather than its display name.
        // https://github.com/NuGet/NuGet.Client/tree/dev/src/NuGet.Core/NuGet.Configuration/Settings/Items
        var identityAttribute = itemName switch
        {
            _ when string.Equals(itemName, "repository", StringComparison.OrdinalIgnoreCase) => "serviceIndex",
            _ when string.Equals(itemName, "author", StringComparison.OrdinalIgnoreCase) => "name",
            _ when string.Equals(itemName, "certificate", StringComparison.OrdinalIgnoreCase) => "fingerprint",
            _ when string.Equals(itemName, "fileCert", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(itemName, "storeCert", StringComparison.OrdinalIgnoreCase) => "packageSource",
            _ => null
        };

        return identityAttribute is null ||
            string.Equals(GetAttributeValue(first, identityAttribute), GetAttributeValue(second, identityAttribute), StringComparison.Ordinal);
    }

    private static bool IsUnknownItem(string sectionName, XElement item)
    {
        var itemName = item.Name.LocalName;
        if (string.Equals(sectionName, "packageSourceCredentials", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sectionName, "packageSources", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sectionName, "auditSources", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sectionName, "packageSourceMapping", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !s_knownItemNames.Contains(itemName);
    }

    private static void MergeUnknownItem(XElement existingItem, XElement incomingItem)
    {
        foreach (var attribute in incomingItem.Attributes())
        {
            existingItem.SetAttributeValue(attribute.Name, attribute.Value);
        }

        foreach (var incomingChild in incomingItem.Elements())
        {
            var existingChild = existingItem.Elements()
                .FirstOrDefault(candidate => AreEquivalentItems(existingItem.Name.LocalName, candidate, incomingChild));
            if (existingChild is null)
            {
                existingItem.Add(new XElement(incomingChild));
            }
            else
            {
                existingChild.ReplaceWith(new XElement(incomingChild));
            }
        }
    }

    private static void ResolveRelativePaths(string sectionName, XElement item, string originDirectory)
    {
        if (string.Equals(item.Name.LocalName, "add", StringComparison.OrdinalIgnoreCase))
        {
            var key = GetAttributeValue(item, "key");
            var isPathSetting =
                string.Equals(sectionName, "packageSources", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sectionName, "auditSources", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sectionName, "fallbackPackageFolders", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(sectionName, "config", StringComparison.OrdinalIgnoreCase) &&
                    (string.Equals(key, "globalPackagesFolder", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(key, "repositoryPath", StringComparison.OrdinalIgnoreCase));
            if (isPathSetting)
            {
                ResolveRelativePathAttribute(item, "value", originDirectory);
            }
        }

        else if (string.Equals(sectionName, "clientCertificates", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Name.LocalName, "fileCert", StringComparison.OrdinalIgnoreCase))
        {
            ResolveRelativePathAttribute(item, "path", originDirectory);
        }
    }

    private static void ApplyEnvironmentTransforms(XElement item)
    {
        foreach (var attribute in item.DescendantsAndSelf().Attributes())
        {
            attribute.Value = Environment.ExpandEnvironmentVariables(attribute.Value);
        }
    }

    private static void ResolveRelativePathAttribute(XElement element, string attributeName, string originDirectory)
    {
        var attribute = element.Attributes()
            .FirstOrDefault(candidate => string.Equals(candidate.Name.LocalName, attributeName, StringComparison.OrdinalIgnoreCase));
        if (attribute is null)
        {
            return;
        }

        var expandedValue = Environment.ExpandEnvironmentVariables(attribute.Value);
        attribute.Value = ResolvePathFromOrigin(originDirectory, expandedValue);
    }

    internal static string ResolvePathFromOrigin(string originDirectory, string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        if (!Uri.TryCreate(path, UriKind.Relative, out _))
        {
            return path;
        }

        // Windows recognizes three rooted forms:
        //   C:\packages, \\server\packages, and \packages.
        // The last form is rooted without naming a drive, so NuGet resolves it against the drive
        // containing NuGet.Config rather than the process's current drive.
        // https://github.com/NuGet/NuGet.Client/blob/dev/src/NuGet.Core/NuGet.Configuration/Settings/Settings.cs
        var root = Path.GetPathRoot(path);
        var resolvedPath = root is { Length: 1 } &&
            (root[0] == Path.DirectorySeparatorChar || path[0] == Path.AltDirectorySeparatorChar)
                ? Path.Combine(Path.GetPathRoot(originDirectory)!, path[1..])
                : Path.Combine(originDirectory, path);

        return Path.GetFullPath(resolvedPath);
    }

    private static string? GetAttributeValue(XElement element, string name)
        => element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
