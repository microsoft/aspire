// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A hidden executable resource that runs 'dotnet build' for a project resource.
/// </summary>
/// <remarks>
/// This uses a DCP executable resource rather than <see cref="System.Diagnostics.Process"/> directly
/// so that the build process benefits from DCP's lifecycle management (automatic cleanup on shutdown),
/// structured log capture, and visibility in the dashboard's diagnostics.
/// </remarks>
internal sealed class ProjectRebuilderResource : ExecutableResource, IResourceWithParent<ProjectResource>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectRebuilderResource"/> class.
    /// </summary>
    /// <param name="name">The name of the rebuilder resource.</param>
    /// <param name="parent">The project resource this rebuilder is associated with.</param>
    /// <param name="projectPath">The path to the project file.</param>
    public ProjectRebuilderResource(string name, ProjectResource parent, string projectPath)
        : base(name, "dotnet", Path.GetDirectoryName(projectPath)!)
    {
        Parent = parent;
        ProjectPath = projectPath;
    }

    /// <summary>
    /// Gets the parent project resource.
    /// </summary>
    public ProjectResource Parent { get; }

    /// <summary>
    /// Gets the path to the project file.
    /// </summary>
    public string ProjectPath { get; }
}
