// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Agents;

/// <summary>
/// Loads embedded text files that make up a skill bundle.
/// </summary>
internal static class EmbeddedSkillResourceLoader
{
    public static Task<IReadOnlyList<SkillAssetFile>> LoadTextFilesAsync(string resourceRoot, CancellationToken cancellationToken)
    {
        return LoadTextFilesAsync(resourceRoot, static _ => true, cancellationToken);
    }

    public static async Task<IReadOnlyList<SkillAssetFile>> LoadTextFilesAsync(string resourceRoot, Func<string, bool> includeFile, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceRoot);
        ArgumentNullException.ThrowIfNull(includeFile);

        var assembly = typeof(EmbeddedSkillResourceLoader).Assembly;
        var resourcePrefix = resourceRoot.EndsWith('/') ? resourceRoot : $"{resourceRoot}/";
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        if (resourceNames.Length == 0)
        {
            throw new InvalidOperationException($"No embedded skill resources found for '{resourceRoot}'.");
        }

        List<SkillAssetFile> files = [];

        foreach (var resourceName in resourceNames)
        {
            var relativePath = resourceName[resourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            if (!includeFile(relativePath))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded skill resource not found: {resourceName}");
            using var reader = new StreamReader(stream);

            files.Add(new SkillAssetFile(relativePath, await reader.ReadToEndAsync(cancellationToken)));
        }

        return files;
    }
}

internal sealed record SkillAssetFile(string RelativePath, string Content);
