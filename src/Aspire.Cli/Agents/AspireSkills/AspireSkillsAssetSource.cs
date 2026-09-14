// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Agents.AspireSkills;

/// <summary>
/// Adapts Aspire bundle acquisition to the source-neutral agent asset contract.
/// </summary>
internal sealed class AspireSkillsAssetSource : IAgentAssetSource
{
    private readonly IAspireSkillsInstaller _installer;
    private readonly AspireSkillsBundleProvider _provider;

    public AspireSkillsAssetSource(
        IAspireSkillsInstaller installer,
        AspireSkillsBundleDescriptor descriptor,
        CliExecutionContext executionContext,
        ILogger<AspireSkillsBundleProvider> logger)
    {
        _installer = installer;
        // Retain the configured reader, including its lazy embedded metadata, across
        // catalog resolutions. The effective CLI identity also supplies the SDK version.
        _provider = new AspireSkillsBundleProvider(
            descriptor, executionContext.IdentitySdkVersion, executionContext.IdentitySdkVersion, logger);
    }

    public async Task<AgentAssetSourceResult> GetAssetsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _installer.InstallAsync(_provider, cancellationToken);
        if (result.Status is not AspireSkillsInstallStatus.Installed)
        {
            var message = result.Message ?? _provider.Descriptor.UnavailableMessage;
            return AgentAssetSourceResult.Unavailable(message);
        }

        var bundle = result.Bundle
            ?? throw new InvalidOperationException("Aspire bundle acquisition returned an installed result without a bundle.");
        return AgentAssetSourceResult.Available(bundle.Assets);
    }
}
