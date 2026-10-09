// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREDOCKERFILEBUILDER001

using Aspire.Hosting.Publishing;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ContainerImageDockerfileTests(ITestOutputHelper output)
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, false)]
    [InlineData(DistributedApplicationOperation.Publish, true)]
    public void DockerfileArtifactsRemainNonCompute(DistributedApplicationOperation operation, bool registered)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var source = builder.AddContainerImage("tools").WithDockerfile("tools", "custom.Dockerfile", "release");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var consumer = builder.AddContainer("consumer", "busybox");
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var annotation = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());

        Assert.Equal(Path.GetFullPath("tools", builder.AppHostDirectory), annotation.ContextPath);
        Assert.Equal(Path.Combine(annotation.ContextPath, "custom.Dockerfile"), annotation.DockerfilePath);
        Assert.Equal("release", annotation.Stage);
        Assert.Null(annotation.DockerfileFactory);
        Assert.Same(annotation, source.Resource.GetSource().Dockerfile);
        Assert.True(source.Resource.RequiresImageBuild());
        Assert.False(source.Resource.RequiresImageBuildAndPush());
        Assert.False(source.Resource.IsContainer());
        Assert.Equal(registered, model.Resources.Contains(source.Resource));
        Assert.Equal(registered, model.Resources.Contains(destination.Resource));
        Assert.Equal([consumer.Resource], model.GetComputeResources().ToArray());
        Assert.Equal(registered ? [source.Resource] : [], model.GetBuildResources().ToArray());
        Assert.Empty(model.GetBuildAndPushResources());
        Assert.Empty(source.Resource.Annotations.OfType<ContainerImageAnnotation>());
    }

    [Fact]
    public void DockerfileContextSupportsFilesystemRootAndAbsoluteDockerfilePaths()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var root = Path.GetPathRoot(builder.AppHostDirectory);
        Assert.NotNull(root);
        var path = Path.Combine(builder.AppHostDirectory, "custom.Dockerfile");
        var source = builder.AddContainerImage("tools").WithDockerfile(root, path);
        var annotation = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());

        Assert.Equal(root, annotation.ContextPath);
        Assert.Equal(path, annotation.DockerfilePath);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, null)]
    [InlineData(DistributedApplicationOperation.Publish, ContainerTargetPlatform.LinuxAmd64)]
    public async Task DockerfileBuildDefaultsRespectExecutionMode(
        DistributedApplicationOperation operation, ContainerTargetPlatform? platform)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var source = builder.AddContainerImage("TOOLS").WithDockerfile(".");
        using var app = builder.Build();
        var options = await source.Resource.ProcessContainerBuildOptionsCallbackAsync(app.Services, NullLogger.Instance);
        var annotation = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());

        Assert.Equal("tools", options.LocalImageName);
        Assert.Equal(annotation.ImageTag, options.LocalImageTag);
        Assert.Equal(platform, options.TargetPlatform);
    }

    [Fact]
    public async Task ReconfigurationPreservesUserBuildOptionsIncludingMultiplePlatforms()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithDockerfile(".")
            .WithContainerBuildOptions(context =>
            {
                context.TargetPlatform = ContainerTargetPlatform.AllLinux;
                context.LocalImageName = "custom";
                context.LocalImageTag = "user";
            });
        source.WithDockerfile("replacement");
        source.WithDockerfileBuilder(".", context => context.Builder.From("scratch"));
        using var app = builder.Build();
        var options = await source.Resource.ProcessContainerBuildOptionsCallbackAsync(app.Services, NullLogger.Instance);

        Assert.Equal(ContainerTargetPlatform.AllLinux, options.TargetPlatform);
        Assert.Equal("custom", options.LocalImageName);
        Assert.Equal("user", options.LocalImageTag);
        Assert.Equal(2, source.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>().Count());
    }

    [Fact]
    public async Task DockerfileBuilderComposesDeferredCallbacksAndPreservesContext()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var invoked = 0;
        var source = builder.AddContainerImage("tools").WithDockerfileBuilder("tools", context =>
        {
            Assert.IsType<ContainerImageResource>(context.Resource);
            invoked++;
            context.Builder.From("scratch");
        }, "release");
        source.WithDockerfileBuilder("ignored", async context =>
        {
            await Task.CompletedTask;
            invoked++;
            context.Builder.Stages[0].Copy("payload", "/payload");
        }, "ignored");
        var annotation = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        using var app = builder.Build();

        Assert.Equal(0, invoked);
        Assert.Equal(Path.GetFullPath("tools", builder.AppHostDirectory), annotation.ContextPath);
        Assert.Equal("release", annotation.Stage);
        Assert.NotNull(annotation.DockerfileFactory);
        var context = new DockerfileFactoryContext { Resource = source.Resource, Services = app.Services };
        var content = await annotation.DockerfileFactory(context);
        Assert.Equal(2, invoked);
        await Verify(content, "dockerfile");
    }

    [Fact]
    public async Task AppendingBuilderCallbackInvalidatesMaterializedContentAndPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithDockerfileBuilder(".", context => context.Builder.From("scratch"));
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        var original = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        var context = new DockerfileFactoryContext { Resource = source.Resource, Services = app.Services };
        await original.MaterializeDockerfileAsync(context, default);
        destination.Resource.RecordPublishedDigest(Digest);
        source.WithDockerfileBuilder(".", callback => callback.Builder.Stages[0].Copy("payload", "/payload"));
        var replacement = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());

        Assert.NotSame(original, replacement);
        Assert.Equal(original.ImageTag, replacement.ImageTag);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        await replacement.MaterializeDockerfileAsync(context, default);
        await Verify(await File.ReadAllTextAsync(replacement.DockerfilePath), "dockerfile");
    }

    [Fact]
    public async Task BuilderArgumentsAndSecretsSurviveCallbackComposition()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var version = builder.AddParameter("version");
        var token = builder.AddParameter("token", secret: true);
        var source = builder.AddContainerImage("tools")
            .WithDockerfileBuilder(".", context => context.Builder.From("scratch"))
            .WithBuildArg("VERSION", version)
            .WithBuildSecret("TOKEN", token);
        source.WithDockerfileBuilder(".", context => context.Builder.Stages[0].Copy("payload", "/payload"));
        var annotation = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());

        Assert.Collection(annotation.BuildArguments, pair =>
        {
            Assert.Equal("VERSION", pair.Key);
            Assert.Same(version.Resource, pair.Value);
        });
        Assert.Collection(annotation.BuildSecrets, pair =>
        {
            Assert.Equal("TOKEN", pair.Key);
            Assert.Same(token.Resource, pair.Value);
        });
        using var app = builder.Build();
        var options = await source.Resource.ProcessContainerBuildOptionsCallbackAsync(app.Services, NullLogger.Instance);
        Assert.Equal(annotation.ImageName, options.LocalImageName);
        Assert.Equal(annotation.ImageTag, options.LocalImageTag);
    }

    [Fact]
    public async Task SelectingRemoteSourceRemovesOnlyBuildConfigurationAndInvalidatesPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools")
            .WithDockerfileBuilder(".", context => context.Builder.From("scratch"))
            .WithBuildArg("VERSION", "v1")
            .WithContainerBuildOptions(context => context.LocalImageTag = "custom");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        destination.Resource.RecordPublishedDigest(Digest);
        source.WithImageSource("busybox:v1");

        Assert.Equal("docker.io/library/busybox:v1", source.Resource.GetSource().Image);
        Assert.Null(source.Resource.GetSource().Dockerfile);
        Assert.Empty(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Empty(source.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>());
        Assert.Single(source.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>());
        Assert.False(source.Resource.RequiresImageBuild());
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        source.WithDockerfileBuilder(".", context => context.Builder.From("busybox"));
        using var app = builder.Build();
        var annotation = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.Empty(annotation.BuildArguments);
        Assert.Empty(annotation.BuildSecrets);
        var options = await source.Resource.ProcessContainerBuildOptionsCallbackAsync(app.Services, NullLogger.Instance);
        Assert.Equal("custom", options.LocalImageTag);
    }

    [Fact]
    public void ReplacingDockerfileClearsPreviousBuildDetailsAndInvalidatesPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools")
            .WithDockerfileBuilder(".", context => context.Builder.From("scratch"))
            .WithBuildArg("VERSION", "v1")
            .WithBuildSecret("TOKEN", builder.AddParameter("token", secret: true));
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        destination.Resource.RecordPublishedDigest(Digest);
        source.WithDockerfile("replacement", "new.Dockerfile");
        var annotation = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());

        Assert.Empty(annotation.BuildArguments);
        Assert.Empty(annotation.BuildSecrets);
        Assert.Null(annotation.DockerfileFactory);
        Assert.Empty(source.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>());
        Assert.Single(source.Resource.Annotations.OfType<ContainerBuildOptionsCallbackAnnotation>());
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
    }

    [Fact]
    public void ArgumentsAndSecretsInvalidatePublicationWithoutMutatingPreviousSource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithDockerfile(".");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var original = source.Resource.GetSource();
        destination.Resource.RecordPublishedDigest(Digest);
        source.WithBuildArg("VERSION", "v1");

        Assert.Empty(original.Dockerfile!.BuildArguments);
        Assert.NotSame(original, source.Resource.GetSource());
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        destination.Resource.RecordPublishedDigest(Digest);
        source.WithBuildSecret("TOKEN", builder.AddParameter("token", secret: true));
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
    }

    [Fact]
    public async Task BuildOptionsInvalidatePublicationAndGeneratedContent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithDockerfileBuilder(".", context => context.Builder.From("scratch"));
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        var original = source.Resource.GetSource();
        var annotation = Assert.IsType<DockerfileBuildAnnotation>(original.Dockerfile);
        await annotation.MaterializeDockerfileAsync(new() { Resource = source.Resource, Services = app.Services }, default);
        destination.Resource.RecordPublishedDigest(Digest);
        source.WithContainerBuildOptions(async context =>
        {
            context.TargetPlatform = ContainerTargetPlatform.AllLinux;
            await Task.CompletedTask;
        });

        Assert.NotSame(original, source.Resource.GetSource());
        Assert.NotSame(annotation, source.Resource.GetSource().Dockerfile);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        Assert.Single(source.Resource.Annotations.OfType<DockerfileBuilderCallbackAnnotation>());
        var options = await source.Resource.ProcessContainerBuildOptionsCallbackAsync(app.Services, NullLogger.Instance);
        Assert.Equal(ContainerTargetPlatform.AllLinux, options.TargetPlatform);
    }

    [Fact]
    public async Task BuilderCallbackReceivesServicesAndCancellationAndPropagatesFailures()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var cancellation = new CancellationTokenSource();
        var expected = new InvalidOperationException("callback failed");
        IServiceProvider? observedServices = null;
        CancellationToken observedCancellation = default;
        var source = builder.AddContainerImage("tools").WithDockerfileBuilder(".", context =>
        {
            observedServices = context.Services;
            observedCancellation = context.CancellationToken;
            throw expected;
        });
        using var app = builder.Build();
        var annotation = Assert.Single(source.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(annotation.DockerfileFactory);
        var context = new DockerfileFactoryContext
        {
            Resource = source.Resource, Services = app.Services, CancellationToken = cancellation.Token
        };
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => annotation.DockerfileFactory(context));

        Assert.Same(expected, actual);
        Assert.Same(app.Services, observedServices);
        Assert.Equal(cancellation.Token, observedCancellation);
    }

    [Fact]
    public void InvalidConfigurationDoesNotDiscardActiveSourceOrPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithDockerfile(".");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var original = source.Resource.GetSource();
        destination.Resource.RecordPublishedDigest(Digest);

        Assert.Throws<ArgumentException>(() => source.WithDockerfile(""));
        Assert.Throws<ArgumentException>(() => source.WithImageSource("https://invalid"));
        Assert.Throws<ArgumentNullException>(() => source.WithDockerfileBuilder(".", (Action<DockerfileBuilderCallbackContext>)null!));
        Assert.Same(original, source.Resource.GetSource());
        Assert.Equal(Digest, destination.Resource.GetPublishedDigest());
    }

    [Fact]
    public void BuildInputsAreValidatedAndSecretsCannotBecomeArguments()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var foreign = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools");
        var secret = builder.AddParameter("secret", secret: true);
        var other = foreign.AddParameter("foreign");
        IResourceBuilder<ContainerImageResource> nullBuilder = null!;

        Assert.Throws<ArgumentNullException>(() => nullBuilder.WithDockerfile("."));
        Assert.Throws<ArgumentNullException>(() => source.WithDockerfile(null!));
        Assert.Throws<ArgumentNullException>(() => source.WithDockerfileBuilder(".", (Func<DockerfileBuilderCallbackContext, Task>)null!));
        Assert.Throws<InvalidOperationException>(() => source.WithBuildArg("VERSION", "v1"));
        Assert.Throws<InvalidOperationException>(() => source.WithBuildSecret("TOKEN", secret));
        source.WithDockerfile(".");
        Assert.Throws<InvalidOperationException>(() => source.WithBuildArg("TOKEN", secret));
        Assert.Throws<InvalidOperationException>(() => source.WithBuildArg("TOKEN", (object)secret.Resource));
        Assert.Throws<ArgumentException>(() => source.WithBuildArg("VERSION", other));
        Assert.Throws<ArgumentException>(() => source.WithBuildSecret("TOKEN", other));
        Assert.Throws<ArgumentException>(() => source.WithBuildArg("", "v1"));
        Assert.Throws<ArgumentNullException>(() => source.WithBuildSecret("TOKEN", null!));
        Assert.Empty(source.Resource.GetSource().Dockerfile!.BuildArguments);
        Assert.Empty(source.Resource.GetSource().Dockerfile!.BuildSecrets);
    }

    [Fact]
    public void ReplacingBuildAnnotationDirectlyRejectsStaleSourceEvidence()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithDockerfile(".");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        destination.Resource.RecordPublishedDigest(Digest);
        source.WithAnnotation(new DockerfileBuildAnnotation("replacement", "Dockerfile", null), ResourceAnnotationMutationBehavior.Replace);

        Assert.Throws<InvalidOperationException>(source.Resource.GetSource);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
    }

    [Fact]
    public async Task ManifestCapturesBuildInputsAndDestinationsWithoutResolvingParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var version = builder.AddParameter("version");
        var token = builder.AddParameter("token", secret: true);
        var source = builder.AddContainerImage("tools").WithDockerfile("tools", "custom.Dockerfile", "release")
            .WithBuildArg("VERSION", version).WithBuildArg("ENABLED", true)
            .WithBuildSecret("TOKEN", token);
        builder.AddContainerRegistry("first", "first.example.com").AddImage("first-tools", source);
        builder.AddContainerRegistry("second", "second.example.com").AddImage("second-tools", source);
        var manifest = await ManifestUtils.GetManifest(source.Resource, builder.AppHostDirectory);

        await Verify(manifest.ToJsonString(new() { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task ManifestEmitsGeneratedDockerfileWithoutBuildingOrPublishing()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithDockerfileBuilder(workspace.Path, context =>
        {
            context.Builder.From("scratch").Copy("payload", "/payload");
        });
        var manifest = await ManifestUtils.GetManifest(source.Resource, workspace.Path);

        await Verify(new
        {
            Manifest = manifest.ToJsonString(new() { WriteIndented = true }),
            Dockerfile = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "tools.Dockerfile"))
        });
    }
}
