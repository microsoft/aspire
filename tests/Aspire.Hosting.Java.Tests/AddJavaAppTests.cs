// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Java.Tests;

public class AddJavaAppTests
{
    // An absolute path on the current platform. Run mode resolves the agent through
    // Path.GetFullPath, which rewrites a POSIX-style literal to C:\opt\... on Windows, so the
    // expected value has to be platform-specific.
    private static readonly string s_absoluteAgentPath =
        OperatingSystem.IsWindows() ? @"C:\opt\otel\agent.jar" : "/opt/otel/agent.jar";

    private static string AbsoluteAgentPath => s_absoluteAgentPath;

    // Publish mode emits the authored path verbatim, and it is read inside the Linux image the
    // application is published to, so the absolute form is POSIX on every platform. Using the
    // platform-specific literal above instead made this scenario unreachable on Windows: publishing
    // rejects a Windows-rooted agent outright, which
    // VerifyPublish_AWindowsAbsoluteOtelAgentIsRejectedOnEveryPlatform covers.
    private const string ContainerProvidedAgentPath = "/opt/otel/agent.jar";

    // ---- Launch mode -------------------------------------------------------

    [Fact]
    public async Task AddJavaApp_MavenGoal_LaunchesThroughTheWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        // The command has to become the wrapper. Leaving it as "java" while the goal was still contributed
        // as an argument produced the uninvokable command line "java spring-boot:run".
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper), tempDir.Path, "spring-boot:run"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public async Task AddJavaApp_GradleTask_LaunchesThroughTheWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path).WithGradleTask("bootRun", "--no-daemon");

        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper), tempDir.Path, "bootRun", "--no-daemon"), await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
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
        using var tempDir = new TempJavaAppDirectory();

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
    public async Task AddJavaApp_WithoutLaunchMode_ThrowsWhenArgumentsAreGathered()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // A bare "java" with no arguments prints the JVM usage text and exits, so the failure is raised
        // where it can name the resource and the fix.
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ArgumentEvaluator.GetArgumentListAsync(app.Resource));

        Assert.Equal(
            "Java application 'api' has no launch mode configured. Call WithMavenGoal or WithGradleTask to run it through a build tool, or use the AddJavaApp overload that takes a jarPath to run a prebuilt JAR.",
            exception.Message);
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
        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path);

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
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("spring-boot:run", args);
    }

    [Fact]
    public async Task WithMavenGoal_WithArgs_IncludesGoalAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path)
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
        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path);

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
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("bootRun", args);
    }

    [Fact]
    public async Task WithGradleTask_WrapperPathIsResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the wrapper as the command, args contain only the task
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultGradleWrapper));
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(expectedWrapper, tempDir.Path, "bootRun"), args);
    }

    [Fact]
    public async Task WithMavenGoal_WrapperPathIsResolved()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the wrapper as the command, args contain only the goal
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper));
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(expectedWrapper, tempDir.Path, "spring-boot:run"), args);
    }

    [Fact]
    public async Task WithGradleTask_FindsTheWrapperAtTheRootOfAMultiProjectBuild()
    {
        // A Gradle multi-project build has exactly one gradlew, at the root next to settings.gradle.
        // Subprojects never carry their own, so a resource pointed at services/api has to walk up to it.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var repo = new TempJavaAppDirectory();
        repo.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);
        File.WriteAllText(Path.Combine(repo.Path, "settings.gradle"), "include 'services:api'");
        var module = Directory.CreateDirectory(Path.Combine(repo.Path, "services", "api")).FullName;
        File.WriteAllText(Path.Combine(module, "build.gradle"), "");

        var app = builder.AddJavaApp("api", module).WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        var expectedWrapper = Path.GetFullPath(Path.Combine(repo.Path, JavaHostingExtensions.s_defaultGradleWrapper));
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(expectedWrapper, module, "bootRun"), args);
    }

    [Fact]
    public async Task WithMavenGoal_FindsTheWrapperAtTheRootOfAMultiModuleBuild()
    {
        // Maven multi-module repositories keep mvnw next to the aggregator pom, not in each module.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var repo = new TempJavaAppDirectory();
        repo.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);
        File.WriteAllText(Path.Combine(repo.Path, "pom.xml"), "<project />");
        var module = Directory.CreateDirectory(Path.Combine(repo.Path, "services", "api")).FullName;
        File.WriteAllText(Path.Combine(module, "pom.xml"), "<project />");

        var app = builder.AddJavaApp("api", module).WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        var expectedWrapper = Path.GetFullPath(Path.Combine(repo.Path, JavaHostingExtensions.s_defaultMavenWrapper));
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(expectedWrapper, module, "spring-boot:run"), args);
    }

    [Fact]
    public async Task WithGradleTask_PrefersTheWrapperInTheApplicationDirectory()
    {
        // A module that carries its own wrapper keeps using it, even when an ancestor has one too.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var repo = new TempJavaAppDirectory();
        repo.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);
        var module = Directory.CreateDirectory(Path.Combine(repo.Path, "services", "api")).FullName;
        File.WriteAllText(Path.Combine(module, JavaHostingExtensions.s_defaultGradleWrapper), "#!/bin/sh\n");

        var app = builder.AddJavaApp("api", module).WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        var expectedWrapper = Path.GetFullPath(Path.Combine(module, JavaHostingExtensions.s_defaultGradleWrapper));
        Assert.Equal(ExpectedWrapperInvocation.Args(expectedWrapper, module, "bootRun"), args);
    }

    [Theory]
    [InlineData("mvnw.cmd")]
    [InlineData("gradlew.bat")]
    public void WrapperInvocationForWindowsRunsTheWrapperThroughCall(string wrapperName)
    {
        // cmd strips the first and last quote on the line when the first token is quoted, so a wrapper
        // path containing a space would be mangled if it led. "call" keeps a quote off the front.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "repo", "services", "api");
        var wrapperPath = Path.Combine(Path.GetTempPath(), "repo", "build tools", wrapperName);

        var (command, leadingArgs) = JavaHostingExtensions.WrapperInvocationFor(wrapperPath, workingDirectory, isWindows: true);

        Assert.Equal(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", command);
        Assert.Equal(["/c", "call", Path.Combine("..", "..", "build tools", wrapperName)], leadingArgs);
    }

    [Fact]
    public void WrapperInvocationForUnixRunsTheWrapperThroughShWithItsFullPath()
    {
        // A wrapper checked out on Windows arrives without its executable bit, so it is run by sh
        // rather than executed. The path stays absolute because no shell resolves it.
        var wrapperPath = Path.Combine(Path.GetTempPath(), "repo", "mvnw");

        var (command, leadingArgs) = JavaHostingExtensions.WrapperInvocationFor(
            wrapperPath, Path.Combine(Path.GetTempPath(), "repo", "services", "api"), isWindows: false);

        Assert.Equal("sh", command);
        Assert.Equal([wrapperPath], leadingArgs);
    }

    [Fact]
    public async Task WithGradleTask_WithArgs_IncludesTaskAndArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun", "--no-daemon");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Contains("bootRun", args);
        Assert.Contains("--no-daemon", args);
    }

    [Fact]
    public void WithGradleTask_ThrowsWhenJarPathIsSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path, "app.jar");

        var action = () => app.WithGradleTask("bootRun");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "WithGradleTask cannot be used when a JAR path has been specified. Use either the AddJavaApp overload that takes a jarPath, or WithGradleTask, not both.",
            exception.Message);
    }

    [Fact]
    public void WithMavenGoal_ThrowsWhenJarPathIsSet()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path, "app.jar");

        var action = () => app.WithMavenGoal("spring-boot:run");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "WithMavenGoal cannot be used when a JAR path has been specified. Use either the AddJavaApp overload that takes a jarPath, or WithMavenGoal, not both.",
            exception.Message);
    }

    [Fact]
    public void WithGradleTask_ThrowsWhenMavenGoalIsAlreadyConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        var action = () => app.WithGradleTask("bootRun");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "WithGradleTask cannot be used when the application is already configured to launch with Maven. A Java application is launched by a single build tool.",
            exception.Message);
    }

    [Fact]
    public void WithMavenGoal_ThrowsWhenGradleTaskIsAlreadyConfigured()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path).WithGradleTask("bootRun");

        var action = () => app.WithMavenGoal("spring-boot:run");

        var exception = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(
            "WithMavenGoal cannot be used when the application is already configured to launch with Gradle. A Java application is launched by a single build tool.",
            exception.Message);
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
    public void WithWrapperPathShouldThrowWhenWrapperPathIsNullOrEmpty()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var nullAction = () => app.WithWrapperPath(null!);
        var emptyAction = () => app.WithWrapperPath(string.Empty);

        var nullEx = Assert.Throws<ArgumentNullException>(nullAction);
        Assert.Equal("wrapperPath", nullEx.ParamName);

        var emptyEx = Assert.Throws<ArgumentException>(emptyAction);
        Assert.Equal("wrapperPath", emptyEx.ParamName);
    }

    [Fact]
    public async Task WithWrapperPath_OverridesMavenDefaultWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("scripts/custom-mvnw")
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "scripts/custom-mvnw"));
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(expectedWrapper, tempDir.Path, "spring-boot:run"), args);
    }

    [Fact]
    public async Task WithWrapperPath_OverridesGradleDefaultWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("scripts/custom-gradlew")
            .WithGradleTask("bootRun");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "scripts/custom-gradlew"));
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(expectedWrapper, tempDir.Path, "bootRun"), args);
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

        var action = () => builder.WithOtelAgent("/opt/otel/agent.jar");

        var exception = Assert.Throws<ArgumentNullException>(action);
        Assert.Equal(nameof(builder), exception.ParamName);
    }

    [Fact]
    public void WithOtelAgentShouldThrowWhenAgentPathIsNullOrWhiteSpace()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        Assert.Throws<ArgumentException>(() => app.WithOtelAgent("  "));
    }

    [Fact]
    public async Task AddJavaApp_ConfiguresOtlpExporterWithoutAnAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // The OTLP exporter is wired by AddJavaApp itself, so a Java application reports telemetry
        // through Micrometer/OTel SDK instrumentation even when the Java agent is not used.
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.False(string.IsNullOrEmpty(envVars["OTEL_EXPORTER_OTLP_ENDPOINT"]));

        // The other half of what this test claims: no agent was requested, so nothing should have put
        // -javaagent on the JVM. Without this the test passes just as happily when an agent is wired.
        Assert.False(envVars.ContainsKey("JAVA_TOOL_OPTIONS"));
    }

    [Fact]
    public async Task WithOtelAgent_WithAgentPath_SetsJavaAgentInToolOptions()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent(AbsoluteAgentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"-javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_CalledTwice_UsesOnlyTheLastAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        // The annotation replaces, so the second call has to win outright. Two -javaagent: entries
        // would start the JVM with both agents attached, which double-instruments the application.
        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent("/opt/otel/first.jar")
            .WithOtelAgent(AbsoluteAgentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"-javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_WithAgentPath_CombinedWithJvmArgs()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Xmx512m"])
            .WithOtelAgent(AbsoluteAgentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        Assert.Equal($"-Xmx512m -javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithMavenGoal_WithoutAWrapperOnDisk_IsRejectedWhenTheResourceStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        // A globally installed Maven is deliberately not used as a fallback: the wrapper pins the tool
        // version in the repository, so the AppHost, CI, and the published image all build with the same
        // one. Failing here names the fix instead of silently building with whatever is on the machine.
        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        using var built = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(app.Resource, built.Services),
                CancellationToken.None));

        Assert.Contains("has no mvnw", ex.Message);
        Assert.Contains("mvn -N wrapper:wrapper", ex.Message);
    }

    [Fact]
    public async Task WithGradleTask_WithoutAWrapperOnDisk_IsRejectedWhenTheResourceStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        var app = builder.AddJavaApp("api", tempDir.Path).WithGradleTask("bootRun");

        using var built = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(app.Resource, built.Services),
                CancellationToken.None));

        Assert.Contains("has no gradlew", ex.Message);
        Assert.Contains("gradle wrapper", ex.Message);
    }

    [Fact]
    public async Task WithWrapperPath_AfterTheBuildTool_WorksWhenTheProjectHasNoDefaultWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        var customWrapper = Path.Combine(tempDir.Path, "tools", "mvnw");
        Directory.CreateDirectory(Path.GetDirectoryName(customWrapper)!);
        File.WriteAllText(customWrapper, "#!/bin/sh\n");

        // WithWrapperPath is documented as order-independent. Resolving the wrapper eagerly inside
        // WithMavenGoal made that untrue for the one project shape where the override actually matters:
        // a project whose only wrapper is the custom one threw before the override could be applied.
        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithWrapperPath(Path.Combine("tools", "mvnw"));

        using var built = builder.Build();

        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(app.Resource, built.Services),
            CancellationToken.None);

        // Path.GetFullPath matches the normalization the resource applies, and resolves the symlinked
        // temp directory the same way on both sides so the comparison is about the wrapper, not the path.
        // On Unix the wrapper moves into the first argument, so asserting only the command would stop
        // checking that the override was applied at all.
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(
            ExpectedWrapperInvocation.Args(Path.GetFullPath(customWrapper), Path.GetFullPath(tempDir.Path), "spring-boot:run"),
            await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Windows has no executable bit, and its wrappers are batch files sh cannot run")]
    public async Task WithMavenGoal_WrapperWithoutTheExecutableBit_StillProducesALaunchableCommandLine()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        // Git records no executable bit on Windows, so a wrapper committed there checks out like this.
        // Executing it directly fails with "permission denied"; the launch has to survive that.
        var wrapper = Path.Combine(tempDir.Path, JavaHostingExtensions.s_defaultMavenWrapper);
        await File.WriteAllTextAsync(wrapper, "#!/bin/sh\nexit 0\n");
        // CA1416 does not understand SkipOnPlatform, which already keeps this off Windows.
#pragma warning disable CA1416
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
#pragma warning restore CA1416

        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        using var process = Process.Start(new ProcessStartInfo(app.Resource.Command)
        {
            // The goal is dropped: the point is that the wrapper runs at all, not what it does.
            ArgumentList = { args[0] },
            WorkingDirectory = tempDir.Path,
            RedirectStandardError = true
        })!;

        await process.WaitForExitAsync(CancellationToken.None);

        Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync());
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task WithWrapperPath_PointingAtAMissingFile_IsRejectedWhenTheResourceStarts()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithWrapperPath("tools/mvnw");

        using var built = builder.Build();

        var ex = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => builder.Eventing.PublishAsync(
                new BeforeResourceStartedEvent(app.Resource, built.Services),
                CancellationToken.None));

        // The override is what is wrong here, so the message points at WithWrapperPath rather than
        // telling the user to generate a wrapper they did not ask for.
        Assert.Contains(nameof(JavaHostingExtensions.WithWrapperPath), ex.Message);
        Assert.DoesNotContain("mvn -N wrapper:wrapper", ex.Message);
    }

    [Fact]
    public async Task WithWrapperPath_IsHonouredEvenWhenNoWrapperExistsOnDisk()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory(withWrappers: false);

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("/opt/maven/bin/mvn")
            .WithMavenGoal("spring-boot:run");

        // An explicit override is a deliberate choice and must win over the default wrapper probe, which
        // would otherwise reject this project for shipping no mvnw.
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(
            ExpectedWrapperInvocation.Args("/opt/maven/bin/mvn", tempDir.Path, "spring-boot:run"),
            await ArgumentEvaluator.GetArgumentListAsync(app.Resource));
    }

    [Fact]
    public void AddJavaApp_RequestsSystemCertificateTrustScope()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory);

        // -Djavax.net.ssl.trustStore replaces the JVM's trust anchors rather than adding to them, so the
        // generated bundle has to contain the system roots as well. Under the default Append scope the
        // bundle would hold only Aspire's own certificates and the JVM would stop trusting every public
        // CA -- which also breaks Maven Central and Gradle distribution downloads, because
        // JAVA_TOOL_OPTIONS is inherited by the build tool's JVM.
        Assert.True(app.Resource.TryGetLastAnnotation<CertificateAuthorityCollectionAnnotation>(out var certAnnotation));
        Assert.Equal(CertificateTrustScope.System, certAnnotation.Scope);
    }

    [Fact]
    public async Task AddJavaApp_WithAppendCertificateTrustScope_DoesNotOverrideTheJvmTrustStore()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var javaApp = builder.AddJavaApp("api", AppContext.BaseDirectory);

        using var app = builder.Build();

        Assert.True(javaApp.Resource.TryGetLastAnnotation<CertificateTrustConfigurationCallbackAnnotation>(out var annotation));

        var envVars = new Dictionary<string, object>();
        await annotation.Callback(new CertificateTrustConfigurationCallbackAnnotationContext
        {
            ExecutionContext = new DistributedApplicationExecutionContext(
                new DistributedApplicationExecutionContextOptions(DistributedApplicationOperation.Run)
                {
                    Services = app.Services
                }),
            Resource = javaApp.Resource,
            Arguments = [],
            EnvironmentVariables = envVars,
            CertificateBundlePath = ReferenceExpression.Create($"/etc/ssl/aspire/bundle.p12"),
            CertificateDirectoriesPath = ReferenceExpression.Create($"/etc/ssl/aspire/certs"),
            Scope = CertificateTrustScope.Append,
            CancellationToken = default
        });

        // Under Append the bundle holds only Aspire's own certificates. Pointing the JVM at it would
        // drop every public certificate authority, so the override is skipped entirely rather than
        // applied against an incomplete bundle.
        Assert.Empty(envVars);
    }

    [Fact]
    public async Task WithOtelAgent_AgentPathContainingSpaces_IsQuotedForTheJvm()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var agentPath = OperatingSystem.IsWindows()
            ? @"C:\opt\java agents\opentelemetry-javaagent.jar"
            : "/opt/java agents/opentelemetry-javaagent.jar";

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent(agentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // The JVM splits JAVA_TOOL_OPTIONS on whitespace but honours double quotes, so an unquoted path
        // containing a space aborts startup with "Unrecognized option: agents/opentelemetry-javaagent.jar"
        // before any application code runs. -javaagent: has no '=' so the whole option is quoted.
        Assert.Equal($"\"-javaagent:{agentPath}\"", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithJvmArgs_ValueContainingSpaces_IsQuotedAfterTheAssignment()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithJvmArgs(["-Dapp.data.dir=/var/lib/my app"]);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // For an option with an '=' only the value is quoted. Quoting the whole option would make the JVM
        // treat "-Dapp.data.dir" as part of the property name and the property would silently not be set.
        Assert.Equal("-Dapp.data.dir=\"/var/lib/my app\"", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_RelativeAgentPath_IsMadeAbsoluteInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent(Path.Combine("target", "agent", "opentelemetry-javaagent.jar"));

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);

        // JAVA_TOOL_OPTIONS is inherited by every JVM started beneath the resource, and build tools start
        // JVMs from directories other than the application directory. The Gradle daemon in particular
        // starts from its own distribution directory, so a relative -javaagent: path fails to resolve and
        // the daemon dies during VM initialization rather than reporting a normal build failure.
        var expected = Path.GetFullPath(
            Path.Combine(app.Resource.WorkingDirectory, "target", "agent", "opentelemetry-javaagent.jar"));

        Assert.Equal($"-javaagent:{expected}", envVars["JAVA_TOOL_OPTIONS"]);
        Assert.True(Path.IsPathFullyQualified(expected));
    }

    [Fact]
    public async Task WithOtelAgent_RelativeAgentPath_PointsAtContainerPathInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent("target/agent/opentelemetry-javaagent.jar");

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        // The path has to be interpreted inside the container, so a build-machine path would be wrong.
        // The generated Dockerfile copies the build-produced agent to a fixed location, and this has to
        // agree with it or the container starts a JVM pointing at a JAR that is not in the image.
        Assert.Equal("-javaagent:/app/agent.jar", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithOtelAgent_AbsoluteAgentPath_IsLeftUnchangedInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory)
            .WithOtelAgent(ContainerProvidedAgentPath);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        // An absolute path cannot have come out of the build context, so it is the base image's or a
        // mount's responsibility and rewriting it would break that arrangement.
        Assert.Equal($"-javaagent:{ContainerProvidedAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    // ---- WithMavenBuild / WithGradleBuild -----------------------------------

    [Fact]
    public void WithMavenBuild_CreatesMavenBuildResourceInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild();

        Assert.Contains(builder.Resources, r => r.Name == "api-maven-build");
        Assert.Equal(JavaBuildTool.Maven, Assert.IsType<JavaBuildResource>(builder.Resources.First(r => r.Name == "api-maven-build")).Tool);
    }

    [Fact]
    public void WithMavenBuild_CustomArgs_CreatesBuildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild("clean", "install", "-DskipTests");

        var buildResource = builder.Resources.First(r => r.Name == "api-maven-build");
        Assert.Equal(JavaBuildTool.Maven, Assert.IsType<JavaBuildResource>(buildResource).Tool);
    }

    [Fact]
    public void WithGradleBuild_CreatesGradleBuildResourceInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        Assert.Contains(builder.Resources, r => r.Name == "api-gradle-build");
        Assert.Equal(JavaBuildTool.Gradle, Assert.IsType<JavaBuildResource>(builder.Resources.First(r => r.Name == "api-gradle-build")).Tool);
    }

    [Fact]
    public void WithGradleBuild_CustomArgs_CreatesBuildResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild("clean", "assemble", "--info");

        var buildResource = builder.Resources.First(r => r.Name == "api-gradle-build");
        Assert.Equal(JavaBuildTool.Gradle, Assert.IsType<JavaBuildResource>(buildResource).Tool);
    }

    [Fact]
    public void WithMavenBuild_DoesNotCreateBuildResourceInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithMavenBuild();

        Assert.DoesNotContain(builder.Resources, r => r.Name == "api-maven-build");
    }

    [Fact]
    public void WithGradleBuild_DoesNotCreateBuildResourceInPublishMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        Assert.DoesNotContain(builder.Resources, r => r.Name == "api-gradle-build");
    }

    [Theory]
    [InlineData(nameof(JavaBuildTool.Maven), true)]
    [InlineData(nameof(JavaBuildTool.Maven), false)]
    [InlineData(nameof(JavaBuildTool.Gradle), true)]
    [InlineData(nameof(JavaBuildTool.Gradle), false)]
    public void WithBuildAndLaunch_DoesNotCreateASeparateBuildResource(string toolName, bool buildFirst)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path);

        if (toolName is nameof(JavaBuildTool.Maven))
        {
            if (buildFirst)
            {
                app.WithMavenBuild();
                app.WithMavenGoal("spring-boot:run");
            }
            else
            {
                app.WithMavenGoal("spring-boot:run");
                app.WithMavenBuild();
            }
        }
        else
        {
            if (buildFirst)
            {
                app.WithGradleBuild();
                app.WithGradleTask("bootRun");
            }
            else
            {
                app.WithGradleTask("bootRun");
                app.WithGradleBuild();
            }
        }

        Assert.Same(app.Resource, Assert.Single(builder.Resources));
        Assert.Null(Assert.Single(app.Resource.Annotations.OfType<JavaBuildStepAnnotation>()).ResourceName);
    }

    [Fact]
    public void WithMavenBuild_BuildResourceHasParentRelationship()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

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
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleBuild();

        var buildResource = builder.Resources.First(r => r.Name == "api-gradle-build");
        Assert.True(buildResource.TryGetAnnotationsOfType<ResourceRelationshipAnnotation>(out var relationships));
        Assert.Contains(relationships, r => r.Type == "Parent" && r.Resource == app.Resource);
    }

    // ---- JAR path -----------------------------------------------------------

    [Theory]
    [InlineData("target/app.jar")]
    [InlineData(@"target\app.jar")]
    public async Task AddJavaApp_WithJarPath_LaunchesTheJar(string jarPath)
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        var app = builder.AddJavaApp("api", AppContext.BaseDirectory, jarPath);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        Assert.Equal("java", app.Resource.Command);
        Assert.Equal(["-jar", "target/app.jar"], args);
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

    [Fact]
    public async Task WithJvmArgs_IdeDebugLaunchUsesJavaToolOptionsOnly()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project><artifactId>api</artifactId></project>");

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithJvmArgs("-javaagent:/opt/coverage-agent.jar");

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        var launchConfiguration = await GetLaunchConfigurationAsync(app);
        var serializedLaunchConfiguration = JsonSerializer.SerializeToElement(launchConfiguration);

        // The IDE-launched JVM inherits the resource environment. Repeating this value as vm_args would
        // attach single-instance options such as -javaagent twice and either fail or double-instrument.
        Assert.Equal("-javaagent:/opt/coverage-agent.jar", envVars["JAVA_TOOL_OPTIONS"]);
        Assert.False(serializedLaunchConfiguration.TryGetProperty("vm_args", out _));
    }

    // ---- Chaining multiple methods ------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithMavenBuild_AndWithMavenGoal_ProduceTheSameGraphInEitherOrder(bool goalFirst)
    {
        // The launch goal compiles the application, so the build resource is redundant either way.
        // One order never creates it; the other creates it and then unwinds it by hand. Those two
        // paths have to converge, or which methods a user happens to chain first decides whether an
        // extra Maven build runs before every launch.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "");

        var app = builder.AddJavaApp("api", tempDir.Path);
        if (goalFirst)
        {
            app.WithMavenGoal("spring-boot:run").WithMavenBuild("clean", "package");
        }
        else
        {
            app.WithMavenBuild("clean", "package").WithMavenGoal("spring-boot:run");
        }

        Assert.Equal(["api"], builder.Resources.Select(resource => resource.Name).Order());
        Assert.Empty(app.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Null(Assert.Single(app.Resource.Annotations.OfType<JavaBuildStepAnnotation>()).ResourceName);
    }

    [Fact]
    public async Task WithMavenGoal_ThenWithJvmArgs_SetsBothConfigurations()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path)
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

        using var tempDir = new TempJavaAppDirectory();
        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun")
            .WithOtelAgent(AbsoluteAgentPath);

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);
        Assert.Contains("bootRun", args);

        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            app.Resource, DistributedApplicationOperation.Run, TestServiceProvider.Instance);
        Assert.Equal($"-javaagent:{AbsoluteAgentPath}", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task WithWrapperPath_ThenWithMavenGoal_UsesCustomWrapper()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithWrapperPath("tools/mvn")
            .WithMavenGoal("spring-boot:run");

        var args = await ArgumentEvaluator.GetArgumentListAsync(app.Resource);

        // WithCommand sets the custom wrapper as the command
        var expectedWrapper = Path.GetFullPath(Path.Combine(tempDir.Path, "tools/mvn"));
        Assert.Equal(ExpectedWrapperInvocation.Command(), app.Resource.Command);
        Assert.Equal(ExpectedWrapperInvocation.Args(expectedWrapper, tempDir.Path, "spring-boot:run"), args);
    }

    // ---- Manifest with Maven/Gradle goals -----------------------------------

    [Fact]
    public async Task VerifyManifest_WithMavenGoal()
    {
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();

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
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithGradleTask("bootRun");

        var manifest = await ManifestUtils.GetManifest(app.Resource);

        var args = manifest?["args"]?.AsArray();
        Assert.NotNull(args);
        Assert.Contains("bootRun", args!.Select(a => a?.ToString()));
    }

    private static async Task<JavaLaunchConfiguration> GetLaunchConfigurationAsync(IResourceBuilder<JavaAppResource> app)
    {
        var annotation = Assert.Single(app.Resource.Annotations.OfType<SupportsDebuggingAnnotation>());
        Assert.Equal("java", annotation.LaunchConfigurationType);

        var context = new LaunchConfigurationCallbackContext(
            ExecutableLaunchMode.Debug,
            app.Resource,
            new Dictionary<string, string>(),
            CancellationToken.None);

        return Assert.IsType<JavaLaunchConfiguration>(await annotation.LaunchConfigurationProducer(context));
    }

    [Theory]
    [InlineData("pom.xml", "maven")]
    [InlineData("settings.gradle", "gradle")]
    public async Task AddSpringBootApp_DebugConfigurationUsesTheDetectedBuildTool(string marker, string expectedBuildTool)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(marker, "");

        var app = builder.AddSpringBootApp("catalog", tempDir.Path);

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal(expectedBuildTool, launchConfiguration.BuildTool);
    }

    [Theory]
    [InlineData("pom.xml", "target")]
    [InlineData("build.gradle", "build")]
    public async Task AddQuarkusApp_DebugConfigurationStartsTheFastJarTheBuildProduced(string buildFile, string outputDirectory)
    {
        // Quarkus' entry point lives in the fast JAR's boot classpath rather than in the project, so the
        // language server's classpath cannot start it. The archive goes on the classpath and its manifest
        // names io.quarkus.bootstrap.runner.QuarkusEntryPoint, exactly as `java -jar` would resolve it.
        // Breakpoints still bind, because the debugger maps loaded classes back to sources by name.
        using var builder = TestDistributedApplicationBuilder.Create().WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write(buildFile, "");
        var runJar = WriteJarWithManifest(
            tempDir.Path,
            Path.Combine(outputDirectory, "quarkus-app", "quarkus-run.jar"),
            "io.quarkus.bootstrap.runner.QuarkusEntryPoint");

        var app = builder.AddQuarkusApp("inventory", tempDir.Path);

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal("io.quarkus.bootstrap.runner.QuarkusEntryPoint", launchConfiguration.MainClass);
        Assert.Equal([runJar], launchConfiguration.ClassPaths!);
    }

    /// <summary>
    /// Writes a JAR whose manifest declares <paramref name="mainClass"/>. A JAR is a ZIP archive, so
    /// the entry only has to exist at META-INF/MANIFEST.MF with the documented Name: value shape.
    /// </summary>
    private static string WriteJarWithManifest(string directory, string fileName, string? mainClass, bool wrapLongValue = false, string? startClass = null)
    {
        var jarPath = Path.Combine(directory, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        using var archive = ZipFile.Open(jarPath, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("META-INF/MANIFEST.MF").Open());

        writer.Write("Manifest-Version: 1.0\r\n");
        if (mainClass is not null)
        {
            if (wrapLongValue)
            {
                // The manifest format limits a line to 72 bytes and continues longer values on the
                // next line with a single leading space, which is not part of the value.
                var split = mainClass.Length / 2;
                writer.Write($"Main-Class: {mainClass[..split]}\r\n {mainClass[split..]}\r\n");
            }
            else
            {
                writer.Write($"Main-Class: {mainClass}\r\n");
            }
        }

        if (startClass is not null)
        {
            writer.Write($"Start-Class: {startClass}\r\n");
        }

        writer.Write("\r\n");

        // Returned fully resolved because that is the shape the resource reports: the launch
        // configuration runs the authored path through Path.GetFullPath. Path.Combine does not
        // normalize separators, so a fileName such as "target/api.jar" would otherwise produce
        // "C:\...\target/api.jar" on Windows and never compare equal to it.
        return Path.GetFullPath(jarPath);
    }

    [Fact]
    public async Task AddJavaApp_WithMavenBuild_SendsNoEntryPointWhenNothingOnDiskNamesOne()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        var app = builder.AddJavaApp("api", tempDir.Path).WithMavenGoal("spring-boot:run");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Null(launchConfiguration.MainClass);
        Assert.Null(launchConfiguration.ClassPaths);
        Assert.Equal("maven", launchConfiguration.BuildTool);
        Assert.Equal(tempDir.Path, launchConfiguration.WorkingDirectory);
    }

    [Fact]
    public async Task AddJavaApp_WithJar_PutsTheArchiveOnTheClasspathAndLaunchesItsManifestMainClass()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        var jarPath = WriteJarWithManifest(tempDir.Path, "api.jar", "com.example.catalog.CatalogApplication");

        var app = builder.AddJavaApp("api", tempDir.Path, "api.jar");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        // The archive itself is never the main class: the debug adapter documents that attribute as a
        // fully qualified class name or a .java path, so a JAR path leaves it unable to resolve an
        // entry point. The archive belongs on the classpath instead.
        Assert.Equal("com.example.catalog.CatalogApplication", launchConfiguration.MainClass);
        Assert.Equal(jarPath, Assert.Single(launchConfiguration.ClassPaths!));
        Assert.Null(launchConfiguration.BuildTool);
    }

    [Fact]
    public async Task AddJavaApp_WithWindowsStyleJarPath_DebugsTheArchiveOnEveryPlatform()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        var jarPath = WriteJarWithManifest(tempDir.Path, "target/api.jar", "com.example.catalog.CatalogApplication");

        var app = builder.AddJavaApp("api", tempDir.Path, @"target\api.jar");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal("com.example.catalog.CatalogApplication", launchConfiguration.MainClass);
        Assert.Equal(jarPath, Assert.Single(launchConfiguration.ClassPaths!));
    }

    [Fact]
    public async Task AddJavaApp_WithJar_ReadsAMainClassThatTheManifestWrappedAcrossLines()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        const string MainClass = "com.example.catalog.averylongpackagename.that.forces.wrapping.CatalogApplication";
        WriteJarWithManifest(tempDir.Path, "api.jar", MainClass, wrapLongValue: true);

        var app = builder.AddJavaApp("api", tempDir.Path, "api.jar");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal(MainClass, launchConfiguration.MainClass);
    }

    [Fact]
    public async Task AddJavaApp_WithJar_WithMainClass_PrefersTheExplicitMainClassOverTheManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        var jarPath = WriteJarWithManifest(tempDir.Path, "api.jar", "org.springframework.boot.loader.JarLauncher");

        var app = builder.AddJavaApp("api", tempDir.Path, "api.jar")
            .WithMainClass("com.example.catalog.CatalogApplication");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal("com.example.catalog.CatalogApplication", launchConfiguration.MainClass);
        Assert.Equal(jarPath, Assert.Single(launchConfiguration.ClassPaths!));
    }

    [Fact]
    public async Task AddJavaApp_WithJar_ThatIsMissingOrHasNoMainClass_StillSendsTheClasspath()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        WriteJarWithManifest(tempDir.Path, "no-main.jar", mainClass: null);

        var noMainClass = builder.AddJavaApp("no-main", tempDir.Path, "no-main.jar");
        var missing = builder.AddJavaApp("missing", tempDir.Path, "does-not-exist.jar");

        // Neither case is fatal: the IDE can still resolve an entry point from the project, and
        // failing the launch over an unreadable archive would be worse than letting it try.
        var noMainClassConfiguration = await GetLaunchConfigurationAsync(noMainClass);
        Assert.Null(noMainClassConfiguration.MainClass);
        Assert.Equal(Path.Combine(tempDir.Path, "no-main.jar"), Assert.Single(noMainClassConfiguration.ClassPaths!));

        var missingConfiguration = await GetLaunchConfigurationAsync(missing);
        Assert.Null(missingConfiguration.MainClass);
        Assert.Equal(Path.Combine(tempDir.Path, "does-not-exist.jar"), Assert.Single(missingConfiguration.ClassPaths!));
    }

    [Theory]
    [InlineData("maven", "target")]
    [InlineData("gradle", "build/libs")]
    public async Task AddJavaApp_WithoutAJar_LaunchesTheStartClassOfTheRepackagedSpringBootArchive(string tool, string outputDirectory)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory(directoryName: "catalog");
        tempDir.WriteWrapper(tool == "maven" ? JavaHostingExtensions.s_defaultMavenWrapper : JavaHostingExtensions.s_defaultGradleWrapper);

        // Repackaging points Main-Class at the launcher and records the application's own entry point
        // in Start-Class, so a debugger that started Main-Class would step through Spring's loader.
        WriteJarWithManifest(
            tempDir.Path,
            Path.Combine(outputDirectory, "catalog-0.0.1-SNAPSHOT.jar"),
            mainClass: "org.springframework.boot.loader.launch.JarLauncher",
            startClass: "com.example.catalog.CatalogApplication");

        var app = builder.AddJavaApp("catalog", tempDir.Path);
        _ = tool == "maven" ? app.WithMavenGoal("spring-boot:run") : app.WithGradleTask("bootRun");

        // The build file is what the project name is read from, and the directory is named to match it,
        // which is the condition under which the IDE imports the project under that same name.
        File.WriteAllText(
            Path.Combine(tempDir.Path, tool == "maven" ? "pom.xml" : "settings.gradle"),
            tool == "maven"
                ? """
                    <?xml version="1.0" encoding="UTF-8"?>
                    <project xmlns="http://maven.apache.org/POM/4.0.0">
                        <modelVersion>4.0.0</modelVersion>
                        <groupId>com.example</groupId>
                        <artifactId>catalog</artifactId>
                        <version>0.0.1-SNAPSHOT</version>
                    </project>
                    """
                : "rootProject.name = 'catalog'\n");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal("com.example.catalog.CatalogApplication", launchConfiguration.MainClass);
        // The classpath stays with the language server so breakpoints bind to the source being edited
        // rather than to the classes inside the archive.
        Assert.Null(launchConfiguration.ClassPaths);
        // Sent alongside the main class, not instead of it. Given a class and no project the adapter
        // searches every project in the workspace and refuses to launch with "Main class ... isn't
        // unique in the workspace" whenever the class is visible through more than one of them.
        Assert.Equal("catalog", launchConfiguration.ProjectName);
    }

    [Fact]
    public async Task AddJavaApp_WithoutAJar_IgnoresTheArchivesABuildLeavesAlongsideTheApplication()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);

        WriteJarWithManifest(tempDir.Path, "build/libs/orders.jar", mainClass: null, startClass: "com.example.orders.OrdersApplication");
        // The Gradle Spring Boot plugin writes the unrepackaged classes next to the real artifact, and
        // Maven publishes these two whenever the corresponding plugins are bound to the build.
        WriteJarWithManifest(tempDir.Path, "build/libs/orders-plain.jar", mainClass: null);
        WriteJarWithManifest(tempDir.Path, "build/libs/orders-sources.jar", mainClass: null);
        WriteJarWithManifest(tempDir.Path, "build/libs/orders-javadoc.jar", mainClass: null);

        var app = builder.AddJavaApp("orders", tempDir.Path).WithGradleTask("bootRun");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal("com.example.orders.OrdersApplication", launchConfiguration.MainClass);
    }

    [Fact]
    public async Task AddJavaApp_WithoutAJar_SendsNoMainClassWhenTheBuildOutputIsAmbiguous()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        WriteJarWithManifest(tempDir.Path, "target/catalog-0.0.1-SNAPSHOT.jar", mainClass: null, startClass: "com.example.catalog.CatalogApplication");
        WriteJarWithManifest(tempDir.Path, "target/catalog-0.0.1-SNAPSHOT-shaded.jar", mainClass: null, startClass: "com.example.catalog.Other");

        var app = builder.AddJavaApp("catalog", tempDir.Path).WithMavenGoal("spring-boot:run");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        // Picking one of two application archives would silently debug the wrong program.
        Assert.Null(launchConfiguration.MainClass);
    }

    [Fact]
    public async Task AddJavaApp_WithoutAJar_SendsNoMainClassWhenTheArchiveOnlyNamesASpringBootLauncher()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        WriteJarWithManifest(tempDir.Path, "target/catalog.jar", mainClass: "org.springframework.boot.loader.launch.JarLauncher");

        var app = builder.AddJavaApp("catalog", tempDir.Path).WithMavenGoal("spring-boot:run");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        // The launcher can only start with the archive on the classpath, which this launch mode does
        // not send, so reporting it would produce a JVM that fails immediately.
        Assert.Null(launchConfiguration.MainClass);
    }

    [Fact]
    public async Task AddJavaApp_WithoutAJar_PrefersAnExplicitMainClassOverTheBuildOutput()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        WriteJarWithManifest(tempDir.Path, "target/catalog.jar", mainClass: null, startClass: "com.example.catalog.CatalogApplication");

        var app = builder.AddJavaApp("catalog", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithMainClass("com.example.catalog.DebugEntryPoint");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal("com.example.catalog.DebugEntryPoint", launchConfiguration.MainClass);
    }

    [Fact]
    public async Task AddJavaApp_WithNoResolvableEntryPoint_NamesTheMavenProjectSoTheIdeDoesNotPrompt()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory(directoryName: "catalog");
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        // <parent> declares an artifactId too, and it is the one a descendant search finds first.
        File.WriteAllText(Path.Combine(tempDir.Path, "pom.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
                <modelVersion>4.0.0</modelVersion>
                <parent>
                    <groupId>org.springframework.boot</groupId>
                    <artifactId>spring-boot-starter-parent</artifactId>
                    <version>3.5.0</version>
                </parent>
                <groupId>com.example</groupId>
                <artifactId>catalog</artifactId>
                <version>0.0.1-SNAPSHOT</version>
            </project>
            """);

        var app = builder.AddJavaApp("catalog", tempDir.Path).WithMavenGoal("spring-boot:run");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Null(launchConfiguration.MainClass);
        Assert.Equal("catalog", launchConfiguration.ProjectName);
    }

    [Fact]
    public async Task AddJavaApp_WithNoResolvableEntryPoint_NamesTheGradleProjectFromItsSettingsFile()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory(directoryName: "orders");
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);

        File.WriteAllText(Path.Combine(tempDir.Path, "settings.gradle"), "rootProject.name = 'orders'\n");

        var app = builder.AddJavaApp("orders", tempDir.Path).WithGradleTask("bootRun");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Null(launchConfiguration.MainClass);
        Assert.Equal("orders", launchConfiguration.ProjectName);
    }

    [Fact]
    public async Task AddJavaApp_WithNoResolvableEntryPoint_FallsBackToTheDirectoryNameGradleWouldUse()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);

        var app = builder.AddJavaApp("orders", tempDir.Path).WithGradleTask("bootRun");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        Assert.Equal(
            Path.GetFileName(tempDir.Path.TrimEnd(Path.DirectorySeparatorChar)),
            launchConfiguration.ProjectName);
    }

    [Fact]
    public async Task AddJavaApp_WhenTheProjectDirectoryIsNamedDifferently_DoesNotGuessTheIdeProjectName()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory(directoryName: "JavaSpringBoot.AppHost.Java");
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultGradleWrapper);

        File.WriteAllText(
            Path.Combine(tempDir.Path, "settings.gradle"),
            "rootProject.name = 'javaspringboot-apphost'\n");

        var app = builder.AddJavaApp("apphost", tempDir.Path).WithGradleTask("bootRun");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        // The language server appends the directory when it disagrees with the declared name, importing
        // this project as "javaspringboot-apphost-JavaSpringBoot.AppHost.Java". Sending the declared name
        // would name a project that does not exist and fail every launch, so nothing is sent and the
        // adapter resolves the entry point across the workspace as it did before.
        Assert.Null(launchConfiguration.ProjectName);
    }

    [Fact]
    public async Task AddJavaApp_WithAnExplicitJar_DoesNotScopeResolutionToAProject()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        using var tempDir = new TempJavaAppDirectory(directoryName: "worker");
        tempDir.WriteWrapper(JavaHostingExtensions.s_defaultMavenWrapper);

        File.WriteAllText(Path.Combine(tempDir.Path, "pom.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
                <modelVersion>4.0.0</modelVersion>
                <groupId>com.example</groupId>
                <artifactId>worker</artifactId>
                <version>0.0.1-SNAPSHOT</version>
            </project>
            """);

        WriteJarWithManifest(
            tempDir.Path,
            Path.Combine("target", "worker-0.0.1-SNAPSHOT.jar"),
            mainClass: "com.example.worker.Worker");

        var app = builder
            .AddJavaApp("worker", tempDir.Path, Path.Combine("target", "worker-0.0.1-SNAPSHOT.jar"))
            .WithMavenBuild("package");

        var launchConfiguration = await GetLaunchConfigurationAsync(app);

        // The archive is on the classpath, so the adapter launches from it rather than from a project
        // the language server compiled. Naming a project as well would ask it to resolve the class
        // somewhere it does not have to exist.
        Assert.Equal("com.example.worker.Worker", launchConfiguration.MainClass);
        Assert.NotNull(launchConfiguration.ClassPaths);
        Assert.Null(launchConfiguration.ProjectName);
    }
}
