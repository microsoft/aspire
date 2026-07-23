// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
#pragma warning disable ASPIREPIPELINES004 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;

namespace Aspire.Hosting.Utils;

internal static class PublishingContextUtils
{
    public static string GetEnvironmentOutputPath(PipelineStepContext context, IComputeEnvironmentResource environment)
    {
        var primaryOutputPath = context.Outputs.PrimaryOutput.OutputPath;
        if (context.Model.Resources.OfType<IComputeEnvironmentResource>().Count() > 1)
        {
            // If there are multiple compute environments, use resource-specific output path
            return Path.Combine(primaryOutputPath, environment.Name);
        }

        // If there is only one compute environment, use the root output path
        return primaryOutputPath;
    }
}
