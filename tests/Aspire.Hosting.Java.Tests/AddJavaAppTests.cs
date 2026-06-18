// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class AddJavaAppTests
{
    // ---- Manifest: baseline ------------------------------------------------

    [Fact]
    public async Task VerifyManifest_AddJavaApp_Baseline()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "java"
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_AddJavaAppWithJar()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "java",
              "args": [
                "-jar",
                "app.jar"
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    [Fact]
    public async Task VerifyManifest_AddJavaAppWithJarAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar", ["--server.port=8080"]);

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var expected = """
            {
              "type": "executable.v0",
              "workingDirectory": ".",
              "command": "java",
              "args": [
                "-jar",
                "app.jar",
                "--server.port=8080"
              ]
            }
            """;
        Assert.Equal(expected, manifest.ToString());
    }

    // ---- Resource properties ------------------------------------------------

    [Fact]
    public void AddJavaApp_SetsResourceName()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("myapi", AppContext.BaseDirectory);

        Assert.Equal("myapi", app.Resource.Name);
    }

    [Fact]
    public void AddJavaApp_UsesJavaAsCommand()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.Equal("java", app.Resource.Command);
    }

    [Fact]
    public void AddJavaApp_ResolvesWorkingDirectoryFullPath()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path);

        var expectedPath = Path.GetFullPath(tempDir.Path, builder.AppHostDirectory);
        Assert.Equal(expectedPath, app.Resource.WorkingDirectory);
    }

    [Fact]
    public void AddJavaApp_ImplementsIResourceWithServiceDiscovery()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(app.Resource);
    }

    [Fact]
    public void AddJavaApp_ImplementsIContainerFilesDestinationResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.IsAssignableFrom<IContainerFilesDestinationResource>(app.Resource);
    }

    [Fact]
    public async Task AddJavaApp_DefaultArgsAreEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Empty(args);
    }

    [Fact]
    public async Task AddJavaAppWithJar_ArgsAreJarAndUserArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar", ["--port=9090"]);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["-jar", "app.jar", "--port=9090"], args);
    }

    [Fact]
    public async Task AddJavaAppWithJar_NoUserArgs_OnlyJarArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal(["-jar", "app.jar"], args);
    }

    // ---- WithMavenGoal ------------------------------------------------------

    [Fact]
    public void WithMavenGoalShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithMavenGoal("spring-boot:run");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithMavenGoalShouldThrowWhenGoalIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var nullAction = () => app.WithMavenGoal(null!);
        var emptyAction = () => app.WithMavenGoal(string.Empty);

        var nullEx = Assert.Throws<ArgumentNullException>(nullAction);
        Assert.Equal("goal", nullEx.ParamName);

        var emptyEx = Assert.Throws<ArgumentException>(emptyAction);
        Assert.Equal("goal", emptyEx.ParamName);
    }

    [Fact]
    public async Task WithMavenGoal_PassesGoalAsArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("spring-boot:run", args);
    }

    [Fact]
    public async Task WithMavenGoal_WithArgs_IncludesGoalAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithMavenGoal("spring-boot:run", "-DskipTests");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("spring-boot:run", args);
        Assert.Contains("-DskipTests", args);
    }

    // ---- WithGradleTask -----------------------------------------------------

    [Fact]
    public void WithGradleTaskShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithGradleTask("bootRun");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithGradleTaskShouldThrowWhenTaskIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var nullAction = () => app.WithGradleTask(null!);
        var emptyAction = () => app.WithGradleTask(string.Empty);

        var nullEx = Assert.Throws<ArgumentNullException>(nullAction);
        Assert.Equal("task", nullEx.ParamName);

        var emptyEx = Assert.Throws<ArgumentException>(emptyAction);
        Assert.Equal("task", emptyEx.ParamName);
    }

    [Fact]
    public async Task WithGradleTask_PassesTaskAsArgument()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("bootRun", args);
    }

    [Fact]
    public async Task WithGradleTask_WrapperPathIsResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the wrapper as the command, args contain only the task
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, JavaHostingExtensions.DefaultGradleWrapper));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("bootRun", args);
    }

    [Fact]
    public async Task WithMavenGoal_WrapperPathIsResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the wrapper as the command, args contain only the goal
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, JavaHostingExtensions.DefaultMavenWrapper));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("spring-boot:run", args);
    }

    [Fact]
    public async Task WithGradleTask_WithArgs_IncludesTaskAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithGradleTask("bootRun", "--no-daemon");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("bootRun", args);
        Assert.Contains("--no-daemon", args);
    }

    [Fact]
    public void WithGradleTask_ThrowsWhenJarPathIsSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar");

        var action = () => app.WithGradleTask("bootRun");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains(nameof(JavaAppResource.JarPath), exception.Message);
    }

    [Fact]
    public async Task WithMavenGoal_DoesNotThrowWhenJarPathIsSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "app.jar")
            .WithMavenGoal("spring-boot:run");

        // When a MavenGoal is set, the WithArgs callback emits the goal args
        // instead of -jar, which is the expected behavior.
        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);
        Assert.Contains("spring-boot:run", args);
        Assert.DoesNotContain("-jar", args);
    }

    // ---- WithWrapperPath ----------------------------------------------------

    [Fact]
    public void WithWrapperPathShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithWrapperPath("custom-mvnw");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithWrapperPathShouldThrowWhenWrapperScriptIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var nullAction = () => app.WithWrapperPath(null!);
        var emptyAction = () => app.WithWrapperPath(string.Empty);

        var nullEx = Assert.Throws<ArgumentNullException>(nullAction);
        Assert.Equal("wrapperScript", nullEx.ParamName);

        var emptyEx = Assert.Throws<ArgumentException>(emptyAction);
        Assert.Equal("wrapperScript", emptyEx.ParamName);
    }

    [Fact]
    public async Task WithWrapperPath_OverridesMavenDefaultWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("scripts/custom-mvnw")
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "scripts/custom-mvnw"));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("spring-boot:run", args);
    }

    [Fact]
    public async Task WithWrapperPath_OverridesGradleDefaultWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("scripts/custom-gradlew")
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "scripts/custom-gradlew"));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("bootRun", args);
    }

    // ---- WithJvmArgs --------------------------------------------------------

    [Fact]
    public void WithJvmArgsShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithJvmArgs(["-Xmx512m"]);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithJvmArgsShouldThrowWhenArgsIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var action = () => app.WithJvmArgs(null!);

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal("args", exception.ParamName);
    }

    [Fact]
    public async Task WithJvmArgs_SetsJavaToolOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Xmx512m", "-Xms256m"]);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx512m -Xms256m", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithJvmArgs_EmptyArgs_DoesNotSetJavaToolOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs([]);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.False(envVars.ContainsKey("JAVA_TOOL_OPTIONS"));
    }

    [Fact]
    public async Task WithJvmArgs_MultipleCalls_MergeValues()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Xmx512m"])
            .WithJvmArgs(["-Xms256m"]);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx512m -Xms256m", envVars["JAVA_TOOL_OPTIONS"]);
    }

    // ---- WithOtelAgent ------------------------------------------------------

    [Fact]
    public void WithOtelAgentShouldThrowWhenBuilderIsNull()
    {
        IResourceBuilder<JavaAppResource> builder = null!;

        var action = () => builder.WithOtelAgent();

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public async Task WithOtelAgent_WithoutAgentPath_CallsWithOtlpExporter()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent();

        // WithOtlpExporter is called internally; verify the env vars have OTLP settings.
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.True(envVars.ContainsKey("OTEL_EXPORTER_OTLP_ENDPOINT"));
    }

    [Fact]
    public async Task WithOtelAgent_WithAgentPath_SetsJavaAgentInToolOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent("/opt/otel/agent.jar");

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-javaagent:/opt/otel/agent.jar", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_WithAgentPath_CombinedWithJvmArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Xmx512m"])
            .WithOtelAgent("/opt/otel/agent.jar");

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal("-Xmx512m -javaagent:/opt/otel/agent.jar", envVars["JAVA_TOOL_OPTIONS"]);
    }

    // ---- WithMavenBuild / WithGradleBuild -----------------------------------

    [Fact]
    public void WithMavenBuild_CreatesMavenBuildResourceInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild();

        Assert.Contains(builder.Resources, r => r.Name == "api-maven-build");
        Assert.IsType<MavenBuildResource>(builder.Resources.First(r => r.Name == "api-maven-build"));
    }

    [Fact]
    public void WithMavenBuild_CustomArgs_CreatesBuildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild("clean", "install", "-DskipTests");

        var buildResource = builder.Resources.First(r => r.Name == "api-maven-build");
        Assert.IsType<MavenBuildResource>(buildResource);
    }

    [Fact]
    public void WithGradleBuild_CreatesGradleBuildResourceInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        Assert.Contains(builder.Resources, r => r.Name == "api-gradle-build");
        Assert.IsType<GradleBuildResource>(builder.Resources.First(r => r.Name == "api-gradle-build"));
    }

    [Fact]
    public void WithGradleBuild_CustomArgs_CreatesBuildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild("clean", "assemble", "--info");

        var buildResource = builder.Resources.First(r => r.Name == "api-gradle-build");
        Assert.IsType<GradleBuildResource>(buildResource);
    }

    [Fact]
    public void WithMavenBuild_DoesNotCreateBuildResourceInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild();

        Assert.DoesNotContain(builder.Resources, r => r.Name == "api-maven-build");
    }

    [Fact]
    public void WithGradleBuild_DoesNotCreateBuildResourceInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        Assert.DoesNotContain(builder.Resources, r => r.Name == "api-gradle-build");
    }

    [Fact]
    public void WithMavenBuild_BuildResourceHasParentRelationship()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild();

        var buildResource = builder.Resources.First(r => r.Name == "api-maven-build");
        Assert.True(buildResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Contains(relationships, r => r.Type == "Parent" && r.Resource == app.Resource);
    }

    [Fact]
    public void WithGradleBuild_BuildResourceHasParentRelationship()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        var buildResource = builder.Resources.First(r => r.Name == "api-gradle-build");
        Assert.True(buildResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Contains(relationships, r => r.Type == "Parent" && r.Resource == app.Resource);
    }

    // ---- JarPath property ---------------------------------------------------

    [Fact]
    public void AddJavaApp_WithJarPath_SetsJarPathProperty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, "target/app.jar");

        Assert.Equal("target/app.jar", app.Resource.JarPath);
    }

    [Fact]
    public void AddJavaApp_WithoutJarPath_JarPathIsNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.Null(app.Resource.JarPath);
    }

    // ---- VS Code debugging --------------------------------------------------

    [Fact]
    public void AddJavaApp_InRunMode_SupportsDebugging()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var annotation = app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.NotNull(annotation);
        Assert.Equal("java", annotation!.LaunchConfigurationType);
    }

    [Fact]
    public void AddJavaApp_InPublishMode_DoesNotAddDebuggingAnnotation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var annotation = app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>().SingleOrDefault();
        Assert.Null(annotation);
    }

    // ---- Chaining multiple methods ------------------------------------------

    [Fact]
    public async Task WithMavenGoal_ThenWithJvmArgs_SetsBothConfigurations()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithMavenGoal("spring-boot:run")
            .WithJvmArgs(["-Xmx1g"]);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);
        Assert.Contains("spring-boot:run", args);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        Assert.Equal("-Xmx1g", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithGradleTask_ThenWithOtelAgent_SetsBothConfigurations()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithGradleTask("bootRun")
            .WithOtelAgent("/opt/otel/agent.jar");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);
        Assert.Contains("bootRun", args);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        Assert.Equal("-javaagent:/opt/otel/agent.jar", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithWrapperPath_ThenWithMavenGoal_UsesCustomWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("tools/mvn")
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "tools/mvn"));
        Assert.Equal(expectedWrapper, app.Resource.Command);
        Assert.Contains("spring-boot:run", args);
    }

    // ---- Manifest with Maven/Gradle goals -----------------------------------

    [Fact]
    public async Task VerifyManifest_WithMavenGoal()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        // The manifest should show the maven wrapper as the command with the goal as args.
        var args = manifest?["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Contains("spring-boot:run", args!.Select(a => a?.ToString()));
    }

    [Fact]
    public async Task VerifyManifest_WithGradleTask()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TestTempDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var args = manifest?["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Contains("bootRun", args!.Select(a => a?.ToString()));
    }
}
