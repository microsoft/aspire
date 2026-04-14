// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Models;
using Aspire.Hosting.Radius.Publishing;

namespace Aspire.Hosting.Radius;

/// <summary>
/// Annotation that stores a <see cref="RadiusResourceCustomization"/> on a resource.
/// </summary>
internal sealed class RadiusResourceCustomizationAnnotation(RadiusResourceCustomization customization) : IResourceAnnotation
{
    public RadiusResourceCustomization Customization { get; } = customization;
}

/// <summary>
/// Annotation that stores a <see cref="RadiusInfrastructureOptions"/> configuration callback
/// on a <see cref="RadiusEnvironmentResource"/>.
/// </summary>
internal sealed class RadiusInfrastructureConfigureAnnotation(Action<RadiusInfrastructureOptions> configure) : IResourceAnnotation
{
    public Action<RadiusInfrastructureOptions> Configure { get; } = configure;
}
