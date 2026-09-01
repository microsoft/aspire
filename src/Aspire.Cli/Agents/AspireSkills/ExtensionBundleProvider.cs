// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Provides the Aspire Skills bundle containing agent extensions.
/// </summary>
internal class ExtensionBundleProvider : AspireSkillsBundleProvider
{
    public ExtensionBundleProvider(
        CliExecutionContext executionContext,
        ILogger<ExtensionBundleProvider> logger)
        : base(executionContext, logger)
    {
    }

    internal ExtensionBundleProvider(CliExecutionContext executionContext)
        : base(executionContext, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
    {
    }

    internal ExtensionBundleProvider()
    {
    }

    internal ExtensionBundleProvider(string currentCliVersion, string currentSdkVersion)
        : base(currentCliVersion, currentSdkVersion)
    {
    }

    public override AgentAssetKind AssetKind => AgentAssetKind.Extension;

    public override string AssetKindName => "extensions";

    public override string AssetPrefix => "aspire-extensions";

    public override string CacheDirectoryName => "aspire-extensions";

    public override string DisplayName => "Aspire extensions";

    public override string ManifestFileName => "extension-manifest.json";

    public override string ManifestAssetsPropertyName => "extensions";

    public override string ContentRootDirectoryName => "extensions";

    public override string RequiredFileName => "extension.mjs";

    public override string EmbeddedArchiveResourceName => "aspire-extensions.bundle.tgz";

    public override string EmbeddedMetadataResourceName => "aspire-extensions.metadata.json";

}
