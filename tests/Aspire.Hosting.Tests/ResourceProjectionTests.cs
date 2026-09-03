// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class ResourceProjectionTests
{
    [Fact]
    public async Task TypedProjectionConfiguresOwnerAndOwnerRemainsSoleModelMember()
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

        var model = new DistributedApplicationModel(builder.Resources);

        Assert.Collection(builder.Resources, resource => Assert.Same(executable.Resource, resource));
        Assert.Collection(model.Resources, resource => Assert.Same(executable.Resource, resource));
        Assert.True(model.Resources.TryGetByName("worker", out var modelResource));
        Assert.Same(executable.Resource, modelResource);
        Assert.Collection(model.GetContainerResources(), resource => Assert.Same(executable.Resource, resource));
        Assert.Collection(model.GetComputeResources(), resource => Assert.Same(executable.Resource, resource));
        Assert.Empty(model.GetExecutableResources());

        Assert.True(builder.TryCreateResourceBuilder<ExecutableResource>("worker", out var ownerBuilder));
        Assert.Same(executable.Resource, ownerBuilder.Resource);
        Assert.True(builder.TryCreateResourceBuilder<ContainerResource>("worker", out var projectionBuilder));
        Assert.NotSame(executable.Resource, projectionBuilder.Resource);
        Assert.Same(executable.Resource.Annotations, projectionBuilder.Resource.Annotations);

        Assert.True(executable.Resource.TryGetContainerImageName(out var image));
        Assert.Equal("projected-image:v2", image);

        var mount = Assert.Single(executable.Resource.Annotations.OfType<ContainerMountAnnotation>());
        Assert.Equal("projection-data", mount.Source);

        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            executable.Resource,
            DistributedApplicationOperation.Publish,
            TestServiceProvider.Instance);

        Assert.Equal("owner", environment["OWNER_SETTING"]);
        Assert.Equal("projection", environment["PROJECTION_SETTING"]);
    }

    [Fact]
    public void DirectProjectionPropertyChangesRemainVisibleFromOwner()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile();

        Assert.True(builder.TryCreateResourceBuilder<ContainerResource>("worker", out var projectionBuilder));
        projectionBuilder.Resource.Entrypoint = "/app/worker";
#pragma warning disable ASPIRECONTAINERSHELLEXECUTION001
        projectionBuilder.Resource.ShellExecution = true;
