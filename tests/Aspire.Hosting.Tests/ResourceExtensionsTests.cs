// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

public class ResourceExtensionsTests
{
    [Fact]
    public void TryGetAnnotationsOfTypeReturnsFalseWhenNoAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"));

        Assert.False(parent.Resource.HasAnnotationOfType<DummyAnnotation>());
        Assert.False(parent.Resource.TryGetAnnotationsOfType<DummyAnnotation>(out var annotations));
        Assert.Null(annotations);
    }

    [Fact]
    public void TryGetAnnotationsOfTypeReturnsFalseWhenOnlyAnnotationsOfOtherTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithAnnotation(new AnotherDummyAnnotation());

        Assert.False(parent.Resource.HasAnnotationOfType<DummyAnnotation>());
        Assert.False(parent.Resource.TryGetAnnotationsOfType<DummyAnnotation>(out var annotations));
        Assert.Null(annotations);
    }

    [Fact]
    public void TryGetAnnotationsOfTypeReturnsTrueWhenNoAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithAnnotation(new DummyAnnotation());

        Assert.True(parent.Resource.HasAnnotationOfType<DummyAnnotation>());
        Assert.True(parent.Resource.TryGetAnnotationsOfType<DummyAnnotation>(out var annotations));
        Assert.Single(annotations);
    }

    [Fact]
    public void TryGetAnnotationsIncludingAncestorsOfTypeReturnsAnnotationFromParentDirectly()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithAnnotation(new DummyAnnotation());

        Assert.True(parent.Resource.HasAnnotationIncludingAncestorsOfType<DummyAnnotation>());
        Assert.True(parent.Resource.TryGetAnnotationsIncludingAncestorsOfType<DummyAnnotation>(out var annotations));
        Assert.Single(annotations);
    }

    [Fact]
    public void TryGetAnnotationIncludingAncestorsOfTypeReturnsFalseWhenNoAnnotations()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"));

        Assert.False(parent.Resource.HasAnnotationIncludingAncestorsOfType<DummyAnnotation>());
        Assert.False(parent.Resource.TryGetAnnotationsIncludingAncestorsOfType<DummyAnnotation>(out var annotations));
        Assert.Null(annotations);
    }

    [Fact]
    public void TryGetAnnotationIncludingAncestorsOfTypeReturnsFalseWhenOnlyAnnotationsOfOtherTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithAnnotation(new AnotherDummyAnnotation());

        Assert.False(parent.Resource.HasAnnotationIncludingAncestorsOfType<DummyAnnotation>());
        Assert.False(parent.Resource.TryGetAnnotationsIncludingAncestorsOfType<DummyAnnotation>(out var annotations));
        Assert.Null(annotations);
    }

    [Fact]
    public void TryGetAnnotationIncludingAncestorsOfTypeReturnsFalseWhenOnlyAnnotationsOfOtherTypesIncludingParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithAnnotation(new AnotherDummyAnnotation());

        var child = builder.AddResource(new ChildResource("child", parent.Resource))
                           .WithAnnotation(new AnotherDummyAnnotation());

        Assert.False(parent.Resource.HasAnnotationIncludingAncestorsOfType<DummyAnnotation>());
        Assert.False(child.Resource.TryGetAnnotationsIncludingAncestorsOfType<DummyAnnotation>(out var annotations));
        Assert.Null(annotations);
    }

    [Fact]
    public void TryGetAnnotationsIncludingAncestorsOfTypeReturnsAnnotationFromParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithAnnotation(new DummyAnnotation());

        var child = builder.AddResource(new ChildResource("child", parent.Resource));

        Assert.True(parent.Resource.HasAnnotationIncludingAncestorsOfType<DummyAnnotation>());
        Assert.True(child.Resource.TryGetAnnotationsIncludingAncestorsOfType<DummyAnnotation>(out var annotations));
        Assert.Single(annotations);
    }

    [Fact]
    public void TryGetAnnotationsIncludingAncestorsOfTypeCombinesAnnotationsFromParentAndChild()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithAnnotation(new DummyAnnotation());

        var child = builder.AddResource(new ChildResource("child", parent.Resource))
                           .WithAnnotation(new DummyAnnotation());

        Assert.True(parent.Resource.HasAnnotationIncludingAncestorsOfType<DummyAnnotation>());
        Assert.True(child.Resource.TryGetAnnotationsIncludingAncestorsOfType<DummyAnnotation>(out var annotations));
        Assert.Equal(2, annotations.Count());
    }

    [Fact]
    public void TryGetAnnotationsIncludingAncestorsOfTypeCombinesAnnotationsFromParentAndChildAndGrandchild()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var parent = builder.AddResource(new ParentResource("parent"))
                            .WithAnnotation(new DummyAnnotation());

        var child = builder.AddResource(new ChildResource("child", parent: parent.Resource))
                           .WithAnnotation(new DummyAnnotation());

        var grandchild = builder.AddResource(new ChildResource("grandchild", parent: child.Resource))
                                .WithAnnotation(new DummyAnnotation());

        Assert.True(parent.Resource.HasAnnotationIncludingAncestorsOfType<DummyAnnotation>());
        Assert.True(grandchild.Resource.TryGetAnnotationsIncludingAncestorsOfType<DummyAnnotation>(out var annotations));
        Assert.Equal(3, annotations.Count());
    }

    [Fact]
    public void TryGetContainerImageNameReturnsCorrectFormatWhenShaSupplied()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder.AddContainer("grafana", "grafana/grafana", "latest").WithImageSHA256("1adbcc2df3866ff5ec1d836e9d2220c904c7f98901b918d3cc5e1118ab1af991");

        Assert.True(container.Resource.TryGetContainerImageName(out var imageName));
        Assert.Equal("grafana/grafana@sha256:1adbcc2df3866ff5ec1d836e9d2220c904c7f98901b918d3cc5e1118ab1af991", imageName);
    }

    [Fact]
    public void TryGetContainerImageNameReturnsCorrectFormatWhenShaNotSupplied()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder.AddContainer("grafana", "grafana/grafana", "10.3.1");

        Assert.True(container.Resource.TryGetContainerImageName(out var imageName));
        Assert.Equal("grafana/grafana:10.3.1", imageName);
    }

    [Fact]
    public async Task GetEnvironmentVariableValuesAsyncReturnCorrectVariablesInRunMode()
    {
        var builder = DistributedApplication.CreateBuilder();
        var container = builder.AddContainer("elasticsearch", "library/elasticsearch", "8.14.0")
         .WithEnvironment("discovery.type", "single-node")
         .WithEnvironment("xpack.security.enabled", "true")
         .WithEnvironment(context =>
         {
             Assert.NotNull(context.Resource);

             context.EnvironmentVariables["ELASTIC_PASSWORD"] = "p@ssw0rd1";
         });

#pragma warning disable CS0618 // Type or member is obsolete
        var env = await container.Resource.GetEnvironmentVariableValuesAsync().DefaultTimeout();
#pragma warning restore CS0618 // Type or member is obsolete

        Assert.Collection(env,
            env =>
            {
                Assert.Equal("discovery.type", env.Key);
                Assert.Equal("single-node", env.Value);
            },
            env =>
            {
                Assert.Equal("xpack.security.enabled", env.Key);
                Assert.Equal("true", env.Value);
            },
            env =>
            {
                Assert.Equal("ELASTIC_PASSWORD", env.Key);
                Assert.Equal("p@ssw0rd1", env.Value);
            });
    }

    [Fact]
    public async Task GetEnvironmentVariableValuesAsyncReturnCorrectVariablesUsingValueProviderInRunMode()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:ElasticPassword"] = "p@ssw0rd1";

        var passwordParameter = builder.AddParameter("ElasticPassword");

        var container = builder.AddContainer("elasticsearch", "library/elasticsearch", "8.14.0")
         .WithEnvironment("discovery.type", "single-node")
         .WithEnvironment("xpack.security.enabled", "true")
         .WithEnvironment("ELASTIC_PASSWORD", passwordParameter);

