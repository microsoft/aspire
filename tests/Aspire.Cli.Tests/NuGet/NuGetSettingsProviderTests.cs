// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetSettingsProviderTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task IsPackageSourceMappingEnabledAsync_WhenManagedComponentIsUnavailable_UsesDotNetConfigHierarchy()
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
        var dotNetCliRunner = new TestDotNetCliRunner
        {
            GetNuGetConfigPathsAsyncCallback = (workingDirectory, options, _) =>
            {
                Assert.Equal(projectDirectory.FullName, workingDirectory.FullName);
                Assert.True(options.SuppressLogging);
                return (0, [configPath]);
            }
        };
        var layoutRoot = workspace.CreateDirectory("layout");
        var bundleNuGetService = new BundleNuGetService(
            new FixedLayoutDiscovery(new LayoutConfiguration { LayoutPath = layoutRoot.FullName }),
            new LayoutProcessRunner(new TestProcessExecutionFactory()),
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<BundleNuGetService>.Instance);
        var provider = new NuGetSettingsProvider(bundleNuGetService, dotNetCliRunner);

        var enabled = await provider.IsPackageSourceMappingEnabledAsync(
            projectDirectory,
            TestContext.Current.CancellationToken);

        Assert.True(enabled);
    }
}
