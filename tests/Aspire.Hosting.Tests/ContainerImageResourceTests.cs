// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE003
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.Utils;
using Aspire.Hosting.Tests.TestServices;
using Aspire.Hosting.Tests.Utils;
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
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var destination = registry.AddImage("published", image);
        var consumer = builder.AddContainer("consumer", "busybox");
        IDistributedApplicationBuilder nullBuilder = null!;
        IResourceBuilder<ContainerImageResource> nullImage = null!;
        IResourceBuilder<ContainerRegistryResource> nullRegistry = null!;
        IResourceBuilder<DestinationImageResource> nullDestination = null!;

        Assert.Throws<ArgumentNullException>(() => nullBuilder.AddContainerImage("tools"));
        Assert.Throws<ArgumentNullException>(() => builder.AddContainerImage(null!));
        Assert.Throws<ArgumentException>(() => builder.AddContainerImage(""));
        Assert.Throws<ArgumentNullException>(() => nullImage.WithImageSource("busybox"));
        Assert.Throws<ArgumentNullException>(() => image.WithImageSource(null!));
        Assert.Throws<ArgumentNullException>(() => nullRegistry.AddImage("destination", image));
        Assert.Throws<ArgumentNullException>(() => registry.AddImage("destination", nullImage));
        Assert.Throws<ArgumentNullException>(() => registry.AddImage(null!, image));
        Assert.Throws<ArgumentException>(() => registry.AddImage("", image));
        Assert.Throws<ArgumentNullException>(() => consumer.WithEnvironment("IMAGE", nullDestination));
        Assert.Throws<ArgumentNullException>(() => consumer.WithReference(nullDestination));
        Assert.Throws<ArgumentNullException>(() => new ContainerImageRegistryTargetAnnotation((IContainerRegistry)null!));
        Assert.Throws<ArgumentNullException>(() => new ContainerImageRegistryTargetAnnotation((Func<IContainerRegistry?>)null!));
        Assert.Same(registry.Resource, destination.Resource.Parent);
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
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddContainerImage("tools").WithImageSource(image).Resource;

        Assert.Equal(normalized, resource.GetSource().Image);
        Assert.Equal(new ContainerReference(registry, repository, tag, digest), resource.GetSource().Source);
        Assert.IsAssignableFrom<IResourceWithoutLifetime>(resource);
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
    [InlineData("user:password@registry.example.com/tools")]
    [InlineData("image@sha256:1234")]
    [InlineData("image@digest")]
    [InlineData("image:")]
    [InlineData("image:-invalid")]
    [InlineData("registry.example.com:70000/tools")]
    public void InvalidSourcesAreRejected(string image)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddContainerImage("tools");
        var exception = Assert.ThrowsAny<ArgumentException>(() => resource.WithImageSource(image));

        Assert.Equal("image", exception.ParamName);
        Assert.IsNotType<ArgumentOutOfRangeException>(exception);
        Assert.Empty(resource.Resource.Annotations.OfType<ContainerImageSourceAnnotation>());
    }

    [Theory]
    [InlineData("https://user:password@registry.example.com/tools:v1", "The source must be a container image reference without a URL scheme or credentials.")]
    [InlineData("user:password@registry.example.com/tools", "The source digest must contain a SHA-256, SHA-384, or SHA-512 algorithm and its full hexadecimal digest.")]
    public void CredentialBearingSourcesAreRejectedWithSafeMessages(string image, string message)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var source = builder.AddContainerImage("tools");
        var exception = Assert.Throws<ArgumentException>(() => source.WithImageSource(image));

        Assert.Equal($"{message} (Parameter 'image')", exception.Message);
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run, false)]
    [InlineData(DistributedApplicationOperation.Publish, true)]
    public void ArtifactRegistrationIsPublishOnly(DistributedApplicationOperation operation, bool inModel)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var destination = registry.AddImage("published", source);
        var container = builder.AddContainer("consumer", "busybox");
        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        Assert.Equal(inModel, model.Resources.Contains(source.Resource));
        Assert.Equal(inModel, model.Resources.Contains(destination.Resource));
        Assert.Equal([container.Resource], model.GetComputeResources().ToArray());
        Assert.Empty(model.GetBuildResources());
        Assert.Empty(model.GetBuildAndPushResources());
    }

    [Fact]
    public void DestinationsAreNamedResourcesAndCanShareASourceAndRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox:v1");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        var one = first.AddImage("one", source);
        var two = first.AddImage("two", source);
        var three = second.AddImage("three", source);

        Assert.Collection(source.Resource.GetPublications(),
            publication => Assert.Same(one.Resource, publication.Destination),
            publication => Assert.Same(two.Resource, publication.Destination),
            publication => Assert.Same(three.Resource, publication.Destination));
        Assert.Same(source.Resource, one.Resource.Source);
        Assert.Same(first.Resource, one.Resource.Parent);
        Assert.IsAssignableFrom<IResourceWithoutLifetime>(one.Resource);
        Assert.IsAssignableFrom<IResourceWithParent<IResource>>(one.Resource);
        Assert.Throws<DistributedApplicationException>(() => second.AddImage("one", source));
        Assert.Equal(3, source.Resource.GetPublications().Count);
        Assert.Throws<InvalidOperationException>(() => source.WithContainerRegistry(first));
        Assert.Throws<InvalidOperationException>(() => one.WithContainerRegistry(second));
    }

    [Fact]
    public async Task MissingSourceFailsAtPublishPreparationNotArtifactConstruction()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddContainerImage("tools");
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => app.ExecuteBeforeStartHooksAsync(default));
        Assert.Equal("Image artifact 'tools' has no source. Configure it with WithImageSource, WithDockerfile, or WithDockerfileBuilder.", exception.Message);
    }

    [Fact]
    public async Task SourceChangesInvalidatePublishedDigestsAndInvalidChangesPreserveState()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox:v1");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var destination = registry.AddImage("published", source);
        destination.Resource.RecordPublishedDigest(Digest);
        Assert.Throws<ArgumentException>(() => source.WithImageSource("https://invalid/image"));
        Assert.Equal("registry.example.com/published@" + Digest, await ((IValueProvider)destination.Resource).GetValueAsync(default));

        source.WithImageSource("busybox:v2");
        Assert.Equal("docker.io/library/busybox:v2", source.Resource.GetSource().Image);
        Assert.Single(source.Resource.Annotations.OfType<ContainerImageSourceAnnotation>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IValueProvider)destination.Resource).GetValueAsync(default).AsTask());
    }

    [Theory]
    [InlineData("busybox:latest")]
    [InlineData("busybox@" + Digest)]
    public async Task DestinationDoesNotInventAPublishedDigest(string image)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource(image);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var destination = registry.AddImage("published", source);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => ((IValueProvider)destination.Resource).GetValueAsync(default).AsTask());
        Assert.Equal("Destination image 'published' has no published digest for its current source. Publish the image before resolving its value.", exception.Message);
        Assert.Throws<ArgumentException>(() => destination.Resource.RecordPublishedDigest("sha256:1234"));
    }

    [Fact]
    public async Task DestinationReferencesAreDigestQualifiedAndRetainProvenance()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox:v1");
        var registry = builder.AddContainerRegistry("registry", "localhost:5000", "team");
        var destination = registry.AddImage("Published", source);
        destination.Resource.RecordPublishedDigest(Digest);

        Assert.Equal("{Published.image}", destination.Resource.ValueExpression);
        Assert.Equal("localhost:5000/team/published@" + Digest, await ((IValueProvider)destination.Resource).GetValueAsync(default));
        Assert.Collection(destination.Resource.References,
            resource => Assert.Same(destination.Resource, resource),
            resource => Assert.Same(source.Resource, resource),
            resource => Assert.Same(registry.Resource, resource));
        var expression = ReferenceExpression.Create($"image={destination.Resource}");
        Assert.Equal("image={Published.image}", expression.ValueExpression);
        Assert.Equal("image=localhost:5000/team/published@" + Digest, await expression.GetValueAsync(default));
    }

    [Fact]
    public async Task EnvironmentValuesAndConsumptionIntentRemainSeparate()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var destination = registry.AddImage("published", source);
        var consumer = builder.AddContainer("consumer", "busybox");

        Assert.Same(consumer, consumer.WithEnvironment("IMAGE_NAME", destination));
        Assert.Empty(consumer.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>());
        Assert.Same(consumer, consumer.WithReference(destination));
        consumer.WithReference(destination);
        Assert.Same(destination.Resource, Assert.Single(consumer.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>()).Image);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Publish);
        Assert.Equal("{published.image}", environment["IMAGE_NAME"]);
        Assert.Equal("{published.tag}", environment["PUBLISHED_TAG"]);
        Assert.Equal("{published.sha256}", environment["PUBLISHED_SHA256"]);
        Assert.Equal("{published.registryEndpoint}", environment["PUBLISHED_REGISTRY"]);
        Assert.Equal("{published.repository}", environment["PUBLISHED_REPOSITORY"]);
    }

    [Fact]
    public async Task DestinationPropertiesDescribeTheVerifiedPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("source").WithImageSource("ghcr.io/example/tools:source-tag");
        var image = builder.AddContainerRegistry("registry", "localhost:5000", "team/nested").AddImage("Published", source);
        var properties = new Dictionary<string, ReferenceExpression>
        {
            ["image"] = image.Resource.ImageExpression,
            ["tag"] = image.Resource.TagExpression,
            ["sha256"] = image.Resource.Sha256Expression,
            ["registryEndpoint"] = image.Resource.RegistryExpression,
            ["repository"] = image.Resource.RepositoryExpression
        };
        foreach (var (name, expression) in properties)
        {
            Assert.Equal($"{{Published.{name}}}", expression.ValueExpression);
            await Assert.ThrowsAsync<InvalidOperationException>(() => expression.GetValueAsync(default).AsTask());
        }
        image.Resource.RecordPublishedImage("localhost:5000/team/nested/published", Digest, "destination-tag");
        var values = new Dictionary<string, string?>();
        foreach (var (name, expression) in properties)
        {
            values.Add(name, await expression.GetValueAsync(default));
        }
        await Verify(values);

        source.WithImageSource("busybox:v2");
        foreach (var expression in properties.Values)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => expression.GetValueAsync(default).AsTask());
        }
    }

    [Theory]
    [InlineData("image")]
    [InlineData("tag")]
    [InlineData("sha256")]
    [InlineData("registry")]
    [InlineData("repository")]
    public async Task IndividualImagePropertiesPreservePublicationDependencies(string property)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("source").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var image = registry.AddImage("published", source);
        var value = property switch
        {
            "image" => image.Resource.ImageExpression,
            "tag" => image.Resource.TagExpression,
            "sha256" => image.Resource.Sha256Expression,
            "registry" => image.Resource.RegistryExpression,
            "repository" => image.Resource.RepositoryExpression,
            _ => throw new InvalidOperationException()
        };
        var consumer = builder.AddContainer("consumer", "busybox").WithEnvironment("CUSTOM", value);
        var dependencies = await consumer.Resource.GetResourceDependenciesAsync(builder.ExecutionContext);
        Assert.Equal(new IResource[] { image.Resource, source.Resource, registry.Resource }.OrderBy(resource => resource.Name),
            dependencies.OrderBy(resource => resource.Name));
        Assert.Empty(consumer.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("worker-image", false)]
    [InlineData("worker-image", true)]
    public async Task ImageReferenceInjectsPropertiesWithTheDefaultOrAliasedPrefix(string? name, bool dispatcher)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("source").WithImageSource("busybox");
        var image = builder.AddContainerRegistry("registry", "registry.example.com", "team").AddImage("published-image", source);
        var consumer = builder.AddContainer("consumer", "busybox");
        if (dispatcher)
        {
            ResourceBuilderExtensions.WithReference(consumer, (object)image, name: name);
        }
        else
        {
            consumer.WithReference(image, name);
        }
        consumer.WithReference(image, name);
        var prefix = name is null ? "PUBLISHED_IMAGE_" : "WORKER_IMAGE_";
        var deferred = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Publish);
        Assert.Equal(5, deferred.Count);
        Assert.Equal("{published-image.image}", deferred[$"{prefix}IMAGE"]);
        Assert.Single(consumer.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>());
        image.Resource.RecordPublishedImage("registry.example.com/team/published-image", Digest, "publication-tag");
        var resolved = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource);
        await Verify(new { Deferred = deferred, Resolved = resolved });
    }

    [Fact]
    public async Task ImageReferencesCanUseDistinctAliasesButCannotCollideAfterEncoding()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("source").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var first = registry.AddImage("first-image", source);
        var second = registry.AddImage("second-image", source);
        var consumer = builder.AddContainer("consumer", "busybox").WithReference(first, name: "worker-image");
        consumer.WithReference(first, name: "other");
        Assert.Throws<DistributedApplicationException>(() => consumer.WithReference(second, name: "WORKER_IMAGE"));
        consumer.WithReference(second, name: "second");
        Assert.Equal(3, consumer.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>().Count());
        var values = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Publish);
        await Verify(values);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ImageReferenceAliasMustNotBeEmpty(string name)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("source").WithImageSource("busybox");
        var image = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var consumer = builder.AddContainer("consumer", "busybox");
        Assert.Throws<ArgumentException>(() => consumer.WithReference(image, name));
        Assert.Empty(consumer.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>());
    }

    [Fact]
    public async Task DisablingPropertyInjectionRetainsImageConsumption()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("source").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var image = registry.AddImage("published", source);
        var consumer = builder.AddContainer("consumer", "busybox")
            .WithReferenceEnvironment(ReferenceEnvironmentInjectionFlags.None)
            .WithReference(image);
        Assert.Empty(await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Publish));
        Assert.Same(image.Resource, Assert.Single(consumer.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>()).Image);
        Assert.Equal(3, (await consumer.Resource.GetResourceDependenciesAsync(builder.ExecutionContext)).Count);
    }

    [Fact]
    public async Task ImagePropertiesRejectEvidenceForAnotherRepositoryAndClearOnRetry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("source").WithImageSource("busybox");
        var image = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        image.Resource.RecordPublishedImage("other.example.com/published", Digest, "old-tag");
        var properties = new[] { image.Resource.ImageExpression, image.Resource.TagExpression, image.Resource.Sha256Expression,
            image.Resource.RegistryExpression, image.Resource.RepositoryExpression };
        foreach (var property in properties)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => property.GetValueAsync(default).AsTask());
        }
        image.Resource.RecordPublishedImage("registry.example.com/published", Digest, "new-tag");
        Assert.Equal("new-tag", await image.Resource.TagExpression.GetValueAsync(default));
        image.Resource.ClearPublishedDigest();
        foreach (var property in properties)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => property.GetValueAsync(default).AsTask());
        }
    }

    [Fact]
    public async Task SHA256PropertyDoesNotMisrepresentAnotherAlgorithm()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("source").WithImageSource("busybox");
        var image = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var digest = "sha512:" + new string('a', 128);
        image.Resource.RecordPublishedImage("registry.example.com/published", digest, "tag");
        Assert.Equal("registry.example.com/published@" + digest, await image.Resource.ImageExpression.GetValueAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => image.Resource.Sha256Expression.GetValueAsync(default).AsTask());
    }

    [Fact]
    public async Task RunModeDoesNotSubstituteSourceValuesForImageProperties()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var source = builder.AddContainerImage("source").WithImageSource("busybox@" + Digest);
        var image = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var consumer = builder.AddContainer("consumer", "busybox").WithReference(image);
        var exception = await Assert.ThrowsAsync<AggregateException>(() =>
            EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource).AsTask());
        Assert.Equal(5, exception.InnerExceptions.Count);
        Assert.All(exception.InnerExceptions, error =>
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal("Destination image 'published' has no published digest for its current source. Publish the image before resolving its value.", error.Message);
        });
    }

    [Theory]
    [InlineData("", "registry.example.com:5000/published@")]
    [InlineData("team", "registry.example.com:5000/team/published@")]
    public async Task EmptyAndParameterBackedRegistryNamespacesResolveConsistentlyInProcess(string namespaceValue, string qualifiedRepository)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var endpoint = builder.AddParameter("endpoint");
        var repository = builder.AddParameter("repository");
        var registry = builder.AddContainerRegistry("registry", endpoint, repository);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox:v1");
        var destination = registry.AddImage("published", source);
        destination.Resource.RecordPublishedDigest(Digest);
        Assert.Equal("{endpoint.value}/{repository.value}/published@{published.digest}", destination.Resource.GetImageExpression().ValueExpression);
        builder.Configuration["Parameters:endpoint"] = "registry.example.com:5000";
        builder.Configuration["Parameters:repository"] = namespaceValue;
        Assert.Equal(qualifiedRepository + Digest, await ((IValueProvider)destination.Resource).GetValueAsync(default));
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
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = registry.AddImage("published", source);
        destination.Resource.RecordPublishedDigest(Digest);
        builder.Configuration["Parameters:endpoint"] = endpointValue;

        await Assert.ThrowsAnyAsync<ArgumentException>(() => ((IValueProvider)destination.Resource).GetValueAsync(default).AsTask());
    }

    [Fact]
    public async Task UnassociatedImageAdoptsRegistryAfterEnvironmentPreparation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox:v1");
        var endpoint = builder.AddParameter("endpoint");
        var registry = builder.AddContainerRegistry("registry", endpoint);
        var environment = builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        var publication = Assert.Single(source.Resource.GetPublications());
        Assert.Same(registry.Resource, publication.Registry);
        Assert.Same(environment.Resource, publication.DefaultEnvironment);
        Assert.Equal("{tools-registry.image}", publication.Destination.ValueExpression);
        Assert.Empty(source.Resource.Annotations.OfType<DeploymentTargetAnnotation>());
        Assert.Empty(app.Services.GetRequiredService<DistributedApplicationModel>().GetComputeResources());
        var manifest = await ManifestUtils.GetManifest(source.Resource);
        await Verify(manifest.ToJsonString(new() { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task ExplicitAssociationSuppressesAllEnvironmentDefaults()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("first-env") { ImageRegistry = first.Resource });
        builder.AddResource(new TestImageComputeEnvironmentResource("second-env") { ImageRegistry = second.Resource });
        var destination = first.AddImage("published", image);
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        var publication = Assert.Single(image.Resource.GetPublications());
        Assert.Same(destination.Resource, publication.Destination);
        Assert.Null(publication.DefaultEnvironment);
    }

    [Fact]
    public async Task NonPublicationAssociationSuppressesAutomaticPublication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
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
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
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
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
        var other = builder.AddContainerImage("other").WithImageSource("busybox");
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
            "Create each destination explicitly using 'registry.AddImage(name, image)'.", exception.Message);
        Assert.Empty(image.Resource.GetPublications());
        Assert.Empty(other.Resource.GetPublications());
    }

    [Fact]
    public async Task InferredDestinationIsRecomputedAndRemovedWhenDefaultDisappears()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
        var first = builder.AddContainerRegistry("first", "first.example.com");
        var second = builder.AddContainerRegistry("second", "second.example.com");
        var environment = builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = first.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        var original = Assert.Single(image.Resource.GetPublications()).Destination;
        environment.Resource.ImageRegistry = second.Resource;
        await app.ExecuteBeforeStartHooksAsync(default);

        Assert.Same(second.Resource, Assert.Single(image.Resource.GetPublications()).Registry);
        Assert.Throws<InvalidOperationException>(() => original.ValueExpression);
        Assert.False(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.Contains(original));
        environment.Resource.ImageRegistry = null;
        await app.ExecuteBeforeStartHooksAsync(default);
        Assert.Empty(image.Resource.GetPublications());
        Assert.Empty(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DestinationImageResource>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExcludedImagesAndEnvironmentsDoNotParticipateInDefaultAdoption(bool excludeImage)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        var environment = builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        if (excludeImage)
        {
            image.ExcludeFromManifest();
        }
        else
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
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com").ExcludeFromManifest();
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));
        Assert.Equal("Compute environment 'env' advertises image registry 'registry', which is not a publishable container registry resource in the application model.", exception.Message);
        Assert.Empty(image.Resource.GetPublications());
        app.Services.GetRequiredService<DistributedApplicationModel>().Resources.Remove(registry.Resource);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));
    }

    [Fact]
    public void BuildersFromDifferentApplicationsAreRejected()
    {
        using var first = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var second = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = first.AddContainerImage("tools").WithImageSource("busybox");
        var registry = second.AddContainerRegistry("registry", "registry.example.com");
        Assert.Throws<ArgumentException>(() => registry.AddImage("published", image));
        var localRegistry = first.AddContainerRegistry("local-registry", "registry.example.com");
        var destination = localRegistry.AddImage("published", image);
        var consumer = second.AddContainer("consumer", "busybox");
        Assert.Throws<ArgumentException>(() => consumer.WithEnvironment("IMAGE", destination));
        Assert.Throws<ArgumentException>(() => consumer.WithReference(destination));
    }

    [Fact]
    public async Task ManifestCapturesSourceAndAllExplicitDestinationsWithoutResolvingParameters()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("ghcr.io/example/tools:v1");
        var endpoint = builder.AddParameter("endpoint");
        var repository = builder.AddParameter("repository");
        var first = builder.AddContainerRegistry("first", endpoint, repository);
        var second = builder.AddContainerRegistry("second", "second.example.com");
        var firstImage = first.AddImage("first-tools", source);
        second.AddImage("second-tools", source);
        builder.AddContainer("consumer", "busybox")
            .WithEnvironment("TOOLS_IMAGE", firstImage);
        using var app = builder.Build();
        var manifest = await ManifestUtils.GetManifestForModel(app.Services.GetRequiredService<DistributedApplicationModel>());
        await Verify(manifest.ToJsonString(new() { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task ManifestWithoutAssociationsDoesNotSelectADefaultRegistry()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
        builder.AddContainerRegistry("registry", "registry.example.com");
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        Assert.Empty(image.Resource.GetPublications());
        var manifest = await ManifestUtils.GetManifest(image.Resource);
        await Verify(manifest.ToJsonString(new() { WriteIndented = true }), "json");
    }

    [Fact]
    public async Task LateExplicitDestinationImmediatelyRemovesOnlyInferredResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);
        var inferred = Assert.Single(source.Resource.GetPublications()).Destination;
        var destination = registry.AddImage("explicit-tools", source);
        var another = registry.AddImage("another", source);

        Assert.Throws<InvalidOperationException>(() => inferred.ValueExpression);
        Assert.Equal([destination.Resource, another.Resource],
            app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DestinationImageResource>().ToArray());
        await app.ExecuteBeforeStartHooksAsync(default);
        Assert.Equal([destination.Resource, another.Resource], source.Resource.GetPublications().Select(publication => publication.Destination).ToArray());
    }

    [Fact]
    public async Task DestinationDispatchersAcceptResourceBuildersWithoutConnectionStringSemantics()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var destination = builder.AddContainerRegistry("registry", "registry.example.com").AddImage("published", source);
        var consumer = builder.AddContainer("consumer", "busybox");

        Assert.Same(consumer, ResourceBuilderExtensions.WithEnvironment(consumer, "IMAGE_NAME", (object)destination));
        Assert.Same(consumer, ResourceBuilderExtensions.WithReference(consumer, (object)destination));
        Assert.Same(destination.Resource, Assert.Single(consumer.Resource.Annotations.OfType<DestinationImageReferenceAnnotation>()).Image);
        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() =>
            ResourceBuilderExtensions.WithReference(consumer, (object)destination, optional: true));
        Assert.IsType<InvalidOperationException>(exception.InnerException);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(consumer.Resource, DistributedApplicationOperation.Publish);
        Assert.Equal("{published.image}", environment["IMAGE_NAME"]);
    }

    [Fact]
    public async Task RunModeNeverAdoptsDefaultsEvenForManuallyRegisteredArtifacts()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var image = builder.AddContainerImage("tools").WithImageSource("busybox");
        builder.AddResource(image.Resource);
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(default);

        Assert.Empty(image.Resource.GetPublications());
        Assert.Empty(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<DestinationImageResource>());
    }

    [Fact]
    public async Task ResolvedDestinationLengthLimitIncludesTheRegistryEndpoint()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var repository = builder.AddParameter("repository");
        var endpoint = builder.AddParameter("endpoint");
        builder.Configuration["Parameters:endpoint"] = "registry.example.com";
        var registry = builder.AddContainerRegistry("registry", endpoint, repository);
        var destination = registry.AddImage("published", source);
        destination.Resource.RecordPublishedDigest(Digest);
        builder.Configuration["Parameters:repository"] = new string('a', 240);

        await Assert.ThrowsAsync<InvalidOperationException>(() => ((IValueProvider)destination.Resource).GetValueAsync(default).AsTask());
        Assert.Throws<ArgumentException>(() => source.WithImageSource(new string('a', 250)));
    }

    [Fact]
    public async Task InferredNamesNeverOverwriteUserResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var source = builder.AddContainerImage("tools").WithImageSource("busybox");
        var registry = builder.AddContainerRegistry("registry", "registry.example.com");
        builder.AddResource(new TestImageComputeEnvironmentResource("env") { ImageRegistry = registry.Resource });
        var existing = builder.AddParameter("tools-registry");
        using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(() => app.ExecuteBeforeStartHooksAsync(default));
        Assert.Equal("Default destination image name 'tools-registry' conflicts with another resource. Create an explicitly named destination with registry.AddImage(name, image).", exception.Message);
        Assert.Empty(source.Resource.GetPublications());
        Assert.True(app.Services.GetRequiredService<DistributedApplicationModel>().Resources.Contains(existing.Resource));
    }
}
