// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.Rust.Tests;

public class AddRustAppPublishTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task VerifyPublish_DefaultsToAMuslBuildAndRuntimePair()
    {
        var content = await PublishDockerfileAsync();

        Assert.StartsWith("FROM docker.io/library/rust:1.97-alpine3.24 AS build", content);
        Assert.Contains("\nFROM docker.io/library/alpine:3.24\n", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_LeavesAPinnedToolchainToRustupInsideTheImage()
    {
        // A pinned toolchain deliberately does not change the build image. rustup is present in the official
        // image and installs whatever the toolchain file names, so the pin is honoured inside the container
        // without the host having to map channel names onto image tags.
        var content = await PublishDockerfileAsync(configureSource: source =>
            File.WriteAllText(Path.Combine(source, "rust-toolchain.toml"), """
                [toolchain]
                channel = "1.89.0"
                """));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursWithCargoBinTarget()
    {
        var content = await PublishDockerfileAsync(
            metadata: CargoMetadataFactory.SinglePackage("my-service", extraBins: ["worker"]),
            configureResource: app => app.WithCargoBinTarget("worker"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursWithCargoExample()
    {
        // Examples land in target/<profile>/examples/, so the COPY --from path gets an extra segment.
        var content = await PublishDockerfileAsync(configureResource: app => app.WithCargoExample("demo"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursWithCargoProfile()
    {
        // A custom profile writes to target/<profile>/, so the COPY --from path must follow it rather than
        // assuming target/release.
        var content = await PublishDockerfileAsync(configureResource: app => app.WithCargoProfile("dist"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursWithCargoReleaseBuild()
    {
        var content = await PublishDockerfileAsync(configureResource: app => app.WithCargoReleaseBuild());

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursWithCargoTarget()
    {
        var content = await PublishDockerfileAsync(configureResource: app => app.WithCargoTarget("aarch64-unknown-linux-musl"));

        await Verify(content);
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-musl", ContainerTargetPlatform.LinuxAmd64, false, false)]
    [InlineData("aarch64-unknown-linux-musl", ContainerTargetPlatform.LinuxArm64, false, false)]
    [InlineData("x86_64-unknown-linux-gnu", ContainerTargetPlatform.LinuxAmd64, true, true)]
    [InlineData("aarch64-unknown-linux-gnu", ContainerTargetPlatform.LinuxArm64, true, true)]
    [InlineData("arm-unknown-linux-musleabi", ContainerTargetPlatform.LinuxArm, true, false)]
    [InlineData("arm-unknown-linux-musleabihf", ContainerTargetPlatform.LinuxArm, true, false)]
    [InlineData("armv7-unknown-linux-musleabi", ContainerTargetPlatform.LinuxArm, true, false)]
    [InlineData("armv7-unknown-linux-musleabihf", ContainerTargetPlatform.LinuxArm, true, false)]
    [InlineData("armv7-unknown-linux-gnueabi", ContainerTargetPlatform.LinuxArm, true, true)]
    [InlineData("armv7-unknown-linux-gnueabihf", ContainerTargetPlatform.LinuxArm, true, true)]
    [InlineData("i686-unknown-linux-musl", ContainerTargetPlatform.Linux386, true, false)]
    [InlineData("i686-unknown-linux-gnu", ContainerTargetPlatform.Linux386, true, true)]
    public async Task PublishMapsSupportedCargoTargetsToTheContainerPlatform(
        string target,
        ContainerTargetPlatform expectedPlatform,
        bool useCustomBuildImage,
        bool useCustomRuntimeImage)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        var rust = builder.AddRustApp("api", sourceDir.FullName).WithCargoTarget(target);
        if (useCustomBuildImage || useCustomRuntimeImage)
        {
            rust.WithDockerfileBaseImage(
                buildImage: useCustomBuildImage ? "example.invalid/rust-cross-build:latest" : null,
                runtimeImage: useCustomRuntimeImage ? "example.invalid/cross-runtime:latest" : null);
        }

        using var app = builder.Build();
        app.Run();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var logger = app.Services.GetRequiredService<ILogger<AddRustAppPublishTests>>();
        var buildOptions = await container.ProcessContainerBuildOptionsCallbackAsync(
            app.Services,
            logger,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(expectedPlatform, buildOptions.TargetPlatform);
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("armv7-unknown-linux-gnueabihf")]
    [InlineData("i686-unknown-linux-gnu")]
    public async Task PublishRejectsATargetThatNeedsBothCustomImages(string target)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName).WithCargoTarget(target);
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The Rust app 'api' targets '{target}', which is not compatible with the default " +
            "musl build and runtime images. Configure both images in a single " +
            "WithDockerfileBaseImage(buildImage: ..., runtimeImage: ...) call before publishing; later calls replace " +
            "the previous configuration.",
            exception.Message);
    }

    [Theory]
    [InlineData("armv7-unknown-linux-musleabihf", ContainerTargetPlatform.LinuxArm)]
    [InlineData("i686-unknown-linux-musl", ContainerTargetPlatform.Linux386)]
    public async Task PublishRejectsA32BitTargetWithoutACustomBuildImage(
        string target,
        ContainerTargetPlatform targetPlatform)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName).WithCargoTarget(target);
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            $"The Rust app 'api' targets '{target}', but the default Rust build image does not support container " +
            $"target platform '{targetPlatform}'. Configure buildImage with WithDockerfileBaseImage before publishing.",
            exception.Message);
    }

    [Fact]
    public async Task PublishRejectsSplitBaseImageCallsForANonMuslTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName)
            .WithCargoTarget("x86_64-unknown-linux-gnu")
            .WithDockerfileBaseImage(buildImage: "docker.io/library/rust:1.97.1-bookworm")
            .WithDockerfileBaseImage(runtimeImage: "docker.io/library/debian:bookworm-slim");
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "The Rust app 'api' targets 'x86_64-unknown-linux-gnu', which is not compatible with the default " +
            "musl build and runtime images. Configure both images in a single " +
            "WithDockerfileBaseImage(buildImage: ..., runtimeImage: ...) call before publishing; later calls replace " +
            "the previous configuration.",
            exception.Message);
    }

    [Fact]
    public async Task PublishRejectsATargetThatCannotMapToAContainerPlatformEvenWithCustomImages()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName)
            .WithCargoTarget("wasm32-wasip1")
            .WithDockerfileBaseImage(
                buildImage: "example.invalid/custom-rust-build:latest",
                runtimeImage: "example.invalid/custom-runtime:latest");
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.RunAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "The Rust app 'api' targets 'wasm32-wasip1', which cannot be mapped to a supported Docker Linux " +
            "container platform. Generated Rust containers support native Linux x86_64, aarch64, 32-bit ARM, " +
            "and 32-bit x86 targets. Use an authored Dockerfile to publish this target.",
            exception.Message);
    }

    [Fact]
    public async Task PublishRejectsAContainerPlatformThatConflictsWithTheCargoTarget()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName)
            .WithCargoTarget("aarch64-unknown-linux-musl")
            .PublishAsDockerFile(container => container.WithContainerBuildOptions(
                context => context.TargetPlatform = ContainerTargetPlatform.LinuxAmd64));

        using var app = builder.Build();
        app.Run();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var logger = app.Services.GetRequiredService<ILogger<AddRustAppPublishTests>>();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => container.ProcessContainerBuildOptionsCallbackAsync(
                app.Services,
                logger,
                cancellationToken: TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(
            "The Rust app 'api' targets 'aarch64-unknown-linux-musl', which requires container target platform " +
            "'LinuxArm64', but WithContainerBuildOptions selected 'LinuxAmd64'.",
            exception.Message);
    }

    [Fact]
    public void PublishingPreservesACallerPipelineStepAnnotation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");
        var pipelineSteps = new PipelineStepAnnotation(_ => Array.Empty<PipelineStep>());

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName)
            .PublishAsDockerFile(container => container.WithAnnotation(
                pipelineSteps,
                ResourceAnnotationMutationBehavior.Replace));
        builder.Build().Run();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());

        Assert.Same(pipelineSteps, Assert.Single(container.Annotations.OfType<PipelineStepAnnotation>()));
    }

    [Fact]
    public async Task PublishMakesTheRustImageBuildDependOnEachContainerFilesSource()
    {
        // The generated Dockerfile copies out of each container files source, so those sources have to be
        // built first; otherwise the COPY --from reads an image that does not exist yet.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));

        var frontend = builder.AddResource(new RustFilesContainer("frontend"))
            .WithImage("frontend-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });
        var assets = builder.AddResource(new RustFilesContainer("assets"))
            .WithImage("assets-image")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/public" });
        var rust = builder.AddRustApp("api", sourceDir.FullName);
        rust.PublishWithContainerFiles(frontend, "/app/static");
        rust.PublishWithContainerFiles(assets, "/app/public");

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var rustBuildStep = CreateBuildComputeStep("build-api", rust.Resource);
        var frontendBuildStep = CreateBuildComputeStep("build-frontend", frontend.Resource);
        var assetsBuildStep = CreateBuildComputeStep("build-assets", assets.Resource);

        // Run every configuration callback the pipeline would, from the model rather than from the Rust
        // resource: PublishAsDockerFile removes that resource, so this also proves the annotation is still
        // reachable through the container substituted in its place.
        foreach (var annotation in model.Resources.SelectMany(resource => resource.Annotations.OfType<PipelineConfigurationAnnotation>()))
        {
            await annotation.Callback(new PipelineConfigurationContext
            {
                Services = app.Services,
                Model = model,
                Steps = [rustBuildStep, frontendBuildStep, assetsBuildStep]
            });
        }

        Assert.Equal(["build-frontend", "build-assets"], rustBuildStep.DependsOnSteps);
        Assert.Equal([], frontendBuildStep.DependsOnSteps);
        Assert.Equal([], assetsBuildStep.DependsOnSteps);
    }

    private static PipelineStep CreateBuildComputeStep(string name, IResource resource)
        => new()
        {
            Name = name,
            Action = static _ => Task.CompletedTask,
            Tags = [WellKnownPipelineTags.BuildCompute],
            Resource = resource
        };

    #pragma warning restore ASPIREPIPELINES003

    [Fact]
    public async Task VerifyPublish_ClearsStaleArtifactsBeforeACustomTargetProfileBuild()
    {
        var content = await PublishDockerfileAsync(
            configureResource: app => app
                .WithCargoExample("demo")
                .WithCargoProfile("dist")
                .WithCargoTarget("aarch64-unknown-linux-musl"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursWithDockerfileBaseImage()
    {
        var content = await PublishDockerfileAsync(
            configureResource: app => app.WithDockerfileBaseImage(buildImage: "rust:1.89-bookworm", runtimeImage: "debian:bookworm-slim"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_ForwardsCargoFeaturesAndRawArgs()
    {
        // Regression: the publish build previously hard-coded `cargo build --release`, dropping every
        // configured cargo argument, so a crate needing --no-default-features published a binary that
        // differed from the one that ran locally.
        var content = await PublishDockerfileAsync(
            configureResource: app => app.WithCargoFeatures("grpc-tonic", "tls-ring").WithCargoArgs("--no-default-features", "--locked"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_ShellQuotesArgumentsWithMetacharacters()
    {
        var content = await PublishDockerfileAsync(
            configureResource: app => app.WithCargoArgs("--config", "build.rustflags=[\"--cfg\", 'has_quote']"));

        await Verify(content);
    }

    [Theory]
    [InlineData("--config", "registries.private.token=\"private-registry-token\"")]
    [InlineData("--config=env.PGPASSWORD=\"database-password\"", null)]
    [InlineData("--config", "registries.private.index=\"https://user:registry-password@example.invalid/index\"")]
    public async Task PublishRejectsCredentialBearingCargoConfigArguments(string argument, string? value)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        var rust = builder.AddRustApp("api", sourceDir.FullName);
        rust.WithCargoArgs(value is null ? [argument] : [argument, value]);

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CompletionState.CompletedWithError, reporter.ResultCompletionState);
        Assert.Equal(
            "The Rust app 'api' has a Cargo --config argument that may contain credentials. " +
            "Generated Dockerfiles cannot embed credentials; use a hand-written Dockerfile with a BuildKit secret mount instead.",
            reporter.CompletionMessage);
        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Theory]
    [InlineData("raw-argument", "cargo argument", "U+000A")]
    [InlineData("nul-argument", "cargo argument", "U+0000")]
    [InlineData("feature", "cargo argument", "U+000D")]
    [InlineData("manifest-path", "cargo manifest path", "U+000A")]
    [InlineData("binary-path", "resolved Cargo target executable name", "U+001B")]
    [InlineData("build-image", "Dockerfile build image", "U+000A")]
    [InlineData("runtime-image", "Dockerfile runtime image", "U+000D")]
    [InlineData("resolved-executable", "resolved Cargo target executable name", "U+000A")]
    [InlineData("profile-directory-after-args-replaced", "resolved Cargo profile directory", "U+000D")]
    public async Task PublishRejectsDockerfileValuesContainingControlCharacters(
        string valueKind,
        string valueDescription,
        string codePoint)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        var reporter = new TestPipelineActivityReporter(outputHelper);
        builder.Services.AddSingleton<IPipelineActivityReporter>(reporter);
        var metadata = valueKind == "resolved-executable"
            ? CargoMetadataFactory.SinglePackage("my\\u000aservice")
            : CargoMetadataFactory.SinglePackage("my-service");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(metadata));
        var rust = builder.AddRustApp("api", sourceDir.FullName);

        _ = valueKind switch
        {
            "raw-argument" => rust.WithCargoArgs("--config\nFROM scratch"),
            "nul-argument" => rust.WithCargoArgs("--config\0FROM scratch"),
            "feature" => rust.WithCargoFeatures("safe\rRUN echo injected"),
            "manifest-path" => rust.WithCargoManifestPath("Cargo.toml\nFROM scratch"),
            "binary-path" => rust.WithCargoBinTarget("api\u001b"),
            "build-image" => rust.WithDockerfileBaseImage(buildImage: "rust:1.97\nFROM scratch"),
            "runtime-image" => rust.WithDockerfileBaseImage(runtimeImage: "alpine:3.24\rRUN echo injected"),
            "resolved-executable" => rust,
            "profile-directory-after-args-replaced" => rust
                .WithCargoProfile("dist\r\nRUN echo injected")
                .WithCargoArgs(context => context.Args.Clear()),
            _ => throw new ArgumentOutOfRangeException(nameof(valueKind))
        };

        using var app = builder.Build();
        await app.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(CompletionState.CompletedWithError, reporter.ResultCompletionState);
        Assert.Equal(
            $"The Rust app 'api' has a {valueDescription} containing the control character {codePoint}. " +
            "Control characters cannot be written to a generated Dockerfile.",
            reporter.CompletionMessage);
        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Fact]
    public async Task VerifyPublish_SelectsAWorkspacePackage()
    {
        var content = await PublishDockerfileAsync(
            metadata: CargoMetadataFactory.Workspace(
                new CargoPackageSpec("api", ["api"]),
                new CargoPackageSpec("worker", ["worker"])),
            configureResource: app => app.WithCargoPackage("worker"));

        await Verify(content);
    }

    [Fact]
    public void PublishPrefersAHandWrittenDockerfile()
    {
        // A crate that already has a Dockerfile owns its own container build; generating one would silently
        // shadow it.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddRustApp("api", sourceDir.FullName);
        builder.Build().Run();

        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Theory]
    [InlineData("wasm32-wasip1")]
    [InlineData("x86_64-unknown-linux-gnu")]
    public void PublishingLeavesCargoTargetHandlingToAHandWrittenDockerfile(string target)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddRustApp("api", sourceDir.FullName).WithCargoTarget(target);
        builder.Build().Run();

        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Fact]
    public void PublishingPreservesACallerConfiguredDockerfile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var customContext = workspace.CreateDirectory("custom");
        var outputDir = workspace.CreateDirectory("output");
        var customDockerfile = Path.Combine(customContext.FullName, "Dockerfile.prod");

        File.WriteAllText(customDockerfile, "FROM scratch\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddRustApp("api", sourceDir.FullName)
            .PublishAsDockerFile(container => container.WithDockerfile(customContext.FullName, "Dockerfile.prod"));
        builder.Build().Run();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var dockerfile = Assert.Single(container.Annotations.OfType<DockerfileBuildAnnotation>());

        Assert.Equal(customContext.FullName, dockerfile.ContextPath);
        Assert.Equal(customDockerfile, dockerfile.DockerfilePath);
        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Fact]
    public async Task PublishingPreservesACallerConfiguredDockerfileFactory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var customContext = workspace.CreateDirectory("custom");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddRustApp("api", sourceDir.FullName)
            .PublishAsDockerFile(container => container.WithDockerfileFactory(
                customContext.FullName,
                _ => Task.FromResult("FROM scratch\n")));
        builder.Build().Run();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var dockerfile = Assert.Single(container.Annotations.OfType<DockerfileBuildAnnotation>());
        var content = await File.ReadAllTextAsync(
            Path.Combine(outputDir.FullName, "api.Dockerfile"),
            TestContext.Current.CancellationToken);

        Assert.Equal(customContext.FullName, dockerfile.ContextPath);
        Assert.Equal("FROM scratch\n", content);
    }

    [Fact]
    public async Task VerifyPublish_KeepsTheManifestPathRelative()
    {
        // Cargo runs from the app directory inside the image, so a relative manifest path names the same file
        // there as it does on the host and is passed through unchanged.
        var content = await PublishDockerfileAsync(
            configureResource: app => app.WithCargoManifestPath("crates/api/Cargo.toml"));

        await Verify(content);
    }

    [Fact]
    public async Task PublishUsesTheFilesystemCasingForTheManifestPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");
        var manifestDirectory = Directory.CreateDirectory(Path.Combine(sourceDir.FullName, "Crates", "API"));
        File.WriteAllText(Path.Combine(manifestDirectory.FullName, "Cargo.toml"), "[package]\nname = \"api\"\n");

        var differentlyCasedPath = Path.Combine("crates", "api", "cargo.toml");
        if (!File.Exists(Path.Combine(sourceDir.FullName, differentlyCasedPath)))
        {
            Assert.Skip("The test filesystem is case-sensitive.");
        }

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName).WithCargoManifestPath(differentlyCasedPath);
        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);

        Assert.Contains("cargo build --manifest-path Crates/API/Cargo.toml", content);
    }

    [Fact]
    public async Task PublishUsesTheStoredUnicodeNormalizationForTheManifestPath()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");
        var decomposedDirectoryName = "Café".Normalize(NormalizationForm.FormD);
        var composedDirectoryName = decomposedDirectoryName.Normalize(NormalizationForm.FormC);
        var manifestDirectory = Directory.CreateDirectory(Path.Combine(sourceDir.FullName, decomposedDirectoryName));

        File.WriteAllText(Path.Combine(manifestDirectory.FullName, "Cargo.toml"), "[package]\nname = \"api\"\n");

        var composedManifestPath = Path.Combine(composedDirectoryName, "Cargo.toml");
        if (!File.Exists(Path.Combine(sourceDir.FullName, composedManifestPath)))
        {
            Assert.Skip("The test filesystem distinguishes Unicode normalization forms.");
        }

        var storedDirectoryName = Path.GetFileName(Assert.Single(Directory.EnumerateDirectories(sourceDir.FullName)));

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName).WithCargoManifestPath(composedManifestPath);
        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);
        var expectedManifestPath = $"{storedDirectoryName}/Cargo.toml";

        Assert.Contains($"cargo build --manifest-path '{expectedManifestPath}'", content);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task PublishDoesNotEnumerateAncestorsAboveTheBuildContext()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix directory traversal permission regression test.");
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var restrictedAncestor = workspace.CreateDirectory("restricted");
        var sourceDir = Directory.CreateDirectory(Path.Combine(restrictedAncestor.FullName, "source"));
        var manifestDirectory = Directory.CreateDirectory(Path.Combine(sourceDir.FullName, "Crates", "API"));
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(manifestDirectory.FullName, "Cargo.toml"), "[package]\nname = \"api\"\n");

        var originalMode = File.GetUnixFileMode(restrictedAncestor.FullName);
        try
        {
            // Execute permission permits access through a known path, while removing read permission prevents
            // enumerating this ancestor. Resolving a manifest below the build context must not need that listing.
            File.SetUnixFileMode(restrictedAncestor.FullName, UnixFileMode.UserExecute);

            try
            {
                Directory.GetFileSystemEntries(restrictedAncestor.FullName);
                Assert.Skip("The test filesystem still permits enumerating an execute-only directory.");
            }
            catch (UnauthorizedAccessException)
            {
            }

            using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
            builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
            builder.AddRustApp("api", sourceDir.FullName).WithCargoManifestPath("Crates/API/Cargo.toml");
            builder.Build().Run();

            var content = await File.ReadAllTextAsync(
                Path.Combine(outputDir.FullName, "api.Dockerfile"),
                TestContext.Current.CancellationToken);

            Assert.Contains("cargo build --manifest-path Crates/API/Cargo.toml", content);
        }
        finally
        {
            File.SetUnixFileMode(restrictedAncestor.FullName, originalMode);
        }
    }

    [Fact]
    public async Task VerifyPublish_RewritesWindowsSeparatorsInTheManifestPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows manifest separator regression test.");
        }

        // A backslash is an ordinary filename character on Linux rather than a separator, so a manifest path
        // authored on Windows would name a file that does not exist in the image.
        var content = await PublishDockerfileAsync(
            configureResource: app => app.WithCargoManifestPath(@"crates\api\Cargo.toml"));

        await Verify(content);
    }

    [Fact]
    public async Task PublishPreservesBackslashesThatAreFilenameCharactersOnUnix()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix filename regression test.");
        }

        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                var manifestDirectory = Directory.CreateDirectory(Path.Combine(source, @"crates\api"));
                File.WriteAllText(Path.Combine(manifestDirectory.FullName, "Cargo.toml"), "[package]\nname = \"api\"\n");
            },
            configureResource: app => app.WithCargoManifestPath(@"crates\api/Cargo.toml"));

        Assert.Contains("""cargo build --manifest-path 'crates\api/Cargo.toml'""", content);
    }

    [Fact]
    public async Task VerifyPublish_CanonicalizesMacOSAliasesWhenValidatingTheManifestPath()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS filesystem alias regression test.");
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");
        var canonicalSourceDir = TestPathNormalizer.ResolveSymlinks(sourceDir.FullName);

        if (string.Equals(sourceDir.FullName, canonicalSourceDir, StringComparison.Ordinal))
        {
            Assert.Skip("The test temporary directory does not traverse a macOS filesystem alias.");
        }

        // Cargo reports realpath-resolved paths such as /private/var/..., while the app directory can retain
        // the equivalent /var/... spelling. Express the canonical manifest as a relative path from the
        // lexical app directory to reproduce that mismatch without depending on /tmp.
        var canonicalManifest = Path.Combine(canonicalSourceDir, "crates", "api", "Cargo.toml");
        var manifestPath = Path.GetRelativePath(sourceDir.FullName, canonicalManifest);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName).WithCargoManifestPath(manifestPath);
        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);

        await Verify(content);
    }

    [Fact]
    public async Task PublishPreservesManifestCasingAcrossAMacOSFilesystemAlias()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("macOS filesystem alias regression test.");
        }

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");
        var canonicalSourceDir = TestPathNormalizer.ResolveSymlinks(sourceDir.FullName);
        var manifestDirectory = Directory.CreateDirectory(Path.Combine(sourceDir.FullName, "Crates", "API"));

        File.WriteAllText(Path.Combine(manifestDirectory.FullName, "Cargo.toml"), "[package]\nname = \"api\"\n");

        if (string.Equals(sourceDir.FullName, canonicalSourceDir, StringComparison.Ordinal))
        {
            Assert.Skip("The test temporary directory does not traverse a macOS filesystem alias.");
        }

        var differentlyCasedCanonicalManifest = Path.Combine(canonicalSourceDir, "crates", "api", "cargo.toml");
        if (!File.Exists(differentlyCasedCanonicalManifest))
        {
            Assert.Skip("The test filesystem is case-sensitive.");
        }

        var manifestPath = Path.GetRelativePath(sourceDir.FullName, differentlyCasedCanonicalManifest);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName).WithCargoManifestPath(manifestPath);
        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);

        Assert.Contains("cargo build --manifest-path Crates/API/Cargo.toml", content);
    }

    [Fact]
    public void PublishRejectsAnInContextSymlinkThatResolvesOutsideTheBuildContext()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outsideDir = workspace.CreateDirectory("outside");
        var outputDir = workspace.CreateDirectory("output");
        File.WriteAllText(Path.Combine(outsideDir.FullName, "Cargo.toml"), "[package]\nname = \"outside\"\n");
        CreateDirectorySymbolicLinkOrSkip(Path.Combine(sourceDir.FullName, "linked"), outsideDir.FullName);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName).WithCargoManifestPath("linked/Cargo.toml");
        builder.Build().Run();

        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Fact]
    public void PublishFailsClosedWhenAManifestTraversesCircularSymlinks()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");
        var firstLink = Path.Combine(sourceDir.FullName, "linked");
        var secondLink = Path.Combine(sourceDir.FullName, "loop");
        CreateDirectorySymbolicLinkOrSkip(
            firstLink,
            secondLink);
        CreateDirectorySymbolicLinkOrSkip(
            secondLink,
            firstLink);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName).WithCargoManifestPath("linked/Cargo.toml");
        builder.Build().Run();

        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Theory]
    [InlineData("../elsewhere/Cargo.toml")]
    [InlineData("crates/../../elsewhere/Cargo.toml")]
    [InlineData("crates/api/../../../../../../Cargo.toml")]
    [InlineData("../appsuffix/Cargo.toml")]
    public async Task PublishFailsWhenTheManifestIsOutsideTheAppDirectory(string manifestPath)
    {
        // Only the app directory is copied into the image, so a manifest above it could never be built there.
        // The .. segments are collapsed before the path is judged, so an escape buried mid-path is caught too.
        // The publish pipeline reports the failure through the host rather than rethrowing, so the observable
        // result is that no Dockerfile is produced.
        var exception = await Record.ExceptionAsync(
            () => PublishDockerfileAsync(configureResource: app => app.WithCargoManifestPath(manifestPath)));

        Assert.IsType<FileNotFoundException>(exception);
    }

    [Fact]
    public async Task PublishFailsWhenTheManifestPathIsAbsolute()
    {
        // An absolute path is fine when running, but publishing copies only the app directory into the image,
        // and an absolute path can spell that directory differently to the app host.
        var exception = await Record.ExceptionAsync(
            () => PublishDockerfileAsync(
                configureResource: app => app.WithCargoManifestPath(Path.Combine(Path.GetTempPath(), "Cargo.toml"))));

        Assert.IsType<FileNotFoundException>(exception);
    }

    [Fact]
    public async Task VerifyPublish_AddsLockedWhenTheCrateHasALockFile()
    {
        // A committed lock file is the whole point of --locked: the image must build the dependency versions
        // that were reviewed, not whatever resolves at build time.
        var content = await PublishDockerfileAsync(configureSource: source =>
            File.WriteAllText(Path.Combine(source, "Cargo.lock"), "version = 4\n"));

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursOptingOutOfTheLockedAndReleaseDefaults()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => File.WriteAllText(Path.Combine(source, "Cargo.lock"), "version = 4\n"),
            configureResource: app => app.WithCargoLocked(false).WithCargoReleaseBuild(false));

        await Verify(content);
    }

    [Fact]
    public async Task PublishScopesTheTargetCacheToTheCrateDirectory()
    {
        // A BuildKit cache mount id is global to the daemon. Unrelated app hosts commonly both build an `api`
        // resource from `Cargo.toml`, and sharing one Cargo target directory while both source trees appear at
        // /app lets cargo accept the other tree's local-library artifacts as fresh on fingerprint and mtime.
        var first = await PublishDockerfileAsync();
        var second = await PublishDockerfileAsync();

        var firstCacheId = ReadTargetCacheId(first);
        var secondCacheId = ReadTargetCacheId(second);

        Assert.NotEqual(firstCacheId, secondCacheId);

        // The cache identity is the only thing that may differ between checkouts of an equivalent crate.
        Assert.Equal(
            first.Replace(firstCacheId, "aspire-rust-scope", StringComparison.Ordinal),
            second.Replace(secondCacheId, "aspire-rust-scope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PublishReusesTheTargetCacheForTheSameCrateDirectory()
    {
        // Scoping the cache must not defeat it: republishing the same crate has to keep hitting the cargo
        // target directory it filled last time.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var firstOutputDir = workspace.CreateDirectory("output-first");
        var secondOutputDir = workspace.CreateDirectory("output-second");

        foreach (var outputDir in new[] { firstOutputDir, secondOutputDir })
        {
            using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
            builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
            builder.AddRustApp("api", sourceDir.FullName);
            builder.Build().Run();
        }

        var first = await File.ReadAllTextAsync(Path.Combine(firstOutputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);
        var second = await File.ReadAllTextAsync(Path.Combine(secondOutputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);

        Assert.Equal(ReadTargetCacheId(first), ReadTargetCacheId(second));
    }

    [Fact]
    public async Task PublishIsolatesTheTargetCacheByResourceName()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName);
        builder.AddRustApp("worker", sourceDir.FullName);
        builder.Build().Run();

        var first = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);
        var second = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "worker.Dockerfile"), TestContext.Current.CancellationToken);

        Assert.NotEqual(ReadTargetCacheId(first), ReadTargetCacheId(second));
    }

    [Fact]
    public async Task VerifyPublish_DoesNotRepeatLockedWhenTheResourceAlreadyAskedForIt()
    {
        // Run mode already emitted --locked, and passing it twice makes cargo's own error messages confusing.
        var content = await PublishDockerfileAsync(configureResource: app => app.WithCargoLocked());

        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_EmitsABuildContextIgnoreThatExcludesTargetDirectories()
    {
        // `COPY . .` would otherwise upload the crate's local target/ directory, which is routinely several
        // gigabytes and is rebuilt inside the image regardless.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName);
        builder.Build().Run();

        var ignore = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile.dockerignore"), TestContext.Current.CancellationToken);

        await Verify(ignore);
    }

    [Fact]
    public async Task AnAuthoredDockerignoreTakesOverFromTheDefaults()
    {
        // Docker gives <dockerfile>.dockerignore precedence over the context root's .dockerignore instead of
        // merging them, so emitting the defaults would silently discard the crate's own rules.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(sourceDir.FullName, ".dockerignore"), "*.md\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName);
        builder.Build().Run();

        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile.dockerignore")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishingMaterializesTheDockerfileFromTheFinalWorkingDirectory(bool hasDockerfile)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var relocatedDir = workspace.CreateDirectory("relocated");
        var outputDir = workspace.CreateDirectory("output");

        if (hasDockerfile)
        {
            File.WriteAllText(Path.Combine(relocatedDir.FullName, "Dockerfile"), "FROM scratch\n");
        }

        var reader = new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service"));

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(reader);
        builder.AddRustApp("api", sourceDir.FullName).WithWorkingDirectory(relocatedDir.FullName);
        var initialContainer = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var initialDockerfile = Assert.Single(initialContainer.Annotations.OfType<DockerfileBuildAnnotation>());
        initialDockerfile.BuildContextIgnoreContent = "custom-ignore\n";
        builder.Build().Run();

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var dockerfile = Assert.Single(container.Annotations.OfType<DockerfileBuildAnnotation>());

        Assert.Equal(relocatedDir.FullName, dockerfile.ContextPath);
        Assert.Equal("custom-ignore\n", dockerfile.BuildContextIgnoreContent);

        if (hasDockerfile)
        {
            Assert.Equal(Path.Combine(relocatedDir.FullName, "Dockerfile"), dockerfile.DockerfilePath);
            Assert.Equal(0, reader.ReadCount);
        }
        else
        {
            Assert.Equal(relocatedDir.FullName, reader.LastWorkingDirectory);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PublishBoundsTheLockFileSearchToAnAppDirectoryWithATrailingSeparator(bool lockFileInsideAppDirectory)
    {
        // Path.GetFullPath keeps a trailing separator while Path.GetDirectoryName drops it, so an app
        // directory spelled "../rust-api/" compared unequal to the directory the manifest resolved to and the
        // search climbed into the repository above it. Only the app directory is copied into the image, so a
        // Cargo.lock found above it would make --locked fail the container build.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "Cargo.lock"), "version = 4\n");

        if (lockFileInsideAppDirectory)
        {
            File.WriteAllText(Path.Combine(sourceDir.FullName, "Cargo.lock"), "version = 4\n");
        }

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.Services.AddSingleton<ICargoMetadataReader>(new FakeCargoMetadataReader(CargoMetadataFactory.SinglePackage("my-service")));
        builder.AddRustApp("api", sourceDir.FullName + Path.DirectorySeparatorChar)
            .WithCargoManifestPath("Cargo.toml");
        builder.Build().Run();

        var content = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);
        var expectedCargoCommand = lockFileInsideAppDirectory
            ? "cargo build --manifest-path Cargo.toml --locked --release --target-dir /build/target"
            : "cargo build --manifest-path Cargo.toml --release --target-dir /build/target";

        Assert.Equal(expectedCargoCommand, ReadCargoBuildCommand(content));
    }

    // Reads the id from the generated target cache mount, which is emitted as:
    //   RUN --mount=type=cache,target=/usr/local/cargo/registry --mount=type=cache,id=aspire-rust-0123456789abcdef,target=/build/target,sharing=locked \
    private static string ReadTargetCacheId(string dockerfile)
    {
        var match = Regex.Match(
            dockerfile,
            @"--mount=type=cache,id=(aspire-rust-[0-9a-f]{16}),target=/build/target,sharing=locked");

        Assert.True(match.Success, "The generated Dockerfile does not contain a scoped target cache mount.");

        return match.Groups[1].Value;
    }

    // Reads the cargo invocation out of the generated RUN, whose lines are joined by " && \" continuations:
    //     for candidate in ...; done && \
    //     cargo build --release --target-dir /build/target && \
    //     count=0 && \
    private static string ReadCargoBuildCommand(string dockerfile)
    {
        var line = Assert.Single(
            dockerfile.Split('\n'),
            static l => l.TrimStart().StartsWith("cargo build", StringComparison.Ordinal));

        return line.Trim().TrimEnd('\\').TrimEnd().TrimEnd('&').TrimEnd();
    }

    [Fact]
    public async Task PublishCargoArgsCallbackReceivesTheRustResource()
    {
        // Publishing evaluates the cargo argument callbacks on its own path (Dockerfile generation) rather
        // than through the run-mode argument pipeline, so the resource has to arrive there too.
        RustAppResource? configuredResource = null;
        var observed = new List<RustAppResource>();

        await PublishDockerfileAsync(configureResource: app =>
        {
            configuredResource = app.Resource;
            return app.WithCargoArgs(context => observed.Add(context.Resource));
        });

        Assert.NotNull(configuredResource);
        Assert.NotEmpty(observed);
        Assert.All(observed, resource => Assert.Same(configuredResource, resource));
    }

    private async Task<string> PublishDockerfileAsync(
        Action<string>? configureSource = null,
        string? metadata = null,
        Func<IResourceBuilder<RustAppResource>, IResourceBuilder<RustAppResource>>? configureResource = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        configureSource?.Invoke(sourceDir.FullName);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

        // Answer cargo metadata from a canned document so these tests exercise Dockerfile generation on
        // machines without a Rust toolchain installed.
        builder.Services.AddSingleton<ICargoMetadataReader>(
            new FakeCargoMetadataReader(metadata ?? CargoMetadataFactory.SinglePackage("my-service")));

        var app = builder.AddRustApp("api", sourceDir.FullName);

        configureResource?.Invoke(app);

        builder.Build().Run();

        return await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);
    }

    private static void CreateDirectorySymbolicLinkOrSkip(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Assert.Skip($"Symbolic links are unavailable in this test environment: {ex.Message}");
        }
    }

    // Minimal container resource that can act as a container files source, so PublishWithContainerFiles can be
    // exercised without depending on a real container integration.
    private sealed class RustFilesContainer(string name) : ContainerResource(name), IResourceWithContainerFiles;
}
