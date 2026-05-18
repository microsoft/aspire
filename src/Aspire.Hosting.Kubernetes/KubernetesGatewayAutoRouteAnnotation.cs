// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Marks a <see cref="KubernetesGatewayResource"/> as eligible for auto-routing the single
/// external HTTP frontend in the app model onto the gateway's root path. Evaluated by the
/// gateway-emission pipeline step so that auto-routing happens deterministically inside the
/// publish pipeline rather than being gated on the <c>BeforePublishEvent</c> (which only fires
/// for the legacy <c>dotnet run -- --publisher kubernetes</c> path; <c>aspire deploy</c> drives
/// pipeline steps directly over JSON-RPC and never raises that event).
/// </summary>
/// <param name="InfrastructureResourceNames">Names of resources to exclude from auto-routing
/// — typically the infra resources the recipe created itself (vnet, subnets, gateway, load
/// balancer, cert-manager, issuer). Existing routes set by the user via <c>WithRoute</c> are
/// always preserved (user-wins).</param>
/// <remarks>
/// The auto-router enforces a single-external-frontend constraint: if more than one resource
/// in the model exposes external HTTP endpoints, it throws because multi-frontend hostname
/// allocation requires stable per-deploy endpoint-to-listener mappings and bumps against the
/// per-AGC frontend cap. Callers who need multiple frontends should drop down to the verbose
/// gateway/route/cert-manager wiring.
/// </remarks>
internal sealed record KubernetesGatewayAutoRouteAnnotation(
    IReadOnlySet<string> InfrastructureResourceNames) : IResourceAnnotation;
