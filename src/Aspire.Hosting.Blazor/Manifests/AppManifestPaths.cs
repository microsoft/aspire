// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting;

internal readonly struct AppManifestPaths(GatewayAppRegistration registration, string endpointsManifest, string runtimeManifest)
{
    public GatewayAppRegistration Registration { get; } = registration;
    public string EndpointsManifest { get; } = endpointsManifest;
    public string RuntimeManifest { get; } = runtimeManifest;
}
