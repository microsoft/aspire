// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Configuration;

namespace Aspire.Cli.Projects;

internal sealed class CSharpProjectFile(string sdk = "Microsoft.NET.Sdk")
{
    public string Sdk { get; } = sdk;

    public List<CSharpProjectProperty> Properties { get; } = [];

    public List<CSharpPackageReference> PackageReferences { get; } = [];

    public List<CSharpProjectReference> ProjectReferences { get; } = [];

    public List<CSharpProjectImport> Imports { get; } = [];

    public List<CSharpNoneItem> NoneItems { get; } = [];

    public List<XElement> Targets { get; } = [];

    public void AddProperty(string name, string value)
    {
        Properties.Add(new CSharpProjectProperty(name, value));
    }

    public void AddImportIfExists(string projectPath)
    {
        if (File.Exists(projectPath))
        {
            Imports.Add(new CSharpProjectImport(projectPath));
        }
    }

    public void AddIntegrationReferences(
        IEnumerable<IntegrationReference> integrationReferences,
        string? repoRoot,
        bool? isAspireProjectResource = null,
        bool? referenceOutputAssembly = null,
        bool? privateReference = null,
        ISet<string>? addedProjectPaths = null)
    {
        var addedIntegrations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var integrationReference in integrationReferences)
        {
            if (integrationReference.IsProjectReference)
            {
                if (addedIntegrations.Add(integrationReference.Name))
                {
                    AddProjectReference(
                        integrationReference.ProjectPath!,
                        isAspireProjectResource,
                        referenceOutputAssembly,
                        privateReference,
                        addedProjectPaths);
                }

                continue;
            }

            if (integrationReference.Name.StartsWith("Aspire.Hosting", StringComparison.OrdinalIgnoreCase) &&
                TryGetRepositoryProject(repoRoot, integrationReference.Name, out var projectPath))
            {
                if (addedIntegrations.Add(integrationReference.Name))
                {
                    AddProjectReference(
                        projectPath,
                        isAspireProjectResource,
                        referenceOutputAssembly,
                        privateReference,
                        addedProjectPaths);
                }

                continue;
            }

            if (integrationReference.Version is null)
            {
                throw new InvalidOperationException($"Integration '{integrationReference.Name}' is neither a project reference nor a package reference (both Version and ProjectPath are null).");
            }

            PackageReferences.Add(new CSharpPackageReference(integrationReference.Name, integrationReference.Version));
        }
    }

    public bool AddRepositoryProjectReferenceIfExists(
        string? repoRoot,
        string projectName,
        bool? isAspireProjectResource = null,
        bool? referenceOutputAssembly = null,
        bool? privateReference = null,
        ISet<string>? addedProjectPaths = null)
    {
        if (!TryGetRepositoryProject(repoRoot, projectName, out var projectPath) ||
            (addedProjectPaths is not null && !addedProjectPaths.Add(projectPath)))
        {
            return false;
        }

        AddProjectReference(
            projectPath,
            isAspireProjectResource,
            referenceOutputAssembly,
            privateReference);

        return true;
    }

    public static bool TryGetRepositoryProject(string? repoRoot, string projectName, out string projectPath)
    {
        projectPath = null!;
        if (repoRoot is null)
        {
            return false;
        }

        var candidatePath = Path.Combine(repoRoot, "src", projectName, $"{projectName}.csproj");
        if (!File.Exists(candidatePath))
        {
            return false;
        }

        projectPath = candidatePath;
        return true;
    }

    private void AddProjectReference(
        string projectPath,
        bool? isAspireProjectResource,
        bool? referenceOutputAssembly,
        bool? privateReference,
        ISet<string>? addedProjectPaths = null)
    {
        if (addedProjectPaths is not null && !addedProjectPaths.Add(projectPath))
        {
            return;
        }

        ProjectReferences.Add(new CSharpProjectReference(
            projectPath,
            isAspireProjectResource,
            referenceOutputAssembly,
            privateReference));
    }

    public XDocument ToXDocument()
    {
        var root = new XElement("Project", new XAttribute("Sdk", Sdk));

        if (Properties.Count > 0)
        {
            root.Add(new XElement("PropertyGroup",
                Properties.Select(property => new XElement(property.Name, property.Value))));
        }

        if (PackageReferences.Count > 0)
        {
            root.Add(new XElement("ItemGroup",
                PackageReferences.Select(CreatePackageReferenceElement)));
        }

        if (ProjectReferences.Count > 0)
        {
            root.Add(new XElement("ItemGroup",
                ProjectReferences.Select(CreateProjectReferenceElement)));
        }

        if (NoneItems.Count > 0)
        {
            root.Add(new XElement("ItemGroup",
                NoneItems.Select(CreateNoneElement)));
        }

        root.Add(Imports.Select(import => new XElement("Import", new XAttribute("Project", import.ProjectPath))));
        root.Add(Targets);

        return new XDocument(root);
    }

    private static XElement CreatePackageReferenceElement(CSharpPackageReference packageReference)
    {
        var element = new XElement("PackageReference", new XAttribute("Include", packageReference.Name));

        if (packageReference.Version is not null)
        {
            element.Add(new XAttribute("Version", packageReference.Version));
        }

        return element;
    }

    private static XElement CreateProjectReferenceElement(CSharpProjectReference projectReference)
    {
        var element = new XElement("ProjectReference", new XAttribute("Include", projectReference.ProjectPath));

        AddBooleanElement(element, "IsAspireProjectResource", projectReference.IsAspireProjectResource);
        AddBooleanElement(element, "ReferenceOutputAssembly", projectReference.ReferenceOutputAssembly);
        AddBooleanElement(element, "Private", projectReference.Private);

        return element;
    }

    private static XElement CreateNoneElement(CSharpNoneItem noneItem)
    {
        var element = new XElement("None", new XAttribute("Include", noneItem.Include));

        if (noneItem.CopyToOutputDirectory is not null)
        {
            element.Add(new XAttribute("CopyToOutputDirectory", noneItem.CopyToOutputDirectory));
        }

        return element;
    }

    private static void AddBooleanElement(XElement element, string name, bool? value)
    {
        if (value is not null)
        {
            element.Add(new XElement(name, value.Value ? "true" : "false"));
        }
    }
}

internal sealed record CSharpProjectProperty(string Name, string Value);

internal sealed record CSharpPackageReference(string Name, string? Version = null);

internal sealed record CSharpProjectReference(
    string ProjectPath,
    bool? IsAspireProjectResource = null,
    bool? ReferenceOutputAssembly = null,
    bool? Private = null);

internal sealed record CSharpProjectImport(string ProjectPath);

internal sealed record CSharpNoneItem(string Include, string? CopyToOutputDirectory = null);
