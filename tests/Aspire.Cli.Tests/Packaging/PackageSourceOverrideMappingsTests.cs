// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Packaging;

namespace Aspire.Cli.Tests.Packaging;

public class PackageSourceOverrideMappingsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void HasCredentialMaterial_MalformedHttpSource_FailsClosed()
    {
        const string source = "https://user:p#word@host/";

        Assert.True(PackageSourceOverrideMappings.HasCredentialMaterial(source));
        var mappings = PackageSourceOverrideMappings.Create(source, requestedChannel: null, nugetServiceIndexOverride: source);
        Assert.Equal(2, mappings.Length);
        Assert.All(mappings, mapping => Assert.Equal(source, mapping.Source));
    }

    [Theory]
    [InlineData("******packages.example.com/v3/index.json?sig=secret", true)]
    [InlineData("******packages.example.com/v3/index.json", false)]
    public void HasCredentialMaterial_RecognizesNuGetMaskedSourceShapes(string source, bool expected)
    {
        Assert.Equal(expected, PackageSourceOverrideMappings.HasCredentialMaterial(source));
    }

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
    public void ResolveForWorkingDirectory_MalformedHttpSource_ReturnsUnchanged()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        const string source = "https://user:p#word@packages.example.com/v3/index.json";

        var result = PackageSourceOverrideMappings.ResolveForWorkingDirectory(source, workspace.WorkspaceRoot);

        Assert.Equal(source, result);
        Assert.True(PackageSourceOverrideMappings.HasCredentialMaterial(result));
        Assert.Null(PackageSourceOverrideMappings.GetMissingLocalDirectory(result));
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
}
