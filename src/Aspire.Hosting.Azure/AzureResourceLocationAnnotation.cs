// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Records an explicit Azure region for a single Azure resource, set via <c>WithLocation</c>.
/// </summary>
/// <remarks>
/// The location itself lives in <see cref="AzureBicepResource.Parameters"/> under
/// <see cref="AzureBicepResource.KnownParameters.Location"/>, but the provisioner writes an inferred
/// environment location into that same slot during deployment. This annotation is what lets publish-time
/// code distinguish an author's explicit choice from an inferred value.
/// </remarks>
/// <param name="location">The location value, either a <see cref="string"/> or a <see cref="ParameterResource"/>.</param>
internal sealed class AzureResourceLocationAnnotation(object location) : IResourceAnnotation
{
    /// <summary>
    /// Gets the configured location, which is either a <see cref="string"/> or a <see cref="ParameterResource"/>.
    /// </summary>
    public object Location { get; } = location ?? throw new ArgumentNullException(nameof(location));
}