#pragma warning disable CS0618 // Type or member is obsolete
        var env = await container.Resource.GetEnvironmentVariableValuesAsync().DefaultTimeout();
#pragma warning restore CS0618 // Type or member is obsolete

        Assert.Collection(env,
            env =>
            {
                Assert.Equal("discovery.type", env.Key);
                Assert.Equal("single-node", env.Value);
            },
            env =>
            {
                Assert.Equal("xpack.security.enabled", env.Key);
                Assert.Equal("true", env.Value);
            },
            env =>
            {
                Assert.Equal("ELASTIC_PASSWORD", env.Key);
                Assert.Equal("p@ssw0rd1", env.Value);
            });
    }

    [Fact]
    public async Task GetEnvironmentVariableValuesAsyncReturnCorrectVariablesUsingManifestExpressionProviderInPublishMode()
    {
        var builder = DistributedApplication.CreateBuilder();
        builder.Configuration["Parameters:ElasticPassword"] = "p@ssw0rd1";

        var passwordParameter = builder.AddParameter("ElasticPassword");

        var container = builder.AddContainer("elasticsearch", "library/elasticsearch", "8.14.0")
         .WithEnvironment("discovery.type", "single-node")
         .WithEnvironment("xpack.security.enabled", "true")
         .WithEnvironment("ELASTIC_PASSWORD", passwordParameter);

#pragma warning disable CS0618 // Type or member is obsolete
        var env = await container.Resource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish).DefaultTimeout();
