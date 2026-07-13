// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Radius.Secrets;

/// <summary>
/// Computes the location, relative to an emitted <c>app.bicep</c>, where a sealed secret
/// store's (encrypted) <c>SealedSecret</c> manifest is copied at publish time and read back
/// at deploy time. Both the publish copy target and the deploy resolve MUST go through this
/// single helper so the two sides can never drift, and so two stores that reference manifests
/// with the same file name in different source directories cannot collide (each is namespaced
/// by its unique store name). See <c>ASPIRERADIUS044</c> / FR-007.
/// </summary>
internal static class SealedSecretArtifact
{
    private const string RootDirectoryName = "sealed-secrets";

    /// <summary>
    /// The output-relative path for <paramref name="storeName"/>'s manifest:
    /// <c>sealed-secrets/&lt;storeName&gt;/&lt;fileName&gt;</c>. The store name is a validated,
    /// path-safe identifier (duplicate names are rejected before emission), so it is a stable,
    /// collision-free directory segment.
    /// </summary>
    internal static string RelativePath(string storeName, string sourceManifestPath) =>
        Path.Combine(RootDirectoryName, storeName, Path.GetFileName(sourceManifestPath));

    /// <summary>
    /// The absolute copy/read location under <paramref name="outputDirectory"/> (the directory
    /// that holds the emitted <c>app.bicep</c>) for <paramref name="storeName"/>'s manifest.
    /// </summary>
    internal static string ResolvePath(string outputDirectory, string storeName, string sourceManifestPath) =>
        Path.Combine(outputDirectory, RelativePath(storeName, sourceManifestPath));
}
