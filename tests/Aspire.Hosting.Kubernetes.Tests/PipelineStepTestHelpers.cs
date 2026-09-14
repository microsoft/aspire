// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Kubernetes.Tests;

/// <summary>
/// Helpers for inspecting the pipeline steps a resource registers, without needing a cluster.
/// </summary>
internal static class PipelineStepTestHelpers
{
    /// <summary>
    /// Re-creates the pipeline steps contributed by <paramref name="resource"/>. Steps are built
    /// from the resource's <see cref="PipelineStepAnnotation"/>s, which is how the deploy pipeline
    /// discovers them, so tests can assert on which steps a given app model produces.
    /// </summary>
    public static async Task<List<PipelineStep>> CreateStepsAsync(IServiceProvider services, IResource resource)
    {
        var pipelineContext = new PipelineContext(
            services.GetRequiredService<DistributedApplicationModel>(),
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
            services,
            NullLogger.Instance,
            CancellationToken.None);

        var results = new List<PipelineStep>();
        foreach (var annotation in resource.Annotations.OfType<PipelineStepAnnotation>())
        {
            results.AddRange(await annotation.CreateStepsAsync(new PipelineStepFactoryContext
            {
                PipelineContext = pipelineContext,
                Resource = resource
            }));
        }

        return results;
    }

    /// <summary>
    /// Returns the sorted names of gateway- and TLS-related steps. Tests assert on this whole set
    /// rather than probing individual step names, so a newly added gateway/TLS step that skips the
    /// materialization-eligibility filter is caught rather than silently ignored.
    /// </summary>
    public static List<string> GatewayOrTlsStepNames(IEnumerable<PipelineStep> steps) =>
        [.. steps
            .Select(step => step.Name)
            .Where(name => name.Contains("tls", StringComparison.Ordinal) || name.Contains("gateway", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
}
