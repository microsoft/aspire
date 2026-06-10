// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Configuration;

namespace Aspire.Cli.Projects;

internal static class CSharpIntegrationProjectReferences
{
    public static ResolvedReferences Resolve(IEnumerable<IntegrationReference> integrationReferences, string? repoRoot)
    {
        var projectReferences = new List<XElement>();
        var packageReferences = new List<XElement>();
        var addedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var integrationReference in integrationReferences)
        {
            if (integrationReference.IsProjectReference)
            {
                if (addedProjects.Add(integrationReference.Name))
                {
                    projectReferences.Add(CreateProjectReferenceElement(integrationReference.ProjectPath!));
                }

                continue;
            }

            if (TryGetRepositoryProjectReference(repoRoot, integrationReference.Name, out var projectPath))
            {
                if (addedProjects.Add(integrationReference.Name))
                {
                    projectReferences.Add(CreateProjectReferenceElement(projectPath));
                }

                continue;
            }

            if (integrationReference.Version is null)
            {
                throw new InvalidOperationException($"Integration '{integrationReference.Name}' is neither a project reference nor a package reference (both Version and ProjectPath are null).");
            }

            packageReferences.Add(new XElement("PackageReference",
                new XAttribute("Include", integrationReference.Name),
                new XAttribute("Version", integrationReference.Version)));
        }

        return new ResolvedReferences(projectReferences, packageReferences);
    }

    private static bool TryGetRepositoryProjectReference(string? repoRoot, string packageName, out string projectPath)
    {
        projectPath = null!;
        if (repoRoot is null || !packageName.StartsWith("Aspire.Hosting", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidatePath = Path.Combine(repoRoot, "src", packageName, $"{packageName}.csproj");
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        projectPath = candidatePath;
        return true;
    }

    private static XElement CreateProjectReferenceElement(string projectPath)
    {
        return new XElement("ProjectReference",
            new XAttribute("Include", projectPath),
            new XElement("IsAspireProjectResource", "false"),
            new XElement("ReferenceOutputAssembly", "true"),
            new XElement("Private", "false"));
    }

    internal sealed record ResolvedReferences(
        IReadOnlyList<XElement> ProjectReferences,
        IReadOnlyList<XElement> PackageReferences);
}
