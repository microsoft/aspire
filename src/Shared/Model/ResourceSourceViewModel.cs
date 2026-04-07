// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;

namespace Aspire.Shared.Model;

internal record ResourceSource(string Value, string OriginalValue)
{
    public static ResourceSource? GetSourceModel(string? resourceType, IReadOnlyDictionary<string, string?> properties)
    {
        // NOTE project and tools are also executables, so check for those first
        if (string.Equals(resourceType, KnownResourceTypes.Project, StringComparisons.ResourceType) &&
            properties.TryGetValue(KnownProperties.Project.Path, out var projectPath) &&
            !string.IsNullOrEmpty(projectPath))
        {
            return new ResourceSource(Path.GetFileName(projectPath), projectPath);
        }

        if (string.Equals(resourceType, KnownResourceTypes.Tool, StringComparisons.ResourceType) &&
            properties.TryGetValue(KnownProperties.Tool.Package, out var toolPackage) &&
            !string.IsNullOrEmpty(toolPackage))
        {
            return new ResourceSource(toolPackage, toolPackage);
        }

        if (properties.TryGetValue(KnownProperties.Executable.Path, out var executablePath) &&
            !string.IsNullOrEmpty(executablePath))
        {
            return new ResourceSource(Path.GetFileName(executablePath), executablePath);
        }

        if (properties.TryGetValue(KnownProperties.Container.Image, out var containerImage) &&
            !string.IsNullOrEmpty(containerImage))
        {
            return new ResourceSource(containerImage, containerImage);
        }

        if (properties.TryGetValue(KnownProperties.Resource.Source, out var source) &&
            !string.IsNullOrEmpty(source))
        {
            return new ResourceSource(source, source);
        }

        return null;
    }
}
