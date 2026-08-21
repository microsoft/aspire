// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;
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
}
