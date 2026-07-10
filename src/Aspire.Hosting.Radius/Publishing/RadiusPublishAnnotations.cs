// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting.Radius;

/// <summary>
/// Annotation that stores a <see cref="RadiusInfrastructureOptions"/> configuration callback
/// on a <see cref="RadiusEnvironmentResource"/>.
/// </summary>
internal sealed class RadiusInfrastructureConfigureAnnotation(Action<RadiusInfrastructureOptions> configure) : IResourceAnnotation
{
    public Action<RadiusInfrastructureOptions> Configure { get; } = configure;
}

/// <summary>
/// Annotation that records the mapping from emitted Bicep parameter identifier to the
/// originating Aspire <see cref="ParameterResource"/>, written by the publish step and read by
/// the deploy step so each valueless <c>param</c> can be supplied via <c>rad deploy --parameters</c>.
/// </summary>
/// <remarks>
/// State is shared via an annotation on the environment resource (rather than a sidecar file)
/// because the publish and deploy steps run in the same process against the same resource
/// instance, mirroring how cloud-provider credentials flow to the credential-register step.
/// </remarks>
internal sealed class RadiusDeployParametersAnnotation(IReadOnlyDictionary<string, ParameterResource> parameters) : IResourceAnnotation
{
    public IReadOnlyDictionary<string, ParameterResource> Parameters { get; } = parameters;
}
