// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Utils;
using Aspire.Hosting.Tests.TestServices;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ContainerImageResourceTests
{
    private const string Digest = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void RequiredArgumentsAreValidated()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        IDistributedApplicationBuilder nullBuilder = null!;
        IResourceBuilder<ContainerImageResource> nullImage = null!;
        IResourceBuilder<ContainerRegistryResource> nullRegistry = null!;

        Assert.Throws<ArgumentNullException>(() => nullBuilder.AddContainerImage("tools", "busybox"));
        Assert.Throws<ArgumentNullException>(() => builder.AddContainerImage(null!, "busybox"));
        Assert.Throws<ArgumentException>(() => builder.AddContainerImage("", "busybox"));
        Assert.Throws<ArgumentNullException>(() => builder.AddContainerImage("null-image", null!));
        Assert.Throws<ArgumentNullException>(() => nullRegistry.WithPushedImage(image));
        Assert.Throws<ArgumentNullException>(() => registry.WithPushedImage(nullImage));
        Assert.Throws<ArgumentNullException>(() => nullImage.GetImageReference(registry));
        Assert.Throws<ArgumentNullException>(() => image.GetImageReference(nullRegistry));
        Assert.Throws<ArgumentNullException>(() => new ContainerImageRegistryTargetAnnotation((IContainerRegistry)null!));
        Assert.Throws<ArgumentNullException>(() => new ContainerImageRegistryTargetAnnotation((Func<IContainerRegistry?>)null!));
    }

    [Theory]
    [InlineData("busybox", "docker.io/library/busybox:latest", "docker.io", "library/busybox", "latest", null)]
    [InlineData("example/tools:v1", "docker.io/example/tools:v1", "docker.io", "example/tools", "v1", null)]
    [InlineData("index.docker.io/busybox", "docker.io/library/busybox:latest", "docker.io", "library/busybox", "latest", null)]
    [InlineData("ghcr.io/example/tools:V1", "ghcr.io/example/tools:V1", "ghcr.io", "example/tools", "V1", null)]
    [InlineData("localhost:5000/team/tools", "localhost:5000/team/tools:latest", "localhost:5000", "team/tools", "latest", null)]
    [InlineData("[::1]:5000/tools:v1", "[::1]:5000/tools:v1", "[::1]:5000", "tools", "v1", null)]
    [InlineData("ghcr.io/tools@" + Digest, "ghcr.io/tools@" + Digest, "ghcr.io", "tools", null, Digest)]
    [InlineData("ghcr.io/tools:v1@" + Digest, "ghcr.io/tools:v1@" + Digest, "ghcr.io", "tools", "v1", Digest)]
    public void SourceReferenceIsNormalized(string image, string normalized, string registry, string repository, string? tag, string? digest)
    {
        var resource = new ContainerImageResource("tools", image);

        Assert.Equal(normalized, resource.SourceImage);
        Assert.Equal(new ContainerReference(registry, repository, tag, digest), resource.Source);
        Assert.IsAssignableFrom<IResourceWithoutLifetime>(resource);
        Assert.Empty(resource.Annotations);
        Assert.False(resource.IsContainer());
        Assert.False(resource.RequiresImageBuild());
        Assert.False(resource.RequiresImageBuildAndPush());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("image\n")]
    [InlineData("UPPERCASE")]
    [InlineData("repo//image")]
    [InlineData("repo/../image")]
    [InlineData("repo/<image>")]
    [InlineData("https://registry.example.com/tools:v1")]
    [InlineData("https://user:password@registry.example.com/tools:v1")]
    [InlineData("user:password@registry.example.com/tools")]
    [InlineData("image@sha256:1234")]
    [InlineData("image@digest")]
    [InlineData("image:")]
    [InlineData("image:-invalid")]
    [InlineData("registry.example.com:70000/tools")]
    public void InvalidSourcesAreRejectedWithoutEchoingTheirValues(string image)
    {
        var exception = Assert.ThrowsAny<ArgumentException>(() => new ContainerImageResource("tools", image));

        Assert.Equal("image", exception.ParamName);
        Assert.IsNotType<ArgumentOutOfRangeException>(exception);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, false)]
    [InlineData(DistributedApplicationOperation.Publish, true)]
    public void ArtifactRegistrationIsPublishOnly(DistributedApplicationOperation operation, bool inModel)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var artifact = builder.AddContainerImage("tools", "busybox");
        var container = builder.AddContainer("consumer", "busybox");
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Equal(inModel, model.Resources.Contains(artifact.Resource));
        Assert.Equal([container.Resource], model.GetComputeResources().ToArray());
        Assert.Empty(model.GetBuildResources());
        Assert.Empty(model.GetBuildAndPushResources());
        Assert.Collection(artifact.Resource.Annotations, annotation => Assert.IsType<ManifestPublishingCallbackAnnotation>(annotation));
    }

    [Fact]
    public void AssociationsAreAdditiveAndIdenticalCallsAreIdempotent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox:v1");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");

        Assert.Same(first, first.WithPushedImage(image));
        first.WithPushedImage(image, tag: "v1");
        second.WithPushedImage(image, repository: "team/tools", tag: "release");

        Assert.Collection(image.Resource.GetPublications(),
            publication =>
            {
                Assert.Same(first.Resource, publication.Registry);
                Assert.Null(publication.Repository);
                Assert.Equal("v1", publication.Tag);
            },
            publication =>
            {
                Assert.Same(second.Resource, publication.Registry);
                Assert.Equal("team/tools", publication.Repository);
                Assert.Equal("release", publication.Tag);
            });
    }

    [Fact]
    public void ConflictingAssociationIsRejectedWithoutChangingTheModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox:v1");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        registry.WithPushedImage(image, repository: "team/tools", tag: "v2");

        Assert.Throws<InvalidOperationException>(() => registry.WithPushedImage(image, repository: "other/tools", tag: "v2"));
        Assert.Throws<InvalidOperationException>(() => registry.WithPushedImage(image, repository: "team/tools", tag: "v3"));

        var publication = Assert.Single(image.Resource.GetPublications());
        Assert.Equal("team/tools", publication.Repository);
        Assert.Equal("v2", publication.Tag);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("team//tools")]
    [InlineData("TEAM/tools")]
    [InlineData("registry.example.com:5000/tools")]
    [InlineData("team/tools:v1")]
    [InlineData("team/tools@" + Digest)]
    public void InvalidDestinationRepositoriesAreRejected(string repository)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");

        Assert.Throws<ArgumentException>(() => registry.WithPushedImage(image, repository: repository));
        Assert.Empty(image.Resource.GetPublications());
    }

    [Theory]
    [InlineData("")]
    [InlineData(".invalid")]
    [InlineData("-invalid")]
    [InlineData("not a tag")]
    [InlineData("image:v1")]
    public void InvalidDestinationTagsAreRejected(string tag)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");

        Assert.Throws<ArgumentException>(() => registry.WithPushedImage(image, tag: tag));
        Assert.Throws<ArgumentException>(() => registry.WithPushedImage(image, tag: new string('a', 129)));
        Assert.Empty(image.Resource.GetPublications());
    }

    [Fact]
    public void ExplicitRegistrySelectionRemainsLastWinsAndMergesWithAdditiveAssociations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox:v1");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        var third = builder.AddContainerRegistry("third", "third.example.com");
        first.WithPushedImage(image);
        image.WithContainerRegistry(second).WithContainerRegistry(third);

        Assert.Collection(image.Resource.GetPublications(),
            publication => Assert.Same(first.Resource, publication.Registry),
            publication => Assert.Same(third.Resource, publication.Registry));
        Assert.Throws<InvalidOperationException>(() => image.GetImageReference(second));

        first.WithPushedImage(image);
        image.WithContainerRegistry(first);
        Assert.Same(first.Resource, Assert.Single(image.Resource.GetPublications()).Registry);
    }

    [Fact]
    public async Task DefaultRegistryAnnotationsDoNotCreatePublicationAssociations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        image.WithAnnotation(new RegistryTargetAnnotation(first.Resource));
        image.WithAnnotation(new RegistryTargetAnnotation(second.Resource));

        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        Assert.Empty(image.Resource.GetPublications());
        Assert.Throws<InvalidOperationException>(() => image.GetImageReference(first));
        Assert.Throws<InvalidOperationException>(() => image.GetImageReference(second));
    }

    [Fact]
    public async Task UnassociatedImageAdoptsRegistryAfterEnvironmentPreparation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox:v1");
        var endpoint = builder.AddParameter("endpoint");
        var registry = builder.AddContainerRegistry("registry", endpoint);
        var environment = builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        using var app = builder.Build();

        Assert.Empty(image.Resource.GetPublications());
        await app.ExecuteBeforeStartHooksAsync(default);

        var publication = Assert.Single(image.Resource.GetPublications());
        Assert.Same(registry.Resource, publication.Registry);
        Assert.Same(environment.Resource, publication.DefaultEnvironment);
        Assert.Equal("v1", publication.Tag);
        Assert.Null(publication.Repository);
        Assert.Equal("{tools.publications.registry.image}", image.GetImageReference(registry).ValueExpression);
        Assert.Empty(image.Resource.Annotations.OfType<DeploymentTargetAnnotation>());
        Assert.Empty(app.Services.GetRequiredService<DistributedApplicationModel>().GetComputeResources());

        var manifest = await ManifestUtils.GetManifest(image.Resource);
        await Verify(manifest.ToJsonString(new() { WriteIndented = true }), "json");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitAssociationSuppressesAllEnvironmentDefaults(bool useWithPushedImage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("first-env") { ImageRegistry = first.Resource });
        builder.AddResource(new TestImageComputeEnvironmentResource("second-env") { ImageRegistry = second.Resource });
        if (useWithPushedImage)
        {
            first.WithPushedImage(image);
        }
        else
        {
            image.WithContainerRegistry(first);
        }

        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        var publication = Assert.Single(image.Resource.GetPublications());
        Assert.Same(first.Resource, publication.Registry);
        Assert.Null(publication.DefaultEnvironment);
    }

    [Fact]
    public async Task NonPublicationAssociationSuppressesAutomaticPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        image.WithAnnotation(new TestContainerImageRegistryAssociationAnnotation(registry.Resource));

        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        Assert.True(image.Resource.HasExplicitRegistryAssociation);
        Assert.Empty(image.Resource.GetPublications());
    }

    [Fact]
    public async Task EnvironmentsSharingOneRegistryDoNotCreateAmbiguousDefaults()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("second-env") { ImageRegistry = registry.Resource });
        var first = builder.AddResource(new TestImageComputeEnvironmentResource("first-env") { ImageRegistry = registry.Resource });

        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        var publication = Assert.Single(image.Resource.GetPublications());
        Assert.Same(registry.Resource, publication.Registry);
        Assert.Same(first.Resource, publication.DefaultEnvironment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctDefaultRegistriesAreRejectedRegardlessOfRegistrationOrder(bool reverseOrder)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var other = builder.AddContainerImage("other", "busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        var environments = new[]
        {
            new TestImageComputeEnvironmentResource("first-env") { ImageRegistry = first.Resource },
            new TestImageComputeEnvironmentResource("second-env") { ImageRegistry = second.Resource }
        };
        foreach (var environment in reverseOrder ? environments.AsEnumerable().Reverse() : environments)
        {
            builder.AddResource(environment);
        }

        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));

        Assert.Equal(
            "Image artifact(s) 'other', 'tools' have multiple default container registries available ('first', 'second'). " +
            "Associate each image explicitly using 'registry.WithPushedImage(image)' or 'image.WithContainerRegistry(registry)'.",
            exception.Message);
        Assert.Empty(image.Resource.GetPublications());
        Assert.Empty(other.Resource.GetPublications());
    }

    [Fact]
    public async Task InferredAssociationIsRecomputedAndRemovedWhenDefaultDisappears()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        var environment = builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = first.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        var reference = image.GetImageReference(first);
        await app.ExecuteBeforeStartHooksAsync(default);
        Assert.Same(first.Resource, Assert.Single(image.Resource.GetPublications()).Registry);

        environment.Resource.ImageRegistry = second.Resource;
        await app.ExecuteBeforeStartHooksAsync(default);

        Assert.Same(second.Resource, Assert.Single(image.Resource.GetPublications()).Registry);
        Assert.Throws<InvalidOperationException>(() => reference.ValueExpression);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IValueProvider)reference).GetValueAsync(default).AsTask());

        environment.Resource.ImageRegistry = null;
        await app.ExecuteBeforeStartHooksAsync(default);
        Assert.Empty(image.Resource.GetPublications());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateExplicitAssociationImmediatelySuppressesInferredDestination(bool sameRegistry)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = first.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        var selected = sameRegistry ? first : second;
        selected.WithPushedImage(image, repository: "custom/tools", tag: "release");

        var publication = Assert.Single(image.Resource.GetPublications());
        Assert.Same(selected.Resource, publication.Registry);
        Assert.Null(publication.DefaultEnvironment);
        Assert.Equal("custom/tools", publication.Repository);
        Assert.Equal("release", publication.Tag);

        await app.ExecuteBeforeStartHooksAsync(default);
        Assert.Same(publication, Assert.Single(image.Resource.GetPublications()));
        Assert.Null(Assert.Single(image.Resource.Annotations.OfType<ContainerImagePublicationAnnotation>()).DefaultEnvironment);
    }

    [Fact]
    public async Task LateWithContainerRegistryImmediatelySuppressesInferredDestination()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = first.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        image.WithContainerRegistry(second);
        Assert.Same(second.Resource, Assert.Single(image.Resource.GetPublications()).Registry);

        await app.ExecuteBeforeStartHooksAsync(default);
        Assert.Same(second.Resource, Assert.Single(image.Resource.GetPublications()).Registry);
        Assert.Empty(image.Resource.Annotations.OfType<ContainerImagePublicationAnnotation>());
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ExcludedImagesAndEnvironmentsDoNotParticipateInDefaultAdoption(bool excludeImage, bool excludeEnvironment)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var environment = builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        if (excludeImage)
        {
            image.ExcludeFromManifest();
        }
        if (excludeEnvironment)
        {
            environment.ExcludeFromManifest();
        }

        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        Assert.Empty(image.Resource.GetPublications());
    }

    [Fact]
    public async Task DefaultRegistryMustBePublishableAndPresentInModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com").ExcludeFromManifest();
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));
        Assert.Equal(
            "Compute environment 'env' advertises image registry 'registry', which is not a publishable container registry resource in the application model.",
            exception.Message);
        Assert.Empty(image.Resource.GetPublications());

        app.Services.GetRequiredService<DistributedApplicationModel>().Resources.Remove(registry.Resource);
        exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));
        Assert.Equal(
            "Compute environment 'env' advertises image registry 'registry', which is not a publishable container registry resource in the application model.",
            exception.Message);
    }

    [Fact]
    public async Task RunModeNeverAdoptsRegistriesEvenForManuallyAddedImageResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var image = builder.AddResource(new ContainerImageResource("tools", "busybox"));
        var registry = new ContainerRegistryResource("registry", ReferenceExpression.Create($"registry.example.com"));
        builder.AddResource(registry);
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry })
            .WithAnnotation(new ContainerImageRegistryTargetAnnotation(registry));
        using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(default);

        Assert.Empty(image.Resource.GetPublications());
    }

    [Fact]
    public async Task DestinationRetainsArtifactAndRegistryProvenance()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("Tools", "busybox:v1");
        var registry = builder.AddContainerRegistry("registry", "localhost:5000", "namespace");
        registry.WithPushedImage(image);
        var reference = image.GetImageReference(registry);

        Assert.Same(image.Resource, reference.Resource);
        Assert.Same(registry.Resource, reference.Registry);
        Assert.Equal("{Tools.publications.registry.image}", reference.ValueExpression);
        Assert.Equal("localhost:5000/namespace/tools:v1", await ((IValueProvider)reference).GetValueAsync(default));
        Assert.Collection(reference.References,
            resource => Assert.Same(image.Resource, resource),
            resource => Assert.Same(registry.Resource, resource));
        var expression = ReferenceExpression.Create($"image={reference}");
        Assert.Equal("image={Tools.publications.registry.image}", expression.ValueExpression);
        Assert.Equal("image=localhost:5000/namespace/tools:v1", await expression.GetValueAsync(default));
    }

    [Fact]
    public async Task ExplicitRepositoryNeverOverridesTheRegistryEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com", "namespace");
        registry.WithPushedImage(image, repository: "other.example.com/team/tools", tag: "release");

        Assert.Equal("registry.example.com/other.example.com/team/tools:release",
            await ((IValueProvider)image.GetImageReference(registry)).GetValueAsync(default));
    }

    [Fact]
    public async Task EmptyRegistryNamespaceDoesNotAddAnEmptyPathSegment()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com", "");
        registry.WithPushedImage(image);
        var reference = image.GetImageReference(registry);

        Assert.Equal("registry.example.com/tools:latest", reference.GetImageExpression().ValueExpression);
        Assert.Equal("registry.example.com/tools:latest", await ((IValueProvider)reference).GetValueAsync(default));
    }

    [Fact]
    public async Task DestinationRegistryParametersRemainDeferred()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var endpoint = builder.AddParameter("endpoint");
        var repository = builder.AddParameter("repository");
        var registry = builder.AddContainerRegistry("registry", endpoint, repository);
        var image = builder.AddContainerImage("tools", "busybox:v1");
        registry.WithPushedImage(image);
        var reference = image.GetImageReference(registry);

        Assert.Equal("{endpoint.value}/{repository.value}/tools:v1", reference.GetImageExpression().ValueExpression);
        builder.Configuration["Parameters:endpoint"] = "registry.example.com:5000";
        builder.Configuration["Parameters:repository"] = "team";
        Assert.Equal("registry.example.com:5000/team/tools:v1", await ((IValueProvider)reference).GetValueAsync(default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://registry.example.com")]
    [InlineData("user:password@registry.example.com")]
    [InlineData("registry.example.com/path")]
    public async Task InvalidResolvedRegistryEndpointFailsExplicitly(string endpointValue)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var endpoint = builder.AddParameter("endpoint");
        var registry = builder.AddContainerRegistry("registry", endpoint);
        var image = builder.AddContainerImage("tools", "busybox");
        registry.WithPushedImage(image);
        builder.Configuration["Parameters:endpoint"] = endpointValue;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => ((IValueProvider)image.GetImageReference(registry)).GetValueAsync(default).AsTask());
    }

    [Fact]
    public async Task DigestOnlySourcesUseDeterministicDestinationTags()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox@" + Digest);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        registry.WithPushedImage(image);

        Assert.Equal("registry.example.com/tools:" + Digest.Replace(':', '-'),
            await ((IValueProvider)image.GetImageReference(registry)).GetValueAsync(default));
    }

    [Fact]
    public void SourceAndDestinationLengthLimitsAreEnforced()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var digest = "sha512:" + new string('a', 128);
        var image = builder.AddContainerImage("tools", "busybox@" + digest);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        registry.WithPushedImage(image);

        Assert.Equal(128, Assert.Single(image.Resource.GetPublications()).Tag.Length);
        Assert.Equal(digest.Replace(':', '-')[..128], image.Resource.DefaultTag);
        Assert.Throws<ArgumentException>(() => builder.AddContainerImage("long-source", new string('a', 256)));
        Assert.Throws<ArgumentException>(() => registry.WithPushedImage(image, repository: new string('a', 256)));
        Assert.Throws<ArgumentException>(() => registry.WithPushedImage(image, tag: new string('a', 129)));
    }

    [Fact]
    public async Task ResolvedDestinationLengthLimitIncludesTheRegistryEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var validRepository = new string('a', 255 - "registry.example.com/".Length);
        registry.WithPushedImage(image, repository: validRepository);

        var reference = image.GetImageReference(registry);
        Assert.Equal("registry.example.com/" + validRepository + ":latest",
            await ((IValueProvider)reference).GetValueAsync(default));

        var tooLong = builder.AddContainerImage("long-tools", "busybox");
        registry.WithPushedImage(tooLong, repository: validRepository + "a");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IValueProvider)tooLong.GetImageReference(registry)).GetValueAsync(default).AsTask());
    }

    [Fact]
    public async Task DestinationReferenceCannotRetainAnAssociationRemovedByLastWinsSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        image.WithContainerRegistry(first);
        var reference = image.GetImageReference(first);
        image.WithContainerRegistry(second);

        Assert.Throws<InvalidOperationException>(() => reference.ValueExpression);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IValueProvider)reference).GetValueAsync(default).AsTask());
        Assert.Same(second.Resource, Assert.Single(image.Resource.GetPublications()).Registry);
    }

    [Fact]
    public void BuildersFromDifferentApplicationsAreRejected()
    {
        using var first = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var second = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = first.AddContainerImage("tools", "busybox");
        var registry = second.AddContainerRegistry("registry", "registry.example.com");

        Assert.Throws<ArgumentException>(() => registry.WithPushedImage(image));
        Assert.Throws<ArgumentException>(() => image.GetImageReference(registry));
        Assert.Throws<ArgumentException>(() => image.WithContainerRegistry(registry));
        Assert.Empty(image.Resource.GetPublications());
    }

    [Fact]
    public async Task ManifestCapturesSourceAndAllExplicitDestinationsWithoutResolvingParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox:v1@" + Digest);
        var endpoint = builder.AddParameter("endpoint");
        var repository = builder.AddParameter("repository");
        var first = builder.AddContainerRegistry("first", endpoint, repository);
        var second = builder.AddContainerRegistry("second", "second.example.com", "ignored-namespace");
        second.WithPushedImage(image, repository: "team/tools", tag: "release");
        first.WithPushedImage(image);
        builder.AddContainer("consumer", "busybox")
            .WithEnvironment("TOOLS_IMAGE", image.GetImageReference(first));

        var manifest = await ManifestUtils.GetManifestForModel(new DistributedApplicationModel(builder.Resources));

        await Verify(manifest.ToJsonString(new() { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task ManifestWithoutAssociationsDoesNotSelectADefaultRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools", "busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        image.WithAnnotation(new RegistryTargetAnnotation(registry.Resource));

        var manifest = await ManifestUtils.GetManifest(image.Resource);

        await Verify(manifest.ToJsonString(new() { WriteIndented = true }), "json");
    }
}
