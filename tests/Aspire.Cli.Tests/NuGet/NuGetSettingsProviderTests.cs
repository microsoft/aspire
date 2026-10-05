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
    public void IsPackageSourceMappingEnabled_UsesNuGetConfigHierarchy()
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
        var provider = CreateProvider();

        var enabled = provider.IsPackageSourceMappingEnabled(
            projectDirectory,
            TestContext.Current.CancellationToken);

        Assert.True(enabled);
    }

    [Fact]
    public void IsPackageSourceMappingEnabled_MatchesNuGetSectionAndItemSemantics()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var nonCanonicalDirectory = workspace.CreateDirectory("noncanonical");
        var mappingThenClearDirectory = workspace.CreateDirectory("mapping-then-clear");
        var clearThenMappingDirectory = workspace.CreateDirectory("clear-then-mapping");

        File.WriteAllText(
            Path.Combine(nonCanonicalDirectory.FullName, "NuGet.Config"),
            """
            <configuration>
              <PackageSourceMapping>
                <packageSource key="private">
                  <package pattern="*" />
                </packageSource>
              </PackageSourceMapping>
              <packageSourceMapping>
                <clear />
              </packageSourceMapping>
            </configuration>
            """);
        File.WriteAllText(
            Path.Combine(mappingThenClearDirectory.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSourceMapping>
                <packageSource key="private">
                  <package pattern="*" />
                </packageSource>
                <clear />
              </packageSourceMapping>
            </configuration>
            """);
        File.WriteAllText(
            Path.Combine(clearThenMappingDirectory.FullName, "NuGet.Config"),
            """
            <configuration>
              <packageSourceMapping>
                <clear />
                <PackageSource key="private">
                  <package pattern="*" />
                </PackageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var provider = CreateProvider();

        Assert.False(provider.IsPackageSourceMappingEnabled(
            nonCanonicalDirectory,
            TestContext.Current.CancellationToken));
        Assert.False(provider.IsPackageSourceMappingEnabled(
            mappingThenClearDirectory,
            TestContext.Current.CancellationToken));
        Assert.True(provider.IsPackageSourceMappingEnabled(
            clearThenMappingDirectory,
            TestContext.Current.CancellationToken));
    }

    private static NuGetSettingsProvider CreateProvider()
    {
        var nuGetClient = new NuGetClient(
            new TestFeatures(),
            new TestEnvironment(),
            NullLogger<NuGetClient>.Instance);
        var bundleNuGetService = new BundleNuGetService(
            NullLogger<BundleNuGetService>.Instance,
            nuGetClient);
        return new NuGetSettingsProvider(bundleNuGetService);
    }
}