#pragma warning restore CS0618 // Type or member is obsolete

        Assert.Collection(env,
            env =>
            {
                Assert.Equal("discovery.type", env.Key);
                Assert.Equal("single-node", env.Value);
            },
            env =>
            {
                Assert.Equal("xpack.security.enabled", env.Key);
                Assert.Equal("true", env.Value);
            },
            env =>
            {
                Assert.Equal("{ElasticPassword.value}", env.Value);
                Assert.False(string.IsNullOrEmpty(env.Value));
            });
    }

    [Fact]
    public async Task GetArgumentValuesAsync_ReturnsCorrectValuesForSpecialCases()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var builder = DistributedApplication.CreateBuilder();
        var surrogate = builder.AddResource(new ConnectionStringParameterResource("ResourceWithConnectionStringSurrogate", _ => "ConnectionString", null));
        var secretParameter = builder.AddResource(new ParameterResource("SecretParameter", _ => "SecretParameter", true));
        var nonSecretParameter = builder.AddResource(new ParameterResource("NonSecretParameter", _ => "NonSecretParameter"));

        var containerArgs = await builder.AddContainer("elasticsearch", "library/elasticsearch", "8.14.0")
            .WithArgs(surrogate)
            .WithArgs(secretParameter)
            .WithArgs(nonSecretParameter)
            .Resource.GetArgumentValuesAsync().DefaultTimeout();

        Assert.Equal<IEnumerable<string>>(["ConnectionString", "SecretParameter", "NonSecretParameter"], containerArgs);

        // Executables can also have arguments passed in AddExecutable
        var executableArgs = await builder.AddExecutable(
                "ping",
                "ping",
                string.Empty,
                surrogate,
                secretParameter,
                nonSecretParameter)
            .Resource.GetArgumentValuesAsync().DefaultTimeout();

        Assert.Equal<IEnumerable<string>>(["ConnectionString", "SecretParameter", "NonSecretParameter"], executableArgs);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    [Fact]
    public async Task WithImagePushOptions_AddsImagePushOptionsCallbackAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var containerResource = builder.AddContainer("test-container", "nginx")
            .WithImagePushOptions(context =>
            {
                context.Options.RemoteImageName = "myrepo/myimage";
                context.Options.RemoteImageTag = "test-tag";
            });

        var annotation = Assert.Single(containerResource.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());

        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "",
            RemoteImageTag = ""
        };
        var context = new ContainerImagePushOptionsCallbackContext
        {
            Resource = containerResource.Resource,
            CancellationToken = CancellationToken.None,
            Options = options
        };
        await annotation.Callback(context);
        Assert.Equal("myrepo/myimage", context.Options.RemoteImageName);
        Assert.Equal("test-tag", context.Options.RemoteImageTag);
    }

    [Fact]
    public void WithImagePushOptions_WithNullBuilder_ThrowsArgumentNullException()
    {
        IResourceBuilder<ContainerResource> builder = null!;

        var ex = Assert.Throws<ArgumentNullException>(() => builder.WithImagePushOptions(context => { }));
        Assert.Equal("builder", ex.ParamName);
    }

    [Fact]
    public void WithImagePushOptions_WithNullCallback_ThrowsArgumentNullException()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var containerResource = builder.AddContainer("test-container", "nginx");

        var ex = Assert.Throws<ArgumentNullException>(() =>
            containerResource.WithImagePushOptions((Action<ContainerImagePushOptionsCallbackContext>)null!));
        Assert.Equal("callback", ex.ParamName);
    }

    [Fact]
    public async Task WithImagePushOptions_CanBeCalledMultipleTimes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var containerResource = builder.AddContainer("test-container", "nginx")
            .WithImagePushOptions(context => context.Options.RemoteImageTag = "tag1")
            .WithImagePushOptions(context => context.Options.RemoteImageName = "myrepo/myimage");

        var annotations = containerResource.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>().ToList();
        Assert.Equal(2, annotations.Count);

        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "",
            RemoteImageTag = ""
        };
        var context = new ContainerImagePushOptionsCallbackContext
        {
            Resource = containerResource.Resource,
            CancellationToken = CancellationToken.None,
            Options = options
        };

        await annotations[0].Callback(context);
        Assert.Equal("tag1", context.Options.RemoteImageTag);

        await annotations[1].Callback(context);
        Assert.Equal("myrepo/myimage", context.Options.RemoteImageName);
    }

    [Fact]
    public void WithImagePushOptions_WorksWithDifferentResourceTypes()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        static void callback(ContainerImagePushOptionsCallbackContext context) => context.Options.RemoteImageTag = "test-tag";

        var containerResource = builder.AddContainer("test-container", "nginx")
            .WithImagePushOptions(callback);
        Assert.Single(containerResource.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());

        var projectResource = builder.AddProject<Projects.ServiceA>("ServiceA")
            .WithImagePushOptions(callback);
        Assert.Single(projectResource.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());

        var executableResource = builder.AddExecutable("test-exec", "dotnet", "myapp.dll")
            .WithImagePushOptions(callback);
        Assert.Single(executableResource.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());
    }

    [Fact]
    public async Task WithImagePushOptions_WithAsyncCallback_AddsCorrectAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var callback = async (ContainerImagePushOptionsCallbackContext context) =>
        {
            await Task.Delay(1);
            context.Options.RemoteImageName = $"myrepo/{context.Resource.Name}";
            context.Options.RemoteImageTag = "async-tag";
        };

        var containerResource = builder.AddContainer("test-container", "nginx")
            .WithImagePushOptions(callback);

        var annotation = Assert.Single(containerResource.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());
        Assert.NotNull(annotation.Callback);

        var options = new ContainerImagePushOptions
        {
            RemoteImageName = "",
            RemoteImageTag = ""
        };
        var context = new ContainerImagePushOptionsCallbackContext
        {
            Resource = containerResource.Resource,
            CancellationToken = CancellationToken.None,
            Options = options
        };
        await annotation.Callback(context);
        Assert.Equal("myrepo/test-container", context.Options.RemoteImageName);
        Assert.Equal("async-tag", context.Options.RemoteImageTag);
    }

    [Fact]
    public void WithRemoteImageName_SetsImageNameViaAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var containerResource = builder.AddContainer("test-container", "nginx")
            .WithRemoteImageName("myrepo/myimage");

        var annotation = Assert.Single(containerResource.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());
        Assert.NotNull(annotation);
    }

    [Fact]
    public void WithRemoteImageTag_SetsImageTagViaAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var containerResource = builder.AddContainer("test-container", "nginx")
            .WithRemoteImageTag("v1.0.0");

        var annotation = Assert.Single(containerResource.Resource.Annotations.OfType<ContainerImagePushOptionsCallbackAnnotation>());
        Assert.NotNull(annotation);
    }

    [Fact]
    public async Task WithContainerFilesSource_Works()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var resource = builder.AddResource(new TestContainerFilesResource("test-container"))
            .WithContainerFilesSource("src/path1")
            .WithContainerFilesSource("src/path2");
        Assert.Collection(resource.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>(),
            a => Assert.Equal("src/path1", a.SourcePath),
            a => Assert.Equal("src/path2", a.SourcePath));

        resource
            .ClearContainerFilesSources()
            .WithContainerFilesSource("src/override");

        var annotation = Assert.Single(resource.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>());
        Assert.Equal("src/override", annotation.SourcePath);

        resource.WithContainerFilesSource("src/override2");
        Assert.Collection(resource.Resource.Annotations.OfType<ContainerFilesSourceAnnotation>(),
            a => Assert.Equal("src/override", a.SourcePath),
            a => Assert.Equal("src/override2", a.SourcePath));
    }

    private sealed class ComputeEnvironmentResource(string name) : Resource(name), IComputeEnvironmentResource
    {
    }

    private sealed class ParentResource(string name) : Resource(name)
    {

    }

    private sealed class ChildResource(string name, Resource parent) : Resource(name), IResourceWithParent<Resource>
    {
        public Resource Parent => parent;
    }

    private sealed class DummyAnnotation : IResourceAnnotation
    {

    }

    private sealed class AnotherDummyAnnotation : IResourceAnnotation
    {

    }

    private sealed class TestContainerFilesResource(string name) : ContainerResource(name), IResourceWithContainerFiles
    {
    }
}
