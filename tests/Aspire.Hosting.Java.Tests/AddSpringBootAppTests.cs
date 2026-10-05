// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class AddSpringBootAppTests
{
    [Fact]
    public async Task AddSpringBootApp_MavenProject_LaunchesThroughSpringBootRun()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);
        using var application = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, application.Services),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper), tempDir.Path, "spring-boot:run"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddSpringBootApp_GradleProject_LaunchesThroughBootRun()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle", "plugins { id 'org.springframework.boot' }");

        var app = builder.AddSpringBootApp("orders", tempDir.Path);
        using var application = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, application.Services),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper), tempDir.Path, "bootRun"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddSpringBootApp_KotlinGradleProject_IsDetectedAsGradle()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("build.gradle.kts", "plugins { id(\"org.springframework.boot\") }");

        var app = builder.AddSpringBootApp("orders", tempDir.Path);
        using var application = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, application.Services),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
    }

    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    [InlineData("settings.gradle")]
    [InlineData("settings.gradle.kts")]
    public async Task BuildToolDetection_GradleMarkersAgreeBetweenRunAndPublish(string marker)
    {
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(marker, "");

        using var runBuilder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var runApp = runBuilder.AddSpringBootApp("catalog", tempDir.Path);
        using var application = runBuilder.Build();
        await runBuilder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(runApp.Resource, application.Services),
            TestContext.Current.CancellationToken);
        var runTool = Assert.Single(runApp.Resource.Annotations.OfType<JavaBuildToolAnnotation>()).Tool;

        using var publishBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var publishApp = publishBuilder.AddJavaApp("catalog", tempDir.Path, "build/libs/catalog.jar");
        var (publishTool, _) = JavaDockerfileGenerator.ResolveBuildTool(publishApp.Resource, tempDir.Path);

        Assert.Equal(JavaBuildTool.Gradle, runTool);
        Assert.Equal(runTool, publishTool);
    }

    [Theory]
    [InlineData("pom.xml")]
    [InlineData("build.gradle")]
    public void AddSpringBootApp_DoesNotCreateASeparateBuildResource(string buildFile)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);

        Assert.Same(app.Resource, Assert.Single(builder.Resources));
    }

    [Fact]
    public async Task AddSpringBootApp_ExplicitLaunchGoalOverridesTheDetectedDefault()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        var app = builder.AddSpringBootApp("catalog", tempDir.Path)
            .WithMavenGoal("spring-boot:test-run");
        using var application = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, application.Services),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ExpectedWrapperInvocation.Args(
                Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper),
                tempDir.Path,
                "spring-boot:test-run"),
            await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddSpringBootApp_ExplicitBuildArgumentsDoNotCreateASeparateBuildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        var app = builder.AddSpringBootApp("catalog", tempDir.Path)
            .WithMavenBuild("verify");
        using var application = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, application.Services),
            TestContext.Current.CancellationToken);

        Assert.Same(app.Resource, Assert.Single(builder.Resources));
        Assert.Equal(["verify"], Assert.Single(app.Resource.Annotations.OfType<JavaBuildStepAnnotation>()).Args);
        Assert.Equal(["spring-boot:run"], Assert.Single(app.Resource.Annotations.OfType<JavaBuildToolAnnotation>()).Args);
    }

    [Fact]
    public void AddSpringBootApp_DeclaresHttpEndpointThroughServerPort()
    {
        // Spring Boot reads SERVER_PORT, which is how the port Aspire allocates reaches the application
        // without any code in the application itself.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);

        var endpoint = Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("SERVER_PORT", endpoint.TargetPortEnvironmentVariable);

        // Pinning a target port would make two Spring Boot services collide on a real port on the machine,
        // because these run as host processes rather than containers.
        Assert.Null(endpoint.TargetPort);
    }

    [Fact]
    public void AddSpringBootApp_AddsNoHealthCheck()
    {
        // /actuator/health only exists with spring-boot-starter-actuator. Adding it unconditionally would
        // leave applications without that dependency permanently unhealthy and stall every WaitFor on them.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);

        Assert.Empty(app.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public async Task AddSpringBootApp_NoBuildFile_ThrowsWhenTheResourceStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);
        using var application = builder.Build();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(app.Resource, application.Services),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            $"Directory '{tempDir.Path}' contains no pom.xml, build.gradle, build.gradle.kts, settings.gradle, or settings.gradle.kts, " +
            "so the build tool for resource 'catalog' cannot be detected. Check the path, or use AddJavaApp for an application laid out differently.",
            ex.Message);
    }

    [Fact]
    public async Task BuildToolDetection_BothBuildToolsAreRejectedInRunAndPublish()
    {
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");
        tempDir.Write("build.gradle", "");

        using var runBuilder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var runApp = runBuilder.AddSpringBootApp("catalog", tempDir.Path);
        using var application = runBuilder.Build();
        var runException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runBuilder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(runApp.Resource, application.Services),
                TestContext.Current.CancellationToken));

        using var publishBuilder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var publishApp = publishBuilder.AddJavaApp("catalog", tempDir.Path, "target/catalog.jar");
        var publishException = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveBuildTool(publishApp.Resource, tempDir.Path));

        Assert.Equal(
            $"Directory '{tempDir.Path}' contains both Maven and Gradle build files, so the build tool for resource 'catalog' is ambiguous. " +
            "Use AddJavaApp and call WithMavenBuild, WithGradleBuild, WithMavenGoal, or WithGradleTask to choose one explicitly.",
            runException.Message);
        Assert.Equal(runException.Message, publishException.Message);
    }

    [Theory]
    [InlineData("pom.xml", "target")]
    [InlineData("build.gradle", "build")]
    public async Task WithOtelAgent_NoPath_ResolvesTheBuildToolsOutputDirectory(string buildFile, string outputDirectory)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path).WithOtelAgent();
        using var application = builder.Build();
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, application.Services),
            TestContext.Current.CancellationToken);

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var expected = Path.GetFullPath(Path.Combine(tempDir.Path, outputDirectory, "agent", "opentelemetry-javaagent.jar"));
        Assert.Equal($"-javaagent:{expected}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Theory]
    [InlineData("pom.xml", "target")]
    [InlineData("build.gradle", "build")]
    public async Task WithOtelAgent_NoPath_ResolvesTheBuildToolConfiguredAfterIt(string buildFile, string outputDirectory)
    {
        // The build tool is deliberately configured after the agent. WithWrapperPath promises order
        // independence, and the agent overload has no reason to be the one method that does not.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        var app = builder.AddJavaApp("catalog", tempDir.Path).WithOtelAgent();

        if (buildFile is "pom.xml")
        {
            app.WithMavenGoal("spring-boot:run").WithMavenBuild();
        }
        else
        {
            app.WithGradleTask("bootRun").WithGradleBuild();
        }

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        var expected = Path.GetFullPath(Path.Combine(tempDir.Path, outputDirectory, "agent", "opentelemetry-javaagent.jar"));
        Assert.Equal($"-javaagent:{expected}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Theory]
    [InlineData("pom.xml", "maven")]
    [InlineData("build.gradle", "gradle")]
    public async Task WithOtelAgent_BuildProducedAgent_AddsABuildTheApplicationWaitsFor(string buildFile, string toolName)
    {
        // spring-boot:run and bootRun compile the application themselves, so a Spring Boot resource
        // normally has no separate build resource. A build-produced agent makes one mandatory anyway:
        // JAVA_TOOL_OPTIONS is read by the wrapper's own JVM, so without a build that has already
        // written the agent, that JVM dies at VM initialization before the launch goal ever runs.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path).WithOtelAgent();

        var buildResource = Assert.Single(
            builder.Resources.OfType<JavaBuildResource>(),
            resource => resource.Name == $"catalog-{toolName}-build");

        Assert.Contains(
            app.Resource.Annotations.OfType<WaitAnnotation>(),
            wait => ReferenceEquals(wait.Resource, buildResource));

        // The build resource is the one that produces the agent, so it must not be asked to load it.
        var buildEnv = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            buildResource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.DoesNotContain("JAVA_TOOL_OPTIONS", buildEnv.Keys);
    }

    [Fact]
    public async Task WithOtelAgent_AbsoluteAgentPath_AddsNoBuild()
    {
        // An absolute path is supplied by the machine rather than produced by the build, so there is
        // nothing to build first and adding a resource would just slow the application down.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path)
            .WithOtelAgent(Path.Combine(Path.GetTempPath(), "opentelemetry-javaagent.jar"));

        Assert.Empty(builder.Resources.OfType<JavaBuildResource>());
        Assert.Empty(app.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public async Task WithOtelAgent_NoPath_WithoutABuild_Throws()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        // Resolution is deferred so the build tool can be configured afterwards, so the failure for a
        // resource that never configures one surfaces when the environment is evaluated.
        var app = builder.AddJavaApp("api", tempDir.Path).WithOtelAgent();

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance));

        Assert.Contains("has no Maven or Gradle build configured", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddSpringBootApp_RemainsAJavaAppResource()
    {
        // The helper is AddJavaApp with the Spring Boot defaults applied, so every other With… method has
        // to keep working on the result.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path)
            .WithJvmArgs("-Xmx256m")
            .WithExternalHttpEndpoints();

        TestEndpointAllocator.AllocateEndpoints(app.Resource);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx256m", envVars["JAVA_TOOL_OPTIONS"]);
        Assert.True(Assert.Single(app.Resource.Annotations.OfType<EndpointAnnotation>()).IsExternal);
    }

    [Fact]
    public void WithMavenBuildThenWithOtelAgent_CreatesTheBuildResourceTheAgentNeeds()
    {
        // spring-boot:run compiles on its way to running, so the first WithMavenBuild records the build
        // step without adding a resource. A relative OpenTelemetry agent path then makes the build
        // mandatory - nothing else writes target/agent/opentelemetry-javaagent.jar - and the second pass
        // through WithJavaBuildStep is the one that has to add it. Skipping on the annotation alone left
        // the annotation naming a resource that was never created, and the JVM failed at startup with
        // "Error opening zip file or JAR manifest missing".
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path)
            .WithMavenBuild("-DskipTests", "package");

        Assert.DoesNotContain(builder.Resources, r => r.Name == "catalog-maven-build");

        app.WithOtelAgent();

        var buildResource = Assert.Single(builder.Resources, r => r.Name == "catalog-maven-build");
        Assert.Equal(JavaBuildTool.Maven, Assert.IsType<JavaBuildResource>(buildResource).Tool);
    }
}
