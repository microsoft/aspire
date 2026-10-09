// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIRECONTAINERRUNTIME001
#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREDOCKERFILEBUILDER001

using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Tests.Publishing;
using Aspire.Hosting.Tests.TestServices;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class ContainerImagePublishingTests(ITestOutputHelper outputHelper)
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string PinnedSource = "docker.io/library/busybox@" + Digest;
    private const string DefaultImageTag = "aspire-deploy-20261005040506";

    [Theory]
    [InlineData("build", false)]
    [InlineData("deploy", false)]
    [InlineData("push", true)]
    [InlineData("push-first", true)]
    public async Task DockerfileSourcesBuildOnceAndPublishOnlySelectedDestinations(string step, bool generated)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Dockerfile"), "FROM scratch\n");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        var runtime = AddRuntime(builder);
        runtime.BuildImageArtifactAsyncCallback = _ => Task.FromResult(Digest);
        runtime.PublishImageArtifactAsyncCallback = (digest, destination, _) =>
            Task.FromResult(destination[..destination.LastIndexOf(':')] + "@" + digest);
        var generatedCalls = 0;
        var source = builder.AddContainerImage("tools");
        if (generated)
        {
            source.WithDockerfileBuilder(workspace.Path, context =>
            {
                generatedCalls++;
                context.Builder.From("scratch");
            });
        }
        else
        {
            source.WithDockerfile(workspace.Path);
        }
        source.WithBuildArg("VERSION", builder.AddParameter("version", "v1"))
            .WithBuildSecret("TOKEN", builder.AddParameter("token", "secret-value", secret: true))
            .WithContainerBuildOptions(context =>
            {
                context.TargetPlatform = ContainerTargetPlatform.AllLinux;
                context.LocalImageName = "custom-tools";
                context.LocalImageTag = "build-tag";
            });
        var first = builder.AddContainerRegistry("one", "first.example.com").AddImage("first", source);
        var second = builder.AddContainerRegistry("two", "second.example.com").AddImage("second", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        var build = Assert.Single(runtime.BuildImageCalls);
        Assert.Equal(workspace.Path, build.contextPath);
        Assert.Equal("custom-tools", build.options!.ImageName);
        Assert.Equal("build-tag", build.options.Tag);
        Assert.Equal(ContainerTargetPlatform.AllLinux, build.options.TargetPlatform);
        Assert.Equal("v1", runtime.CapturedBuildArguments!["VERSION"]);
        Assert.Equal("secret-value", runtime.CapturedBuildSecrets!["TOKEN"].Value);
        Assert.Equal(generated ? 1 : 0, generatedCalls);
        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
        var publicationCount = step == "build" ? 0 : step == "push-first" ? 1 : 2;
        Assert.Equal(publicationCount, runtime.ArtifactPublishCalls.Count);
        Assert.All(runtime.ArtifactPublishCalls, publication =>
        {
            Assert.Equal(Digest, publication.Digest);
            Assert.Equal(DefaultImageTag, publication.Destination[(publication.Destination.LastIndexOf(':') + 1)..]);
        });
        if (publicationCount == 0)
        {
            Assert.Throws<InvalidOperationException>(first.Resource.GetPublishedDigest);
            Assert.Throws<InvalidOperationException>(second.Resource.GetPublishedDigest);
        }
        else
        {
            Assert.Equal(Digest, first.Resource.GetPublishedDigest());
        }
    }

    [Fact]
    public async Task UnassociatedDockerfileParticipatesInBuildWithoutRegistryWork()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "build");
        var runtime = AddRuntime(builder);
        runtime.BuildImageArtifactAsyncCallback = _ => Task.FromResult(Digest);
        builder.AddContainerImage("tools").WithDockerfileBuilder(".", context => context.Builder.From("scratch"));
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Single(runtime.BuildImageCalls);
        Assert.Empty(runtime.ArtifactPublishCalls);
        Assert.Empty(runtime.RemoteResolveCalls);
    }

    [Theory]
    [InlineData("build-failure")]
    [InlineData("invalid-digest")]
    [InlineData("changed-source")]
    [InlineData("publication-mismatch")]
    public async Task DockerfilePreparationAndPublicationFailuresDoNotApplyConsumers(string failure)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "apply-consumer");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithDockerfileBuilder(".", context => context.Builder.From("scratch"));
        runtime.BuildImageArtifactAsyncCallback = _ =>
        {
            if (failure == "build-failure")
            {
                throw new DistributedApplicationException("Build failed.");
            }
            if (failure == "changed-source")
            {
                source.WithImageSource("busybox");
            }

            return Task.FromResult(failure == "invalid-digest" ? "not-a-digest" : Digest);
        };
        runtime.PublishImageArtifactAsyncCallback = (_, _, _) => Task.FromResult("registry.example.com/published@" + new string('a', 64));
        var image = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var applied = false;
        builder.AddContainer("consumer", "busybox").WithReference(image).WithPipelineStepFactory(_ => new PipelineStep
        {
            Name = "apply-consumer",
            Tags = [WellKnownPipelineTags.DeployCompute],
            Action = _ => { applied = true; return Task.CompletedTask; }
        });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));

        Assert.False(applied);
        Assert.Throws<InvalidOperationException>(image.Resource.GetPublishedDigest);
        Assert.Equal(failure == "publication-mismatch" ? 1 : 0, runtime.ArtifactPublishCalls.Count);
    }

    [Fact]
    public async Task RepeatedDockerfileBuildsRematerializeCallbacksAndDoNotReusePreparedResults()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "build");
        var runtime = AddRuntime(builder);
        runtime.BuildImageArtifactAsyncCallback = _ => Task.FromResult(Digest);
        var calls = 0;
        builder.AddContainerImage("tools").WithDockerfileBuilder(".", context =>
        {
            calls++;
            context.Builder.From("scratch");
        });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);
        await ExecuteAsync(app);

        Assert.Equal(2, calls);
        Assert.Equal(2, runtime.BuildImageCalls.Count);
        Assert.Empty(runtime.ArtifactPublishCalls);
    }

    [Fact]
    public async Task CancellationAfterArtifactBuildDoesNotPublishOrRecordEvidence()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        using var cancellation = new CancellationTokenSource();
        var runtime = AddRuntime(builder);
        runtime.BuildImageArtifactAsyncCallback = _ =>
        {
            cancellation.Cancel();
            return Task.FromResult(Digest);
        };
        var source = builder.AddContainerImage("tools").WithDockerfileBuilder(".", context => context.Builder.From("scratch"));
        var image = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, cancellation.Token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(context));

        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.Empty(runtime.ArtifactPublishCalls);
        Assert.Throws<InvalidOperationException>(image.Resource.GetPublishedDigest);
    }

    [Theory]
    [InlineData("exclude-source")]
    [InlineData("exclude-destination")]
    [InlineData("remove-source")]
    [InlineData("remove-destination")]
    public async Task ConsumerRejectsUnavailableImagePublication(string change)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "apply-consumer");
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var image = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        builder.AddContainer("consumer", "busybox").WithReference(image);
        switch (change)
        {
            case "exclude-source":
                source.ExcludeFromManifest();
                break;
            case "exclude-destination":
                image.ExcludeFromManifest();
                break;
            case "remove-source":
                builder.Resources.Remove(source.Resource);
                break;
            case "remove-destination":
                builder.Resources.Remove(image.Resource);
                break;
        }
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));
        Assert.Contains("published", exception.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ConsumerCannotApplyBeforeVerifiedImagePublication(bool environmentOnly, bool failPublication)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "apply-consumer");
        var runtime = AddRuntime(builder);
        if (failPublication)
        {
            runtime.CopyRemoteImageAsyncCallback = (_, _, _) => throw new DistributedApplicationException("Copy failed.");
        }
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var image = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var consumer = builder.AddContainer("consumer", "busybox");
        if (environmentOnly)
        {
            consumer.WithEnvironment("IMAGE", image);
        }
        else
        {
            consumer.WithReference(image);
        }
        string? appliedImage = null;
        consumer.WithPipelineStepFactory(_ => new PipelineStep
        {
            Name = "apply-consumer",
            Tags = [WellKnownPipelineTags.DeployCompute],
            Action = async context => appliedImage = await ((IValueProvider)image.Resource).GetValueAsync(context.CancellationToken)
        });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        if (failPublication)
        {
            await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
            Assert.Null(appliedImage);
        }
        else
        {
            await ExecuteAsync(app);
            Assert.Equal("registry.example.com/published@" + Digest, appliedImage);
            Assert.Single(runtime.RemoteCopyCalls);
        }
    }

    [Theory]
    [InlineData("deploy")]
    [InlineData("push")]
    [InlineData("push-first")]
    public async Task ImageOnlyPipelinePreparesOnceAndPublishesNamedDestinations(string step)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox:latest");
        var first = builder.AddContainerRegistry("one", "first.example.com", "team").AddImage("first", source);
        var second = builder.AddContainerRegistry("two", "second.example.com").AddImage("second", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Equal(["docker.io/library/busybox:latest"], runtime.RemoteResolveCalls);
        var expectedCount = step == "push-first" ? 1 : 2;
        Assert.Equal(expectedCount, runtime.RemoteCopyCalls.Count);
        Assert.All(runtime.RemoteCopyCalls, call =>
        {
            Assert.Equal(PinnedSource, call.Source);
            Assert.Equal(DefaultImageTag, call.Destination[(call.Destination.LastIndexOf(':') + 1)..]);
        });
        Assert.Equal("first.example.com/team/first@" + Digest, await ((IValueProvider)first.Resource).GetValueAsync(default));
        if (expectedCount == 2)
        {
            Assert.Equal("second.example.com/second@" + Digest, await ((IValueProvider)second.Resource).GetValueAsync(default));
            Assert.Single(runtime.RemoteCopyCalls.Select(call => call.Destination[(call.Destination.LastIndexOf(':') + 1)..]).Distinct());
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await ((IValueProvider)second.Resource).GetValueAsync(default));
        }
        Assert.Empty(runtime.BuildImageCalls);
        Assert.Empty(runtime.PushImageCalls);
        Assert.Empty(runtime.TagImageCalls);
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("sha256")]
    [InlineData("registry")]
    [InlineData("repository")]
    public async Task IndividualPropertiesWaitForPublicationBeforeConsumerResolution(string property)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "apply-consumer");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("source").WithImageSource("busybox:source-tag");
        var image = builder.AddContainerRegistry("registry", "registry.example.com", "team").AddImage("published", source);
        var expression = property switch
        {
            "tag" => image.Resource.TagExpression,
            "sha256" => image.Resource.Sha256Expression,
            "registry" => image.Resource.RegistryExpression,
            "repository" => image.Resource.RepositoryExpression,
            _ => throw new InvalidOperationException()
        };
        string? resolved = null;
        builder.AddContainer("consumer", "busybox")
            .WithEnvironment("VALUE", expression)
            .WithPipelineStepFactory(_ => new PipelineStep
            {
                Name = "apply-consumer",
                Tags = [WellKnownPipelineTags.DeployCompute],
                Action = async context => resolved = await expression.GetValueAsync(context.CancellationToken)
            });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);
        Assert.Single(runtime.RemoteCopyCalls);
        Assert.Equal(property switch
        {
            "tag" => DefaultImageTag,
            "sha256" => Digest["sha256:".Length..],
            "registry" => "registry.example.com",
            "repository" => "team/published",
            _ => throw new InvalidOperationException()
        }, resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("release-1")]
    public async Task ArtifactSourcesShareTheComputeDefaultLabelWithoutOverridingCustomTags(string? customTag)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Dockerfile"), "FROM busybox\nENTRYPOINT [\"/bin/sh\"]\n");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "push");
        var runtime = AddRuntime(builder);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var compute = builder.AddDockerfile("compute", workspace.Path).WithContainerRegistry(registry);
        if (customTag is not null)
        {
            compute.WithAnnotation(new ContainerImagePushOptionsCallbackAnnotation(context =>
                context.Options.RemoteImageTag = customTag));
        }
        var first = builder.AddContainerImage("first").WithImageSource("busybox");
        var second = builder.AddContainerImage("second").WithImageSource("busybox:v2");
        registry.AddImage("first-image", first);
        registry.AddImage("second-image", second);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Equal(2, runtime.RemoteCopyCalls.Count);
        Assert.All(runtime.RemoteCopyCalls, call =>
            Assert.Equal(DefaultImageTag, call.Destination[(call.Destination.LastIndexOf(':') + 1)..]));
        var options = await compute.Resource.ProcessImagePushOptionsCallbackAsync(default);
        Assert.Equal(customTag ?? DefaultImageTag, options.RemoteImageTag);
        Assert.Single(runtime.BuildImageCalls);
        Assert.Same(compute.Resource, Assert.Single(runtime.PushImageCalls));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("release-1")]
    public async Task EachExecutionRefreshesGeneratedLabelsWithoutReplacingUserCallbacks(string? customTag)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Dockerfile"), "FROM busybox\nENTRYPOINT [\"/bin/sh\"]\n");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "push");
        var runtime = AddRuntime(builder);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var compute = builder.AddDockerfile("compute", workspace.Path).WithContainerRegistry(registry);
        ContainerImagePushOptionsCallbackAnnotation? customOptions = null;
        if (customTag is { } tag)
        {
            customOptions = new ContainerImagePushOptionsCallbackAnnotation(context => context.Options.RemoteImageTag = tag);
            compute.WithAnnotation(customOptions);
        }
        var first = builder.AddContainerImage("first").WithImageSource("busybox");
        var second = builder.AddContainerImage("second").WithImageSource("busybox:v2");
        registry.AddImage("first-image", first);
        registry.AddImage("second-image", second);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);
        var clock = Assert.IsType<FakeTimeProvider>(app.Services.GetRequiredService<TimeProvider>());
        clock.Advance(TimeSpan.FromMinutes(1));
        await ExecuteAsync(app);

        const string nextTag = "aspire-deploy-20261005040606";
        Assert.Equal(new[] { DefaultImageTag, DefaultImageTag, nextTag, nextTag }.Order(StringComparer.Ordinal),
            runtime.RemoteCopyCalls.Select(call => call.Destination[(call.Destination.LastIndexOf(':') + 1)..]).Order(StringComparer.Ordinal));
        Assert.Equal(customTag ?? nextTag, (await compute.Resource.ProcessImagePushOptionsCallbackAsync(default)).RemoteImageTag);
        var computeOptions = Assert.Single(compute.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());
        if (customOptions is not null)
        {
            Assert.Same(customOptions, computeOptions);
        }
        else
        {
            Assert.True(computeOptions.IsDefaultImageTag);
        }
        Assert.True(Assert.Single(first.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>()).IsDefaultImageTag);
        Assert.True(Assert.Single(second.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>()).IsDefaultImageTag);
    }

    [Fact]
    public async Task UserCallbackAddedAfterDeploymentSupersedesTheGeneratedDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "push");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);
        var customOptions = new ContainerImagePushOptionsCallbackAnnotation(context => context.Options.RemoteImageTag = "release-1");
        source.Resource.Annotations.Add(customOptions);
        await ExecuteAsync(app);

        Assert.Same(customOptions, Assert.Single(source.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>()));
        Assert.Equal(new[] { DefaultImageTag, "release-1" }.Order(StringComparer.Ordinal),
            runtime.RemoteCopyCalls.Select(call => call.Destination[(call.Destination.LastIndexOf(':') + 1)..]).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyPublicationTagFailsBeforeRemoteResolution(string? tag)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "push");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox")
            .WithAnnotation(new ContainerImagePushOptionsCallbackAnnotation(context => context.Options.RemoteImageTag = tag));
        builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Contains("publication tag", exception.Message);
        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
    }

    [Fact]
    public async Task BeforeStartAndStepInspectionDoNotInitializePublicationLabels()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "Dockerfile"), "FROM busybox\nENTRYPOINT [\"/bin/sh\"]\n");
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "push");
        var runtime = AddRuntime(builder);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var compute = builder.AddDockerfile("compute", workspace.Path).WithContainerRegistry(registry);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        registry.AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services, NullLogger.Instance, default);
        await Assert.IsType<DistributedApplicationPipeline>(app.Services.GetRequiredService<IDistributedApplicationPipeline>()).ResolveStepsAsync(context);

        Assert.Empty(compute.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());
        Assert.Empty(source.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());
        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
    }

    [Fact]
    public async Task VerifiedReferencesArePersistedAndSummarized()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        var context = await ExecuteAsync(app);
        var state = app.Services.GetRequiredService<IDeploymentStateManager>();
        var section = await state.AcquireSectionAsync("ContainerImages:published");
        var publicationTag = section.Data["publicationTag"]!.GetValue<string>();
        Assert.Equal($"registry.example.com/published:{DefaultImageTag}", publicationTag);

        await Verify(new
        {
            PublishedReference = await ((IValueProvider)destination.Resource).GetValueAsync(default),
            State = section.Data.ToJsonString(),
            Summary = context.Summary
        }).AddScrubber(text => text.Replace(publicationTag, "registry.example.com/published:aspire-deploy-<execution>"));
    }

    [Fact]
    public async Task EachExecutionResolvesTheSourceAgainAndFailureClearsPriorEvidence()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);
        Assert.Equal(Digest, destination.Resource.GetPublishedDigest());

        runtime.ResolveRemoteImageAsyncCallback = (_, _) => throw new DistributedApplicationException("Source is unavailable.");
        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        Assert.Single(runtime.RemoteCopyCalls);

        runtime.ResolveRemoteImageAsyncCallback = (_, _) => Task.FromResult(PinnedSource);
        await ExecuteAsync(app);
        Assert.Equal(3, runtime.RemoteResolveCalls.Count);
        Assert.Equal(2, runtime.RemoteCopyCalls.Count);
        Assert.Equal(Digest, destination.Resource.GetPublishedDigest());
    }

    [Theory]
    [InlineData("docker.io/library/busybox:latest")]
    [InlineData("busybox@" + Digest)]
    [InlineData("other.example.com/busybox@" + Digest)]
    public async Task IncorrectPreparedReferenceFailsBeforeCopy(string result)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        runtime.ResolveRemoteImageAsyncCallback = (_, _) => Task.FromResult(result);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Empty(runtime.RemoteCopyCalls);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
    }

    [Fact]
    public async Task CopyFailureRecordsNoPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        runtime.CopyRemoteImageAsyncCallback = (_, _, _) => throw new DistributedApplicationException("Registry rejected the copy.");
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Single(runtime.RemoteCopyCalls);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        var section = await app.Services.GetRequiredService<IDeploymentStateManager>().AcquireSectionAsync("ContainerImages:published");
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task ReferenceCannotUseEvidenceForAnotherRepository()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        destination.Resource.RecordPublishedImage("registry.example.com/previous-repository", Digest, DefaultImageTag);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ((IValueProvider)destination.Resource).GetValueAsync(default));
        Assert.Equal("Destination image 'published' has a different registry or repository than its verified publication. Publish the image again before resolving its value.", exception.Message);
    }

    [Theory]
    [InlineData("registry.example.com/published:latest")]
    [InlineData("other.example.com/published@" + Digest)]
    [InlineData("registry.example.com/published@sha256:1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task IncorrectCopyEvidenceFailsWithoutRecordingPublication(string result)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        runtime.CopyRemoteImageAsyncCallback = (_, _, _) => Task.FromResult(result);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        var section = await app.Services.GetRequiredService<IDeploymentStateManager>().AcquireSectionAsync("ContainerImages:published");
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task ConfigurationChangedDuringCopyCannotClaimPublicationForANewSource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        runtime.CopyRemoteImageAsyncCallback = (_, _, _) =>
        {
            source.WithImageSource("busybox:v2");
            return Task.FromResult("registry.example.com/published@" + Digest);
        };
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteAsync(app));
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExcludedArtifactsOrDestinationsAreNotPublished(bool excludeSource)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        if (excludeSource)
        {
            source.ExcludeFromManifest();
        }
        else
        {
            destination.ExcludeFromManifest();
        }
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("build")]
    public async Task PublishAndBuildDoNotCopyImages(string step)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: step);
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
    }

    [Fact]
    public async Task CancellationDuringCopyRecordsNoPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        using var cancellation = new CancellationTokenSource();
        runtime.CopyRemoteImageAsyncCallback = (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The pipeline did not pass its cancellation token to the runtime.");
        };
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, cancellation.Token);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(context));
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Throws<InvalidOperationException>(destination.Resource.GetPublishedDigest);
        var section = await app.Services.GetRequiredService<IDeploymentStateManager>().AcquireSectionAsync("ContainerImages:published");
        Assert.Empty(section.Data);
    }

    [Fact]
    public async Task InferredDestinationParticipatesInDeployment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("environment") { ImageRegistry = registry.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        var destination = Assert.Single(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DestinationImageResource>());
        Assert.Equal("tools-registry", destination.Name);
        Assert.Equal("registry.example.com/tools-registry@" + Digest, await ((IValueProvider)destination).GetValueAsync(default));
        Assert.Single(runtime.RemoteResolveCalls);
        Assert.Single(runtime.RemoteCopyCalls);
    }

    [Fact]
    public async Task MissingRegistryIsRejectedBeforeTransfer()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        registry.AddImage("published", source);
        builder.Resources.Remove(registry.Resource);
        using var app = builder.Build();

        await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));
        Assert.Empty(runtime.RemoteResolveCalls);
        Assert.Empty(runtime.RemoteCopyCalls);
    }

    [Fact]
    public async Task RegistryProvisioningAndPushPrerequisitesCompleteBeforePreparation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, step: "deploy");
        var runtime = AddRuntime(builder);
        var provisioned = false;
        var loggedIn = false;
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        registry.WithPipelineStepFactory(_ => new PipelineStep
        {
            Name = "provision-registry",
            Tags = [WellKnownPipelineTags.ProvisionInfrastructure],
            Action = _ => { provisioned = true; return Task.CompletedTask; }
        });
        registry.WithPipelineStepFactory(_ => new PipelineStep
        {
            Name = "login-registry",
            DependsOnSteps = ["provision-registry"],
            RequiredBySteps = [WellKnownPipelineSteps.PushPrereq],
            Action = _ => { loggedIn = true; return Task.CompletedTask; }
        });
        registry.AddImage("published", source);
        runtime.ResolveRemoteImageAsyncCallback = (_, _) =>
        {
            Assert.True(provisioned);
            Assert.True(loggedIn);
            return Task.FromResult(PinnedSource);
        };
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        await ExecuteAsync(app);

        Assert.Single(runtime.RemoteCopyCalls);
    }

    private static FakeRemoteContainerRuntime AddRuntime(IDistributedApplicationBuilder builder)
    {
        var runtime = new FakeRemoteContainerRuntime
        {
            ResolveRemoteImageAsyncCallback = (_, _) => Task.FromResult(PinnedSource),
            // Transport references look like localhost:5000/tools:aspire-deploy-<suffix>.
            // The last colon separates the tag, not the registry's optional port.
            CopyRemoteImageAsyncCallback = (_, destination, _) => Task.FromResult(destination[..destination.LastIndexOf(':')] + "@" + Digest)
        };
        builder.Services.AddSingleton<IContainerRuntimeResolver>(runtime);
        builder.Services.AddSingleton<IDeploymentStateManager, InMemoryDeploymentStateManager>();
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 4, 5, 6, TimeSpan.Zero)));

        return runtime;
    }

    private static async Task<PipelineContext> ExecuteAsync(DistributedApplication app)
    {
        var context = new PipelineContext(app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<DistributedApplicationExecutionContext>(), app.Services,
            NullLogger.Instance, default);
        await app.Services.GetRequiredService<IDistributedApplicationPipeline>().ExecuteAsync(context).DefaultTimeout();

        return context;
    }
}
