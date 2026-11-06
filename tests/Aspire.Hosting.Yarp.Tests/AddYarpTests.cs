#pragma warning disable ASPIRECERTIFICATES001

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography.X509Certificates;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Yarp.Tests;

public class AddYarpTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void VerifyYarpResourceWithTargetPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var yarp = builder.AddYarp("yarp");
        using var app = builder.Build();

        var resource = Assert.Single(builder.Resources.OfType<YarpResource>());
        Assert.Collection(resource.Annotations.OfType<EndpointAnnotation>(),
            endpoint =>
            {
                Assert.Equal("http", endpoint.Name);
                Assert.Equal(5000, endpoint.TargetPort);
            });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyRunEnvVariablesAreSet(bool containerCertificateSupport)
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        // Report that developer certificates won't support container scenarios
        var testProvider = new TestServiceProvider();
        testProvider.AddService<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            new List<X509Certificate2>(),
            containerCertificateSupport,
            trustCertificate: true,
            tlsTerminate: false));
        testProvider.AddService(new DistributedApplicationOptions());
        testProvider.AddService(Options.Create(new DcpOptions()));

        var yarp = builder.AddYarp("yarp");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Run, testProvider);

        Assert.Contains("OTEL_EXPORTER_OTLP_ENDPOINT", env);
        Assert.Contains("OTEL_EXPORTER_OTLP_PROTOCOL", env);
        Assert.Contains("ASPNETCORE_ENVIRONMENT", env);

        if (containerCertificateSupport)
        {
            Assert.DoesNotContain("YARP_UNSAFE_OLTP_CERT_ACCEPT_ANY_SERVER_CERTIFICATE", env);
        }
        else
        {
            Assert.Contains("YARP_UNSAFE_OLTP_CERT_ACCEPT_ANY_SERVER_CERTIFICATE", env);
        }
    }

    [Fact]
    public async Task VerifyPublishEnvVariablesAreSet()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var yarp = builder.AddYarp("yarp");

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_ENDPOINT", env);
        Assert.DoesNotContain("OTEL_EXPORTER_OTLP_PROTOCOL", env);
        Assert.Contains("ASPNETCORE_ENVIRONMENT", env);

        Assert.DoesNotContain("YARP_UNSAFE_OLTP_CERT_ACCEPT_ANY_SERVER_CERTIFICATE", env);
    }

    [Fact]
    public void VerifyResourceRouteDestinationUsesLatestEndpointScheme()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        var backend = builder.AddResource(new TestServiceDiscoveryResource("backend"))
            .WithHttpEndpoint();

        var yarp = builder.AddYarp("yarp")
            .WithConfiguration(config =>
            {
                config.AddRoute("/api/{**catchall}", backend);
            });

        backend.WithEndpoint("http", endpoint => endpoint.UriScheme = "https");

        var env = new Dictionary<string, object>();
        YarpEnvConfigGenerator.PopulateEnvVariables(env, yarp.Resource.Routes, yarp.Resource.Clusters);

        var address = Assert.Contains("REVERSEPROXY__CLUSTERS__cluster_backend__DESTINATIONS__destination1__ADDRESS", env);
        Assert.Equal("https://backend", address);
    }

    [Fact]
    public async Task VerifyWithStaticFilesAddsEnvironmentVariable()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        // Yarp requires an IDeveloperCertificateService in run mode when building it's environment variables.
        var testProvider = new TestServiceProvider();
        testProvider.AddService<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            new List<X509Certificate2>(),
            supportsContainerTrust: false,
            trustCertificate: true,
            tlsTerminate: false));
        testProvider.AddService(new DistributedApplicationOptions());
        testProvider.AddService(Options.Create(new DcpOptions()));

        var yarp = builder.AddYarp("yarp").WithStaticFiles();

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Run, testProvider);

        var value = Assert.Contains("YARP_ENABLE_STATIC_FILES", env);
        Assert.Equal("true", value);
        Assert.Equal(Array.Empty<string>(), env.Keys.Where(key =>
            key.StartsWith("REVERSEPROXY__", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("YARP_DISABLE_SPA_FALLBACK", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifySpaFallbackCanBeEnabledBeforeOrAfterStaticFiles(bool configureFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var yarp = builder.AddYarp("yarp");
        if (configureFirst)
        {
            yarp.WithSpaFallback();
        }

        yarp.WithStaticFiles();
        if (!configureFirst)
        {
            yarp.WithSpaFallback();
        }

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Equal("/{**path:nonfile}", env["REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__PATH"]);
        Assert.Equal("2147483647", env["REVERSEPROXY__ROUTES__aspireSpaFallback__ORDER"]);
        Assert.Equal("*", env["REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__HOSTS__0"]);
        Assert.Equal("http://127.0.0.1:{yarp.bindings.http.targetPort}", env["REVERSEPROXY__CLUSTERS__aspireSpaFallback__DESTINATIONS__self__ADDRESS"]);
        Assert.Equal("true", env["YARP_DISABLE_SPA_FALLBACK"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifySpaFallbackCanBeDisabledBeforeOrAfterStaticFiles(bool configureFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var yarp = builder.AddYarp("yarp");
        if (configureFirst)
        {
            yarp.WithSpaFallback(false);
        }

        yarp.WithStaticFiles();
        if (!configureFirst)
        {
            yarp.WithSpaFallback(false);
        }

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Equal("true", env["YARP_ENABLE_STATIC_FILES"]);
        Assert.Equal("true", env["YARP_DISABLE_SPA_FALLBACK"]);
        Assert.Equal(Array.Empty<string>(), env.Keys.Where(key => key.StartsWith("REVERSEPROXY__", StringComparison.OrdinalIgnoreCase)).ToArray());
    }

    [Fact]
    public async Task VerifySpaFallbackRequiresStaticFilesEvenWhenConfiguredFirst()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var yarp = builder.AddYarp("yarp").WithSpaFallback();

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Equal(Array.Empty<string>(), env.Keys.Where(key => key.StartsWith("REVERSEPROXY__", StringComparison.OrdinalIgnoreCase)).ToArray());

        var yarpWithStaticFiles = builder.AddYarp("yarp-with-static-files").WithSpaFallback().WithStaticFiles();
        env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarpWithStaticFiles.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Equal("/{**path:nonfile}", env["REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__PATH"]);
    }

    [Fact]
    public async Task VerifyRepeatedStaticFilesConfigureOnlyOneFallback()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var yarp = builder.AddYarp("yarp")
            .WithStaticFiles()
            .WithSpaFallback(false)
            .WithStaticFiles()
            .WithSpaFallback();

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);
        var keys = env.Keys.Where(key => key.StartsWith("REVERSEPROXY__", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "REVERSEPROXY__CLUSTERS__aspireSpaFallback__DESTINATIONS__self__ADDRESS",
            "REVERSEPROXY__ROUTES__aspireSpaFallback__CLUSTERID",
            "REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__HOSTS__0",
            "REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__METHODS__0",
            "REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__METHODS__1",
            "REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__PATH",
            "REVERSEPROXY__ROUTES__aspireSpaFallback__ORDER",
            "REVERSEPROXY__ROUTES__aspireSpaFallback__TRANSFORMS__0__PATHSET",
        ], keys);
    }

    [Fact]
    public async Task VerifySpaFallbackAvoidsExistingRouteAndClusterNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var yarp = builder.AddYarp("yarp")
            .WithEnvironment("ReverseProxy__Routes__ASPIRESpaFallback__Match__Path", "/existing")
            .WithEnvironment("ReverseProxy__Clusters__aspireSpaFallback1__Destinations__existing__Address", "http://example.test")
            .WithStaticFiles()
            .WithSpaFallback();

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.Equal("/existing", env["ReverseProxy__Routes__ASPIRESpaFallback__Match__Path"]);
        Assert.Equal("http://example.test", env["ReverseProxy__Clusters__aspireSpaFallback1__Destinations__existing__Address"]);
        Assert.Equal("aspireSpaFallback2", env["REVERSEPROXY__ROUTES__aspireSpaFallback2__CLUSTERID"]);
        Assert.Equal("/{**path:nonfile}", env["REVERSEPROXY__ROUTES__aspireSpaFallback2__MATCH__PATH"]);
        Assert.Equal("http://127.0.0.1:{yarp.bindings.http.targetPort}", env["REVERSEPROXY__CLUSTERS__aspireSpaFallback2__DESTINATIONS__self__ADDRESS"]);
    }

    [Fact]
    public async Task VerifyWithStaticFilesWorksInPublishOperation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Yarp requires an IDeveloperCertificateService in run mode when building it's environment variables.
        var testProvider = new TestServiceProvider();
        testProvider.AddService<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            new List<X509Certificate2>(),
            supportsContainerTrust: false,
            trustCertificate: true,
            tlsTerminate: false));
        testProvider.AddService(new DistributedApplicationOptions());

        var yarp = builder.AddYarp("yarp").WithStaticFiles().WithSpaFallback();

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, testProvider);

        var value = Assert.Contains("YARP_ENABLE_STATIC_FILES", env);
        Assert.Equal("true", value);
        Assert.Equal("/{**path:nonfile}", env["REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__PATH"]);
        Assert.Equal("http://127.0.0.1:{yarp.bindings.http.targetPort}", env["REVERSEPROXY__CLUSTERS__aspireSpaFallback__DESTINATIONS__self__ADDRESS"]);
    }

    [Fact]
    public async Task VerifyWithStaticFilesBindMountAddsEnvironmentVariable()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        // Yarp requires an IDeveloperCertificateService in run mode when building it's environment variables.
        var testProvider = new TestServiceProvider();
        testProvider.AddService<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            new List<X509Certificate2>(),
            supportsContainerTrust: false,
            trustCertificate: true,
            tlsTerminate: false));
        testProvider.AddService(new DistributedApplicationOptions());
        testProvider.AddService(Options.Create(new DcpOptions()));

        using var workspace = TemporaryWorkspace.Create(testOutputHelper);

        var yarp = builder.AddYarp("yarp").WithStaticFiles(workspace.Path).WithSpaFallback();

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Run, testProvider);

        var value = Assert.Contains("YARP_ENABLE_STATIC_FILES", env);
        Assert.Equal("true", value);
        Assert.Equal("/{**path:nonfile}", env["REVERSEPROXY__ROUTES__aspireSpaFallback__MATCH__PATH"]);
    }

    [Fact]
    public async Task VerifyWithStaticFilesBindMountWorksInPublishOperation()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        using var workspace = TemporaryWorkspace.Create(testOutputHelper);

        var yarp = builder.AddYarp("yarp").WithStaticFiles(workspace.Path);

        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        var value = Assert.Contains("YARP_ENABLE_STATIC_FILES", env);
        Assert.Equal("true", value);
    }

    [Fact]
    public void VerifyWithStaticFilesBindMountAddsContainerFileSystemAnnotationInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);

        var yarp = builder.AddYarp("yarp").WithStaticFiles(workspace.Path);

        var annotation = Assert.Single(yarp.Resource.Annotations.OfType<ContainerFileSystemCallbackAnnotation>());
        Assert.Equal("/wwwroot", annotation.DestinationPath);
    }

    [Fact]
    public void VerifyWithStaticFilesAddsDockerfileBuildAnnotationInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);

        var yarp = builder.AddYarp("yarp").WithStaticFiles(workspace.Path);

        var annotation = Assert.Single(yarp.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(annotation.DockerfileFactory);
        Assert.Contains(workspace.Path, annotation.ContextPath);
    }

    [Fact]
    public async Task VerifyWithStaticFilesGeneratesCorrectDockerfileInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var workspace = TemporaryWorkspace.Create(testOutputHelper);

        var yarp = builder.AddYarp("yarp").WithStaticFiles(workspace.Path);

        var annotation = Assert.Single(yarp.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(annotation.DockerfileFactory);

        var context = new DockerfileFactoryContext
        {
            Resource = yarp.Resource,
            Services = TestServiceProvider.Instance,
            CancellationToken = CancellationToken.None
        };

        var dockerfile = await annotation.DockerfileFactory(context);

        Assert.Contains("FROM", dockerfile);
        Assert.Contains("dotnet/nightly/yarp:2.3-preview", dockerfile);
        Assert.Contains("AS yarp", dockerfile);
        Assert.Contains("WORKDIR /app", dockerfile);
        Assert.Contains("COPY . /app/wwwroot", dockerfile);
    }

    [Fact]
    public void VerifyPublishWithStaticFilesDoesNothingInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        // Create a test container resource that implements IResourceWithContainerFiles
        var sourceContainerResource = new TestContainerFilesResource("source");
        var sourceContainer = builder.AddResource(sourceContainerResource)
            .WithImage("myimage")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });

        var yarp = builder.AddYarp("yarp").PublishWithStaticFiles(sourceContainer);

        // In run mode, PublishWithStaticFiles should not add any annotations
        Assert.Empty(yarp.Resource.Annotations.OfType<ContainerFilesDestinationAnnotation>());
        Assert.Empty(yarp.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
    }

    [Fact]
    public async Task VerifyPublishWithStaticFilesAddsEnvironmentVariableInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Create a test container resource that implements IResourceWithContainerFiles
        var sourceContainerResource = new TestContainerFilesResource("source");
        var sourceContainer = builder.AddResource(sourceContainerResource)
            .WithImage("myimage")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });

        var yarp = builder.AddYarp("yarp").PublishWithStaticFiles(sourceContainer);

        // Verify ContainerFilesDestinationAnnotation was added
        var containerFilesAnnotation = Assert.Single(yarp.Resource.Annotations.OfType<ContainerFilesDestinationAnnotation>());
        Assert.Equal(sourceContainer.Resource, containerFilesAnnotation.Source);
        Assert.Equal("/app/wwwroot", containerFilesAnnotation.DestinationPath);

        // Verify static files environment variable was set
        var env = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(yarp.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);
        var value = Assert.Contains("YARP_ENABLE_STATIC_FILES", env);
        Assert.Equal("true", value);

        // Verify DockerfileBuildAnnotation was added (instead of DockerfileBuilderCallbackAnnotation)
        Assert.Single(yarp.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
    }

    [Fact]
    public async Task VerifyPublishWithStaticFilesGeneratesCorrectDockerfile()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Create a test container resource that implements IResourceWithContainerFiles
        var sourceContainerResource = new TestContainerFilesResource("source");
        var sourceContainer = builder.AddResource(sourceContainerResource)
            .WithImage("sourceimage")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });

        var yarp = builder.AddYarp("yarp").PublishWithStaticFiles(sourceContainer);

        // Get the DockerfileBuildAnnotation (added by WithDockerfileBuilder)
        var buildAnnotation = Assert.Single(yarp.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(buildAnnotation.DockerfileFactory);

        using var app = builder.Build();

        var context = new DockerfileFactoryContext
        {
            Resource = yarp.Resource,
            Services = app.Services,
            CancellationToken = CancellationToken.None
        };

        var dockerfile = await buildAnnotation.DockerfileFactory(context);

        await Verify(dockerfile);
    }

    [Fact]
    public async Task VerifyPublishWithStaticFilesHandlesMissingSourceImageGracefully()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Create a test container resource without an image name
        var sourceContainerResource = new TestContainerFilesResource("source");
        var sourceContainer = builder.AddResource(sourceContainerResource)
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" });

        var yarp = builder.AddYarp("yarp").PublishWithStaticFiles(sourceContainer);

        // Get the DockerfileBuildAnnotation
        var buildAnnotation = Assert.Single(yarp.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(buildAnnotation.DockerfileFactory);

        // Build the app to get service provider
        using var app = builder.Build();

        var context = new DockerfileFactoryContext
        {
            Resource = yarp.Resource,
            Services = app.Services,
            CancellationToken = CancellationToken.None
        };

        var dockerfile = await buildAnnotation.DockerfileFactory(context);

        await Verify(dockerfile);
    }

    [Fact]
    public async Task VerifyPublishWithStaticFilesGeneratesCorrectDockerfileWithMultipleContainerFiles()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Create a test container resource with multiple container file sources
        var sourceContainerResource = new TestContainerFilesResource("source");
        var sourceContainer = builder.AddResource(sourceContainerResource)
            .WithImage("sourceimage")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" })
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/assets" });

        var yarp = builder.AddYarp("yarp").PublishWithStaticFiles(sourceContainer);

        // Get the DockerfileBuildAnnotation
        var buildAnnotation = Assert.Single(yarp.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(buildAnnotation.DockerfileFactory);

        using var app = builder.Build();

        var context = new DockerfileFactoryContext
        {
            Resource = yarp.Resource,
            Services = app.Services,
            CancellationToken = CancellationToken.None
        };

        var dockerfile = await buildAnnotation.DockerfileFactory(context);

        await Verify(dockerfile);
    }

    [Fact]
    public async Task VerifyPublishWithStaticFilesGeneratesCorrectDockerfileWithMultipleSourceContainerFiles()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Create multiple test container resources with multiple container file sources
        var sourceContainer1 = builder.AddResource(new TestContainerFilesResource("source1"))
            .WithImage("sourceimage")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist" })
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/assets" });

        var sourceContainer2 = builder.AddResource(new TestContainerFilesResource("source2"))
            .WithImage("sourceimage2")
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/dist2" })
            .WithAnnotation(new ContainerFilesSourceAnnotation { SourcePath = "/app/assets2" });

        var yarp = builder.AddYarp("yarp")
            .PublishWithStaticFiles(sourceContainer1)
            .PublishWithStaticFiles(sourceContainer2);

        // Get the DockerfileBuildAnnotation
        var buildAnnotation = Assert.Single(yarp.Resource.Annotations.OfType<DockerfileBuildAnnotation>());
        Assert.NotNull(buildAnnotation.DockerfileFactory);

        using var app = builder.Build();

        var context = new DockerfileFactoryContext
        {
            Resource = yarp.Resource,
            Services = app.Services,
            CancellationToken = CancellationToken.None
        };

        var dockerfile = await buildAnnotation.DockerfileFactory(context);

        await Verify(dockerfile);
    }

    [Fact]
    public async Task VerifyWithHostHttpsPortCreatesHttpsEndpointWithSpecifiedPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            new List<X509Certificate2>(),
            supportsContainerTrust: true,
            trustCertificate: true,
            tlsTerminate: false));

        const int httpsPort = 12345;

        var yarp = builder.AddYarp("yarp")
            .WithHttpsDeveloperCertificate()
            .WithHostHttpsPort(httpsPort);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var beforeStartEvent = new BeforeStartEvent(app.Services, model);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        var httpsEndpoint = Assert.Single(yarp.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "https");
        Assert.Equal(httpsPort, httpsEndpoint.Port);
        Assert.Equal("https", httpsEndpoint.UriScheme);
    }

    [Fact]
    public async Task VerifyWithHostHttpsPortDoesNotCreateHttpsEndpointWithoutCertificateConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);

        builder.Services.AddSingleton<IDeveloperCertificateService>(new TestDeveloperCertificateService(
            new List<X509Certificate2>(),
            supportsContainerTrust: true,
            trustCertificate: true,
            tlsTerminate: false));

        var yarp = builder.AddYarp("yarp")
            .WithHostHttpsPort(12345);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var beforeStartEvent = new BeforeStartEvent(app.Services, model);
        await builder.Eventing.PublishAsync(beforeStartEvent);

        Assert.DoesNotContain(yarp.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "https");
    }

    private sealed class TestContainerFilesResource(string name) : ContainerResource(name), IResourceWithContainerFiles
    {
    }

    private sealed class TestServiceDiscoveryResource(string name) : IResourceWithServiceDiscovery
    {
        public string Name => name;

        public ResourceAnnotationCollection Annotations { get; } = new ResourceAnnotationCollection();
    }
}