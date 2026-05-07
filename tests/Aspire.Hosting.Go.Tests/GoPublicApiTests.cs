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

    // ---- WithBuildTags ------------------------------------------------------

    [Fact]
    public void WithBuildTagsShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithBuildTags("netgo");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public async Task WithBuildTagsInjectsTagsFlag()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithBuildTags("netgo", "osusergo");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-tags=netgo,osusergo", "."], args);
    }

    // ---- WithLdFlags --------------------------------------------------------

    [Fact]
    public void WithLdFlagsShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithLdFlags("-s -w");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithLdFlagsShouldThrowWhenFlagsIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var flags = isNull ? null! : string.Empty;

        var action = () => builder.AddGoApp("api", builder.AppHostDirectory).WithLdFlags(flags);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(flags), exception.ParamName);
    }

    [Fact]
    public async Task WithLdFlagsInjectsLdFlagsArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithLdFlags("-X main.version=1.0.0");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-ldflags=-X main.version=1.0.0", "."], args);
    }

    [Fact]
    public async Task WithBuildTagsAndLdFlagsBothPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithBuildTags("netgo")
                         .WithLdFlags("-s -w");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-tags=netgo", "-ldflags=-s -w", "."], args);
    }

    // ---- WithRaceDetector ---------------------------------------------------

    [Fact]
    public void WithRaceDetectorShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithRaceDetector();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public async Task WithRaceDetectorInjectsRaceFlag()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithRaceDetector();

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-race", "."], args);
    }

    [Fact]
    public async Task WithRaceDetectorIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithRaceDetector()
                         .WithRaceDetector();

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-race", "."], args);
    }

    [Fact]
    public async Task WithRaceDetectorAndBuildFlagsTogether()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithRaceDetector()
                         .WithBuildTags("integration")
                         .WithLdFlags("-s -w");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-race", "-tags=integration", "-ldflags=-s -w", "."], args);
    }

    // ---- WithGcFlags --------------------------------------------------------

    [Fact]
    public void WithGcFlagsShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithGcFlags("all=-N -l");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithGcFlagsShouldThrowWhenFlagsIsNullOrEmpty(bool isNull)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var flags = isNull ? null! : string.Empty;

        var action = () => builder.AddGoApp("api", builder.AppHostDirectory).WithGcFlags(flags);

        var exception = isNull
            ? Assert.Throws<ArgumentNullException>(action)
            : Assert.Throws<ArgumentException>(action);
        Assert.Equal(nameof(flags), exception.ParamName);
    }

    [Fact]
    public async Task WithGcFlagsInjectsGcFlagsArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithGcFlags("all=-N -l");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-gcflags=all=-N -l", "."], args);
    }

    [Fact]
    public async Task WithGcFlagsAndOtherFlagsPreserveOrdering()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithBuildTags("netgo")
                         .WithLdFlags("-s -w")
                         .WithGcFlags("all=-N -l");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["run", "-tags=netgo", "-ldflags=-s -w", "-gcflags=all=-N -l", "."], args);
    }

    // ---- WithTidy -----------------------------------------------------------

    [Fact]
    public void WithTidyShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithTidy();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithTidyIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithTidy()
                         .WithTidy();

        // Only one tidy sibling should have been created
        var tidyResources = builder.Resources.Where(r => r.Name == "api-tidy").ToList();
        Assert.Single(tidyResources);
    }

    [Fact]
    public void WithTidyCreatesSiblingResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory).WithTidy();

        Assert.Contains(builder.Resources, r => r.Name == "api-tidy");
    }

    // ---- WithVendor ---------------------------------------------------------

    [Fact]
    public void WithVendorShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithVendor();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithVendorIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory)
               .WithVendor()
               .WithVendor();

        var vendorResources = builder.Resources.Where(r => r.Name == "api-vendor").ToList();
        Assert.Single(vendorResources);
    }

    [Fact]
    public void WithVendorCreatesSiblingResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory).WithVendor();

        Assert.Contains(builder.Resources, r => r.Name == "api-vendor");
    }

    // ---- WithVet -----------------------------------------------------------

    [Fact]
    public void WithVetShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<GoAppResource> builder = null!;

        var action = () => builder.WithVet();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithVetIsIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory)
               .WithVet()
               .WithVet();

        var lintResources = builder.Resources.Where(r => r.Name == "api-vet").ToList();
        Assert.Single(lintResources);
    }

    [Fact]
    public void WithVetCreatesSiblingResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddGoApp("api", builder.AppHostDirectory).WithVet();

        Assert.Contains(builder.Resources, r => r.Name == "api-vet");
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

        Assert.Equal(["--headless=true", "--listen=:2345", "--api-version=2", "debug", "."], args);
    }

    [Fact]
    public async Task WithDelveServerIncludesBuildFlagsWhenPresent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithBuildTags("netgo")
                         .WithLdFlags("-s -w")
                         .WithDelveServer(port: 2345);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["--headless=true", "--listen=:2345", "--api-version=2", "debug", "--build-flags=-tags=netgo -ldflags=-s -w", "."], args);
    }

    [Fact]
    public async Task WithDelveServerPassesAppArgsAfterDoubleDash()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var app = builder.AddGoApp("api", builder.AppHostDirectory)
                         .WithAppArgs("--config", "dev.yaml")
                         .WithDelveServer(port: 2345);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["--headless=true", "--listen=:2345", "--api-version=2", "debug", ".", "--", "--config", "dev.yaml"], args);
    }
}
