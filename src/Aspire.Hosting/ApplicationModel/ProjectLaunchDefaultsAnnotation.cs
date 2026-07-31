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
    /// <summary>
    /// The config host for each endpoint that originated from Kestrel configuration. Used when
    /// rebuilding the <c>Kestrel__Endpoints__*__Url</c> override environment variables.
    /// </summary>
    internal Dictionary<EndpointAnnotation, string> KestrelEndpointAnnotationHosts { get; } = [];

    /// <summary>
    /// The https endpoint that was added as a default. It is excluded from the port and Kestrel
    /// override environment because the target (e.g. a container) likely won't listen on https.
    /// </summary>
    internal EndpointAnnotation? DefaultHttpsEndpoint { get; set; }

    /// <summary>
    /// Whether any endpoints originated from Kestrel configuration.
    /// </summary>
    internal bool HasKestrelEndpoints => KestrelEndpointAnnotationHosts.Count > 0;

    /// <summary>
    /// True if <see cref="ProjectResourceBuilderExtensions.WithProjectDefaults{TProjectResource}(IResourceBuilder{TProjectResource}, ProjectResourceOptions)"/>
    /// has already run for the resource, otherwise false.
    /// </summary>
    /// <remarks>
    /// The flag is used to ensure that multiple calls to <see cref="ProjectResourceBuilderExtensions.WithProjectDefaults{TProjectResource}(IResourceBuilder{TProjectResource}, ProjectResourceOptions)"/>
    /// are idempotent and don't add duplicate endpoints or environment variables.
    /// </remarks>
    internal bool Applied { get; set; }
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
