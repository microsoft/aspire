// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Configuration;
using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Mcp;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Configuration;

/// <summary>
/// Behavioral guards on <see cref="PrebuiltAppHostServer"/>'s channel resolution: it
/// consults only per-project state (<c>aspire.config.json</c>) and returns
/// <see langword="null"/> when no per-project channel is set.
/// </summary>
public class PrebuiltAppHostServerChannelResolutionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PrebuiltAppHostServer_ResolveRequestedChannel_ReturnsNullWhenNoAspireConfigJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var server = CreateServer(appHostDirectory.FullName);

        Assert.Null(server.ResolveRequestedChannel());
    }

    [Fact]
    public void PrebuiltAppHostServer_ResolveRequestedChannel_HonorsAspireConfigJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var config = AspireConfigFile.LoadOrCreate(appHostDirectory.FullName);
        config.Channel = "staging";
        config.Save(appHostDirectory.FullName);

        var server = CreateServer(appHostDirectory.FullName);

        Assert.Equal("staging", server.ResolveRequestedChannel());
    }

    [Fact]
    public void PrebuiltAppHostServer_ResolveRequestedChannel_FallsBackToLegacyAspireSettings_WhenAspireConfigJsonMissing()
    {
        // Migration safety: projects scaffolded before the per-project aspire.config.json
        // landed wrote their channel into .aspire/settings.json (AspireJsonConfiguration).
        // PrebuiltAppHostServer must still resolve that legacy file so existing on-disk
        // projects keep working until they're migrated. The new file is preferred when
        // both exist (asserted by the next test).
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var legacy = new AspireJsonConfiguration { Channel = "daily", SdkVersion = "13.3.0" };
        legacy.Save(appHostDirectory.FullName);

        // Sanity check the precondition: the new-format file must NOT exist for this test
        // to exercise the legacy branch — otherwise it would pass for the wrong reason.
        Assert.False(File.Exists(Path.Combine(appHostDirectory.FullName, AspireConfigFile.FileName)));

        var server = CreateServer(appHostDirectory.FullName);

        Assert.Equal("daily", server.ResolveRequestedChannel());
    }

    [Fact]
    public void PrebuiltAppHostServer_ResolveRequestedChannel_PrefersAspireConfigJsonOverLegacyAspireSettings()
    {
        // When both files exist (e.g. during/after migration), the new format wins. This
        // anchors the precedence and prevents an accidental swap of the `??` operands in
        // PrebuiltAppHostServer.ResolveRequestedChannel from going unnoticed.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var legacy = new AspireJsonConfiguration { Channel = "daily", SdkVersion = "13.3.0" };
        legacy.Save(appHostDirectory.FullName);

        var config = AspireConfigFile.LoadOrCreate(appHostDirectory.FullName);
        config.Channel = "staging";
        config.Save(appHostDirectory.FullName);

        var server = CreateServer(appHostDirectory.FullName);

        Assert.Equal("staging", server.ResolveRequestedChannel());
    }

    private static PrebuiltAppHostServer CreateServer(string appPath)
    {
        var nugetService = new BundleNuGetService(
            new NullLayoutDiscovery(),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger<BundleNuGetService>.Instance);

        return new PrebuiltAppHostServer(
            appPath,
            socketPath: "test.sock",
            new LayoutConfiguration(),
            nugetService,
            new TestDotNetCliRunner(),
            new TestDotNetSdkInstaller(),
            MockPackagingServiceFactory.Create(),
            TestExecutionContextFactory.CreateTestContext(),
            NullLogger.Instance);
    }
}

