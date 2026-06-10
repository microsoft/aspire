// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Go.Tests;

public class GoPublicApiTests
{
    // ---- GoAppResource constructor guards ------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CtorGoAppResourceShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        var name = isNull ? null! : string.Empty;
        const string workingDirectory = "/src/go-app";

        var action = () => new GoAppResource(name, workingDirectory);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Fact]
    public void CtorGoAppResourceShouldThrowWhenWorkingDirectoryIsNull()
    {
        const string name = "api";

        var action = () => new GoAppResource(name, workingDirectory: null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("workingDirectory", exception.ParamName);
    }

    // ---- AddGoApp guards ----------------------------------------------------

    [Fact]
    public void AddGoAppShouldThrowWhenBuilderIsNull()
    {
        IDistributedApplicationBuilder builder = null!;

        var action = () => builder.AddGoApp("api", "/src/go-app");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddGoAppShouldThrowWhenNameIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var name = isNull ? null! : string.Empty;

        var action = () => builder.AddGoApp(name, "/src/go-app");

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(name), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AddGoAppShouldThrowWhenAppDirectoryIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var appDirectory = isNull ? null! : string.Empty;

        var action = () => builder.AddGoApp("api", appDirectory);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(appDirectory), exception.ParamName);
    }

    // ---- AddGoApp behaviour -------------------------------------------------

    [Fact]
    public void AddGoAppUsesGoAsCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var app = builder.AddGoApp("api", builder.AppHostDirectory);

        Assert.Equal("go", app.Resource.Command);
    }

    [Fact]
    public async Task AddGoAppDefaultArgsAreRunDot()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "."], args);
    }

    // ---- packagePath --------------------------------------------------------

    [Fact]
    public async Task AddGoApp_PackagePath_DefaultsToRunDot()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "."], args);
    }

    [Fact]
    public async Task AddGoApp_PackagePath_UsedInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory, packagePath: "./cmd/server");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "./cmd/server"], args);
    }

    [Fact]
    public async Task AddGoApp_PackagePath_UsedInDelveMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory, packagePath: "./cmd/server")
                         .WithDelveServer(port: 2345);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["--headless=true", "--listen=127.0.0.1:2345", "--api-version=2", "debug", "./cmd/server"], args);
    }

    [Fact]
    public async Task AddGoApp_PackagePath_CombinedWithBuildFlags()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory,
                         packagePath: "./cmd/server",
                         buildTags: ["netgo"],
                         raceDetector: true);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-race", "-tags=netgo", "./cmd/server"], args);
    }

    [Fact]
    public async Task AddGoApp_BuildTagsParam_InjectsTagsFlag()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory, buildTags: ["netgo", "osusergo"]);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-tags=netgo,osusergo", "."], args);
    }

    [Fact]
    public async Task AddGoApp_LdFlagsParam_InjectsLdFlagsArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory, ldFlags: "-X main.version=1.0.0");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-ldflags=-X main.version=1.0.0", "."], args);
    }

    [Fact]
    public async Task AddGoApp_GcFlagsParam_InjectsGcFlagsArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory, gcFlags: "all=-N -l");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-gcflags=all=-N -l", "."], args);
    }

    [Fact]
    public async Task AddGoApp_RaceDetectorParam_InjectsRaceFlag()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory, raceDetector: true);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-race", "."], args);
    }

    [Fact]
    public async Task AddGoApp_AllBuildParams_ProduceCorrectOrdering()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory,
            buildTags: ["netgo"],
            ldFlags: "-s -w",
            gcFlags: "all=-N -l",
            raceDetector: true);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // Ordering: run -race -tags=... -ldflags=... -gcflags=... .
        Assert.Equal(["run", "-race", "-tags=netgo", "-ldflags=-s -w", "-gcflags=all=-N -l", "."], args);
    }

    // ---- WithAppArgs --------------------------------------------------------

    [Fact]
    public void WithAppArgsShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithAppArgs("--port", "9090");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public async Task WithAppArgsPassesArgsAfterDot()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithAppArgs("--port", "9090");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", ".", "--port", "9090"], args);
    }

    [Fact]
    public void WithAppArgs_AcceptsReferenceExpression()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        // A parameter whose value is a ReferenceExpression — no container needed.
        var param = builder.AddParameter("db-url");
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithAppArgs("--db", param.Resource);

        // The annotation must store the object[] including the ReferenceExpression — not
        // just strings — so that it is resolved lazily by the args callback at runtime.
        Assert.True(app.Resource.TryGetLastAnnotation<GoAppArgsAnnotation>(out var annotation));
        Assert.Equal(2, annotation!.Args.Length);
        Assert.Equal("--db", annotation.Args[0]);
        Assert.Same(param.Resource, annotation.Args[1]);
    }

    [Fact]
    public async Task WithAppArgsReplacesOnSecondCall()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithAppArgs("--port", "8080")
                         .WithAppArgs("--port", "9090");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // Last call wins
        Assert.Equal(["run", ".", "--port", "9090"], args);
    }

    // ---- WithModTidy --------------------------------------------------------

    [Fact]
    public void WithModTidyShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithModTidy();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithModTidyIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithModTidy()
                         .WithModTidy();

        // Only one tidy sibling should have been created
        var tidyResources = builder.Resources.Where(r => r.Name == "api-mod-tidy").ToList();
        Assert.Single(tidyResources);
    }

    [Fact]
    public void WithModTidyCreatesSiblingResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory).WithModTidy();

        Assert.Contains(builder.Resources, r => r.Name == "api-mod-tidy");
    }

    // ---- WithModVendor ------------------------------------------------------

    [Fact]
    public void WithModVendorShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithModVendor();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithModVendorIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory)
               .WithModVendor()
               .WithModVendor();

        var vendorResources = builder.Resources.Where(r => r.Name == "api-mod-vendor").ToList();
        Assert.Single(vendorResources);
    }

    [Fact]
    public void WithModVendorCreatesSiblingResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory).WithModVendor();

        Assert.Contains(builder.Resources, r => r.Name == "api-mod-vendor");
    }

    // ---- WithModDownload ----------------------------------------------------

    [Fact]
    public void WithModDownloadShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithModDownload();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithModDownloadIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory)
               .WithModDownload()
               .WithModDownload();

        var downloadResources = builder.Resources.Where(r => r.Name == "api-mod-download").ToList();
        Assert.Single(downloadResources);
    }

    [Fact]
    public void WithModDownloadCreatesSiblingResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory).WithModDownload();

        Assert.Contains(builder.Resources, r => r.Name == "api-mod-download");
    }

    // ---- WithVetTool --------------------------------------------------------

    [Fact]
    public void WithVetToolShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithVetTool();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithVetToolIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory)
               .WithVetTool()
               .WithVetTool();

        var vetResources = builder.Resources.Where(r => r.Name == "api-vet-tool").ToList();
        Assert.Single(vetResources);
    }

    [Fact]
    public void WithVetToolCreatesSiblingResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory).WithVetTool();

        Assert.Contains(builder.Resources, r => r.Name == "api-vet-tool");
    }

    // ---- WithDelveServer ----------------------------------------------------

    [Fact]
    public void WithDelveServerShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithDelveServer();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithDelveServerSwitchesCommandToDlv()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithDelveServer(port: 2345);

        Assert.Equal("dlv", app.Resource.Command);
    }

    [Fact]
    public async Task WithDelveServerProducesCorrectArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithDelveServer(port: 2345);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["--headless=true", "--listen=127.0.0.1:2345", "--api-version=2", "debug", "."], args);
    }

    [Fact]
    public async Task WithDelveServerIncludesBuildFlagsWhenPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory,
                            buildTags: ["netgo"],
                            ldFlags: "-s -w")
                         .WithDelveServer(port: 2345);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // All user-influenced values are shell-quoted so the Delve parser keeps them as single tokens.
        Assert.Equal(["--headless=true", "--listen=127.0.0.1:2345", "--api-version=2", "debug", "--build-flags=-tags='netgo' -ldflags='-s -w'", "."], args);
    }

    [Fact]
    public async Task WithDelveServerPassesAppArgsAfterDoubleDash()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithAppArgs("--config", "dev.yaml")
                         .WithDelveServer(port: 2345);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["--headless=true", "--listen=127.0.0.1:2345", "--api-version=2", "debug", ".", "--", "--config", "dev.yaml"], args);
    }
}
