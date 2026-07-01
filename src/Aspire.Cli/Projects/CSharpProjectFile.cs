// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Xml.Linq;
using Aspire.Cli.Configuration;

namespace Aspire.Cli.Projects;

/// <summary>
/// Mutable model used to generate small SDK-style C# project files for Aspire CLI infrastructure.
/// </summary>
/// <param name="sdk">The SDK attribute value to write on the generated project root.</param>
internal sealed class CSharpProjectFile(string sdk = "Microsoft.NET.Sdk")
{
    /// <summary>
    /// Gets the SDK attribute value written on the generated project root.
    /// </summary>
    public string Sdk { get; } = sdk;

    /// <summary>
    /// Gets the generated project properties.
    /// </summary>
    public List<CSharpProjectProperty> Properties { get; } = [];

    /// <summary>
    /// Gets the generated package references.
    /// </summary>
    public List<CSharpPackageReference> PackageReferences { get; } = [];

    /// <summary>
    /// Gets the generated project references.
    /// </summary>
    public List<CSharpProjectReference> ProjectReferences { get; } = [];

    /// <summary>
    /// Gets the generated project imports.
    /// </summary>
    public List<CSharpProjectImport> Imports { get; } = [];

    /// <summary>
    /// Gets the generated none items.
    /// </summary>
    public List<CSharpNoneItem> NoneItems { get; } = [];

    /// <summary>
    /// Gets custom target elements to append to the generated project.
    /// </summary>
    public List<XElement> Targets { get; } = [];

    /// <summary>
    /// Adds a property to the generated project.
    /// </summary>
    public void AddProperty(string name, string value)
    {
        Properties.Add(new CSharpProjectProperty(name, value));
    }

    /// <summary>
    /// Adds integration references as package references or project references.
    /// </summary>
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

    /// <summary>
    /// Adds a reference to a repository-local project when it exists.
    /// </summary>
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

    /// <summary>
    /// Finds a repository-local project under the Aspire source tree.
    /// </summary>
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

    /// <summary>
    /// Converts the project model into an XML document.
    /// </summary>
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

    /// <summary>
    /// Creates a PackageReference XML element.
    /// </summary>
    internal static XElement CreatePackageReferenceElement(CSharpPackageReference packageReference)
    {
        var element = new XElement("PackageReference", new XAttribute("Include", packageReference.Name));

        if (packageReference.Version is not null)
        {
            element.Add(new XAttribute("Version", packageReference.Version));
        }

        return element;
    }

    /// <summary>
    /// Creates a ProjectReference XML element.
    /// </summary>
    internal static XElement CreateProjectReferenceElement(CSharpProjectReference projectReference)
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

    /// <summary>
    /// Adds a boolean child element when the value is specified.
    /// </summary>
    internal static void AddBooleanElement(XElement element, string name, bool? value)
    {
        if (value is not null)
        {
            element.Add(new XElement(name, value.Value ? "true" : "false"));
        }
    }
}

/// <summary>
/// Represents a generated project property.
/// </summary>
internal sealed record CSharpProjectProperty(string Name, string Value);

/// <summary>
/// Represents a generated package reference.
/// </summary>
internal sealed record CSharpPackageReference(string Name, string? Version = null);

/// <summary>
/// Represents a generated project reference.
/// </summary>
internal sealed record CSharpProjectReference(
    string ProjectPath,
    bool? IsAspireProjectResource = null,
    bool? ReferenceOutputAssembly = null,
    bool? Private = null);

/// <summary>
/// Represents a generated project import.
/// </summary>
internal sealed record CSharpProjectImport(string ProjectPath);

/// <summary>
/// Represents a generated None item.
/// </summary>
internal sealed record CSharpNoneItem(string Include, string? CopyToOutputDirectory = null);
