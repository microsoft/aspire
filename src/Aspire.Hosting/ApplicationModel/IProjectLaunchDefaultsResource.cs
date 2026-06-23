// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Internal abstraction shared by .NET-based resources that are launched via the SDK
/// (<see cref="ProjectResource"/> in core and <c>CSharpAppResource</c> in
/// <c>Aspire.Hosting.Dotnet</c>) and reuse the project-defaults wiring in
/// <c>WithProjectDefaults</c>: launch-profile / Kestrel endpoint materialization,
/// <c>ASPNETCORE_URLS</c> / <c>HTTP(S)_PORTS</c> environment, Kestrel URL overrides, and the
/// run-mode rebuilder + Rebuild command.
/// </summary>
internal interface IProjectLaunchDefaultsResource : IResourceWithEnvironment, IResourceWithEndpoints, IResourceWithArgs
{
    /// <summary>
    /// The config host for each endpoint that originated from Kestrel configuration. Used when
    /// rebuilding the <c>Kestrel__Endpoints__*__Url</c> override environment variables.
    /// </summary>
    Dictionary<EndpointAnnotation, string> KestrelEndpointAnnotationHosts { get; }

    /// <summary>
    /// The https endpoint that was added as a default. It is excluded from the port and Kestrel
    /// override environment because the target (e.g. a container) likely won't listen on https.
    /// </summary>
    EndpointAnnotation? DefaultHttpsEndpoint { get; set; }

    /// <summary>
    /// Whether any endpoints originated from Kestrel configuration.
    /// </summary>
    bool HasKestrelEndpoints => KestrelEndpointAnnotationHosts.Count > 0;

    /// <summary>
    /// Determines whether endpoint environment variables should be injected for the given endpoint.
    /// Only http/https endpoints without an explicit target-port environment variable are eligible,
    /// and any <see cref="EndpointEnvironmentInjectionFilterAnnotation"/> may further exclude them.
    /// </summary>
    bool ShouldInjectEndpointEnvironment(EndpointReference e)
    {
        var endpoint = e.EndpointAnnotation;

        if (endpoint.UriScheme is not ("http" or "https") ||    // Only process http and https endpoints
            endpoint.TargetPortEnvironmentVariable is not null) // Skip if target port env variable was set
        {
            return false;
        }

        // If any filter rejects the endpoint, skip it
        return !Annotations.OfType<EndpointEnvironmentInjectionFilterAnnotation>()
            .Select(a => a.Filter)
            .Any(f => !f(endpoint));
    }
}
