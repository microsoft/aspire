// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRERADIUS004 // Experimental: ConfigureRadiusInfrastructure escape-hatch construct types are consumed internally by the publisher.

using Azure.Provisioning;

namespace Aspire.Hosting.Radius.Publishing;

/// <summary>
/// Minimal <see cref="Infrastructure"/> subclass used to compile Radius
/// <see cref="Azure.Provisioning.Primitives.ProvisionableResource"/> constructs to Bicep
/// via <c>Build().Compile()</c>.
/// </summary>
internal sealed class RadiusResourceInfrastructure : Infrastructure
{
    internal RadiusResourceInfrastructure(string environmentName)
        : base($"radius_{BicepPostProcessor.SanitizeIdentifier(environmentName)}")
    {
    }
}
