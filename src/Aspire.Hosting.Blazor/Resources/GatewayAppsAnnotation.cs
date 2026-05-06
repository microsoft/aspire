// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Annotation stored on a gateway resource that tracks all registered Blazor WASM app registrations.
/// Replaces the previous static Dictionary approach, keeping state on the resource itself.
/// </summary>
internal class GatewayAppsAnnotation : IResourceAnnotation
{
    public List<GatewayAppRegistration> Apps { get; } = new();
    public bool IsInitialized { get; set; }
}

internal record GatewayAppRegistration(
    IResourceBuilder<BlazorWasmAppResource> AppBuilder,
    string PathPrefix,
    string[] ServiceNames,
    string ApiPrefix = GatewayConfigurationBuilder.DefaultApiPrefix,
    string OtlpPrefix = GatewayConfigurationBuilder.DefaultOtlpPrefix,
    bool ProxyTelemetry = true)
{
    public BlazorWasmAppResource Resource => AppBuilder.Resource;
}
