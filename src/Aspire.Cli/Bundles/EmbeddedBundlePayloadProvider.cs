// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Bundles;

/// <summary>
/// Reads the bundle payload from an embedded assembly resource.
/// </summary>
internal sealed class EmbeddedBundlePayloadProvider : IBundlePayloadProvider
{
    private const string PayloadResourceName = "bundle.tar.gz";

    /// <inheritdoc/>
    public bool HasPayload { get; } =
        typeof(BundleService).Assembly.GetManifestResourceInfo(PayloadResourceName) is not null;

    /// <inheritdoc/>
    public Stream? OpenPayload() =>
        typeof(BundleService).Assembly.GetManifestResourceStream(PayloadResourceName);
}
