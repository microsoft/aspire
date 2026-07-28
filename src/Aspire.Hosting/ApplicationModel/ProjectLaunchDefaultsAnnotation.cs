// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Marks a resource as launched through the .NET SDK with the project defaults applied by
/// <see cref="ProjectResourceBuilderExtensions.WithProjectDefaults{T}"/>, and carries the per-endpoint
/// state that wiring needs.
/// </summary>
/// <remarks>
/// This is an annotation rather than an interface so resources in other assemblies (for example
/// <c>DotnetProjectResource</c> in <c>Aspire.Hosting.Dotnet</c>) can opt into the project-defaults
/// behavior without implementing a type-level contract that would have to be public and versioned in
/// lockstep. Presence of the annotation is also how core recognizes ".NET-launched" resources for the
/// Restart description and the Rebuild command.
/// </remarks>
internal sealed class ProjectLaunchDefaultsAnnotation : IResourceAnnotation
{
    /// <summary>
    /// The config host for each endpoint that originated from Kestrel configuration. Used when
    /// rebuilding the <c>Kestrel__Endpoints__*__Url</c> override environment variables.
    /// </summary>
    public Dictionary<EndpointAnnotation, string> KestrelEndpointAnnotationHosts { get; } = [];

    /// <summary>
    /// The https endpoint that was added as a default. It is excluded from the port and Kestrel
    /// override environment because the target (e.g. a container) likely won't listen on https.
    /// </summary>
    public EndpointAnnotation? DefaultHttpsEndpoint { get; set; }

    /// <summary>
    /// Whether any endpoints originated from Kestrel configuration.
    /// </summary>
    public bool HasKestrelEndpoints => KestrelEndpointAnnotationHosts.Count > 0;
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
