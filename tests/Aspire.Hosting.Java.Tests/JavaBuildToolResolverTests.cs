// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class JavaBuildToolResolverTests
{
    [Theory]
    [InlineData(nameof(JavaBuildTool.Maven), false, "mvnw")]
    [InlineData(nameof(JavaBuildTool.Maven), true, "mvnw.cmd")]
    [InlineData(nameof(JavaBuildTool.Gradle), false, "gradlew")]
    [InlineData(nameof(JavaBuildTool.Gradle), true, "gradlew.bat")]
    public void ResolveWrapperPath_UsesTheRequestedPlatformsDefault(
        string toolName,
        bool isWindows,
        string expectedWrapperName)
    {
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);
        var resource = new JavaAppResource("api", tempDir.Path);
        var tool = Enum.Parse<JavaBuildTool>(toolName);

        var wrapperPath = JavaBuildToolResolver.ResolveWrapperPath(resource, tool, isWindows);

        Assert.Equal(Path.GetFullPath(Path.Combine(tempDir.Path, expectedWrapperName)), wrapperPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResolveWrapperPath_UsesWithWrapperPathOnEveryPlatform(bool isWindows)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);
        var app = builder.AddJavaApp("api", tempDir.Path).WithWrapperPath("tools/custom-wrapper");

        var wrapperPath = JavaBuildToolResolver.ResolveWrapperPath(app.Resource, JavaBuildTool.Maven, isWindows);

        Assert.Equal(Path.GetFullPath(Path.Combine(tempDir.Path, "tools", "custom-wrapper")), wrapperPath);
    }

    [Fact]
    public void ResolveWrapperPath_UsesAnAncestorWrapperAtTheBuildRoot()
    {
        // A Maven multi-module repository keeps one mvnw next to the aggregator POM, so a resource
        // pointed at a module has to find it there.
        using var root = new TempJavaBuildRootDirectory();
        var module = root.CreateModule("catalog");
        var resource = new JavaAppResource("catalog", module);

        var wrapperPath = JavaBuildToolResolver.ResolveWrapperPath(resource, JavaBuildTool.Maven, isWindows: false);

        Assert.Equal(Path.GetFullPath(Path.Combine(root.RootPath, "mvnw")), wrapperPath);
    }
}

/// <summary>
/// A Maven multi-module layout: an aggregator POM and wrapper at the root, modules beneath it, and no
/// <c>.git</c>, so the ancestor walk is not stopped by a checkout boundary.
/// </summary>
internal sealed class TempJavaBuildRootDirectory : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aspire-java-build-root");

    public TempJavaBuildRootDirectory()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "pom.xml"), "<project/>");
        File.WriteAllText(Path.Combine(_root.FullName, "mvnw"), "#!/bin/sh\n");
    }

    public string RootPath => _root.FullName;

    public string CreateModule(string name)
    {
        var module = _root.CreateSubdirectory(name);
        File.WriteAllText(Path.Combine(module.FullName, "pom.xml"), "<project/>");

        return module.FullName;
    }

    public void Dispose() => _root.Delete(recursive: true);
}
