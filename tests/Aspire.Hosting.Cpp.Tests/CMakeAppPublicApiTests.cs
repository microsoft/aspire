// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMMAND001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Cpp.Tests;

public class CMakeAppPublicApiTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorCMakeAppResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;

        var action = () => new CMakeAppResource(name, "/src/cpp-api", "/src/cpp-api/build", "api", "/src/cpp-api/build/api", "/src/cpp-api/build/bin");

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorCMakeAppResourceShouldThrowWhenTargetNameIsNullOrEmpty(bool isNull)
    {
        var targetName = isNull ? null! : string.Empty;

        var action = () => new CMakeAppResource("api", "/src/cpp-api", "/src/cpp-api/build", targetName, "/src/cpp-api/build/api", "/src/cpp-api/build/bin");

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(targetName), exception.ParamName);
    }

    [Fact]
    public void AddCMakeAppShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddCMakeApp("api", "/src/cpp-api", "api");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddCMakeAppShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddCMakeApp(name, "/src/cpp-api", "api");

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddCMakeAppShouldThrowWhenSourceDirectoryIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var sourceDirectory = isNull ? null! : string.Empty;

        var action = () => builder.AddCMakeApp("api", sourceDirectory, "api");

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(sourceDirectory), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddCMakeAppShouldThrowWhenTargetNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var targetName = isNull ? null! : string.Empty;

        var action = () => builder.AddCMakeApp("api", "/src/cpp-api", targetName);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(targetName), exception.ParamName);
    }

    [Fact]
    public void AddCMakeAppCreatesExecutableResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api");

        Assert.EndsWith(Path.Combine(".aspire", "cmake", "api", "build", "aspire-bin", GetExecutableFileName("api")), app.Resource.Command);
        Assert.Equal(builder.AppHostDirectory, app.Resource.SourceDirectory);
        Assert.Equal("api", app.Resource.TargetName);
    }

    [Fact]
    public async Task AddCMakeAppDefaultApplicationArgsAreEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Empty(args);
    }

    [Fact]
    public void WithAppArgsShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<CMakeAppResource> builder = null!;

        var action = () => builder.WithAppArgs("--config", "dev.json");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public async Task WithAppArgsPassesArgumentsToApplication()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithAppArgs("--config", "dev.json");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["--config", "dev.json"], args);
    }

    [Fact]
    public void WithRequiredBuildToolAddsRequiredCommandAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithRequiredBuildTool("ninja", "https://ninja-build.org/");

        Assert.True(app.Resource.TryGetAnnotationsOfType<RequiredCommandAnnotation>(out var annotations));
        Assert.Contains(annotations, a => a.Command == "cmake");
        Assert.Contains(annotations, a => a.Command == "ninja" && a.HelpLink == "https://ninja-build.org/");
    }

    [Fact]
    public void WithRequiredBuildToolsAddsAllRequiredCommandAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithRequiredBuildTools(
                new CMakeBuildTool("ninja", "https://ninja-build.org/"),
                new CMakeBuildTool("vcpkg", "https://vcpkg.io/"));

        Assert.True(app.Resource.TryGetAnnotationsOfType<RequiredCommandAnnotation>(out var annotations));
        Assert.Contains(annotations, a => a.Command == "ninja");
        Assert.Contains(annotations, a => a.Command == "vcpkg");
    }

    [Fact]
    public void WithVcpkgShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<CMakeAppResource> builder = null!;

        var action = () => builder.WithVcpkg("/vcpkg");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithVcpkgShouldThrowWhenRootIsEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var vcpkgRoot = string.Empty;

        var action = () => builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithVcpkg(vcpkgRoot);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(vcpkgRoot), exception.ParamName);
    }

    [Fact]
    public async Task WithVcpkgAddsRequiredCommandAndConfigureArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var vcpkgRoot = Path.Combine(builder.AppHostDirectory, "vcpkg");

        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithVcpkg(vcpkgRoot);

        Assert.True(app.Resource.TryGetAnnotationsOfType<RequiredCommandAnnotation>(out var annotations));
        Assert.Contains(annotations, a => a.Command == "vcpkg" && a.HelpLink == "https://vcpkg.io/");

        var configure = app.Resource.Annotations.OfType<CMakeConfigureResourceAnnotation>().Single().ResourceBuilder.Resource;
        var args = await ArgumentEvaluator.GetArgumentListAsync(configure);
        Assert.Contains($"-DCMAKE_TOOLCHAIN_FILE={Path.Combine(vcpkgRoot, "scripts", "buildsystems", "vcpkg.cmake")}", args);
    }

    [Fact]
    public void WithVcpkgDoesNotRequireLocalRootInPublishMode()
    {
        using var outputDir = new TestTempDirectory();
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.Path, step: "publish-manifest");

        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithVcpkg();

        Assert.True(app.Resource.TryGetLastAnnotation<CMakeVcpkgAnnotation>(out _));
    }

    [Fact]
    public void WithCMakeInstallShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<CMakeAppResource> builder = null!;

        var action = () => builder.WithCMakeInstall();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/bin/api")]
    [InlineData("../api")]
    [InlineData("bin/../api")]
    [InlineData(@"C:\api\bin\api.exe")]
    public void WithCMakeInstallShouldThrowWhenExecutableRelativePathIsInvalid(string executableRelativePath)
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var action = () => builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithCMakeInstall(executableRelativePath);

        var exception = Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(executableRelativePath), exception.ParamName);
    }

    [Fact]
    public void WithCMakeInstallUpdatesExecutablePath()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithCMakeInstall("sbin/api");

        Assert.EndsWith(Path.Combine(".aspire", "cmake", "api", "build", "aspire-install", "sbin", "api"), app.Resource.Command);
    }

    [Fact]
    public void WithExecutablePathShouldThrowAfterWithCMakeInstall()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddCMakeApp("api", builder.AppHostDirectory, "api")
            .WithCMakeInstall();
        var action = () => app.WithExecutablePath("/bin/api");

        Assert.Throws<InvalidOperationException>(action);
    }

    private static string GetExecutableFileName(string targetName) =>
        OperatingSystem.IsWindows() ? $"{targetName}.exe" : targetName;
}
