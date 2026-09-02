// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Reflection;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class ResourceProjectionTests
{
    [Fact]
    public async Task SelectedProjectionIsAuthoritativeAndOwnerRemainsSoleModelMember()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithAnnotation(new ContainerImageAnnotation
            {
                Image = "legacy-owner-image",
                Tag = "latest"
            })
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container =>
                {
                    container.WithImage("projected-image:v2");
                    container.WithVolume("projection-data", "/var/data");
                    container.WithEnvironment("PROJECTION_SETTING", "projection");
                })
            .WithEnvironment("OWNER_SETTING", "owner");

        Assert.Collection(builder.Resources, resource => Assert.Same(executable.Resource, resource));

        var projection = Assert.IsType<ContainerResourceProjection<ExecutableResource>>(
            executable.Resource.GetEffectiveResource(builder.ExecutionContext));
        var model = new DistributedApplicationModel(builder.Resources);
        Assert.True(executable.Resource.IsContainer());
        Assert.Collection(
            model.Resources,
            resource => Assert.Same(projection, resource));
        Assert.True(model.Resources.TryGetByName("worker", out var effectiveResource));
        Assert.Same(projection, effectiveResource);
        Assert.Collection(
            model.GetResourceOwners(),
            resource => Assert.Same(executable.Resource, resource));
        Assert.Collection(
            model.GetContainerResources(),
            resource => Assert.Same(projection, resource));
        Assert.Collection(
            model.GetComputeResources(),
            resource => Assert.Same(projection, resource));
        Assert.Empty(model.GetExecutableResources());
        Assert.True(builder.TryCreateResourceBuilder<ExecutableResource>("worker", out var ownerBuilder));
        Assert.Same(executable.Resource, ownerBuilder.Resource);
        Assert.True(builder.TryCreateResourceBuilder<ContainerResource>("worker", out var projectionBuilder));
        Assert.Same(projection, projectionBuilder.Resource);

        Assert.Same(executable.Resource, projection.Owner);
        Assert.NotSame(executable.Resource, projection);
        Assert.True(projection.TryGetContainerImageName(out var projectionImage));
        Assert.Equal("projected-image:v2", projectionImage);
        Assert.True(executable.Resource.TryGetContainerImageName(out var ownerImage));
        Assert.Equal("legacy-owner-image:latest", ownerImage);

        var mount = Assert.Single(projection.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("projection-data", mount.Source);
        Assert.Empty(executable.Resource.Annotations.OfType<ContainerMountAnnotation>());

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            projection,
            DistributedApplicationOperation.Publish,
            TestServiceProvider.Instance);

        Assert.Equal("owner", environment["OWNER_SETTING"]);
        Assert.Equal("projection", environment["PROJECTION_SETTING"]);
    }

    [Fact]
    public void ManifestCallbackAddedAfterProjectionRemainsAuthoritative()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile()
            .ExcludeFromManifest();

        var projection = executable.Resource.GetEffectiveResource(builder.ExecutionContext);

        Assert.True(executable.Resource.IsExcludedFromPublish());
        Assert.True(projection.IsExcludedFromPublish());
    }

    [Fact]
    public void ProjectionLocalManifestExclusionIsAuthoritative()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container
                    .WithImage("projected-image")
                    .ExcludeFromManifest());
        var model = new DistributedApplicationModel(builder.Resources);

        Assert.True(executable.Resource.IsExcludedFromPublish());
        Assert.Empty(model.GetComputeResources());
    }

    [Fact]
    public void BuildResourceEnumerationReturnsEffectiveProjection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile();
        var projection = executable.Resource.GetEffectiveResource(builder.ExecutionContext);
        var model = new DistributedApplicationModel(builder.Resources);

        Assert.Same(projection, Assert.Single(model.GetBuildResources()));
        Assert.Same(projection, Assert.Single(model.GetBuildAndPushResources()));
    }

    [Fact]
    public async Task DependencyDiscoveryUsesProjectionConfigurationAndReturnsCanonicalResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var parameter = builder.AddParameter("secret");
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container
                    .WithImage("projected-image")
                    .WithEnvironment("SECRET", parameter));

        var dependencies = await executable.Resource.GetResourceDependenciesAsync(
            builder.ExecutionContext,
            ResourceDependencyDiscoveryMode.DirectOnly,
            TestContext.Current.CancellationToken);

        Assert.Collection(dependencies, dependency => Assert.Same(parameter.Resource, dependency));
    }

    [Fact]
    public void CanonicalEndpointReferencesResolveProjectionOnlyEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".");
        var endpointReference = executable.GetEndpoint("projected");

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container => container
                .WithImage("projected-image")
                .WithHttpEndpoint(targetPort: 8080, name: "projected"));

        var projection = executable.Resource.GetEffectiveResource(builder.ExecutionContext);
        var endpoint = Assert.Single(projection.Annotations.OfType<EndpointAnnotation>());

        Assert.True(endpointReference.Exists);
        Assert.Same(executable.Resource, endpointReference.Resource);
        Assert.Same(endpoint, endpointReference.EndpointAnnotation);
        Assert.Same(endpoint, Assert.Single(executable.Resource.GetEndpoints()).EndpointAnnotation);
    }

    [Fact]
    public void CanonicalEndpointReferencesUseProjectedConfigurationForExistingEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithHttpEndpoint(targetPort: 5000);
        var endpointReference = executable.GetEndpoint("http");

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container => container
                .WithImage("projected-image")
                .WithEndpoint("http", endpoint => endpoint.TargetPort = 8080, createIfNotExists: false));

        Assert.Same(executable.Resource, endpointReference.Resource);
        Assert.Equal(8080, endpointReference.EndpointAnnotation.TargetPort);
    }

    [Fact]
    public async Task ProjectionLocalResourceEventSubscriptionsUseOwnerIdentity()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        ContainerResource? callbackResource = null;
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container
                    .WithImage("projected-image")
                    .OnResourceReady((resource, _, _) =>
                    {
                        callbackResource = resource;
                        return Task.CompletedTask;
                    }));
        var projection = Assert.IsAssignableFrom<ContainerResource>(
            executable.Resource.GetEffectiveResource(builder.ExecutionContext));

        await builder.Eventing.PublishAsync(
            new ResourceReadyEvent(executable.Resource, TestServiceProvider.Instance),
            TestContext.Current.CancellationToken);

        Assert.Same(projection, callbackResource);
    }

    [Fact]
    public async Task OwnerNotificationUsesEffectiveResourceShape()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile();
        using var notificationService = ResourceNotificationServiceTestHelpers.Create();

        await notificationService.PublishUpdateAsync(executable.Resource, state => state);

        Assert.True(notificationService.TryGetCurrentState(executable.Resource.Name, out var resourceEvent));
        Assert.Same(executable.Resource, resourceEvent.Resource);
        Assert.Equal(KnownResourceTypes.Container, resourceEvent.Snapshot.ResourceType);
    }

    [Fact]
    public async Task ProjectionLocalCommandsExecuteThroughCanonicalOwner()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var invoked = false;
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container
                    .WithImage("projected-image")
                    .WithCommand(
                        "projected-command",
                        "Projected command",
                        _ =>
                        {
                            invoked = true;
                            return Task.FromResult(new ExecuteCommandResult { Success = true });
                        }));
        using var app = builder.Build();

        var result = await app.ResourceCommands.ExecuteCommandAsync(
            executable.Resource,
            "projected-command",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.True(invoked);
    }

    [Fact]
    public void EffectiveModelCollectionMutationsPreserveOwnerMembership()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile();
        var model = new DistributedApplicationModel(builder.Resources);
        var projection = Assert.Single(model.Resources);

        Assert.True(model.Resources.Contains(executable.Resource));
        Assert.Equal(0, model.Resources.IndexOf(projection));
        Assert.True(model.Resources.Remove(projection));
        Assert.Empty(builder.Resources);
        Assert.Empty(model.GetResourceOwners());
    }

    [Fact]
    public void RepeatedExecutableProjectionPreservesArgumentsAddedAfterCreation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile()
            .WithArgs("--retained")
            .PublishAsDockerFile();
        var projection = executable.Resource.GetEffectiveResource(builder.ExecutionContext);

        Assert.Single(projection.Annotations.OfType<CommandLineArgsCallbackAnnotation>());
    }

    [Fact]
    public void CanonicalOwnerResolvesProjectionDeploymentTarget()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var target = builder.AddContainer("target", "target-image");
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container
                    .WithImage("projected-image")
                    .WithAnnotation(new DeploymentTargetAnnotation(target.Resource)));

        var annotation = Assert.IsType<DeploymentTargetAnnotation>(
            executable.Resource.GetDeploymentTargetAnnotation());

        Assert.Same(target.Resource, annotation.DeploymentTarget);
    }

    [Fact]
    public async Task LateHttp2ConfigurationAppliesToProjectedEndpoints()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithHttpEndpoint()
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container.WithImage("projected-image"))
            .WithAnnotation(new Http2ServiceAnnotation());
        var model = new DistributedApplicationModel(builder.Resources);

        await BuiltInDistributedApplicationEventSubscriptionHandlers.MutateHttp2TransportAsync(
            new BeforeStartEvent(TestServiceProvider.Instance, model),
            TestContext.Current.CancellationToken);

        var ownerEndpoint = Assert.Single(executable.Resource.Annotations.OfType<EndpointAnnotation>());
        var projection = Assert.Single(model.GetContainerResources());
        var projectionEndpoint = Assert.Single(projection.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", ownerEndpoint.Transport);
        Assert.Equal("http2", projectionEndpoint.Transport);
    }

    [Fact]
    public void ProjectionCallbackDoesNotMutateInheritedAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var dockerfile = new DockerfileBuildAnnotation(".", "Dockerfile", null)
        {
            ImageName = "owner-image",
            ImageTag = "owner-tag"
        };

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithHttpEndpoint()
            .WithAnnotation(dockerfile)
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container =>
                {
                    container.WithImage("projection-image");
                    container.WithEndpoint("http", endpoint => endpoint.TargetPort = 8080, createIfNotExists: false);
                    container.WithHttpEndpoint(targetPort: 8081, name: "projection-only");
                });

        executable.WithEndpoint("http", endpoint => endpoint.TargetPort = 9090, createIfNotExists: false)
            .WithHttpEndpoint(targetPort: 9091, name: "metrics")
            .WithHttpEndpoint(targetPort: 9092, name: "projection-only")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container.WithEndpoint(
                    "metrics",
                    endpoint => endpoint.TargetPort = 7070,
                    createIfNotExists: false));

        var projection = Assert.IsType<ContainerResourceProjection<ExecutableResource>>(
            executable.Resource.GetEffectiveResource(builder.ExecutionContext));

        Assert.Equal(
            9090,
            Assert.Single(executable.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "http").TargetPort);
        Assert.Equal(
            9091,
            Assert.Single(executable.Resource.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "metrics").TargetPort);
        Assert.Equal(
            8080,
            Assert.Single(projection.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "http").TargetPort);
        Assert.Equal(
            7070,
            Assert.Single(projection.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "metrics").TargetPort);
        Assert.Equal(
            8081,
            Assert.Single(projection.Annotations.OfType<EndpointAnnotation>(), endpoint => endpoint.Name == "projection-only").TargetPort);
        Assert.Equal("owner-image", dockerfile.ImageName);
        Assert.Equal("owner-tag", dockerfile.ImageTag);
    }

    [Fact]
    public void ProjectionOverrideRemainsAuthoritativeWhenOwnerAnnotationIsReplaced()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithAnnotation(new SingletonAnnotation("owner-before"))
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container.WithAnnotation(
                    new SingletonAnnotation("projection"),
                    ResourceAnnotationMutationBehavior.Replace))
            .WithAnnotation(
                new SingletonAnnotation("owner-after"),
                ResourceAnnotationMutationBehavior.Replace);

        var projection = executable.Resource.GetEffectiveResource(builder.ExecutionContext);

        Assert.Equal("owner-after", Assert.Single(executable.Resource.Annotations.OfType<SingletonAnnotation>()).Value);
        Assert.Equal("projection", Assert.Single(projection.Annotations.OfType<SingletonAnnotation>()).Value);
    }

    [Fact]
    public void LayeredAnnotationsPreserveIndexedCollectionSemantics()
    {
        var first = new FirstAnnotation();
        var second = new SecondAnnotation();
        var owner = new ResourceAnnotationCollection { first, second };
        var projection = new ResourceAnnotationCollection(owner);
        var prefix = new PrefixAnnotation();
        var replacement = new ReplacementAnnotation();

        projection.Insert(0, prefix);
        projection[1] = replacement;

        Assert.Collection(
            projection,
            annotation => Assert.Same(prefix, annotation),
            annotation => Assert.Same(replacement, annotation),
            annotation => Assert.Same(second, annotation));
    }

    [Fact]
    public void ReplacingOneInheritedIndexPreservesOtherAnnotationsOfTheSameType()
    {
        var first = new SingletonAnnotation("first");
        var second = new SingletonAnnotation("second");
        var owner = new ResourceAnnotationCollection { first, second };
        var projection = new ResourceAnnotationCollection(owner);
        var replacement = new SingletonAnnotation("replacement");

        projection[0] = replacement;

        Assert.Collection(
            projection,
            annotation => Assert.Same(replacement, annotation),
            annotation => Assert.Same(second, annotation));
    }

    [Fact]
    public void LayeredAnnotationsRemoveAtUsesCurrentIndexNotAPriorLookup()
    {
        var first = new FirstAnnotation();
        var second = new SecondAnnotation();
        var owner = new ResourceAnnotationCollection { first, second };
        var projection = new ResourceAnnotationCollection(owner);
        var itemsProperty = typeof(Collection<IResourceAnnotation>).GetProperty(
            "Items",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var items = Assert.IsAssignableFrom<IList<IResourceAnnotation>>(itemsProperty.GetValue(projection));

        var staleIndex = items.IndexOf(first);
        var prefix = new PrefixAnnotation();
        owner.Insert(0, prefix);
        projection.RemoveAt(staleIndex);

        // RemoveAt removes whatever currently occupies the index. Resolving the earlier IndexOf
        // result by identity instead would remove 'first' here and, more importantly, would remove
        // the wrong element for any caller that looked an item up and then removed a different one.
        Assert.Collection(
            projection,
            annotation => Assert.Same(first, annotation),
            annotation => Assert.Same(second, annotation));
    }

    [Fact]
    public void NoSelectedProjectionPreservesLegacyAnnotationDrivenResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithAnnotation(new ContainerImageAnnotation
            {
                Image = "legacy-image",
                Tag = "latest"
            });

        var effectiveResource = executable.Resource.GetEffectiveResource(builder.ExecutionContext);

        Assert.Same(executable.Resource, effectiveResource);
        Assert.True(effectiveResource.IsContainer());
    }

    [Fact]
    public void ProjectionSelectionIsOperationScoped()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container.WithImage("projected-image"));

        var runContext = new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run);

        Assert.Same(executable.Resource, executable.Resource.GetEffectiveResource(runContext));
        Assert.IsType<ContainerResourceProjection<ExecutableResource>>(
            executable.Resource.GetEffectiveResource(builder.ExecutionContext));
    }

    [Fact]
    public void MultipleSelectedProjectionsAreRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".");
        executable.Resource.Annotations.Add(new ResourceProjectionAnnotation(
            new OperationResourceProjectionSource(
                DistributedApplicationOperation.Publish,
                new ContainerResource("worker"))));
        executable.Resource.Annotations.Add(new ResourceProjectionAnnotation(
            new OperationResourceProjectionSource(
                DistributedApplicationOperation.Publish,
                new ContainerResource("worker"))));

        var exception = Assert.Throws<DistributedApplicationException>(
            () => executable.Resource.GetEffectiveResource(builder.ExecutionContext));

        Assert.Contains(executable.Resource.Name, exception.Message);
        Assert.Contains(DistributedApplicationOperation.Publish.ToString(), exception.Message);
    }

    [Fact]
    public void RegisteringASecondProjectionForTheSameOperationIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".");
        executable.Resource.Annotations.Add(new ResourceProjectionAnnotation(
            new OperationResourceProjectionSource(
                DistributedApplicationOperation.Publish,
                new ContainerResource("worker"))));
        executable.Resource.Annotations.Add(new ResourceProjectionAnnotation(
            new OperationResourceProjectionSource(
                DistributedApplicationOperation.Publish,
                new ContainerResource("worker"))));

        // Registration must fail the same way effective resolution does, rather than silently
        // configuring whichever projection happens to be first in annotation order.
        var exception = Assert.Throws<DistributedApplicationException>(
            () => executable.WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container.WithImage("projected-image")));

        Assert.Contains(executable.Resource.Name, exception.Message);
    }

    [Fact]
    public void IndexedAnnotationMutationDuringProjectionConfigurationIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithAnnotation(new FirstAnnotation());

        // Inherited annotations are hidden from the callback, so an indexed insert or set would
        // collapse the layered view onto the local-only snapshot and drop every owner annotation.
        Assert.Throws<InvalidOperationException>(
            () => executable.WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container =>
                {
                    container.Resource.Annotations.Add(new SecondAnnotation());
                    container.Resource.Annotations.Insert(0, new PrefixAnnotation());
                }));
    }

    [Fact]
    public void ProjectionRetainsInheritedAnnotationsAfterConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithAnnotation(new FirstAnnotation())
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container.WithImage("projected-image"));

        var projection = executable.Resource.GetEffectiveResource(builder.ExecutionContext);

        Assert.Single(projection.Annotations.OfType<FirstAnnotation>());
    }

    [Fact]
    public void ReplaceBehaviorStillReportsDuplicateAnnotationsOnAProjection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".");
        executable.Resource.Annotations.Add(new SingletonAnnotation("first"));
        executable.Resource.Annotations.Add(new SingletonAnnotation("second"));

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container => container.WithImage("projected-image"));

        var projection = (ContainerResource)executable.Resource.GetEffectiveResource(builder.ExecutionContext);
        var projectionBuilder = builder.CreateResourceBuilder(projection);

        // Suppression must not run before the duplicate check, otherwise inherited duplicates are
        // hidden and this long-standing diagnostic silently stops firing for projections.
        Assert.Throws<InvalidOperationException>(
            () => projectionBuilder.WithAnnotation(
                new SingletonAnnotation("replacement"),
                ResourceAnnotationMutationBehavior.Replace));
    }

    [Fact]
    public void AnnotationCollectionShapeRemainsBinaryCompatible()
    {
        // Integrations compiled against earlier Aspire versions reference Collection<T>.Add and
        // friends through method tokens on the base class, and reference IResource.Annotations by
        // its exact property type. Changing either shape breaks those call sites at runtime, so
        // guard the shape cheaply here. End-to-end validation against a previously shipped package
        // is a one-time exercise during review rather than a per-build test.
        Assert.Equal(
            typeof(Collection<IResourceAnnotation>),
            typeof(ResourceAnnotationCollection).BaseType);
        Assert.Equal(
            typeof(ResourceAnnotationCollection),
            typeof(IResource).GetProperty(nameof(IResource.Annotations))!.PropertyType);
    }

    private sealed record SingletonAnnotation(string Value) : IResourceAnnotation;

    private sealed class FirstAnnotation : IResourceAnnotation;

    private sealed class SecondAnnotation : IResourceAnnotation;

    private sealed class PrefixAnnotation : IResourceAnnotation;

    private sealed class ReplacementAnnotation : IResourceAnnotation;
}
