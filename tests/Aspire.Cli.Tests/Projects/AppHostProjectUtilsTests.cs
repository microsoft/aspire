// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Projects;

public sealed class AppHostProjectUtilsTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void IsLikelyAppHost_SdkAttributeWithVersion_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = WriteProject(workspace, "MyHost.csproj", """
            <Project Sdk="Aspire.AppHost.Sdk/9.5.0" />
            """);

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_SdkAttributeWithoutVersion_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = WriteProject(workspace, "MyHost.csproj", """
            <Project Sdk="Aspire.AppHost.Sdk" />
            """);

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_SdkAttributeWithMultipleSdks_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = WriteProject(workspace, "MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk;Aspire.AppHost.Sdk/9.5.0" />
            """);

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_NestedSdkElement_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = WriteProject(workspace, "MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
            </Project>
            """);

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_NestedSdkElementInLegacyNamespace_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // Legacy MSBuild XML namespace projects should be matched the same as SDK-style ones.
        var projectFile = WriteProject(workspace, "MyHost.csproj", """
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <Sdk Name="Aspire.AppHost.Sdk" Version="9.5.0" />
            </Project>
            """);

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_IsAspireHostPropertyTrue_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = WriteProject(workspace, "MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsAspireHost>true</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_IsAspireHostPropertyFalse_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var projectFile = WriteProject(workspace, "MyHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <IsAspireHost>false</IsAspireHost>
              </PropertyGroup>
            </Project>
            """);

        Assert.False(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_ParseableNonAppHostNamedLikeAppHost_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // The file name suggests an AppHost, but the (parseable) content has no Aspire signals.
        // Content is authoritative here, so this must not be a false positive.
        var projectFile = WriteProject(workspace, "Test.AppHost.csproj", """
            <Project Sdk="Microsoft.NET.Sdk" />
            """);

        Assert.False(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_UnparseableCsprojNamedLikeAppHost_FallsBackToNameHeuristic()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // Malformed XML can't be parsed, so the name heuristic ("*AppHost.csproj") is used as a fallback.
        var projectFile = WriteProject(workspace, "Test.AppHost.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"");

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_UnparseableCsprojWithAppHostCsSibling_FallsBackToFolderHeuristic()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // Malformed XML, non-AppHost name, but the folder contains an AppHost.cs file.
        var projectFile = WriteProject(workspace, "Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"");
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "AppHost.cs"), "// app host");

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(projectFile));
    }

    [Fact]
    public void IsLikelyAppHost_SingleFileWithSdkDirective_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceFile = WriteProject(workspace, "apphost.cs", """
            #:sdk Aspire.AppHost.Sdk
            var builder = DistributedApplication.CreateBuilder(args);
            """);

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(sourceFile));
    }

    [Fact]
    public void IsLikelyAppHost_SingleFileWithVersionedSdkDirective_ReturnsTrue()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceFile = WriteProject(workspace, "apphost.cs", """
            #:sdk Aspire.AppHost.Sdk@9.5.0
            var builder = DistributedApplication.CreateBuilder(args);
            """);

        Assert.True(AppHostProjectUtils.IsLikelyAppHost(sourceFile));
    }

    [Fact]
    public void IsLikelyAppHost_SingleFileWithoutSdkDirective_ReturnsFalse()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceFile = WriteProject(workspace, "Program.cs", """
            var builder = WebApplication.CreateBuilder(args);
            """);

        Assert.False(AppHostProjectUtils.IsLikelyAppHost(sourceFile));
    }

    private static FileInfo WriteProject(TemporaryWorkspace workspace, string fileName, string content)
    {
        var path = Path.Combine(workspace.WorkspaceRoot.FullName, fileName);
        File.WriteAllText(path, content);
        return new FileInfo(path);
    }
}
