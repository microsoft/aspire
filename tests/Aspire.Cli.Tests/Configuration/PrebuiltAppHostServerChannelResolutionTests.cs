// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
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
    public void PrebuiltAppHostServer_ResolveChannelName_ReturnsNullWhenNoAspireConfigJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var server = CreateServer(appHostDirectory.FullName);

        var resolveChannelName = typeof(PrebuiltAppHostServer)
            .GetMethod("ResolveChannelName", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ResolveChannelName not found on PrebuiltAppHostServer.");

        var resolved = resolveChannelName.Invoke(server, parameters: null);

        Assert.Null(resolved);
    }

    [Fact]
    public void PrebuiltAppHostServer_ResolveChannelName_HonorsAspireConfigJson()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var appHostDirectory = workspace.CreateDirectory("apphost");

        var config = AspireConfigFile.LoadOrCreate(appHostDirectory.FullName);
        config.Channel = "staging";
        config.Save(appHostDirectory.FullName);

        var server = CreateServer(appHostDirectory.FullName);

        var resolveChannelName = typeof(PrebuiltAppHostServer)
            .GetMethod("ResolveChannelName", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var resolved = (string?)resolveChannelName.Invoke(server, parameters: null);

        Assert.Equal("staging", resolved);
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
