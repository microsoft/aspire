// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Cli.Tests.Packaging;

public class PackageSourceOverrideMappingsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public void ResolveForWorkingDirectory_RelativePathContainingColon_ResolvesAgainstWorkingDirectory()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory("relative:feed", workspace.WorkspaceRoot);

        Assert.Equal(Path.Combine(workspace.WorkspaceRoot.FullName, "relative:feed"), result);
    }

    [Theory]
    [InlineData("C:/feed")]
    [InlineData("a:/feed")]
    [PlatformSpecific(TestPlatforms.AnyUnix)]
    public void ResolveForWorkingDirectory_DosShapedRelativePath_ResolvesAgainstWorkingDirectory(string source)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workspace.WorkspaceRoot);

        Assert.Equal(Path.Combine(workspace.WorkspaceRoot.FullName, source), result);
    }

    [Fact]
    public void ResolveForWorkingDirectory_FileUri_ReturnsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "file:///tmp/feed";

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workspace.WorkspaceRoot);

        Assert.Equal(source, result);
    }

    [Fact]
    [PlatformSpecific(TestPlatforms.Windows)]
    public void ResolveForWorkingDirectory_WindowsFullyQualifiedPath_ReturnsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = @"C:\feed";

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workspace.WorkspaceRoot);

        Assert.Equal(source, result);
    }

    [Fact]
    public void SourcesMatch_ResolvesMacOSFilesystemAliases()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Filesystem aliases such as /var -> /private/var are specific to macOS.");

        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var source = workspace.WorkspaceRoot.FullName;
        var canonicalSource = PathNormalizer.ResolveSymlinks(source);
        Assert.NotEqual(source, canonicalSource);

        var result = PackageSourceOverrideMappings.SourcesMatch(source, canonicalSource, new TestEnvironment());

        Assert.True(result);
    }

    [Fact]
    public void SourcesMatch_LocalPathsDifferOnlyByCaseOnLinux_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var upperCaseSource = Path.Combine(workspace.WorkspaceRoot.FullName, "Release");
        var lowerCaseSource = Path.Combine(workspace.WorkspaceRoot.FullName, "release");

        var result = PackageSourceOverrideMappings.SourcesMatch(
            upperCaseSource,
            lowerCaseSource,
            TestEnvironment.CreateLinux());

        Assert.False(result);
    }

    [Fact]
    public void SourcesMatch_MacPathComparisonFollowsFilesystemCasing()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var upperCaseSource = Path.Combine(workspace.WorkspaceRoot.FullName, "Release");
        var lowerCaseSource = Path.Combine(workspace.WorkspaceRoot.FullName, "release");
        Directory.CreateDirectory(upperCaseSource);
        var lowerCasePathResolvesToUpperCaseDirectory = Directory.Exists(lowerCaseSource);
        if (!lowerCasePathResolvesToUpperCaseDirectory)
        {
            Directory.CreateDirectory(lowerCaseSource);
        }

        var result = PackageSourceOverrideMappings.SourcesMatch(
            upperCaseSource,
            lowerCaseSource,
            TestEnvironment.CreateMacOS());

        Assert.Equal(lowerCasePathResolvesToUpperCaseDirectory, result);
    }

    [Fact]
    public void SourcesMatch_HttpPathsDifferOnlyByCase_ReturnsFalse()
    {
        var result = PackageSourceOverrideMappings.SourcesMatch(
            "https://packages.example/feeds/Release/index.json",
            "https://packages.example/feeds/release/index.json",
            TestEnvironment.CreateLinux());

        Assert.False(result);
    }

    [Fact]
    public void SourcesMatch_HttpSchemeAndHostDifferOnlyByCase_ReturnsTrue()
    {
        var result = PackageSourceOverrideMappings.SourcesMatch(
            "HTTPS://PACKAGES.EXAMPLE/feeds/release/index.json",
            "https://packages.example/feeds/release/index.json",
            TestEnvironment.CreateLinux());

        Assert.True(result);
    }

    [Fact]
    public void SourcesMatch_HttpReservedEscapingDiffers_ReturnsFalse()
    {
        var result = PackageSourceOverrideMappings.SourcesMatch(
            "https://packages.example/feeds/aspire%2Frelease/index.json",
            "https://packages.example/feeds/aspire/release/index.json",
            TestEnvironment.CreateLinux());

        Assert.False(result);
    }

    [Fact]
    public void IsSourceMappedForPackage_RelativeLocalSourceRetainsOverrideForRelocatedProject()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configDirectory = workspace.CreateDirectory("config");
        var sourceDirectory = configDirectory.CreateSubdirectory("feeds/configured");
        var configPath = Path.Combine(configDirectory.FullName, "NuGet.Config");
        File.WriteAllText(configPath, """
            <configuration>
              <packageSources>
                <add key="configured" value="feeds/configured" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="configured">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var result = PackageSourceOverrideMappings.IsSourceMappedForPackage(
            sourceDirectory.FullName,
            "Aspire.Hosting.Redis",
            [configPath],
            configDirectory,
            configWillBeRelocated: true,
            new TestEnvironment());

        Assert.False(result);
    }

    [Fact]
    public void IsSourceMappedForPackage_HigherPriorityClearDisablesMapping()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "https://configured.example/v3/index.json";
        var globalConfigPath = Path.Combine(workspace.CreateDirectory("global").FullName, "NuGet.Config");
        File.WriteAllText(globalConfigPath, $$"""
            <configuration>
              <packageSources>
                <add key="configured" value="{{source}}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="configured">
                  <package pattern="Other*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var projectDirectory = workspace.CreateDirectory("project");
        var localConfigPath = Path.Combine(projectDirectory.FullName, "NuGet.Config");
        File.WriteAllText(localConfigPath, """
            <configuration>
              <packageSourceMapping>
                <clear />
              </packageSourceMapping>
            </configuration>
            """);

        var result = PackageSourceOverrideMappings.IsSourceMappedForPackage(
            source,
            "Aspire.Hosting.Redis",
            [localConfigPath, globalConfigPath],
            projectDirectory,
            configWillBeRelocated: false,
            new TestEnvironment());

        Assert.True(result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IsSourceMappedForPackage_ExpandsEnvironmentVariablesInPackageSources(bool configWillBeRelocated)
    {
        const string sourceVariable = "ASPIRE_CLI_TEST_CONFIGURED_SOURCE";
        const string source = "https://configured.example/v3/index.json";
        using var environmentVariable = new EnvVarOverride(sourceVariable, source);
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        File.WriteAllText(configPath, $$"""
            <configuration>
              <packageSources>
                <add key="configured" value="%{{sourceVariable}}%" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="configured">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var result = PackageSourceOverrideMappings.IsSourceMappedForPackage(
            source,
            "Aspire.Hosting.Redis",
            [configPath],
            workspace.WorkspaceRoot,
            configWillBeRelocated,
            new TestEnvironment());

        Assert.True(result);
    }

    [Fact]
    public void IsSourceMappedForPackage_HigherPriorityRemoveDisablesRemovedSourceMapping()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "https://configured.example/v3/index.json";
        var globalConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        File.WriteAllText(globalConfigPath, $$"""
            <configuration>
              <packageSources>
                <add key="removed" value="{{source}}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="removed">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var projectDirectory = workspace.CreateDirectory("project");
        var localConfigPath = Path.Combine(projectDirectory.FullName, "NuGet.Config");
        File.WriteAllText(localConfigPath, $$"""
            <configuration>
              <packageSources>
                <remove key="removed" />
                <add key="active" value="{{source}}" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="active">
                  <package pattern="Other*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var result = PackageSourceOverrideMappings.IsSourceMappedForPackage(
            source,
            "Aspire.Hosting.Redis",
            [localConfigPath, globalConfigPath],
            projectDirectory,
            configWillBeRelocated: false,
            new TestEnvironment());

        Assert.False(result);
    }

    [Fact]
    public void IsSourceMappedForPackage_IgnoresDisabledAliasForSameSource()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "https://configured.example/v3/index.json";
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        File.WriteAllText(configPath, $$"""
            <configuration>
              <packageSources>
                <add key="enabled" value="{{source}}" />
                <add key="disabled" value="{{source}}" />
              </packageSources>
              <disabledPackageSources>
                <add key="disabled" value="true" />
              </disabledPackageSources>
              <packageSourceMapping>
                <packageSource key="enabled">
                  <package pattern="Other*" />
                </packageSource>
                <packageSource key="disabled">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var result = PackageSourceOverrideMappings.IsSourceMappedForPackage(
            source,
            "Aspire.Hosting.Redis",
            [configPath],
            workspace.WorkspaceRoot,
            configWillBeRelocated: false,
            new TestEnvironment());

        Assert.False(result);
    }

    [Fact]
    public void IsSourceMappedForPackage_MultipleLocalConfigsRetainsOverride()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "https://configured.example/v3/index.json";
        var parentConfigPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        File.WriteAllText(parentConfigPath, $$"""
            <configuration>
              <packageSources>
                <add key="configured" value="{{source}}" />
              </packageSources>
            </configuration>
            """);
        var projectDirectory = workspace.CreateDirectory("project");
        var localConfigPath = Path.Combine(projectDirectory.FullName, "NuGet.Config");
        File.WriteAllText(localConfigPath, """
            <configuration>
              <packageSourceMapping>
                <packageSource key="configured">
                  <package pattern="Aspire*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>
            """);

        var result = PackageSourceOverrideMappings.IsSourceMappedForPackage(
            source,
            "Aspire.Hosting.Redis",
            [localConfigPath, parentConfigPath],
            projectDirectory,
            configWillBeRelocated: true,
            new TestEnvironment());

        Assert.False(result);
    }

    [Fact]
    public void IsSourceMappedForPackage_AppliesClearInDocumentOrder()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "https://configured.example/v3/index.json";
        var configPath = Path.Combine(workspace.WorkspaceRoot.FullName, "NuGet.Config");
        File.WriteAllText(configPath, $$"""
            <configuration>
              <PackageSources>
                <Add Key="stale" Value="{{source}}" />
                <Clear />
                <Add Key="active" Value="https://active.example/v3/index.json" />
              </PackageSources>
              <PackageSourceMapping>
                <PackageSource Key="stale">
                  <Package Pattern="Aspire*" />
                </PackageSource>
                <Clear />
                <PackageSource Key="active">
                  <Package Pattern="Other*" />
                </PackageSource>
              </PackageSourceMapping>
            </configuration>
            """);

        var result = PackageSourceOverrideMappings.IsSourceMappedForPackage(
            source,
            "Aspire.Hosting.Redis",
            [configPath],
            workspace.WorkspaceRoot,
            configWillBeRelocated: false,
            new TestEnvironment());

        Assert.False(result);
    }
}
