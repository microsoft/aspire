// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Tests.TestServices;

internal sealed class TestImageComputeEnvironmentResource : Resource, IComputeEnvironmentResource
{
    internal TestImageComputeEnvironmentResource(string name) : base(name)
    {
        Annotations.Add(new PipelineStepAnnotation(_ => Task.FromResult<IEnumerable<PipelineStep>>(
        [
            new PipelineStep
            {
                Name = $"prepare-image-environment-{name}",
                Action = context =>
                {
                    if (context.ExecutionContext.IsPublishMode)
                    {
                        foreach (var target in Annotations.OfType<ContainerImageRegistryTargetAnnotation>().ToArray())
                        {
                            Annotations.Remove(target);
                        }
                        Annotations.Add(new ContainerImageRegistryTargetAnnotation(() => ImageRegistry));
                    }

                    return Task.CompletedTask;
                },
                RequiredBySteps = [WellKnownPipelineSteps.BeforeStart]
            }
        ])));
    }

    internal IContainerRegistry? ImageRegistry { get; set; }
}
