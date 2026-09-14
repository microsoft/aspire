// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

public class AspireSkillsAssetSourceTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void Registration_UsesSharedSourceWithoutAcquisition()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var serviceProvider = services.BuildServiceProvider();

        var source = serviceProvider.GetRequiredService<IAgentAssetSource>();

        Assert.IsType<AspireSkillsAssetSource>(source);
        Assert.Same(source, serviceProvider.GetRequiredService<IAgentAssetSource>());
        Assert.Empty(Assert.IsType<FakeAspireSkillsInstaller>(serviceProvider.GetRequiredService<IAspireSkillsInstaller>()).RequestedProviders);
    }

    [Fact]
    public async Task Resolution_RetainsCommonReadersWithSourceOwnedConfigurations()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (source, installer) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Unavailable);

        foreach (var kind in new[] { AgentAssetKind.Skill, AgentAssetKind.Extension, AgentAssetKind.Skill, AgentAssetKind.Extension })
        {
            await source.GetAssetsAsync(kind, TestContext.Current.CancellationToken);
        }

        var providers = installer.RequestedProviders;
        Assert.Equal([AgentAssetKind.Skill, AgentAssetKind.Extension, AgentAssetKind.Skill, AgentAssetKind.Extension], installer.RequestedAssetKinds);
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
        var kind = extensions ? AgentAssetKind.Extension : AgentAssetKind.Skill;
        var asset = new AgentAssetDefinition(
            "test-asset", "Test asset", [new AgentAssetFile("payload.txt", "Payload")], installExcludedRelativePaths: [], isDefault: true);
        var bundle = new AspireSkillsBundle(AspireSkillsInstaller.Version, kind, [asset]);
        var (source, installer) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Installed(bundle));

        var result = await source.GetAssetsAsync(kind, TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable);
        Assert.Null(result.Message);
        Assert.Same(asset, Assert.Single(result.Assets));
        Assert.True(asset.HasInstallableFiles);
        Assert.Equal([kind], installer.RequestedAssetKinds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolution_PreservesAcquisitionFailureDiagnostic(bool extensions)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string message = "Bundle verification failed.";
        var (source, _) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Failed(message));

        var result = await source.GetAssetsAsync(
            extensions ? AgentAssetKind.Extension : AgentAssetKind.Skill, TestContext.Current.CancellationToken);

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
        var (source, _) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Unavailable);

        var result = await source.GetAssetsAsync(
            extensions ? AgentAssetKind.Extension : AgentAssetKind.Skill, TestContext.Current.CancellationToken);

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
        var (source, _) = CreateSource(workspace.CreateExecutionContext(), new(AspireSkillsInstallStatus.Installed, Bundle: null, Message: null));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetAssetsAsync(
            AgentAssetKind.Skill, TestContext.Current.CancellationToken));

        Assert.Equal("Aspire bundle acquisition returned an installed result without a bundle.", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resolution_RejectsBundleOfAnotherKindEvenWhenEmpty(bool empty)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var bundle = new AspireSkillsBundle(AspireSkillsInstaller.Version, AgentAssetKind.Skill, empty
            ? []
            : [new AgentAssetDefinition(
                "test-skill", "Test skill", [new AgentAssetFile("SKILL.md", "Content")], installExcludedRelativePaths: [], isDefault: true)]);
        var (source, _) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Installed(bundle));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetAssetsAsync(
            AgentAssetKind.Extension, TestContext.Current.CancellationToken));

        Assert.Equal("Aspire bundle acquisition returned a bundle of another kind for 'Extension'.", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task Resolution_RejectsUnknownKindWithoutAcquisition(int kind)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (source, installer) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Unavailable);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.GetAssetsAsync(
            (AgentAssetKind)kind, TestContext.Current.CancellationToken));

        Assert.Empty(installer.RequestedProviders);
    }

    [Fact]
    public async Task Resolution_CancellationDoesNotStartAcquisition()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (source, installer) = CreateSource(workspace.CreateExecutionContext(), AspireSkillsInstallResult.Unavailable);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetAssetsAsync(
            AgentAssetKind.Skill, cancellation.Token));

        Assert.Empty(installer.RequestedProviders);
    }

    private static (AspireSkillsAssetSource Source, FakeAspireSkillsInstaller Installer) CreateSource(
        CliExecutionContext executionContext, AspireSkillsInstallResult result)
    {
        var installer = new FakeAspireSkillsInstaller(executionContext, result) { ExtensionResult = result };
        return (new(installer, executionContext, NullLogger<AspireSkillsBundleProvider>.Instance), installer);
    }
}
