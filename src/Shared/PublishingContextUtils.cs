// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Utils;

internal static class PublishingContextUtils
{
    public static string GetEnvironmentOutputPath(PipelineStepContext context, IComputeEnvironmentResource environment)
    {
        var outputService = context.Services.GetRequiredService<IPipelineOutputService>();
        if (environment.HasAnnotationOfType<PipelineOutputDirectoryAnnotation>() ||
            context.Model.Resources.OfType<IComputeEnvironmentResource>().Count() > 1)
        {
            // Explicit overrides and multiple compute environments select resource resolution.
            return outputService.GetOutputDirectory(environment);
        }

        // Preserve the global layout for zero or one compute environment.
        return outputService.GetOutputDirectory();
    }
}
