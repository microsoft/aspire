// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsAssetSourceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Construction_DoesNotAcquireBundle()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (_, installer) = CreateSource(
            workspace.CreateExecutionContext(), AspireSkillsInstallResult.Unavailable, AspireSkillsBundleDescriptor.Skills);
        Assert.Empty(installer.RequestedProviders);
    }

    [Fact]
    public async Task Resolution_RetainsEachSourcesConfiguredReader()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var executionContext = workspace.CreateExecutionContext();
        var (skills, installer) = CreateSource(
            executionContext, AspireSkillsInstallResult.Unavailable, AspireSkillsBundleDescriptor.Skills);
        var extensions = new AspireSkillsAssetSource(
            installer, AspireSkillsBundleDescriptor.Extensions, executionContext, NullLogger<AspireSkillsBundleProvider>.Instance);

        foreach (var source in new[] { skills, extensions, skills, extensions })
        {
            await source.GetAssetsAsync(TestContext.Current.CancellationToken);
        }

        var providers = installer.RequestedProviders;
        Assert.Equal(
            [AspireSkillsBundleDescriptor.Skills, AspireSkillsBundleDescriptor.Extensions, AspireSkillsBundleDescriptor.Skills, AspireSkillsBundleDescriptor.Extensions],
            providers.Select(provider => provider.Descriptor));
        Assert.All(providers, provider => Assert.IsType<AspireSkillsBundleProvider>(provider));
        Assert.Same(providers[0], providers[2]);
        Assert.Same(providers[1], providers[3]);
        Assert.NotSame(providers[0], providers[1]);
        Assert.Equal("skill-manifest.json", providers[0].Descriptor.ManifestFileName);
        Assert.Equal("skills", providers[0].Descriptor.ManifestAssetsPropertyName);
        Assert.Equal("SKILL.md", providers[0].Descriptor.RequiredFileName);
        Assert.Equal("aspire-skills.bundle.tgz", providers[0].Descriptor.EmbeddedArchiveResourceName);
        Assert.Equal("extension-manifest.json", providers[1].Descriptor.ManifestFileName);
        Assert.Equal("extensions", providers[1].Descriptor.ManifestAssetsPropertyName);
        Assert.Equal("extension.mjs", providers[1].Descriptor.RequiredFileName);
        Assert.Equal("aspire-extensions.bundle.tgz", providers[1].Descriptor.EmbeddedArchiveResourceName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolution_ReturnsResolvedAssetsWithoutExposingBundle(bool extensions)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var descriptor = extensions ? AspireSkillsBundleDescriptor.Extensions : AspireSkillsBundleDescriptor.Skills;
        var asset = new AgentAssetDefinition(
            "test-asset", "Test asset", [new AgentAssetFile("payload.txt", "Payload")], installExcludedRelativePaths: [], isDefault: true);
        var bundle = new AspireSkillsBundle(AspireSkillsInstaller.Version, [asset]);
        var (source, installer) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Installed(bundle), descriptor);

        var result = await source.GetAssetsAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Null(result.Message);
        Assert.Same(asset, Assert.Single(result.Assets));
        Assert.True(asset.HasInstallableFiles);
        Assert.Same(descriptor, Assert.Single(installer.RequestedProviders).Descriptor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolution_PreservesAcquisitionFailureDiagnostic(bool extensions)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string message = "Bundle verification failed.";
        var (source, _) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Failed(message),
            extensions ? AspireSkillsBundleDescriptor.Extensions : AspireSkillsBundleDescriptor.Skills);

        var result = await source.GetAssetsAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.Assets);
        Assert.Equal(message, result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolution_UnavailableBundleSuppliesSourceDiagnostic(bool extensions)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (source, _) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Unavailable,
            extensions ? AspireSkillsBundleDescriptor.Extensions : AspireSkillsBundleDescriptor.Skills);

        var result = await source.GetAssetsAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.Assets);
        Assert.Equal(
            extensions
                ? AgentCommandStrings.InitCommand_ExtensionBundleUnavailable
                : string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.AspireSkillsInstaller_GitHubUnavailable, "Aspire skills"),
            result.Message);
    }

    [Fact]
    public async Task Resolution_RejectsInstalledResultWithoutBundle()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (source, _) = CreateSource(workspace.CreateExecutionContext(),
            new(AspireSkillsInstallStatus.Installed, Bundle: null, Message: null), AspireSkillsBundleDescriptor.Skills);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetAssetsAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Aspire bundle acquisition returned an installed result without a bundle.", exception.Message);
    }

    [Fact]
    public async Task Resolution_CancellationDoesNotStartAcquisition()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (source, installer) = CreateSource(
            workspace.CreateExecutionContext(), AspireSkillsInstallResult.Unavailable, AspireSkillsBundleDescriptor.Skills);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetAssetsAsync(cancellation.Token));

        Assert.Empty(installer.RequestedProviders);
    }

    private static (AspireSkillsAssetSource Source, FakeAspireSkillsInstaller Installer) CreateSource(
        CliExecutionContext executionContext, AspireSkillsInstallResult result, AspireSkillsBundleDescriptor descriptor)
    {
        var installer = new FakeAspireSkillsInstaller(executionContext, result) { ExtensionResult = result };
        return (new(installer, descriptor, executionContext, NullLogger<AspireSkillsBundleProvider>.Instance), installer);
    }
}
