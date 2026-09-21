// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPROBES001
#pragma warning disable ASPIREPROJECTS001

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A resource that represents a specified .NET project.
/// </summary>
[DebuggerDisplay("{DebuggerToString(),nq}")]
public class ProjectResource : Resource, IResourceWithEnvironment, IResourceWithArgs, IResourceWithServiceDiscovery, IResourceWithWaitSupport, IResourceWithProbes,
    IComputeResource, IContainerFilesDestinationResource, IDotnetProgramResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProjectResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    public ProjectResource(string name) : base(name)
    {
        Annotations.Add(new ExecutableLaunchRecipeAnnotation(ProjectExecutableLaunchRecipe.Instance));

        // Every ProjectResource is launched through the .NET SDK, so it always carries the project-launch
        // defaults marker — even when constructed directly via AddResource rather than AddProject. Core
        // uses the annotation (not the type) to recognize .NET-launched resources so that resources from
        // language integration packages, such as DotnetProjectResource, get the same treatment.
        Annotations.Add(new ProjectLaunchDefaultsAnnotation());

        DotnetProgramPublishing.Configure(this);
    }

    private string DebuggerToString()
    {
        var path = "<unknown>";
        if (this.TryGetProjectMetadata(out var metadata))
        {
            path = metadata.ProjectPath;
        }

        return $@"Type = {GetType().Name}, Name = ""{Name}"", Path = ""{path}""";
    }
}