#pragma warning restore ASPIRECONTAINERSHELLEXECUTION001

        var projection = Assert.Single(
            executable.Resource.Annotations.OfType<ContainerResourceProjectionAnnotation>());
        Assert.Equal("/app/worker", projection.Entrypoint);
        Assert.True(projection.ShellExecution);
    }

    [Fact]
    public void ManifestCallbackAddedAfterProjectionUsesOwnerAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile()
            .ExcludeFromManifest();

        Assert.True(executable.Resource.IsExcludedFromPublish());
    }

    [Fact]
    public void ProjectionManifestExclusionRemovesOwnerFromComputeResources()
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
    public void BuildResourceEnumerationReturnsOwner()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile();
        var model = new DistributedApplicationModel(builder.Resources);

        Assert.Same(executable.Resource, Assert.Single(model.GetBuildResources()));
        Assert.Same(executable.Resource, Assert.Single(model.GetBuildAndPushResources()));
    }

    [Fact]
    public async Task DependencyDiscoveryUsesProjectionConfiguration()
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
    public void OwnerEndpointReferencesResolveEndpointsAddedThroughProjection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".");
        var endpointReference = executable.GetEndpoint("projected");

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container => container
                .WithImage("projected-image")
                .WithHttpEndpoint(targetPort: 8080, name: "projected"));

        var endpoint = Assert.Single(executable.Resource.Annotations.OfType<EndpointAnnotation>());

        Assert.True(endpointReference.Exists);
        Assert.Same(executable.Resource, endpointReference.Resource);
        Assert.Same(endpoint, endpointReference.EndpointAnnotation);
        Assert.Same(endpoint, Assert.Single(executable.Resource.GetEndpoints()).EndpointAnnotation);
    }

    [Fact]
    public void ProjectionConfiguresExistingOwnerEndpoints()
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
    public async Task TypedEventCallbackSubscribesToOwner()
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

        await builder.Eventing.PublishAsync(
            new ResourceReadyEvent(executable.Resource, TestServiceProvider.Instance),
            TestContext.Current.CancellationToken);

        Assert.NotNull(callbackResource);
        Assert.Same(executable.Resource, callbackResource.GetOwnerOrSelf());
    }

    [Fact]
    public async Task OwnerNotificationReportsContainerShape()
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
    public async Task ProjectionCommandsExecuteThroughOwner()
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
    public void ModelCollectionMutationsUseOwner()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile();
        var model = new DistributedApplicationModel(builder.Resources);

        Assert.True(model.Resources.Contains(executable.Resource));
        Assert.Equal(0, model.Resources.IndexOf(executable.Resource));
        Assert.True(model.Resources.Remove(executable.Resource));
        Assert.Empty(builder.Resources);
    }

    [Fact]
    public async Task RepeatedExecutableProjectionClearsArgumentsAddedBeforeConversion()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile()
            .WithArgs("--retained")
            .PublishAsDockerFile();

        var arguments = await ArgumentEvaluator.GetArgumentListAsync(executable.Resource);

        Assert.Empty(arguments);
    }

    [Fact]
    public void OwnerResolvesProjectionDeploymentTarget()
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
    public async Task LateHttp2ConfigurationAppliesToOwnerEndpoint()
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
        Assert.Same(executable.Resource, Assert.Single(model.GetContainerResources()));
        Assert.Equal("http2", ownerEndpoint.Transport);
    }

    [Fact]
    public void ProjectionCallbackMutatesOwnerAnnotationsDirectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithAnnotation(new FirstAnnotation())
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container =>
                {
                    container.WithImage("projected-image");
                    container.Resource.Annotations.Add(new SecondAnnotation());
                });

        Assert.True(builder.TryCreateResourceBuilder<ContainerResource>("worker", out var projectionBuilder));
        Assert.Same(executable.Resource.Annotations, projectionBuilder.Resource.Annotations);
        Assert.Single(executable.Resource.Annotations.OfType<FirstAnnotation>());
        Assert.Single(executable.Resource.Annotations.OfType<SecondAnnotation>());
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
        var project = builder.AddResource(new ProjectResource("project"))
            .WithAnnotation(new ContainerImageAnnotation
            {
                Image = "legacy-project-image",
                Tag = "latest"
            });
        var model = new DistributedApplicationModel([executable.Resource, project.Resource]);

        Assert.Equal(KnownResourceTypes.Executable, executable.Resource.GetResourceType());
        Assert.Equal(KnownResourceTypes.Project, project.Resource.GetResourceType());
        Assert.Collection(model.GetExecutableResources(), resource => Assert.Same(executable.Resource, resource));
        Assert.Collection(model.GetProjectResources(), resource => Assert.Same(project.Resource, resource));
        Assert.Collection(
            model.GetContainerResources(),
            resource => Assert.Same(executable.Resource, resource),
            resource => Assert.Same(project.Resource, resource));
    }

    [Fact]
    public void ProjectionCallbackIsOperationScoped()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var callbackInvoked = false;

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Run,
                container =>
                {
                    callbackInvoked = true;
                    container.WithImage("run-image");
                });
        var model = new DistributedApplicationModel(builder.Resources);

        Assert.False(callbackInvoked);
        Assert.Empty(executable.Resource.Annotations.OfType<ContainerResourceProjectionAnnotation>());
        Assert.False(executable.Resource.IsContainer());
        Assert.False(builder.TryCreateResourceBuilder<ContainerResource>("worker", out _));
        Assert.Collection(model.GetExecutableResources(), resource => Assert.Same(executable.Resource, resource));
    }

    [Theory]
    [InlineData(DistributedApplicationOperation.Run)]
    [InlineData(DistributedApplicationOperation.Publish)]
    public void OnlyProjectionForActiveOperationIsApplied(DistributedApplicationOperation operation)
    {
        using var builder = TestDistributedApplicationBuilder.Create(operation);
        var runCallbackInvoked = false;
        var publishCallbackInvoked = false;

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Run,
                container =>
                {
                    runCallbackInvoked = true;
                    container.WithImage("run-image");
                })
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container =>
                {
                    publishCallbackInvoked = true;
                    container.WithImage("publish-image");
                });

        Assert.Equal(operation == DistributedApplicationOperation.Run, runCallbackInvoked);
        Assert.Equal(operation == DistributedApplicationOperation.Publish, publishCallbackInvoked);
        Assert.Single(executable.Resource.Annotations.OfType<ContainerResourceProjectionAnnotation>());
        Assert.True(executable.Resource.TryGetContainerImageName(out var image));
        Assert.Equal(operation == DistributedApplicationOperation.Run ? "run-image:latest" : "publish-image:latest", image);
    }

    [Fact]
    public void ProjectionRelationshipTargetsCanonicalOwner()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var child = builder.AddContainer("child", "image");

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container.WithChildRelationship(child));

        var relationship = Assert.Single(child.Resource.Annotations.OfType<ResourceRelationshipAnnotation>());
        Assert.Same(executable.Resource, relationship.Resource);
    }

    [Fact]
    public void RunProjectionUsesContainerVolumeMountPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(DistributedApplicationOperation.Run, _ => { });
        var binding = new VolumeMountBindingAnnotation("data")
        {
            MountPath = "/srv/data"
        };

        var path = binding.ResolvePath(new EnvironmentCallbackContext(
            builder.ExecutionContext,
            executable.Resource));

        Assert.Equal("/srv/data", path);
    }

    [Fact]
    public void ProjectionMarkerIsAuthoritativeWithoutContainerImageAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(DistributedApplicationOperation.Publish, _ => { });
        var model = new DistributedApplicationModel(builder.Resources);

        Assert.True(executable.Resource.IsContainer());
        Assert.Collection(model.GetContainerResources(), resource => Assert.Same(executable.Resource, resource));
        Assert.Empty(model.GetExecutableResources());
    }

    [Fact]
    public void ProjectionCanBeResolvedDuringInitialConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        IResourceBuilder<ContainerResource>? resolvedBuilder = null;

        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                _ => Assert.True(builder.TryCreateResourceBuilder("worker", out resolvedBuilder)));

        Assert.NotNull(resolvedBuilder);
        Assert.Same(executable.Resource, resolvedBuilder.Resource.GetOwnerOrSelf());
    }

    [Fact]
    public void ReplaceBehaviorStillReportsDuplicateOwnerAnnotationsThroughProjection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".");
        executable.Resource.Annotations.Add(new SingletonAnnotation("first"));
        executable.Resource.Annotations.Add(new SingletonAnnotation("second"));

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container => container.WithImage("projected-image"));

        Assert.True(builder.TryCreateResourceBuilder<ContainerResource>("worker", out var projectionBuilder));
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
}
