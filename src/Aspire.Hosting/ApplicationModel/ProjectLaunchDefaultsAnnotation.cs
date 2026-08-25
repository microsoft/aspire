// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource as launched through the .NET SDK, so that Aspire treats it like a C# project.
/// </summary>
/// <remarks>
/// This is a public annotation rather than an interface so resources in other assemblies can opt into the C# project-defaults behavior
/// without implementing a specific interface.
/// </remarks>
[Experimental("ASPIREPROJECTS001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ProjectLaunchDefaultsAnnotation : IResourceAnnotation
{
    private IProjectMetadata? _appliedProjectMetadata;

    /// <summary>
    /// The config host for each endpoint that originated from Kestrel configuration. Used when
    /// rebuilding the <c>Kestrel__Endpoints__*__Url</c> override environment variables.
    /// </summary>
    internal Dictionary<EndpointAnnotation, string> KestrelEndpointAnnotationHosts { get; } = [];

    /// <summary>
    /// The https endpoint that was added as a default. It is excluded from the port and Kestrel
    /// override environment because the target (e.g. a container) likely won't listen on https.
    /// </summary>
    public EndpointAnnotation? DefaultHttpsEndpoint { get; internal set; }

    /// <summary>
    /// Whether any endpoints originated from Kestrel configuration.
    /// </summary>
    internal bool HasKestrelEndpoints => KestrelEndpointAnnotationHosts.Count > 0;

    /// <summary>
    /// Records the project metadata used to materialize project defaults.
    /// </summary>
    internal bool TrySetAppliedProjectMetadata(IProjectMetadata projectMetadata)
    {
        if (_appliedProjectMetadata is not null)
        {
            return false;
        }

        _appliedProjectMetadata = projectMetadata;
        return true;
    }

    /// <summary>
    /// Validates that project metadata has not changed since project defaults were materialized.
    /// </summary>
    internal void ValidateProjectMetadata(IResource resource, IProjectMetadata projectMetadata)
    {
        if (_appliedProjectMetadata is not null && !ReferenceEquals(projectMetadata, _appliedProjectMetadata))
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' project metadata was replaced after project defaults were applied. " +
                $"Project metadata must remain unchanged after {nameof(ProjectResourceBuilderExtensions.WithProjectDefaults)} configures the resource.");
        }
    }
}

internal static class ProjectLaunchDefaultsExtensions
{
    /// <summary>
    /// Determines whether endpoint environment variables should be injected for the given endpoint.
    /// Only http/https endpoints without an explicit target-port environment variable are eligible,
    /// and any <see cref="EndpointEnvironmentInjectionFilterAnnotation"/> may further exclude them.
    /// </summary>
    [AspireExportIgnore(Reason = "Endpoint environment injection filtering is internal .NET launch wiring and is not part of the ATS surface.")]
    public static bool ShouldInjectEndpointEnvironment(this IResource resource, EndpointReference e)
    {
        var endpoint = e.EndpointAnnotation;

        if (endpoint.UriScheme is not ("http" or "https") ||    // Only process http and https endpoints
            endpoint.TargetPortEnvironmentVariable is not null) // Skip if target port env variable was set
        {
            return false;
        }

        // If any filter rejects the endpoint, skip it
        return !resource.Annotations.OfType<EndpointEnvironmentInjectionFilterAnnotation>()
            .Select(a => a.Filter)
            .Any(f => !f(endpoint));
    }
}
