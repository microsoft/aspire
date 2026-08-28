// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml;
using System.Xml.Linq;

namespace Aspire.Cli.Packaging;

internal static class NuGetConfigComposer
{
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
        var section = configuration.Elements()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, incomingSection.Name.LocalName, StringComparison.OrdinalIgnoreCase));
        if (section is null)
        {
            section = new XElement(incomingSection.Name);
            configuration.Add(section);
        }

        foreach (var attribute in incomingSection.Attributes())
        {
            section.SetAttributeValue(attribute.Name, attribute.Value);
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
            ApplyEnvironmentTransforms(item);
            ResolveRelativePaths(incomingSection.Name.LocalName, item, originDirectory);

            var existingItem = section.Elements()
                .FirstOrDefault(candidate => AreEquivalentItems(incomingSection.Name.LocalName, candidate, item));
            if (existingItem is null)
            {
                section.Add(item);
            }
            else if (IsUnknownItem(incomingSection.Name.LocalName, item))
            {
                MergeUnknownItem(existingItem, item);
            }
            else
            {
                existingItem.ReplaceWith(item);
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
            return string.Equals(GetAttributeValue(first, "key"), GetAttributeValue(second, "key"), StringComparison.Ordinal);
        }

        if (string.Equals(sectionName, "packageSourceCredentials", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(itemName, second.Name.LocalName, StringComparison.Ordinal);
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

        var identityAttribute =
            string.Equals(itemName, "author", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(itemName, "repository", StringComparison.OrdinalIgnoreCase)
                ? "name"
                : string.Equals(itemName, "certificate", StringComparison.OrdinalIgnoreCase)
                    ? "fingerprint"
                    : string.Equals(itemName, "fileCert", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(itemName, "storeCert", StringComparison.OrdinalIgnoreCase)
                        ? "packageSource"
                        : null;

        return identityAttribute is null ||
            string.Equals(GetAttributeValue(first, identityAttribute), GetAttributeValue(second, identityAttribute), StringComparison.OrdinalIgnoreCase);
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
        if (Uri.TryCreate(expandedValue, UriKind.Relative, out _))
        {
            attribute.Value = Path.GetFullPath(Path.Combine(originDirectory, expandedValue));
        }
    }

    private static string? GetAttributeValue(XElement element, string name)
        => element.Attributes()
            .FirstOrDefault(attribute => string.Equals(attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
