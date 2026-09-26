// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetSettingsProviderTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task IsPackageSourceMappingEnabledAsync_UsesNuGetConfigHierarchy()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var projectDirectory = workspace.CreateDirectory("AppHost");
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        File.WriteAllText(
            configPath,
            """
            <configuration>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var nuGetClient = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);
        var bundleNuGetService = new BundleNuGetService(
            NullLogger<BundleNuGetService>.Instance,
            nuGetClient);
        var provider = new NuGetSettingsProvider(bundleNuGetService);

        var enabled = await provider.IsPackageSourceMappingEnabledAsync(
            projectDirectory,
            TestContext.Current.CancellationToken);

        Assert.True(enabled);
    }
}
