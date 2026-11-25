// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting;

internal static class PublishValidationExtensions
{
    internal static PipelineStep AddPublishValidation(
        this IResourceBuilder<ContainerResource> builder,
        string stepName,
        string description,
        string[] requiredBySteps,
        Action<PipelineStepContext> validate)
    {
        var step = new PipelineStep
        {
            Name = stepName,
            Description = description,
            RequiredBySteps = [.. requiredBySteps],
            Resource = builder.Resource,
            Action = context =>
            {
                // Exclusion can be configured after registration. An unpublished resource must not fail validation.
                if (!builder.Resource.IsExcludedFromPublish())
                {
                    validate(context);
                }

                return Task.CompletedTask;
            }
        };

        // WithDockerfile replaces container step annotations, so keep validation in the application pipeline.
        builder.ApplicationBuilder.Pipeline.AddStep(step);
        builder.WithPipelineConfiguration(context =>
        {
            // Azure deploy graphs select BuildCompute directly, without the aggregate Build or Publish steps.
            context.GetSteps(builder.Resource, WellKnownPipelineTags.BuildCompute).DependsOn(stepName);
        });

        return step;
    }
}
