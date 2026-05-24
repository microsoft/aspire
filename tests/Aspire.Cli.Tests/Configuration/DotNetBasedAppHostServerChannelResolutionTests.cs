// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Packaging;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Configuration;

/// <summary>
/// Behavioral guards on <see cref="DotNetBasedAppHostServerProject"/>'s channel resolution:
/// it consults only per-project state (<c>aspire.config.json</c>, then legacy
/// <c>AspireJsonConfiguration</c>) and falls through to the channel system itself when no
/// per-project channel is set. A global-channel read fallback (previously
/// <c>IConfigurationService.GetConfigurationAsync("channel", ...)</c>) was removed in
/// PR1; these tests pin the post-fix contract so a regression can't quietly re-introduce
/// cross-route channel contamination.
/// </summary>
public class DotNetBasedAppHostServerChannelResolutionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task DotNetBasedAppHostServerProject_CreateProjectFiles_ReturnsNullChannel_WhenNoPerProjectChannelAndOnlyImplicit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;

        var project = CreateProject(appPath, MockPackagingServiceFactory.Create());

        var (_, channelName) = await project.CreateProjectFilesAsync(
            [IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")]);

        // No per-project channel + only an Implicit channel registered → no name to pin.
        // Before the fallback was removed, the resolver would have consulted the global
        // IConfigurationService here and could have returned a stale "channel" value.
        Assert.Null(channelName);
    }

    [Fact]
    public async Task DotNetBasedAppHostServerProject_CreateProjectFiles_HonorsAspireConfigJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;

        var config = AspireConfigFile.LoadOrCreate(appPath);
        config.Channel = "staging";
        config.Save(appPath);

        var project = CreateProject(appPath, CreatePackagingServiceWithExplicitChannels("staging", "daily"));

        var (_, channelName) = await project.CreateProjectFilesAsync(
            [IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")]);

        Assert.Equal("staging", channelName);
    }

    [Fact]
    public async Task DotNetBasedAppHostServerProject_CreateProjectFiles_FallsBackToLegacyAspireSettings_WhenAspireConfigJsonMissing()
    {
        // Migration safety: projects scaffolded before per-project aspire.config.json landed
        // wrote their channel into .aspire/settings.json (AspireJsonConfiguration). The reader
        // must still honor the legacy file so existing on-disk projects keep working.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;

        var legacy = new AspireJsonConfiguration { Channel = "daily", SdkVersion = "13.3.0" };
        legacy.Save(appPath);

        Assert.False(File.Exists(Path.Combine(appPath, AspireConfigFile.FileName)));

        var project = CreateProject(appPath, CreatePackagingServiceWithExplicitChannels("staging", "daily"));

        var (_, channelName) = await project.CreateProjectFilesAsync(
            [IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")]);

        Assert.Equal("daily", channelName);
    }

    [Fact]
    public async Task DotNetBasedAppHostServerProject_CreateProjectFiles_PrefersAspireConfigJsonOverLegacyAspireSettings()
    {
        // When both files exist, the new format wins. Pins the `??` operand order in the
        // reader so an accidental swap is caught.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appPath = workspace.WorkspaceRoot.FullName;

        var legacy = new AspireJsonConfiguration { Channel = "daily", SdkVersion = "13.3.0" };
        legacy.Save(appPath);

        var config = AspireConfigFile.LoadOrCreate(appPath);
        config.Channel = "staging";
        config.Save(appPath);

        var project = CreateProject(appPath, CreatePackagingServiceWithExplicitChannels("staging", "daily"));

        var (_, channelName) = await project.CreateProjectFilesAsync(
            [IntegrationReference.FromPackage("Aspire.Hosting", "13.1.0")]);

        Assert.Equal("staging", channelName);
    }

    private static DotNetBasedAppHostServerProject CreateProject(string appPath, TestPackagingService packagingService)
    {
        // Pin ProjectModelPath inside the workspace so test artifacts don't bleed into the
        // user's ~/.aspire/hosts directory.
        var projectModelPath = Path.Combine(appPath, ".aspire_server");

        return new DotNetBasedAppHostServerProject(
            appPath,
            socketPath: "test.sock",
            repoRoot: appPath,
            new TestDotNetCliRunner(),
            packagingService,
            NullLogger<DotNetBasedAppHostServerProject>.Instance,
            projectModelPath);
    }

    private static TestPackagingService CreatePackagingServiceWithExplicitChannels(params string[] channelNames)
    {
        return new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                var cache = new FakeNuGetPackageCache();
                var channels = channelNames
                    .Select(name => PackageChannel.CreateExplicitChannel(
                        name,
                        PackageChannelQuality.Both,
                        mappings: [],
                        cache,
                        new TestFeatures()))
                    .ToArray();
                return Task.FromResult<IEnumerable<PackageChannel>>(channels);
            }
        };
    }
}
