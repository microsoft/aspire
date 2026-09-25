// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class AddQuarkusAppTests
{
    [Theory]
    [InlineData("pom.xml", "maven")]
    [InlineData("build.gradle", "gradle")]
    public void AddQuarkusApp_InADebugSession_AddsABuildTheApplicationWaitsFor(string buildFile, string toolName)
    {
        // Dev mode compiles the application on its way to running it, so a Quarkus resource normally has
        // no build resource. A debug session is the exception: the IDE launches the packaged fast JAR
        // rather than the dev-mode wrapper, and on a clean checkout nothing has written that JAR yet.
        // Without a build in front of the application the debug adapter is handed no entry point and
        // falls back to asking which of the workspace's main classes to start.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");
        EnterDebugSession(builder);

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        var buildResource = Assert.Single(
            builder.Resources.OfType<JavaBuildResource>(),
            resource => resource.Name == $"inventory-{toolName}-build");

        Assert.Contains(
            app.Resource.Annotations.OfType<WaitAnnotation>(),
            wait => ReferenceEquals(wait.Resource, buildResource));
    }

    [Fact]
    public void AddQuarkusApp_OutsideADebugSession_AddsNoBuild()
    {
        // `aspire run` launches dev mode itself, which compiles the application, so a build resource in
        // front of it would only repeat that work and delay every start.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        Assert.Empty(builder.Resources.OfType<JavaBuildResource>());
        Assert.Empty(app.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public void AddSpringBootApp_InADebugSession_AddsNoBuild()
    {
        // Spring Boot is deliberately not given the same treatment. The debug adapter starts it from the
        // classpath the Java language server already compiled, so there is no packaged artifact to wait
        // for and adding a build would only delay every debug session.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "");
        EnterDebugSession(builder);

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);

        Assert.Empty(builder.Resources.OfType<JavaBuildResource>());
        Assert.Empty(app.Resource.Annotations.OfType<WaitAnnotation>());
    }

    /// <summary>
    /// Configures the builder the way an IDE that can launch Java resources does, which is what
    /// <c>SupportsDebugging</c> reads to decide whether this run hands its resources to the IDE.
    /// </summary>
    private static void EnterDebugSession(IDistributedApplicationBuilder builder)
    {
        builder.Configuration["DEBUG_SESSION_PORT"] = "5678";
        builder.Configuration["DEBUG_SESSION_INFO"] = JsonSerializer.Serialize(new
        {
            protocols_supported = new[] { "2024-03-03" },
            supported_launch_configurations = new[] { "java" }
        });
    }

    [Fact]
    public async Task AddQuarkusApp_MavenProject_LaunchesInDevMode()
    {
        // Dev mode is what "run my Quarkus application locally" means: it is the only mode with live coding,
        // and it is what the Quarkus documentation tells every reader to start with.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);
        using var application = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, application.Services),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper), tempDir.Path, "quarkus:dev"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddQuarkusApp_GradleProject_LaunchesInDevMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle", "plugins { id 'io.quarkus' }");

        var app = builder.AddQuarkusApp("pricing", tempDir.Path);
        using var application = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, application.Services),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper), tempDir.Path, "quarkusDev"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Theory]
    [InlineData("pom.xml")]
    [InlineData("build.gradle")]
    public void AddQuarkusApp_DoesNotCreateASeparateBuildResource(string buildFile)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        Assert.Same(app.Resource, Assert.Single(builder.Resources));
    }

    [Fact]
    public void AddQuarkusApp_DeclaresHttpEndpointThroughQuarkusHttpPort()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        var endpoint = Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("QUARKUS_HTTP_PORT", endpoint.TargetPortEnvironmentVariable);
        Assert.Null(endpoint.TargetPort);
    }

    [Fact]
    public void AddQuarkusApp_GradleProject_DeclaresHttpEndpointThroughQuarkusHttpPort()
    {
        // The endpoint has to be declared for both build tools. Attaching it to only one branch of the
        // detection is an easy mistake that leaves half of all applications with no endpoint at all.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle", "plugins { id 'io.quarkus' }");

        var app = builder.AddQuarkusApp("pricing", tempDir.Path);

        var endpoint = Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("QUARKUS_HTTP_PORT", endpoint.TargetPortEnvironmentVariable);
    }

    [Fact]
    public async Task AddQuarkusApp_SetsTheDevProfileInRunMode()
    {
        // The IDE launches the packaged application rather than quarkus:dev, so the profile has to be set
        // as an environment variable for both to resolve the same %dev. configuration.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("dev", envVars["QUARKUS_PROFILE"]);
    }

    [Fact]
    public async Task AddQuarkusApp_DoesNotSetTheDevProfileWhenPublishing()
    {
        // A published image runs the packaged application, which must resolve prod configuration.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.False(envVars.ContainsKey("QUARKUS_PROFILE"));
    }

    [Fact]
    public async Task AddQuarkusApp_DisablesTheObservabilityDevServiceInRunMode()
    {
        // Left on, the Dev Service pulls grafana/otel-lgtm and repoints the exporter at that container,
        // so every span and metric lands somewhere the Aspire dashboard cannot see.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("false", envVars["QUARKUS_OBSERVABILITY_ENABLED"]);
    }

    [Fact]
    public async Task AddQuarkusApp_MirrorsTheOtlpConfigurationOntoTheNamesQuarkusReads()
    {
        // quarkus-opentelemetry reads quarkus.otel.*, not the standard OTEL_* names, so without the mirror
        // it keeps exporting to its own localhost:4317 default.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal(envVars["OTEL_EXPORTER_OTLP_ENDPOINT"], envVars["QUARKUS_OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal(envVars["OTEL_EXPORTER_OTLP_PROTOCOL"], envVars["QUARKUS_OTEL_EXPORTER_OTLP_PROTOCOL"]);
        Assert.Equal(envVars["OTEL_SERVICE_NAME"], envVars["QUARKUS_OTEL_SERVICE_NAME"]);
        Assert.Equal(envVars["OTEL_RESOURCE_ATTRIBUTES"], envVars["QUARKUS_OTEL_RESOURCE_ATTRIBUTES"]);
    }

    [Fact]
    public async Task AddQuarkusApp_DoesNotMirrorTheOtlpConfigurationWhenPublishing()
    {
        // WithOtlpExporter contributes nothing in publish mode, so there is nothing to mirror. The mirror
        // must not invent a value either: an OTLP endpoint baked in here would be the AppHost's, not the
        // one the compute environment goes on to supply. A deployed application maps the value in its own
        // application.properties instead, which both Quarkus playgrounds do.
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        Assert.False(envVars.ContainsKey("QUARKUS_OTEL_EXPORTER_OTLP_ENDPOINT"));
        Assert.False(envVars.ContainsKey("QUARKUS_OBSERVABILITY_ENABLED"));
    }

    [Fact]
    public void AddQuarkusApp_AddsNoHealthCheck()
    {
        // /q/health only exists with the smallrye-health extension. Adding it unconditionally would leave
        // applications without that extension permanently unhealthy and stall every WaitFor on them.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        Assert.Empty(app.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public async Task AddQuarkusApp_NoBuildFile_ThrowsWhenTheResourceStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);
        using var application = builder.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(app.Resource, application.Services),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Directory '{tempDir.Path}' contains no pom.xml, build.gradle, build.gradle.kts, settings.gradle, or settings.gradle.kts, " +
            "so the build tool for resource 'inventory' cannot be detected. Check the path, or use AddJavaApp for an application laid out differently.",
            ex.Message);
    }

    [Fact]
    public async Task AddQuarkusApp_RemainsAJavaAppResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path)
            .WithJvmArgs("-Xmx256m")
            .WithExternalHttpEndpoints();

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx256m", envVars["JAVA_TOOL_OPTIONS"]);
    }
}
