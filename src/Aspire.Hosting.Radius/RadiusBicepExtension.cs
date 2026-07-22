// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius;

/// <summary>
/// Centralizes versions and reference URIs for the Bicep extensions the
/// Radius integration emits into <c>bicepconfig.json</c>. Pinned to a
/// concrete <c>major.minor</c> rather than <c>:latest</c> so generated
/// Bicep deploys deterministically and so a tag move on the upstream
/// registry can't suddenly change the schema the AppHost emits against.
/// </summary>
/// <remarks>
/// Bump in lockstep with the Radius install targeted by the deploy
/// step (<c>rad version</c>). The integration's emitted resource type
/// versions (e.g. <c>Radius.Compute/containers@2025-08-01-preview</c>,
/// <c>Applications.Core/environments@2023-10-01-preview</c>) must be
/// available in the pinned extension package. Newer Radius releases
/// publish extension tags at
/// <see href="https://biceptypes.azurecr.io/v2/radius/tags/list"/>.
/// </remarks>
internal static class RadiusBicepExtension
{
    /// <summary>
    /// The pinned Radius Bicep extension version. Format: <c>major.minor</c>.
    /// </summary>
    internal const string Version = "0.59";

    /// <summary>
    /// The OCI reference for the Radius Bicep extension, with the pinned version.
    /// </summary>
    internal const string Reference = "br:biceptypes.azurecr.io/radius:" + Version;
}
