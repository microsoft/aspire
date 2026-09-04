// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class ResourceProjectionTests
{
    [Fact]
    public void GetOwnerOrSelfIsPublicAndPreservesOrdinaryResourceIdentity()
    {
        Assert.NotNull(typeof(ResourceExtensions).GetMethod(nameof(ResourceExtensions.GetOwnerOrSelf)));

        var resource = new PlainOwnerResource("worker");
        var container = new ContainerResource("container");

        Assert.Same(resource, resource.GetOwnerOrSelf());
        Assert.Same(container, container.GetOwnerOrSelf());
        Assert.Throws<ArgumentNullException>(() => ResourceExtensions.GetOwnerOrSelf(null!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetOwnerOrSelfResolvesSelectedProjectionsInsideConfiguration(bool customProjection)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var owner = builder.AddResource(new PlainOwnerResource("worker"));
        ContainerResource? configuredProjection = null;

        void Configure(IResourceBuilder<ContainerResource> container)
        {
            configuredProjection = container.Resource;
            Assert.Same(owner.Resource, container.Resource.GetOwnerOrSelf());
            Assert.Same(owner.Resource, owner.Resource.GetOwnerOrSelf());
        }

        if (customProjection)
        {
            owner.WithContainerProjection(
                DistributedApplicationOperation.Run,
                () => new FirstTestProjection(owner.Resource),
                Configure);
        }
        else
        {
            owner.RunAsContainerImage("contoso/worker:1.0", Configure);
        }

        Assert.NotNull(configuredProjection);
        Assert.Same(owner.Resource, configuredProjection.GetOwnerOrSelf().GetOwnerOrSelf());
        Assert.Same(owner.Resource, Assert.Single(builder.Resources));
        Assert.False(builder.Resources.Contains(configuredProjection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionEndpointReferencesPreserveContractsAndUseOwnerIdentity(bool ownerHasEndpoints)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var owner = builder.AddResource<IResource>(ownerHasEndpoints
            ? new ExecutableResource("worker", "worker", ".")
            : new PlainOwnerResource("worker"));
        var references = new List<EndpointReference>();

        owner.WithContainerProjection(
            DistributedApplicationOperation.Run,
            () =>
            {
                var projection = new FirstTestProjection(owner.Resource);
                references.Add(new EndpointReference(projection, "http"));
                Assert.Same(projection, references[0].Resource);
                return projection;
            },
            container =>
            {
                container.WithImage("contoso/worker:1.0")
                    .WithHttpEndpoint(targetPort: 8080, env: "PORT");
                var annotation = Assert.Single(container.Resource.Annotations.OfType<EndpointAnnotation>());

                references.Add(new EndpointReference(container.Resource, "http"));
                references.Add(new EndpointReference(container.Resource, annotation));
                references.Add(container.GetEndpoint("http"));
            });

        var context = new EnvironmentCallbackContext(builder.ExecutionContext, owner.Resource);
        foreach (var callback in owner.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            await callback.Callback(context);
        }

        var port = Assert.IsType<EndpointReferenceExpression>(context.EnvironmentVariables["PORT"]);
        references.Add(port.Endpoint);
        Assert.Equal("8080", await port.GetValueAsync(TestContext.Current.CancellationToken));

        var endpoint = Assert.Single(owner.Resource.Annotations.OfType<EndpointAnnotation>());
        var provider = ownerHasEndpoints ? owner.Resource : owner.Resource.AsContainer();
        Assert.NotNull(provider);
        foreach (var reference in references)
        {
            Assert.Same(provider, reference.Resource);
            Assert.Same(endpoint, reference.EndpointAnnotation);
            Assert.Same(owner.Resource, Assert.Single(((IValueWithReferences)reference).References));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HttpsEndpointCallbacksUseOwnerIdentity(bool useProjection, bool useHttps)
    {
#pragma warning disable ASPIRECERTIFICATES001
        using var builder = TestDistributedApplicationBuilder.Create();
        var owner = builder.AddExecutable("worker", "worker", ".");
        var contexts = new List<HttpsEndpointUpdateCallbackContext>();

        void Configure(IResourceBuilder<IResource> resource)
        {
            resource.WithAnnotation(new HttpsCertificateAnnotation { UseDeveloperCertificate = useHttps });
            resource.SubscribeHttpsEndpointsUpdate(contexts.Add);
        }

        if (useProjection)
        {
            owner.RunAsContainerImage("contoso/worker:1.0", Configure);
        }
        else
        {
            Configure(owner);
        }

        builder.Services.AddSingleton<IDeveloperCertificateService>(
            new TestDeveloperCertificateService([], false, false, false));
        await using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(app.Services, model),
            TestContext.Current.CancellationToken);

        if (useHttps)
        {
            var context = Assert.Single(contexts);
            Assert.Same(owner.Resource, context.Resource);
            Assert.Same(owner.Resource, Assert.Single(context.Model.Resources));
            Assert.True(context.Model.Resources.Contains(context.Resource));
            Assert.Same(app.Services, context.Services);
        }
        else
        {
            Assert.Empty(contexts);
        }
#pragma warning restore ASPIRECERTIFICATES001
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContainerFilesSourcesUseOwnerIdentity(bool useProjection)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var owner = new PlainOwnerResource("source");
        var destination = builder.AddResource(new ProjectResource("destination"));
        var files = new ContainerFilesTestProjection(owner);

        void Configure(IResourceBuilder<ContainerFilesTestProjection> source)
        {
            source.WithImage("contoso/files:1.0")
                .WithContainerFilesSource("/files");
            destination.PublishWithContainerFiles(source, "/app");
        }

        if (useProjection)
        {
            builder.AddResource(owner).WithContainerProjection(
                DistributedApplicationOperation.Publish,
                () => files,
                Configure);
        }
        else
        {
            Configure(builder.AddResource(files));
        }

        var annotation = Assert.Single(destination.Resource.Annotations.OfType<ContainerFilesDestinationAnnotation>());
        Assert.Same(useProjection ? owner : (IResource)files, annotation.Source);
        Assert.True(builder.Resources.Contains(annotation.Source));
        Assert.Equal("/app", annotation.DestinationPath);
        Assert.Equal("/files", Assert.Single(annotation.Source.Annotations.OfType<ContainerFilesSourceAnnotation>()).SourcePath);
        Assert.True(annotation.Source.TryGetContainerImageName(out var image));
        Assert.Equal("contoso/files:1.0", image);
    }

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
        Assert.Same(projectionBuilder.Resource, executable.Resource.AsContainer());

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

        var projection = Assert.IsAssignableFrom<ContainerResource>(executable.Resource.AsContainer());
        Assert.Equal("/app/worker", projection.Entrypoint);
        Assert.True(projection.ShellExecution);
#pragma warning restore ASPIRECONTAINERSHELLEXECUTION001
    }

    [Fact]
    public async Task ManifestCallbackAddedAfterProjectionTakesPrecedence()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .PublishAsDockerFile()
            .ExcludeFromManifest();

        Assert.True(executable.Resource.IsExcludedFromPublish());
        Assert.Null(await ManifestUtils.GetManifestOrNull(executable.Resource));
    }

    [Fact]
    public async Task CustomPublishProjectionSerializesOwnerAsContainerByDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddResource(new PlainOwnerResource("worker"));

        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            () => new FirstTestProjection(resource.Resource),
            container => container.WithImage("contoso/worker", "1.0"));

        var manifest = await ManifestUtils.GetManifest(resource.Resource);

        Assert.Equal("container.v0", manifest["type"]?.ToString());
        Assert.Equal("contoso/worker:1.0", manifest["image"]?.ToString());
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
    public void ProjectionCallbacksRunSynchronouslyInCallOrder()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".");
        var invocations = new List<string>();

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            _ =>
            {
                invocations.Add("first-start");
                executable.WithContainerProjection(
                    DistributedApplicationOperation.Publish,
                    _ => invocations.Add("nested"));
                invocations.Add("first-end");
            });

        Assert.Equal(["first-start", "nested", "first-end"], invocations);

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            _ => invocations.Add("second"));

        Assert.Equal(["first-start", "nested", "first-end", "second"], invocations);
    }

    [Fact]
    public void ProjectionCallbacksProcessResourcesAddedSynchronously()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        IResourceBuilder<ExecutableResource>? addedResource = null;

        var executable = builder.AddExecutable("worker", "worker", ".");
        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            _ =>
            {
                addedResource = builder.AddExecutable("added", "added", ".");
                addedResource.WithContainerProjection(
                    DistributedApplicationOperation.Publish,
                    container => container.WithImage("contoso/added", "1.0"));
            });

        Assert.NotNull(executable.Resource.AsContainer());
        Assert.NotNull(addedResource);
        Assert.NotNull(addedResource.Resource.AsContainer());
    }

    [Fact]
    public void ProjectionCallbackFailureBubblesSynchronously()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".");
        var invocations = new List<string>();

        Assert.Throws<InvalidOperationException>(() =>
            executable.WithContainerProjection(
                DistributedApplicationOperation.Publish,
                _ =>
                {
                    invocations.Add("failed");
                    executable.WithContainerProjection(
                        DistributedApplicationOperation.Publish,
                        _ => invocations.Add("nested"));
                    throw new InvalidOperationException("Configuration failed.");
                }));

        Assert.Equal(["failed", "nested"], invocations);
        Assert.NotNull(executable.Resource.AsContainer());

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            _ => invocations.Add("after"));

        Assert.Equal(["failed", "nested", "after"], invocations);
    }

    [Fact]
    public void DefaultProjectionPreventsLaterCustomProjection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddResource(new PlainOwnerResource("worker"));
        var customFactoryInvoked = false;
        var customCallbackInvoked = false;

        resource.WithContainerProjection(DistributedApplicationOperation.Publish, _ => { });
        var selectedProjection = resource.Resource.AsContainer();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resource.WithContainerProjection(
                DistributedApplicationOperation.Publish,
                () =>
                {
                    customFactoryInvoked = true;
                    return new FirstTestProjection(resource.Resource);
                },
                _ => customCallbackInvoked = true));

        Assert.Contains("already uses the default container projection", exception.Message);
        Assert.False(customFactoryInvoked);
        Assert.False(customCallbackInvoked);
        Assert.Same(selectedProjection, resource.Resource.AsContainer());
    }

    [Fact]
    public void DefaultProjectionConfigurationAfterCustomProjectionUsesSelectedInstance()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddResource(new PlainOwnerResource("worker"));
        FirstTestProjection? customProjection = null;
        ContainerResource? defaultProjection = null;

        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            () => new FirstTestProjection(resource.Resource),
            container => customProjection = container.Resource);
        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container => defaultProjection = container.Resource);

        Assert.NotNull(customProjection);
        Assert.Same(customProjection, defaultProjection);
        Assert.Same(customProjection, resource.Resource.AsContainer());
    }

    [Fact]
    public void RepeatedCustomProjectionConfigurationReusesSelectedInstanceWithoutCallingFactory()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddResource(new PlainOwnerResource("worker"));
        var factoryInvocations = 0;
        FirstTestProjection? firstProjection = null;
        FirstTestProjection? secondProjection = null;

        FirstTestProjection CreateProjection()
        {
            factoryInvocations++;
            return new FirstTestProjection(resource.Resource);
        }

        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            CreateProjection,
            container => firstProjection = container.Resource);
        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            CreateProjection,
            container => secondProjection = container.Resource);

        Assert.Equal(1, factoryInvocations);
        Assert.NotNull(firstProjection);
        Assert.Same(firstProjection, secondProjection);
        Assert.Same(firstProjection, resource.Resource.AsContainer());
    }

    [Fact]
    public void CustomProjectionPreventsDifferentCustomProjection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var resource = builder.AddResource(new PlainOwnerResource("worker"));
        var secondFactoryInvoked = false;
        var secondCallbackInvoked = false;

        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            () => new FirstTestProjection(resource.Resource),
            _ => { });

        var exception = Assert.Throws<InvalidOperationException>(() =>
            resource.WithContainerProjection(
                DistributedApplicationOperation.Publish,
                () =>
                {
                    secondFactoryInvoked = true;
                    return new SecondTestProjection(resource.Resource);
                },
                _ => secondCallbackInvoked = true));

        Assert.Contains(nameof(FirstTestProjection), exception.Message);
        Assert.Contains(nameof(SecondTestProjection), exception.Message);
        Assert.False(secondFactoryInvoked);
        Assert.False(secondCallbackInvoked);
        Assert.IsType<FirstTestProjection>(resource.Resource.AsContainer());
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
    public async Task TypedProjectionEventCallbackCanPublishOwnerNotification()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var notificationService = ResourceNotificationServiceTestHelpers.Create();
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                container => container
                    .WithImage("projected-image")
                    .OnResourceReady((resource, @event, _) =>
                    {
                        Assert.Same(resource, @event.Resource.AsContainer());
                        Assert.Same(@event.Resource, resource.GetOwnerOrSelf());
                        return notificationService.PublishUpdateAsync(
                            resource,
                            state => state with { State = KnownResourceStates.Running });
                    }));

        await notificationService.PublishUpdateAsync(executable.Resource, state => state);
        await builder.Eventing.PublishAsync(
            new ResourceReadyEvent(executable.Resource, TestServiceProvider.Instance),
            TestContext.Current.CancellationToken);

        Assert.True(notificationService.TryGetCurrentState(executable.Resource.Name, out var resourceEvent));
        Assert.Same(executable.Resource, resourceEvent.Resource);
        Assert.Equal(KnownResourceStates.Running, resourceEvent.Snapshot.State?.Text);
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
        Assert.Null(executable.Resource.AsContainer());
        Assert.Null(project.Resource.AsContainer());
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
        Assert.Null(executable.Resource.AsContainer());
        Assert.False(builder.TryCreateResourceBuilder<ContainerResource>("worker", out _));
        Assert.Collection(model.GetExecutableResources(), resource => Assert.Same(executable.Resource, resource));
    }

    [Fact]
    public void ProjectionIsSelectedBeforeConfigurationCallbackRuns()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".");
        Assert.False(executable.Resource.IsContainer());
        Assert.Null(executable.Resource.AsContainer());

        executable.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container =>
            {
                Assert.True(executable.Resource.IsContainer());
                Assert.Same(container.Resource, executable.Resource.AsContainer());
                Assert.Same(executable.Resource.Annotations, container.Resource.Annotations);
                Assert.Empty(executable.Resource.Annotations.OfType<ContainerImageAnnotation>());
            });

        Assert.True(executable.Resource.IsContainer());
        Assert.NotNull(executable.Resource.AsContainer());
    }

    [Fact]
    public void ContainerWithoutImageRetainsLegacyNonContainerClassification()
    {
        var container = new ContainerResource("container");

        Assert.Same(container, container.AsContainer());
        Assert.False(container.IsContainer());
    }

    [Fact]
    public void AsContainerReturnsExplicitContainerResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var container = builder.AddContainer("worker", "image");

        Assert.Same(container.Resource, container.Resource.AsContainer());
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
    public void ProjectionCannotBypassSelfWaitValidation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(DistributedApplicationOperation.Publish, _ => { });
        Assert.True(builder.TryCreateResourceBuilder<ContainerResource>("worker", out var projectionBuilder));

        Assert.Throws<DistributedApplicationException>(() => executable.WaitFor(projectionBuilder));
        Assert.Throws<DistributedApplicationException>(() => executable.WaitForStart(projectionBuilder));
        Assert.Throws<DistributedApplicationException>(() => executable.WaitForCompletion(projectionBuilder));
        Assert.Empty(executable.Resource.Annotations.OfType<WaitAnnotation>());
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
        Assert.NotNull(executable.Resource.AsContainer());
        Assert.Collection(model.GetContainerResources(), resource => Assert.Same(executable.Resource, resource));
        Assert.Empty(model.GetExecutableResources());
    }

    [Fact]
    public void ProjectionCanBeResolvedDuringConfigurationCallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var executable = builder.AddExecutable("worker", "worker", ".")
            .WithContainerProjection(
                DistributedApplicationOperation.Publish,
                projection =>
                {
                    Assert.True(builder.TryCreateResourceBuilder<ContainerResource>("worker", out var resolvedBuilder));
                    Assert.Same(projection.Resource, resolvedBuilder.Resource);
                });

        Assert.True(builder.TryCreateResourceBuilder<ContainerResource>("worker", out var resolvedBuilder));
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
        // guard the shape cheaply here. ResourceProjectionBinaryCompatibilityTests also compiles
        // against a previously shipped package and loads it against the current build on every run.
        Assert.Equal(
            typeof(Collection<IResourceAnnotation>),
            typeof(ResourceAnnotationCollection).BaseType);
        Assert.Equal(
            typeof(ResourceAnnotationCollection),
            typeof(IResource).GetProperty(nameof(IResource.Annotations))!.PropertyType);
    }

    [Fact]
    public void ProjectingAContainerResourceThrows()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        // C# has no negative generic constraint, so a container reaching a projection API can only be caught here.
        var container = builder.AddContainer("cache", "redis");

        var exception = Assert.Throws<InvalidOperationException>(
            () => container.RunAsContainerImage("contoso/other:1.0"));

        Assert.Contains("already a container", exception.Message);
    }

    [Fact]
    public void ProjectingAContainerResourceThrowsEvenWhenTheOperationDoesNotMatch()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var container = builder.AddContainer("cache", "redis");

        // The guard runs ahead of the operation gate so the authoring mistake is not hidden in one mode.
        Assert.Throws<InvalidOperationException>(
            () => container.RunAsContainerImage("contoso/other:1.0"));
    }

    [Fact]
    public void RunAsContainerImageAppliesTheImageFromTheMostRecentCall()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var executable = builder.AddExecutable("worker", "worker", ".")
            .RunAsContainerImage("contoso/worker:1.0")
            .RunAsContainerImage("contoso/worker:2.0");

        var container = executable.Resource.AsContainer();
        Assert.NotNull(container);

        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("contoso/worker", image.Image);
        Assert.Equal("2.0", image.Tag);
    }

    [Fact]
    public void ReprojectingWithATagClearsADigestFromTheEarlierImage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        // Tag and SHA256 are mutually exclusive, so overwriting has to leave the annotation carrying only the
        // form the newest reference used.
        var executable = builder.AddExecutable("worker", "worker", ".")
            .RunAsContainerImage("contoso/worker@sha256:0f27a0b0f2e8a9dd2b0d1f9a1b6c8d7e5f4a3b2c1d0e9f8a7b6c5d4e3f2a1b0c")
            .RunAsContainerImage("contoso/worker:2.0");

        var container = executable.Resource.AsContainer();
        Assert.NotNull(container);

        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("2.0", image.Tag);
        Assert.Null(image.SHA256);
    }

    [Fact]
    public void ReprojectingWithADigestClearsATagFromTheEarlierImage()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        const string digest = "0f27a0b0f2e8a9dd2b0d1f9a1b6c8d7e5f4a3b2c1d0e9f8a7b6c5d4e3f2a1b0c";

        var executable = builder.AddExecutable("worker", "worker", ".")
            .RunAsContainerImage("contoso/worker:1.0")
            .RunAsContainerImage($"contoso/worker@sha256:{digest}");

        var container = executable.Resource.AsContainer();
        Assert.NotNull(container);

        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal(digest, image.SHA256);
        Assert.Null(image.Tag);
    }

    [Fact]
    public void TheProjectionCallbackTakesPrecedenceOverTheImageArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        // The image is applied ahead of the caller's callback, so an explicit WithImageTag inside the callback
        // still wins rather than being overwritten by the argument.
        var executable = builder.AddExecutable("worker", "worker", ".")
            .RunAsContainerImage("contoso/worker:1.0", container => container.WithImageTag("override"));

        var container = executable.Resource.AsContainer();
        Assert.NotNull(container);

        var image = Assert.Single(container.Annotations.OfType<ContainerImageAnnotation>());
        Assert.Equal("override", image.Tag);
    }

    [Fact]
    public void PublishAsDockerFileOnAProjectStillRequiresAnImageBuild()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // The projection shares the owner's annotation collection, so the Dockerfile the projection adds is what
        // keeps a projected project classified for build and push.
        var project = builder.AddProject<Projects.ServiceA>("proj")
            .PublishAsDockerFile();

        Assert.True(project.Resource.RequiresImageBuild());
        Assert.True(project.Resource.RequiresImageBuildAndPush());
    }

    [Fact]
    public async Task ProjectionContractsResolveToTheOwnerWhenBothDeclareThem()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // The owner is the identity other resources reference, so it wins a contract both sides declare. The
        // sanctioned way to vary a connection string by shape is for the owner to branch on it, which is what the
        // Azure emulators do (AzureSignalRResource.ConnectionStringExpression tests IsEmulator). If the projection
        // won instead, a resource's effective connection string would change with the operation being run.
        var resource = builder.AddResource(new ConnectionStringOwnerResource("db"));
        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            () => ConnectionStringProjection.CreateProjection(resource.Resource),
            container => container.WithImage("contoso/db", "1.0"));

        var manifest = await ManifestUtils.GetManifest(resource.Resource.AsContainer()!);

        Assert.Equal("Host=owner", manifest["connectionString"]?.ToString());
    }

    [Fact]
    public async Task ProjectionContractsFallBackToTheProjectionWhenTheOwnerLacksThem()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Resolving to the owner must not drop a contract only the projection declares: there is no ambiguity to
        // settle here, so this is purely additive and a typed projection can still contribute one.
        var resource = builder.AddResource(new PlainOwnerResource("db"));
        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            () => ConnectionStringOnlyProjection.CreateProjection(resource.Resource),
            container => container.WithImage("contoso/db", "1.0"));

        var manifest = await ManifestUtils.GetManifest(resource.Resource.AsContainer()!);

        Assert.Equal("Host=projection", manifest["connectionString"]?.ToString());
    }

    private sealed class PlainOwnerResource(string name) : Resource(name);

    private sealed class ContainerFilesTestProjection(PlainOwnerResource owner)
        : ContainerResource(owner.Name), IResourceWithContainerFiles
    {
        public override ResourceAnnotationCollection Annotations => owner.Annotations;
    }

    private sealed class FirstTestProjection(IResource owner) : ContainerResource(owner.Name)
    {
        public override ResourceAnnotationCollection Annotations => owner.Annotations;
    }

    private sealed class SecondTestProjection(PlainOwnerResource owner) : ContainerResource(owner.Name)
    {
        public override ResourceAnnotationCollection Annotations => owner.Annotations;
    }

    private sealed class ConnectionStringProjection(ConnectionStringOwnerResource owner)
        : ContainerResource(owner.Name), IResourceWithConnectionString, IContainerProjection<ConnectionStringOwnerResource, ConnectionStringProjection>
    {
        public override ResourceAnnotationCollection Annotations => owner.Annotations;

        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"Host=projection");

        public static ConnectionStringProjection CreateProjection(ConnectionStringOwnerResource owner) => new(owner);
    }

    private sealed class ConnectionStringOnlyProjection(PlainOwnerResource owner)
        : ContainerResource(owner.Name), IResourceWithConnectionString, IContainerProjection<PlainOwnerResource, ConnectionStringOnlyProjection>
    {
        public override ResourceAnnotationCollection Annotations => owner.Annotations;

        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"Host=projection");

        public static ConnectionStringOnlyProjection CreateProjection(PlainOwnerResource owner) => new(owner);
    }

    [Fact]
    public async Task AProjectionCanOverrideTheOwnerConnectionStringThroughTheSharedAnnotationCollection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var emulator = builder.AddResource(new EmulatorConnectionStringResource("emulator"));

        var resource = builder.AddResource(new RedirectAwareOwnerResource("db"));
        resource.WithContainerProjection(
            DistributedApplicationOperation.Publish,
            container =>
            {
                container.WithImage("contoso/db", "1.0");

                // A projection shares its owner's annotation collection, so a redirect registered from the projected
                // builder is the very same annotation the owner reads back. This is why owner-first contract
                // precedence does not prevent a projection from changing a connection string: the projection supplies
                // the value and the owner, which is what consumers reference, stays the one that hands it out.
                container.WithAnnotation(
                    new ConnectionStringRedirectAnnotation(emulator.Resource),
                    ResourceAnnotationMutationBehavior.Replace);
            });

        var manifest = await ManifestUtils.GetManifest(resource.Resource.AsContainer()!);

        Assert.Equal("Host=container", manifest["connectionString"]?.ToString());
    }

    /// <summary>Mirrors RedisResource and PostgresServerResource: the owner keeps the contract and consults the
    /// redirect annotation, which is how RunAsContainer varies a connection string today.</summary>
    private sealed class RedirectAwareOwnerResource(string name) : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression =>
            this.TryGetLastAnnotation<ConnectionStringRedirectAnnotation>(out var redirect)
                ? redirect.Resource.ConnectionStringExpression
                : ReferenceExpression.Create($"Host=cloud");
    }

    private sealed class EmulatorConnectionStringResource(string name) : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"Host=container");
    }

    private sealed class ConnectionStringOwnerResource(string name) : Resource(name), IResourceWithConnectionString
    {
        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"Host=owner");
    }

    private sealed record SingletonAnnotation(string Value) : IResourceAnnotation;

    private sealed class FirstAnnotation : IResourceAnnotation;

    private sealed class SecondAnnotation : IResourceAnnotation;
}
