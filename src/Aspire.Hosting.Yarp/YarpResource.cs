#pragma warning disable ASPIREPIPELINES001
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Yarp;

/// <summary>
/// A resource that represents a YARP resource independent of the hosting model.
/// </summary>
public class YarpResource : ContainerResource, IResourceWithServiceDiscovery, IContainerFilesDestinationResource
{
    internal List<YarpRoute> Routes { get; } = [];

    internal List<YarpCluster> Clusters { get; } = [];

    /// <summary>
    /// Gets or sets the HTTPS host port for the YARP resource.
    /// </summary>
    internal int? HostHttpsPort { get; set; }

    /// <param name="name">The name of the resource.</param>
    public YarpResource(string name) : base(name)
    {
        Annotations.Add(new PipelineConfigurationAnnotation(context =>
        {
            // Ensure any static file references' images are built first
            if (this.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out var containerFilesAnnotations))
            {
                var buildSteps = context.GetSteps(this, WellKnownPipelineTags.BuildCompute);

                foreach (var containerFile in containerFilesAnnotations)
                {
                    buildSteps.DependsOn(context.GetSteps(containerFile.Source, WellKnownPipelineTags.BuildCompute));
                }
            }
        }));
    }
}
