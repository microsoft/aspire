// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.Hosting.Tests.Utils;
using System.Diagnostics;
using System.Text.Json;
using Aspire.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Java.Tests;

public class AddJavaAppPublishTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task VerifyPublish_GeneratesAMavenBuildAndJreRuntimePair()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.StartsWith("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:21-jdk AS build", content);
        Assert.Contains("\nFROM docker.io/library/eclipse-temurin:21-jre\n", content);
        await Verify(content);
    }

    [Fact]
    [SkipOnPlatform(TestPlatforms.Windows, "Runs the generated POSIX shell the container build would run")]
    public async Task VerifyPublish_TheJarSelectionCopiesAnArtifactWhoseNameContainsWhitespace()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WriteGradleBuild(source, ""),
            configureResource: app => app.WithGradleTask("bootRun"));

        // Everything from the selection onwards, so the test runs the real generated shell rather than a
        // paraphrase of it. The build invocation ahead of it needs Gradle and is not what is under test.
        var selectionIndex = content.IndexOf("jars=$(ls ", StringComparison.Ordinal);
        Assert.True(selectionIndex >= 0, "The generated Dockerfile no longer selects the JAR with a glob.");
        var selection = content[selectionIndex..content.IndexOf('\n', selectionIndex)];

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        Directory.CreateDirectory(Path.Combine(workspace.Path, "build", "libs"));

        // Gradle's archiveFileName and archivesName are freely settable, as is Maven's finalName, so this
        // is a legitimate single build output rather than a malformed one.
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "build", "libs", "reports service.jar"), "");

        var destination = Path.Combine(workspace.Path, "app.jar");
        var command = selection.Replace("/build/app.jar", destination, StringComparison.Ordinal);

        using var process = Process.Start(new ProcessStartInfo("sh")
        {
            ArgumentList = { "-c", command },
            WorkingDirectory = workspace.Path,
            RedirectStandardError = true
        })!;

        await process.WaitForExitAsync(CancellationToken.None);

        Assert.Equal(string.Empty, await process.StandardError.ReadToEndAsync());
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task VerifyPublish_GeneratesAGradleBuild()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WriteGradleBuild(source, """
                java {
                    toolchain {
                        languageVersion = JavaLanguageVersion.of(17)
                    }
                }
                """),
            configureResource: app => app.WithGradleTask("bootRun"));

        Assert.StartsWith("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:17-jdk AS build", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_CopiesABuildProducedOtelAgentIntoTheRuntimeImage()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithOtelAgent("target/agent/opentelemetry-javaagent.jar"));

        // The agent is produced by the build, so it exists only in the build stage. Without this COPY the
        // published container starts a JVM pointing at a JAR that is not in the image and dies during VM
        // initialization with "Error opening zip file or JAR manifest missing".
        Assert.Contains(
            "COPY --from=build --chown=999:999 /app/target/agent/opentelemetry-javaagent.jar /app/agent.jar",
            content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_WithJvmArgs_ComposesWithBuildProducedOtelAgent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish).WithResourceCleanUp(true);
        using var tempDir = new TempJavaAppDirectory();
        tempDir.Write("pom.xml", "<project/>");

        builder.AddJavaApp("worker", tempDir.Path, "target/worker.jar")
            .WithMavenBuild()
            .WithJvmArgs("-Xmx128m", "-javaagent:/app/coverage-agent.jar")
            .WithOtelAgent("target/agent/opentelemetry-javaagent.jar");

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        // Java agents initialize in option order. Preserve the fluent callback order rather than
        // replacing or reordering the user's agent when Aspire appends its own telemetry agent.
        Assert.Equal(
            "-Xmx128m -javaagent:/app/coverage-agent.jar -javaagent:/app/agent.jar",
            envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task VerifyPublish_StripsExactlyOneLeadingDotSlashFromTheOtelAgentPath()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithOtelAgent("./target/agent/opentelemetry-javaagent.jar"));

        Assert.Contains(
            "COPY --from=build --chown=999:999 /app/target/agent/opentelemetry-javaagent.jar /app/agent.jar",
            content);
    }

    [Fact]
    public void AnOtelAgentPathOutsideTheBuildContextIsRejected()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithOtelAgent("../agents/opentelemetry-javaagent.jar");

        // The Docker build context is the application directory, so "../" can never be copied forward.
        // Trimming the leading dots instead would emit a COPY for "agents/opentelemetry-javaagent.jar" --
        // a path the author never wrote -- and fail the container build with a confusing message.
        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetBuildProducedAgentPath(app.Resource, out _));

        Assert.Contains("../agents/opentelemetry-javaagent.jar", exception.Message);
        Assert.Contains("outside the application directory", exception.Message);
    }

    [Fact]
    public async Task VerifyPublish_DoesNotCopyAnAbsoluteOtelAgentPath()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithOtelAgent("/opt/otel/agent.jar"));

        // An absolute path cannot have come out of the build context, so there is nothing to copy from:
        // the base image or a mount provides it. Assert on the COPY instructions the Dockerfile actually
        // contains rather than on the absence of one path, so deleting the agent handling fails the test.
        var copies = content
            .Split('\n')
            .Where(line => line.StartsWith("COPY ", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(
            [
                "COPY mvnw ./mvnw",
                "COPY .mvn ./.mvn",
                "COPY . .",
                "COPY --from=build --chown=999:999 /build/app.jar /app/app.jar"
            ],
            copies);
    }

    /// <summary>
    /// The published image is Linux, so a Windows agent path can never resolve inside it. Left alone it
    /// reaches the container as <c>-javaagent:C:\...</c> and the JVM dies during initialization. Rejecting
    /// it on every platform matches how the jar artifact and the wrapper already behave.
    /// </summary>
    [Fact]
    public void VerifyPublish_AWindowsAbsoluteOtelAgentIsRejectedOnEveryPlatform()
    {
        using var appDirectory = new TempJavaAppDirectory();
        WritePom(appDirectory.Path, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path)
            .WithMavenBuild()
            .WithOtelAgent(@"C:\opt\otel\agent.jar");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetBuildProducedAgentPath(app.Resource, out _));

        Assert.Contains("is a Windows path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_UsesTheWrapperWhenTheProjectShipsOne()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
            },
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        // sh ./mvnw rather than ./mvnw: a wrapper checked out from a Windows clone arrives without the
        // executable bit, and invoking the interpreter directly does not depend on the file mode.
        Assert.StartsWith("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:21-jdk AS build", content);
        Assert.Contains("sh ./mvnw -B -ntp -DskipTests package", content);
        await Verify(content);
    }

    [Fact]
    public void PublishingAProjectWithoutAWrapperIsRejectedRatherThanUsingTheImageTool()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "build.gradle"), "");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // No goal is configured, so nothing resolves a wrapper before the image is generated: the tool is
        // detected from the build file on disk and this is the only check that runs.
        var app = builder.AddJavaApp("api", sourceDir.FullName, "build/libs/api.jar");

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        // A globally installed Gradle in the build image would build the project with a different version
        // than the developer used, which is exactly what the wrapper exists to prevent.
        Assert.Contains("there is no gradlew", ex.Message);
        Assert.Contains("gradle wrapper", ex.Message);
    }

    [Fact]
    public async Task VerifyPublish_ReusesTheArgumentsConfiguredForTheHostBuildStep()
    {
        // The host-side build step only runs in run mode, but the arguments it was given describe how this
        // project produces a deployable artifact, so the container build runs the same ones.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run").WithMavenBuild("-Pprod", "package"));

        Assert.Contains("sh ./mvnw -Pprod package", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_DetectsTheBuildToolFromDiskWhenOnlyAJarPathWasGiven()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            jarPath: "target/worker.jar");

        Assert.Contains("sh ./mvnw -B -ntp -DskipTests package", content);
        await Verify(content);
    }

    [Theory]
    [InlineData("../shared/app.jar")]
    [InlineData("target/../../app.jar")]
    [InlineData(@"..\..\app.jar")]
    [InlineData("/opt/build/app.jar")]
    public void PublishingAJarPathThatEscapesTheBuildDirectoryIsRejectedRatherThanGlobbed(string jarPath)
    {
        // Falling back to the output glob here would publish whichever JAR the build happened to emit
        // instead of the one the AppHost named, and the divergence from run mode would be silent.
        using var appDirectory = new TempJavaAppDirectory();

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path, jarPath);

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetBuildOutputJarPath(app.Resource, out _));

        Assert.Contains(jarPath, exception.Message);
        Assert.Contains("outside the directory the build runs in", exception.Message);
    }

    [Fact]
    public async Task PublishingAJarPathContainingWhitespaceQuotesItRatherThanFallingBackToTheGlob()
    {
        // Unlike a Dockerfile COPY, the artifact is selected by a RUN shell command, which can quote.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            jarPath: "target/my worker.jar");

        Assert.Contains("cp 'target/my worker.jar' /build/app.jar", content);
        Assert.DoesNotContain("expected exactly one application JAR", content);
    }

    [Fact]
    public async Task VerifyPublish_CopiesTheExplicitArtifactWhenWithJarArtifactIsUsed()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run").WithJarArtifact("target/custom-name.jar"));

        Assert.Contains("cp 'target/custom-name.jar' /build/app.jar", content);
        // The glob selection is what WithJarArtifact exists to replace, so it must not also be emitted.
        Assert.DoesNotContain("expected exactly one application JAR", content);
    }

    [Fact]
    public async Task VerifyPublish_FailsTheContainerBuildWhenTheJarIsAmbiguous()
    {
        // Spring Boot's plugin writes app.jar next to the base plugin's app-plain.jar. The -plain suffix is
        // filtered, but anything still ambiguous has to stop the build rather than pick one arbitrarily and
        // produce an image that exits with "no main manifest attribute".
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("grep -Ev '(-plain|-sources|-javadoc)\\.jar$'", content);
        Assert.Contains("exit 1", content);
    }

    [Fact]
    public async Task VerifyPublish_HonoursWithDockerfileBaseImage()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithDockerfileBaseImage("docker.io/library/amazoncorretto:21", "docker.io/library/amazoncorretto:21-alpine"));

        Assert.StartsWith("FROM --platform=$BUILDPLATFORM docker.io/library/amazoncorretto:21 AS build", content);
        Assert.Contains("\nFROM docker.io/library/amazoncorretto:21-alpine\n", content);
        // No user is created in the runtime stage, so an override needs no distro-specific handling.
        Assert.DoesNotContain("adduser", content, StringComparison.Ordinal);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_RunsAsANonRootUser()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        // A numeric id rather than a named account, so the same Dockerfile works on any runtime image
        // including Alpine and distroless, none of which agree on the user-creation tools.
        Assert.Contains("\nUSER 999:999\n", content);
        Assert.DoesNotContain("useradd", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_UsesTheExecFormEntrypointSoTheJvmReceivesSigterm()
    {
        // With the shell form the JVM is not PID 1 and never sees SIGTERM, so Spring's shutdown hooks are
        // skipped and the container is killed after the stop timeout instead.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("""ENTRYPOINT ["java","-jar","/app/app.jar"]""", content);
    }

    [Fact]
    public async Task VerifyPublish_EmitsABuildContextIgnoreThatExcludesBuildOutputDirectories()
    {
        var ignore = await PublishBuildContextIgnoreAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.NotNull(ignore);
        await Verify(ignore);
    }

    [Fact]
    public async Task VerifyPublish_LeavesAnAuthoredDockerignoreAlone()
    {
        // A <dockerfile>.dockerignore replaces the context root's .dockerignore rather than merging with it,
        // so generating one would silently drop every rule the author wrote.
        var ignore = await PublishBuildContextIgnoreAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
                File.WriteAllText(Path.Combine(source, ".dockerignore"), "secrets/\n");
            },
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Null(ignore);
    }

    [Fact]
    public async Task VerifyPublish_LeavesAnAuthoredDockerfileAlone()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        WritePom(sourceDir.FullName, javaVersion: "21");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddJavaApp("api", sourceDir.FullName).WithMavenGoal("spring-boot:run");
        builder.Build().Run();

        Assert.False(File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")));
    }

    [Fact]
    public async Task PublishingAnAuthoredDockerfileRejectsABuildProducedAgent()
    {
        // /app/agent.jar only exists because the generated Dockerfile copies the agent there. An
        // authored Dockerfile has no reason to, so pointing JAVA_TOOL_OPTIONS at it would produce an
        // image whose JVM dies during initialization with "Error opening zip file or JAR manifest
        // missing" — before anything the developer could debug.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        WritePom(sourceDir.FullName, javaVersion: "21");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddJavaApp("api", sourceDir.FullName)
               .WithMavenGoal("spring-boot:run")
               .WithOtelAgent("target/otel/javaagent.jar");

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            async () => await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
                container, DistributedApplicationOperation.Publish, TestServiceProvider.Instance));

        Assert.Equal(
            $"Java application 'api' cannot be published because it uses the Dockerfile in " +
            $"'{sourceDir.FullName}' and its OpenTelemetry agent path 'target/otel/javaagent.jar' is " +
            "relative to the build output. Aspire copies a build-produced agent into the image only in " +
            "the Dockerfile it generates. Copy the agent in your Dockerfile and pass its path inside the " +
            "image to WithOtelAgent, for example WithOtelAgent(\"/opt/otel/javaagent.jar\").",
            exception.Message);
    }

    [Fact]
    public async Task PublishingAnAuthoredDockerfileAcceptsAnAbsoluteAgentPath()
    {
        // An absolute path names a location inside the image, which is the developer's to guarantee.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        WritePom(sourceDir.FullName, javaVersion: "21");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "Dockerfile"), "FROM scratch\n");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddJavaApp("api", sourceDir.FullName)
               .WithMavenGoal("spring-boot:run")
               .WithOtelAgent("/opt/otel/javaagent.jar");

        var container = Assert.Single(builder.Resources.OfType<ContainerResource>());
        var envVars = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            container, DistributedApplicationOperation.Publish, TestServiceProvider.Instance);

        builder.Build().Run();

        Assert.Equal("-javaagent:/opt/otel/javaagent.jar", envVars["JAVA_TOOL_OPTIONS"]);
    }

    [Fact]
    public async Task VerifyPublish_ProducesAContainerManifestEntry()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        WritePom(sourceDir.FullName, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddJavaApp("api", sourceDir.FullName)
               .WithMavenGoal("spring-boot:run")
               .WithHttpEndpoint(targetPort: 8080, env: "SERVER_PORT");

        builder.Build().Run();

        var manifest = await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "aspire-manifest.json"), TestContext.Current.CancellationToken);

        await Verify(manifest, "json")
            .ScrubLinesWithReplace(line => line.Contains("\"context\"", StringComparison.Ordinal) ? "      \"context\": \"{sourceDirectory}\"," : line);
    }

    [Theory]
    [InlineData("<java.version>17</java.version>", "17")]
    [InlineData("<maven.compiler.release>21</maven.compiler.release>", "21")]
    [InlineData("<maven.compiler.target>1.8</maven.compiler.target>", "8")]
    // A property reference cannot be expanded here and must not reach a FROM instruction, so it falls back.
    [InlineData("<maven.compiler.target>${java.version}</maven.compiler.target>", JavaVersionDetector.DefaultJavaVersion)]
    [InlineData("", JavaVersionDetector.DefaultJavaVersion)]
    public void JavaVersionDetector_ReadsTheTargetReleaseFromAPom(string properties, string expected)
    {
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <artifactId>demo</artifactId>
              <properties>
                {properties}
              </properties>
            </project>
            """);

        Assert.Equal(expected, JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void JavaVersionDetector_SkipsAPropertyReferenceInFavourOfALaterLiteral()
    {
        // Stopping at the first <release> would fall back to the default even though the POM plainly
        // declares a target, and publish a runtime image the bytecode does not match.
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <artifactId>demo</artifactId>
              <build>
                <plugins>
                  <plugin>
                    <artifactId>maven-compiler-plugin</artifactId>
                    <configuration><release>${java.release}</release></configuration>
                  </plugin>
                  <plugin>
                    <artifactId>maven-compiler-plugin</artifactId>
                    <configuration><release>17</release></configuration>
                  </plugin>
                </plugins>
              </build>
            </project>
            """);

        Assert.Equal("17", JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void JavaVersionDetector_IgnoresATargetOutsideAPluginConfiguration()
    {
        // <target> is a common element name in other plugins. Matched anywhere, an antrun target that
        // happens to hold a number would decide the runtime image.
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <artifactId>demo</artifactId>
              <profiles>
                <profile>
                  <id>legacy</id>
                  <target>11</target>
                </profile>
              </profiles>
            </project>
            """);

        Assert.Equal(JavaVersionDetector.DefaultJavaVersion, JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Theory]
    [InlineData("java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }", "21")]
    [InlineData("java.toolchain.languageVersion.set(JavaLanguageVersion.of(17))", "17")]
    [InlineData("sourceCompatibility = JavaVersion.VERSION_1_8", "8")]
    [InlineData("sourceCompatibility = JavaVersion.VERSION_21", "21")]
    [InlineData("sourceCompatibility = '17'", "17")]
    [InlineData("targetCompatibility = 1.8", "8")]
    [InlineData("", JavaVersionDetector.DefaultJavaVersion)]
    // A commented-out setting must not win over the active one below it.
    [InlineData("""
        java {
            toolchain {
                // languageVersion = JavaLanguageVersion.of(17)
                languageVersion = JavaLanguageVersion.of(21)
            }
        }
        """, "21")]
    [InlineData("""
        /*
        java { toolchain { languageVersion = JavaLanguageVersion.of(17) } }
        */
        java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }
        """, "21")]
    // A "//" inside a string is not a comment, so the rest of that line still parses. Written on one
    // line because the whole point is that the URL does not swallow what follows it.
    [InlineData("""
        repositories { maven { url "https://repo.example.com/m2" } }; sourceCompatibility = '17'
        """, "17")]
    // A "/*" inside a string would otherwise swallow the remainder of the file.
    [InlineData("""
        tasks.register("noop") { doLast { println("/*") } }
        java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }
        """, "21")]
    // A setting that only exists inside a comment leaves nothing to detect.
    [InlineData("// languageVersion = JavaLanguageVersion.of(17)", JavaVersionDetector.DefaultJavaVersion)]
    // A version-shaped fragment quoted inside a string is not a declaration, and appears first, so
    // reading it would select a JRE too old to run the application's own bytecode.
    [InlineData("""
        tasks.register("noop") { doLast { println("JavaLanguageVersion.of(17)") } }
        java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }
        """, "21")]
    [InlineData("""
        tasks.register("noop") { doLast { println("sourceCompatibility = JavaVersion.VERSION_17") } }
        java { toolchain { languageVersion = JavaLanguageVersion.of(21) } }
        """, "21")]
    [InlineData("""
        tasks.register("noop") { doLast { println("sourceCompatibility = '17'") } }
        sourceCompatibility = '21'
        """, "21")]
    // Quoted and nothing else, so there is no declaration at all.
    [InlineData("""println("JavaLanguageVersion.of(17)")""", JavaVersionDetector.DefaultJavaVersion)]
    // An unterminated literal runs to the end of the file for the compiler, so a declaration after it is
    // not in effect either and must not be read.
    [InlineData("""
        println("oops
        java { toolchain { languageVersion = JavaLanguageVersion.of(17) } }
        """, JavaVersionDetector.DefaultJavaVersion)]
    public void JavaVersionDetector_ReadsTheTargetReleaseFromAGradleBuildScript(string script, string expected)
    {
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "build.gradle"), script);

        Assert.Equal(expected, JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void JavaVersionDetector_PrefersThePomWhenBothBuildFilesArePresent()
    {
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <project><properties><java.version>17</java.version></properties></project>
            """);
        File.WriteAllText(Path.Combine(appDirectory.Path, "build.gradle"), "sourceCompatibility = '21'");

        Assert.Equal("17", JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Theory]
    [InlineData("gradle", "21")]
    [InlineData("maven", "17")]
    public void JavaVersionDetector_FollowsTheResolvedBuildToolWhenBothBuildFilesArePresent(string toolName, string expected)
    {
        // A repository part-way through a Maven to Gradle migration keeps both build files and picks the
        // tool explicitly. Reading the other tool's file would tag the image for a release the application
        // was not compiled for, which surfaces at runtime as UnsupportedClassVersionError rather than as a
        // build failure.
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <project><properties><java.version>17</java.version></properties></project>
            """);
        File.WriteAllText(Path.Combine(appDirectory.Path, "build.gradle"), "sourceCompatibility = '21'");

        var tool = toolName is "gradle" ? JavaBuildTool.Gradle : JavaBuildTool.Maven;

        Assert.Equal(expected, JavaVersionDetector.Detect(appDirectory.Path, tool));
    }

    [Fact]
    public void JavaVersionDetector_FallsBackToTheOtherBuildFileWhenTheResolvedToolDeclaresNothing()
    {
        // Gradle projects commonly leave the release to a toolchain block this does not read. The POM is
        // still better evidence than the default, so the tool preference is an ordering, not a filter.
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <project><properties><java.version>17</java.version></properties></project>
            """);
        File.WriteAllText(Path.Combine(appDirectory.Path, "build.gradle"), "plugins { id 'java' }");

        Assert.Equal("17", JavaVersionDetector.Detect(appDirectory.Path, JavaBuildTool.Gradle));
    }

    [Fact]
    public void JavaVersionDetector_IgnoresReleaseAndTargetOutsideTheCompilerPlugin()
    {
        using var appDirectory = new TempJavaAppDirectory();

        // maven-antrun-plugin's <target> holds Ant XML, and any plugin may name a <release> of its own,
        // so matching either element by its <configuration> parent alone picks up unrelated values.
        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <build><plugins>
                <plugin>
                  <artifactId>maven-antrun-plugin</artifactId>
                  <configuration><target>11</target></configuration>
                </plugin>
                <plugin>
                  <artifactId>maven-compiler-plugin</artifactId>
                  <configuration><release>21</release></configuration>
                </plugin>
              </plugins></build>
            </project>
            """);

        Assert.Equal("21", JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void JavaVersionDetector_ReadsAnExecutionLevelCompilerConfiguration()
    {
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <build><plugins>
                <plugin>
                  <artifactId>maven-compiler-plugin</artifactId>
                  <executions><execution>
                    <id>default-compile</id>
                    <configuration><release>17</release></configuration>
                  </execution></executions>
                </plugin>
              </plugins></build>
            </project>
            """);

        Assert.Equal("17", JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void JavaVersionDetector_FallsBackWhenOnlyAnUnrelatedPluginDeclaresATarget()
    {
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <build><plugins>
                <plugin>
                  <artifactId>maven-antrun-plugin</artifactId>
                  <configuration><target>11</target></configuration>
                </plugin>
              </plugins></build>
            </project>
            """);

        Assert.Equal(JavaVersionDetector.DefaultJavaVersion, JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void JavaVersionDetector_FallsBackWhenThePomCannotBeParsed()
    {
        // A malformed POM is the build tool's problem to report; publishing must not fail before the
        // container build has even started.
        using var appDirectory = new TempJavaAppDirectory();

        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), "<project>");

        Assert.Equal(JavaVersionDetector.DefaultJavaVersion, JavaVersionDetector.Detect(appDirectory.Path));
    }

    [Fact]
    public void ResolveBuildTool_ThrowsWhenNoBuildToolCanBeFound()
    {
        using var appDirectory = new TempJavaAppDirectory();

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path, "target/api.jar");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveBuildTool(app.Resource, appDirectory.Path));

        Assert.Equal(
            $"The Java application 'api' cannot be published because no build tool was found. " +
            $"Add a pom.xml, build.gradle, build.gradle.kts, settings.gradle, or settings.gradle.kts to '{appDirectory.Path}', " +
            "or call WithMavenBuild or WithGradleBuild " +
            "to state how the deployable JAR is produced.",
            exception.Message);
    }

    [Theory]
    [InlineData("build.gradle")]
    [InlineData("build.gradle.kts")]
    [InlineData("settings.gradle")]
    [InlineData("settings.gradle.kts")]
    public void ResolveBuildTool_DetectsGradleFromAnyOfItsBuildFiles(string fileName)
    {
        using var appDirectory = new TempJavaAppDirectory();
        File.WriteAllText(Path.Combine(appDirectory.Path, fileName), "");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path, "build/libs/api.jar");

        var (tool, args) = JavaDockerfileGenerator.ResolveBuildTool(app.Resource, appDirectory.Path);

        Assert.Equal(JavaBuildTool.Gradle, tool);
        Assert.Equal(["--no-daemon", "-x", "test", "build"], args);
    }

    [Fact]
    public void ResolveBuildTool_PrefersTheConfiguredBuildStepOverWhatIsOnDisk()
    {
        // A pom.xml can sit next to a build.gradle in a repository that is mid-migration, so an explicit
        // WithGradleBuild has to win over the file that happens to be checked first.
        using var appDirectory = new TempJavaAppDirectory();
        File.WriteAllText(Path.Combine(appDirectory.Path, "pom.xml"), "<project />");
        File.WriteAllText(Path.Combine(appDirectory.Path, "build.gradle"), "");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path).WithGradleTask("bootRun").WithGradleBuild("bootJar");

        var (tool, args) = JavaDockerfileGenerator.ResolveBuildTool(app.Resource, appDirectory.Path);

        Assert.Equal(JavaBuildTool.Gradle, tool);
        // The container build always adds --no-daemon: a daemon started inside a RUN layer dies with
        // the layer, so it only costs startup time and memory.
        Assert.Equal(["--no-daemon", "bootJar"], args);
    }

    [Fact]
    public async Task ApplicationArgumentsSurvivePublishingWhileLaunchToolArgumentsDoNot()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        WritePom(sourceDir.FullName, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        builder.AddJavaApp("worker", sourceDir.FullName, "target/worker.jar", ["--interval-seconds", "10"])
               .WithMavenBuild();

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // PublishAsDockerFile swaps the executable for a container that shares this annotation collection,
        // so the published resource carries the same name.
        var published = Assert.Single(model.Resources.OfType<ContainerResource>(), r => r.Name == "worker");
        var args = await ArgumentEvaluator.GetArgumentListAsync(published, app.Services);

        // PublishAsDockerFile clears the arguments because they routinely contain host paths, and it does so
        // when AddJavaApp runs. Anything added afterwards — which is every application argument, including
        // the ones passed to the jarPath overload — is appended after that clear and therefore survives,
        // while the launch tool arguments registered before it do not. The image's ENTRYPOINT is the JVM, so these
        // reach main(String[]) exactly as they do when the resource runs on the host.
        Assert.Equal(["--interval-seconds", "10"], args);
    }

    [Fact]
    public void PublishingWithoutAWrapperIsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        File.WriteAllText(Path.Combine(sourceDir.FullName, "pom.xml"), "<project/>");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName, "target/api.jar");

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        Assert.Contains("there is no mvnw", ex.Message);
        Assert.Contains("mvn -N wrapper:wrapper", ex.Message);
    }

    [Fact]
    public void PublishingWithAWrapperOutsideTheBuildContextIsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var siblingDir = workspace.CreateDirectory("sibling");
        WritePom(sourceDir.FullName, javaVersion: "21");
        WriteWrapper(siblingDir.FullName, "mvnw");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName)
                         .WithMavenGoal("spring-boot:run")
                         .WithWrapperPath(Path.Combine("..", "sibling", "mvnw"));

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        // Only files under the context are uploaded to the daemon, so a wrapper outside it is not in the
        // image and the build would fail partway through with an opaque "not found".
        Assert.Contains("is outside the build context", ex.Message);
    }

    [Theory]
    [InlineData("../outside.jar")]
    [InlineData("./../outside.jar")]
    [InlineData("..\\outside.jar")]
    [InlineData("nested/../../outside.jar")]
    public void PublishingAJarOutsideTheBuildContextIsRejected(string jarPath)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName, jarPath);

        // Normalizing with TrimStart('.', '/') used to eat the traversal itself, turning "../outside.jar"
        // into "outside.jar" so this check never fired and the image silently COPYd the wrong file.
        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetPrebuiltJarPath(app.Resource, sourceDir.FullName, out _));

        Assert.Contains("is outside the build context", ex.Message);
    }

    [Theory]
    [InlineData("../outside.jar")]
    [InlineData("./../outside.jar")]
    [InlineData("nested/../../outside.jar")]
    public void PublishingAJarArtifactOutsideTheBuildContextIsRejected(string artifactPath)
    {
        // WithJarArtifact reaches the build stage as a cp after only the application directory has been
        // copied in, so a traversal names something that is not in the image at all.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        WritePom(sourceDir.FullName, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName)
                         .WithMavenBuild()
                         .WithJarArtifact(artifactPath);

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        Assert.Contains("is outside the build context", ex.Message);
    }

    [Theory]
    [InlineData("target/worker.jar", "target/worker.jar")]
    [InlineData("./target/worker.jar", "target/worker.jar")]
    [InlineData("target\\worker.jar", "target/worker.jar")]
    public void PublishingAJarInsideTheBuildContextKeepsItsContextRelativePath(string jarPath, string expected)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName, jarPath);

        // A leading "./" is a normal way to write a context-relative path and must survive the tightened
        // normalization, and Windows separators still have to become POSIX ones for the container.
        Assert.True(JavaDockerfileGenerator.TryGetPrebuiltJarPath(app.Resource, sourceDir.FullName, out var resolved));
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public async Task PublishingAJarPathPrefixedWithDotSlashStillResolvesInsideTheContext()
    {
        var content = await PublishDockerfileAsync(jarPath: "./target/worker.jar");

        // A leading "./" is a normal way to write a context-relative path and must survive normalization.
        Assert.Contains("COPY --chown=999:999 target/worker.jar /app/app.jar", content);
    }

    [Theory]
    [InlineData("maven", "scripts/custom-mvnw", "scripts/custom-mvnw.cmd")]
    [InlineData("gradle", "scripts/custom-gradlew", "scripts/custom-gradlew.bat")]
    public async Task PublishingWithAWindowsBatchWrapperUsesThePosixSibling(
        string tool,
        string posixWrapper,
        string windowsWrapper)
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                if (tool is "maven")
                {
                    WritePom(source, javaVersion: "21");
                }
                else
                {
                    WriteGradleBuild(source, "sourceCompatibility = '21'");
                }

                var wrapperDirectory = Path.Combine(source, "scripts");
                WriteWrapper(wrapperDirectory, Path.GetFileName(posixWrapper));
                WriteWrapper(wrapperDirectory, Path.GetFileName(windowsWrapper));
            },
            configureResource: app => (tool is "maven"
                ? app.WithMavenGoal("spring-boot:run")
                : app.WithGradleTask("bootRun"))
                .WithWrapperPath(windowsWrapper));

        // Selecting the batch wrapper is reasonable on Windows, but the build stage is Linux and cannot
        // execute it. Maven and Gradle ship both scripts, so the POSIX sibling is used for the image.
        Assert.Contains($"sh ./{posixWrapper} ", content);
        await Verify(content).UseParameters(tool);
    }

    [Fact]
    public void PublishingWithAWindowsBatchWrapperAndNoPosixSiblingIsRejected()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        WritePom(sourceDir.FullName, javaVersion: "21");

        // WritePom ships the POSIX wrapper because publishing normally needs one. Remove it so the batch
        // wrapper really is the only one present.
        File.Delete(Path.Combine(sourceDir.FullName, "mvnw"));
        WriteWrapper(sourceDir.FullName, "mvnw.cmd");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName)
                         .WithMavenGoal("spring-boot:run")
                         .WithWrapperPath("mvnw.cmd");

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        Assert.Contains("Windows batch script", ex.Message);
    }

    [Theory]
    [InlineData("maven", "mvnw", ".mvn", "maven-wrapper.properties")]
    [InlineData("gradle", "gradlew", "gradle", "gradle-wrapper.properties")]
    public void PublishingRejectsAWrapperWithoutItsPropertiesFile(
        string tool,
        string wrapperName,
        string supportDirectory,
        string propertiesName)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        WriteWrapper(sourceDir.FullName, wrapperName);
        Directory.Delete(Path.Combine(sourceDir.FullName, supportDirectory), recursive: true);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName);

        _ = tool is "maven"
            ? app.WithMavenGoal("spring-boot:run")
            : app.WithGradleTask("bootRun");

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        Assert.Contains($"{supportDirectory}/wrapper/{propertiesName}", ex.Message);
    }

    [Fact]
    public async Task PublishingHonoursAWrapperSelectedWithWithWrapperPath()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
                Directory.CreateDirectory(Path.Combine(source, "scripts"));
                WriteWrapper(Path.Combine(source, "scripts"), "custom-mvnw");
            },
            configureResource: app => app
                .WithMavenGoal("spring-boot:run")
                .WithWrapperPath(Path.Combine("scripts", "custom-mvnw")));

        // Without this the container silently built with a different Maven than the host did.
        Assert.Contains("sh ./scripts/custom-mvnw", content);
    }

    [Fact]
    public async Task VerifyPublish_WithAPrebuiltJarAndNoBuildTool_CopiesTheJarWithoutABuildStage()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                Directory.CreateDirectory(Path.Combine(source, "target"));
                File.WriteAllText(Path.Combine(source, "target", "worker.jar"), "");
            },
            jarPath: Path.Combine("target", "worker.jar"));

        // A runnable application must stay publishable. Requiring a build tool here made
        // AddJavaApp(name, dir, jarPath) unpublishable even though it runs.
        Assert.DoesNotContain("AS build", content);
        Assert.Contains("COPY --chown=999:999 target/worker.jar /app/app.jar", content);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_WithAPrebuiltJar_WithJarArtifactHasNoEffect()
    {
        // Nothing is built in the image, so there is no build output for WithJarArtifact to select from.
        // The XML doc on WithJarArtifact promises this, and it is the one case where it does not win.
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                Directory.CreateDirectory(Path.Combine(source, "target"));
                File.WriteAllText(Path.Combine(source, "target", "worker.jar"), "");
            },
            jarPath: Path.Combine("target", "worker.jar"),
            configureResource: app => app.WithJarArtifact("target/other.jar"));

        Assert.DoesNotContain("AS build", content);
        Assert.Contains("COPY --chown=999:999 target/worker.jar /app/app.jar", content);
    }

    [Fact]
    public async Task PublishingAPrebuiltJarReincludesItAndItsDirectoriesInTheBuildContext()
    {
        var ignore = await PublishBuildContextIgnoreAsync(
            configureSource: source =>
            {
                Directory.CreateDirectory(Path.Combine(source, "target"));
                File.WriteAllText(Path.Combine(source, "target", "worker.jar"), "");
            },
            jarPath: Path.Combine("target", "worker.jar"));

        // "target" is excluded by default because it is routinely hundreds of megabytes, and Docker does
        // not descend into an excluded directory, so re-including only the JAR would never match.
        Assert.NotNull(ignore);
        Assert.Contains("\n!target\n", ignore);
        Assert.Contains("\n!target/worker.jar\n", ignore);
    }

    [Fact]
    public async Task APrebuiltJarAlongsideAPomIsStillBuiltInTheImage()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            jarPath: Path.Combine("target", "api.jar"));

        // A JAR path next to a pom.xml names the artifact the build produces, not one that already exists,
        // so the image has to build it rather than copy a file that is not in the context.
        Assert.Contains("AS build", content);
        Assert.Contains("sh ./mvnw", content);
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.ContainerImageBuild)]
    [OuterloopTest("Builds and runs a Docker image to verify the generated Java Dockerfile works")]
    public async Task VerifyPublish_PrebuiltJarImageBuildsAndRunsWithItsArguments()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        await File.WriteAllTextAsync(
            Path.Combine(sourceDir.FullName, "App.java"),
            """
            public class App {
                public static void main(String[] args) {
                    System.out.println("runtime ok: " + String.join(" ", args));
                }
            }
            """,
            TestContext.Current.CancellationToken);

        // The JAR is produced inside a JDK container so this test needs Docker and nothing else. A JDK on
        // the agent would work too, but only the runtime image's JDK is guaranteed to emit class files the
        // runtime image can load.
        var buildJarResult = await RunDockerCommandAsync(
            $"run --rm -v {sourceDir.FullName}:/work -w /work docker.io/library/eclipse-temurin:{JavaVersionDetector.DefaultJavaVersion}-jdk " +
            "sh -c \"javac -d classes App.java && mkdir -p target && jar --create --file target/app.jar --main-class App -C classes .\"",
            sourceDir.FullName);
        Assert.True(buildJarResult.ExitCode == 0, $"Building the test JAR failed.\nStdout: {buildJarResult.Stdout}\nStderr: {buildJarResult.Stderr}");

        using (var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest"))
        {
            builder.AddJavaApp("api", sourceDir.FullName, Path.Combine("target", "app.jar"), ["--greeting", "hello"]);
            builder.Build().Run();
        }

        // The image never bakes in the application arguments, exactly like every other container
        // resource: they are part of the deployment spec, so the manifest carries them and the runtime
        // appends them to the entrypoint. Reading them back and passing them to docker run is what makes
        // this test cover the published pair rather than the image alone.
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "aspire-manifest.json"), TestContext.Current.CancellationToken));
        var manifestArgs = manifest.RootElement
            .GetProperty("resources").GetProperty("api").GetProperty("args")
            .EnumerateArray().Select(arg => arg.GetString()!).ToArray();

        Assert.Equal(["--greeting", "hello"], manifestArgs);

        // Copied into the build context under the names docker build expects, so the generated ignore
        // file is exercised too. Its target re-includes are what let the JAR into the image at all.
        File.Copy(
            Path.Combine(outputDir.FullName, "api.Dockerfile"),
            Path.Combine(sourceDir.FullName, "Dockerfile"));
        File.Copy(
            Path.Combine(outputDir.FullName, "api.Dockerfile.dockerignore"),
            Path.Combine(sourceDir.FullName, ".dockerignore"));

        var imageName = $"aspire-java-test-{Guid.NewGuid():N}";

        try
        {
            var buildResult = await RunDockerCommandAsync($"build --network=host -t {imageName} -f Dockerfile .", sourceDir.FullName);
            Assert.True(buildResult.ExitCode == 0, $"Docker build failed with exit code {buildResult.ExitCode}.\nStdout: {buildResult.Stdout}\nStderr: {buildResult.Stderr}");

            // No network, so a passing run cannot depend on anything being downloaded at start up.
            var runResult = await RunDockerCommandAsync($"run --rm --network=none {imageName} {string.Join(' ', manifestArgs)}", sourceDir.FullName);
            Assert.True(runResult.ExitCode == 0, $"Docker run failed with exit code {runResult.ExitCode}.\nStdout: {runResult.Stdout}\nStderr: {runResult.Stderr}");

            // PublishAsDockerFile clears the executable's arguments, so this also pins that the application
            // arguments added after it survive publishing and reach the JAR through the entrypoint.
            Assert.Contains("runtime ok: --greeting hello", runResult.Stdout);
        }
        finally
        {
            await RunDockerCommandAsync($"rmi {imageName}", sourceDir.FullName);
        }
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker | TestFeature.ContainerImageBuild)]
    [OuterloopTest("Builds a real Maven wrapper project inside the generated image and runs it")]
    public async Task VerifyPublish_WrapperBuiltImageBuildsTheProjectAndRunsIt()
    {
        // The prebuilt-JAR test above never exercises the build stage. This one covers the path almost
        // every real project takes: the image runs the project's own wrapper, the wrapper downloads the
        // pinned Maven, the build produces the JAR, and the runtime stage starts it as a non-root user.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        await File.WriteAllTextAsync(
            Path.Combine(sourceDir.FullName, "pom.xml"),
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>api</artifactId>
              <version>1.0.0</version>
              <properties>
                <maven.compiler.release>{JavaVersionDetector.DefaultJavaVersion}</maven.compiler.release>
                <project.build.sourceEncoding>UTF-8</project.build.sourceEncoding>
              </properties>
              <build>
                <plugins>
                  <plugin>
                    <groupId>org.apache.maven.plugins</groupId>
                    <artifactId>maven-jar-plugin</artifactId>
                    <configuration>
                      <archive>
                        <manifest>
                          <mainClass>App</mainClass>
                        </manifest>
                      </archive>
                    </configuration>
                  </plugin>
                </plugins>
              </build>
            </project>
            """,
            TestContext.Current.CancellationToken);

        var sourcePath = Directory.CreateDirectory(Path.Combine(sourceDir.FullName, "src", "main", "java"));
        await File.WriteAllTextAsync(
            Path.Combine(sourcePath.FullName, "App.java"),
            """
            import java.nio.file.Files;
            import java.nio.file.Path;

            public class App {
                public static void main(String[] args) throws Exception {
                    // The runtime stage switches to a numeric UID with no /etc/passwd entry, so
                    // System.getProperty("user.name") reports "?" rather than an account name. Read the
                    // real UID from procfs instead, which is what USER actually changed.
                    System.out.println("wrapper build ok: uid=" + Files.getAttribute(Path.of("/proc/self"), "unix:uid"));
                }
            }
            """,
            TestContext.Current.CancellationToken);

        // Generated with a real Maven so the wrapper and its properties file are the genuine article
        // rather than a stub that would never prove the download-and-unpack layer works.
        var wrapperResult = await RunDockerCommandAsync(
            $"run --rm -v {sourceDir.FullName}:/work -w /work docker.io/library/maven:3.9-eclipse-temurin-{JavaVersionDetector.DefaultJavaVersion} " +
            "mvn -B -q -N wrapper:wrapper",
            sourceDir.FullName);
        Assert.True(wrapperResult.ExitCode == 0, $"Generating the Maven wrapper failed.\nStdout: {wrapperResult.Stdout}\nStderr: {wrapperResult.Stderr}");

        using (var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest"))
        {
            builder.AddJavaApp("api", sourceDir.FullName)
                   .WithMavenBuild()
                   .WithJarArtifact("target/api-1.0.0.jar");

            builder.Build().Run();
        }

        File.Copy(
            Path.Combine(outputDir.FullName, "api.Dockerfile"),
            Path.Combine(sourceDir.FullName, "Dockerfile"));
        File.Copy(
            Path.Combine(outputDir.FullName, "api.Dockerfile.dockerignore"),
            Path.Combine(sourceDir.FullName, ".dockerignore"));

        var imageName = $"aspire-java-wrapper-test-{Guid.NewGuid():N}";

        try
        {
            // The build stage needs the network to download Maven and the plugins, so unlike the prebuilt
            // JAR test this one cannot build offline.
            var buildResult = await RunDockerCommandAsync($"build --network=host -t {imageName} -f Dockerfile .", sourceDir.FullName);
            Assert.True(buildResult.ExitCode == 0, $"Docker build failed with exit code {buildResult.ExitCode}.\nStdout: {buildResult.Stdout}\nStderr: {buildResult.Stderr}");

            var runResult = await RunDockerCommandAsync($"run --rm --network=none {imageName}", sourceDir.FullName);
            Assert.True(runResult.ExitCode == 0, $"Docker run failed with exit code {runResult.ExitCode}.\nStdout: {runResult.Stdout}\nStderr: {runResult.Stderr}");

            // The runtime stage switches to an unprivileged numeric UID, so seeing 999 here proves both
            // that the wrapper build produced a runnable JAR and that USER took effect.
            Assert.Contains("wrapper build ok: uid=999", runResult.Stdout);
        }
        finally
        {
            await RunDockerCommandAsync($"rmi {imageName}", sourceDir.FullName);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunDockerCommandAsync(string arguments, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "docker",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Assert.NotNull(process);

        // Both streams are read concurrently so a full pipe buffer cannot deadlock the build output.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    [Fact]
    public async Task VerifyPublish_Quarkus_StagesTheFastJarDirectoryAndRunsQuarkusRunJar()
    {
        var content = await PublishQuarkusDockerfileAsync(source => WritePom(source, javaVersion: "21"));

        // The fast JAR's manifest Class-Path names lib/, app/, and quarkus/ relatively, so copying only
        // quarkus-run.jar produces an image that starts and immediately fails to find its own classes.
        Assert.Contains("cp -r target/quarkus-app/. /build/app/", content, StringComparison.Ordinal);
        Assert.Contains("COPY --from=build --chown=999:999 /build/app /app", content, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"java\",\"-jar\",\"/app/quarkus-run.jar\"]", content, StringComparison.Ordinal);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_Quarkus_Gradle_StagesFromTheGradleOutputDirectory()
    {
        var content = await PublishQuarkusDockerfileAsync(source => WriteGradleBuild(source, """
            plugins {
                id 'io.quarkus'
            }
            java {
                toolchain {
                    languageVersion = JavaLanguageVersion.of(21)
                }
            }
            """));

        Assert.Contains("cp -r build/quarkus-app/. /build/app/", content, StringComparison.Ordinal);

        // The Gradle daemon outlives the RUN instruction's shell and is killed with the layer, so it only
        // costs startup time here. The framework defaults do not carry the flag because run mode wants the
        // daemon, which is why the container build has to add it.
        Assert.Contains("sh ./gradlew --no-daemon build -x test", content, StringComparison.Ordinal);
        await Verify(content);
    }

    [Fact]
    public async Task VerifyPublish_Quarkus_FallsBackToASingleRunnerJarForUberJarPackaging()
    {
        // The packaging type is chosen in application configuration, which the AppHost cannot read, so both
        // shapes have to be handled in the build stage where the output already exists.
        var content = await PublishQuarkusDockerfileAsync(source => WritePom(source, javaVersion: "21"));

        Assert.Contains("if [ -d target/quarkus-app ]; then", content, StringComparison.Ordinal);
        Assert.Contains("cp \"$jars\" /build/app/quarkus-run.jar", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_Quarkus_StagesTheDependencyDirectoryForLegacyJarPackaging()
    {
        var content = await PublishQuarkusDockerfileAsync(source => WritePom(source, javaVersion: "21"));

        // legacy-jar leaves the runner beside target/lib rather than inside quarkus-app, and the runner's
        // manifest names those dependencies as "lib/..." relative to itself, so the directory has to travel
        // with it. Staging the runner alone produces an image that dies on startup with
        // "NoClassDefFoundError: io/quarkus/runtime/Quarkus".
        Assert.Contains(
            "if [ -d target/lib ]; then cp -r target/lib /build/app/lib; fi",
            content,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fast-jar")]
    [InlineData("legacy-jar")]
    [InlineData("uber-jar")]
    [InlineData("fast-jar-without-metadata")]
    public async Task VerifyPublish_Quarkus_StagesEveryPackagingTypeSoTheRunnerCanResolveItsDependencies(string packagingType)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The staging command is POSIX shell that only ever runs inside the Linux build stage.");
        }

        var content = await PublishQuarkusDockerfileAsync(source => WritePom(source, javaVersion: "21"));

        // The staging command is appended last to the build stage's RUN, so it runs from the marker to the
        // end of that line. Exercising the emitted shell verbatim is the only way to catch a packaging
        // layout it mishandles; asserting on the string only proves the string.
        var start = content.IndexOf($"mkdir -p {StagedArtifactDirectory} &&", StringComparison.Ordinal);
        Assert.NotEqual(-1, start);
        var end = content.IndexOf('\n', start);
        var stagingCommand = content[start..(end < 0 ? content.Length : end)];

        using var workspace = new TempJavaAppDirectory(withWrappers: false);
        var stagingDirectory = Path.Combine(workspace.Path, "staged");

        // These layouts are what the three packaging types actually write, taken from clean builds of the
        // JavaQuarkus playground rather than from the documentation.
        switch (packagingType)
        {
            case "fast-jar":
                workspace.Write("target/quarkus-app/quarkus-run.jar", "runner");
                workspace.Write("target/quarkus-app/lib/dependency.jar", "dependency");
                // The thin JAR the base plugin wrote stays in the output directory in every packaging type,
                // which is why the runner cannot be picked by globbing for a single JAR.
                workspace.Write("target/inventory-1.0.0-SNAPSHOT.jar", "thin");
                workspace.Write("target/quarkus-artifact.properties", "type=jar\npath=quarkus-app/quarkus-run.jar\n");
                break;
            case "legacy-jar":
                workspace.Write("target/inventory-1.0.0-SNAPSHOT-runner.jar", "runner");
                workspace.Write("target/lib/dependency.jar", "dependency");
                workspace.Write("target/inventory-1.0.0-SNAPSHOT.jar", "thin");
                workspace.Write("target/quarkus-artifact.properties", "type=jar\npath=inventory-1.0.0-SNAPSHOT-runner.jar\n");
                break;
            case "uber-jar":
                workspace.Write("target/inventory-1.0.0-SNAPSHOT-runner.jar", "runner");
                workspace.Write("target/quarkus-artifact.properties", "type=jar\npath=inventory-1.0.0-SNAPSHOT-runner.jar\n");
                break;
            case "fast-jar-without-metadata":
                // A Quarkus old enough not to write the metadata file still has to publish, which is what
                // the directory probe behind the empty case is for.
                workspace.Write("target/quarkus-app/quarkus-run.jar", "runner");
                workspace.Write("target/quarkus-app/lib/dependency.jar", "dependency");
                workspace.Write("target/inventory-1.0.0-SNAPSHOT.jar", "thin");
                break;
        }

        var (exitCode, _, stderr) = await RunShellAsync(
            stagingCommand.Replace(StagedArtifactDirectory, stagingDirectory, StringComparison.Ordinal),
            workspace.Path);

        Assert.True(exitCode == 0, $"Staging {packagingType} failed with exit code {exitCode}: {stderr}");

        // Every packaging type has to normalise to the same shape, because the runtime stage copies one
        // directory and runs one well-known file name out of it.
        Assert.Equal("runner", File.ReadAllText(Path.Combine(stagingDirectory, "quarkus-run.jar")));

        var expectedDependency = Path.Combine(stagingDirectory, "lib", "dependency.jar");
        if (packagingType is "uber-jar")
        {
            Assert.False(Directory.Exists(Path.Combine(stagingDirectory, "lib")));
        }
        else
        {
            Assert.Equal("dependency", File.ReadAllText(expectedDependency));
        }
    }

    private const string StagedArtifactDirectory = "/build/app";

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunShellAsync(string command, string workingDirectory)
    {
        using var process = Process.Start(new ProcessStartInfo("/bin/sh")
        {
            ArgumentList = { "-c", command },
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        Assert.NotNull(process);

        // Both streams are read concurrently so a full pipe buffer cannot deadlock the staging output.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    [Fact]
    public async Task VerifyPublish_Quarkus_WithJarArtifact_StagesThatFileInstead()
    {
        // Naming a single artifact is how an application that does not use the fast JAR layout at all opts
        // out, so it has to keep staging as one file.
        var content = await PublishQuarkusDockerfileAsync(
            source => WritePom(source, javaVersion: "21"),
            app => app.WithJarArtifact("target/api-runner.jar"));

        Assert.Contains("cp 'target/api-runner.jar' /build/app.jar", content, StringComparison.Ordinal);
        Assert.Contains("COPY --from=build --chown=999:999 /build/app.jar /app/app.jar", content, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"java\",\"-jar\",\"/app/app.jar\"]", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_InstallsUnzipWhenTheMavenWrapperPinsADistributionChecksum()
    {
        // mvnw downloads the .tar.gz instead of the .zip when unzip is missing, then validates it against
        // the .zip's checksum and stops. The Temurin build images have no unzip and the Quarkus generator
        // pins this checksum by default, so without the install the build fails for every such project.
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
                File.AppendAllText(
                    Path.Combine(source, ".mvn", "wrapper", "maven-wrapper.properties"),
                    "distributionSha256Sum=5af3b743dd8b876b5c45da33b676251e5f1687712644abb4ee519ca56e1d89ce\n");
            },
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        var unzipIndex = content.IndexOf("apt-get install -y --no-install-recommends unzip", StringComparison.Ordinal);
        var wrapperIndex = content.IndexOf("./mvnw -B -ntp --version", StringComparison.Ordinal);

        Assert.NotEqual(-1, unzipIndex);
        // Installed before the wrapper runs, because the wrapper is what fails without it.
        Assert.True(unzipIndex < wrapperIndex, "unzip must be installed before the wrapper runs");
    }

    [Fact]
    public async Task VerifyPublish_DoesNotInstallUnzipWhenNoChecksumIsPinned()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        // unzip only exists to verify a pinned wrapper checksum. Assert the wrapper still runs, so this
        // keeps failing if the build stage disappears rather than passing on an empty Dockerfile.
        Assert.Contains("./mvnw -B -ntp --version", content, StringComparison.Ordinal);
        Assert.DoesNotContain("unzip", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_RaisesTheBuildJdkWhenTheProjectTargetsAnOlderRelease()
    {
        // Maven 4 refuses to start on anything below Java 17, so a build stage matching an older target
        // would die before compiling. The runtime stage still matches the target, because that is what the
        // produced bytecode needs.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "8", mavenVersion: "4.0.0"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:17-jdk AS build", content, StringComparison.Ordinal);
        Assert.Contains("FROM docker.io/library/eclipse-temurin:8-jre", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_RaisesTheBuildJdkForGradle9()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WriteGradleBuild(source, "sourceCompatibility = '11'", gradleVersion: "9.0.0");
            },
            configureResource: app => app.WithGradleTask("bootRun"));

        Assert.Contains("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:17-jdk AS build", content, StringComparison.Ordinal);
        Assert.Contains("FROM docker.io/library/eclipse-temurin:11-jre", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_KeepsTheBuildJdkOnTheTargetForAToolThatCannotRunOnANewerJdk()
    {
        // Gradle releases before 7.3 cannot run *on* Java 17, and are exactly what an old Java 8 project
        // tends to pin. Raising the build stage unconditionally would break them.
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WriteGradleBuild(source, "sourceCompatibility = '8'", gradleVersion: "6.9.4");
            },
            configureResource: app => app.WithGradleTask("bootRun"));

        Assert.Contains("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:8-jdk AS build", content, StringComparison.Ordinal);
        Assert.Contains("FROM docker.io/library/eclipse-temurin:8-jre", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("target/my app.jar")]
    [InlineData(@"target\my app.jar")]
    public void VerifyPublish_RejectsAJarPathContainingWhitespace(string jarPath)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path, jarPath);

        // The Dockerfile builder emits the shell form of COPY, which splits on whitespace, so this would
        // become three arguments and copy two paths that do not exist.
        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetPrebuiltJarPath(app.Resource, tempDir.Path, out _));

        Assert.Contains(jarPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("contains whitespace", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPublish_RejectsAnOtelAgentPathContainingWhitespace()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        using var tempDir = new TempJavaAppDirectory();

        var app = builder.AddJavaApp("api", tempDir.Path)
            .WithMavenGoal("spring-boot:run")
            .WithOtelAgent("target/otel agents/javaagent.jar");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetBuildProducedAgentPath(app.Resource, out _));

        Assert.Contains("target/otel agents/javaagent.jar", exception.Message, StringComparison.Ordinal);
        Assert.Contains("contains whitespace", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_CapsTheBuildJdkAtWhatTheGradleWrapperCanRunOn()
    {
        // Gradle 8.4 can target Java 21 through a toolchain but cannot run on it - that starts at 8.5 -
        // so the build stage stays on 20 while the runtime image keeps the targeted 21.
        // https://docs.gradle.org/current/userguide/compatibility.html
        var content = await PublishDockerfileAsync(
            configureSource: source => WriteGradleBuild(source, "sourceCompatibility = '21'", gradleVersion: "8.4"),
            configureResource: app => app.WithGradleTask("bootRun"));

        Assert.Contains("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:20-jdk AS build", content, StringComparison.Ordinal);
        Assert.Contains("FROM docker.io/library/eclipse-temurin:21-jre", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_AcceptsTheFirstGradleReleaseThatRunsOnTheTargetedJdk()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WriteGradleBuild(source, "sourceCompatibility = '21'", gradleVersion: "8.5"),
            configureResource: app => app.WithGradleTask("bootRun"));

        Assert.Contains("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:21-jdk AS build", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_DoesNotApplyTheGradleCeilingToMaven()
    {
        // Maven has no equivalent runtime ceiling, so a Maven wrapper never blocks a newer target.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "25", mavenVersion: "3.9.9"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:25-jdk AS build", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_KeepsTheBuildJdkOnTheTargetWhenItIsNewEnough()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("FROM --platform=$BUILDPLATFORM docker.io/library/eclipse-temurin:21-jdk AS build", content, StringComparison.Ordinal);
        Assert.Contains("FROM docker.io/library/eclipse-temurin:21-jre", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_BuildsOnTheTargetPlatformWhenMavenSelectsNativeDependenciesByHostArchitecture()
    {
        // os-maven-plugin resolves ${os.detected.classifier} from the machine running the build, so a JAR
        // built on the build platform would carry the wrong native library into the runtime image and only
        // fail on the first call into it.
        var content = await PublishDockerfileAsync(
            configureSource: source =>
            {
                WritePom(source, javaVersion: "21");
                File.WriteAllText(
                    Path.Combine(source, "pom.xml"),
                    File.ReadAllText(Path.Combine(source, "pom.xml")).Replace(
                        "</project>",
                        """
                          <dependencies>
                            <dependency>
                              <groupId>io.netty</groupId>
                              <artifactId>netty-tcnative-boringssl-static</artifactId>
                              <classifier>${os.detected.classifier}</classifier>
                            </dependency>
                          </dependencies>
                        </project>
                        """,
                        StringComparison.Ordinal));
            },
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("FROM docker.io/library/eclipse-temurin:21-jdk AS build", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--platform=$BUILDPLATFORM", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_BuildsOnTheTargetPlatformWhenGradleUsesTheOsDetectorPlugin()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WriteGradleBuild(source, """
                plugins {
                    id 'com.google.osdetector' version '1.7.3'
                }

                sourceCompatibility = '21'
                """),
            configureResource: app => app.WithGradleTask("bootRun"));

        Assert.Contains("FROM docker.io/library/eclipse-temurin:21-jdk AS build", content, StringComparison.Ordinal);
        Assert.DoesNotContain("--platform=$BUILDPLATFORM", content, StringComparison.Ordinal);
    }

    [Fact]
    public void PublishingWithAWrapperPathContainingWhitespaceIsRejected()
    {
        // COPY separates its arguments on whitespace with no quoted form available here, so such a path
        // would copy two paths that do not exist rather than the one that does.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        WritePom(sourceDir.FullName, javaVersion: "21");
        var nested = Directory.CreateDirectory(Path.Combine(sourceDir.FullName, "build tools"));
        WriteWrapper(nested.FullName, "mvnw");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", sourceDir.FullName)
                         .WithMavenGoal("spring-boot:run")
                         .WithWrapperPath(Path.Combine("build tools", "mvnw"));

        var ex = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.ResolveContainerBuildForTesting(app.Resource, sourceDir.FullName));

        Assert.Contains("contains whitespace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublishingAfterChangingTheWorkingDirectoryIsRejected()
    {
        // PublishAsDockerFile fixes the build context when AddJavaApp runs, and ContextPath cannot be
        // changed afterwards, so a later WithWorkingDirectory would build the image from the original
        // directory while the resource points somewhere else.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var movedDir = workspace.CreateDirectory("moved");
        var outputDir = workspace.CreateDirectory("output");

        WritePom(sourceDir.FullName, javaVersion: "21");
        WritePom(movedDir.FullName, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");
        builder.AddJavaApp("api", sourceDir.FullName)
               .WithMavenGoal("spring-boot:run")
               .WithWorkingDirectory(movedDir.FullName);

        builder.Build().Run();

        // The failure surfaces as a missing Dockerfile because the publishing callback's exception is
        // recorded against the step rather than rethrown from Run.
        Assert.False(
            File.Exists(Path.Combine(outputDir.FullName, "api.Dockerfile")),
            "No Dockerfile should be generated from a build context the resource no longer points at.");

        await Task.CompletedTask;
    }

    private async Task<string> PublishQuarkusDockerfileAsync(
        Action<string> configureSource,
        Func<IResourceBuilder<JavaAppResource>, IResourceBuilder<JavaAppResource>>? configureResource = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        configureSource(sourceDir.FullName);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

        // Assigned first because `configureResource?.Invoke(builder.AddQuarkusApp(...))` skips evaluating the
        // argument entirely when the delegate is null, which would add no resource at all.
        var app = builder.AddQuarkusApp("api", sourceDir.FullName);

        configureResource?.Invoke(app);

        builder.Build().Run();

        return await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task VerifyPublish_ExplicitCompilerReleaseWinsOverTheSpringBootProperty()
    {
        // java.version is not a Maven property. It works only because spring-boot-starter-parent maps it
        // onto maven.compiler.release, and a POM that sets the real property overrides that mapping - so
        // Maven emits 21 here and a Java 17 runtime image would fail with UnsupportedClassVersionError.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePomProperties(source, """
                <java.version>17</java.version>
                <maven.compiler.release>21</maven.compiler.release>
                """),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("docker.io/library/eclipse-temurin:21-jre", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_CompilerPluginReleaseWinsOverTheSpringBootProperty()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePomWithCompilerPlugin(source, javaVersion: "17", release: "21"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("docker.io/library/eclipse-temurin:21-jre", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_SpringBootPropertyStillAppliesWhenItIsTheOnlySignal()
    {
        // The overwhelmingly common Spring Boot POM. Reordering the precedence must not break it.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePomProperties(source, "<java.version>17</java.version>"),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("docker.io/library/eclipse-temurin:17-jre", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_SpringBootPropertyResolvesThroughAnUnexpandedReference()
    {
        // The Spring Boot parent's own mapping, written out in the child POM. maven.compiler.release
        // holds a property reference this cannot expand, so detection has to keep looking rather than
        // treat the unresolved value as a version.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePomProperties(source, """
                <java.version>21</java.version>
                <maven.compiler.release>${java.version}</maven.compiler.release>
                """),
            configureResource: app => app.WithMavenGoal("spring-boot:run"));

        Assert.Contains("docker.io/library/eclipse-temurin:21-jre", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_UsesTheNamedJarWhenTheBuildRunsInTheImage()
    {
        // The AppHost already said which JAR matters. Falling back to the glob would fail the
        // "expected exactly one" check for any build that emits a second JAR, such as a shade plugin
        // leaving original-*.jar beside the shaded artifact.
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            jarPath: "target/worker.jar",
            configureResource: app => app.WithMavenBuild());

        Assert.Contains("cp 'target/worker.jar' /build/app.jar", content, StringComparison.Ordinal);
        Assert.DoesNotContain("expected exactly one application JAR", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_NamedJarWindowsSeparatorsBecomePosix()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            jarPath: @"target\worker.jar",
            configureResource: app => app.WithMavenBuild());

        Assert.Contains("cp 'target/worker.jar' /build/app.jar", content, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPublish_NamedJarOutsideTheContextIsRejected()
    {
        // Falling back to the glob would publish whichever JAR the build emitted rather than the one the
        // AppHost named, so the image would quietly run different code than run mode does.
        using var appDirectory = new TempJavaAppDirectory();
        WritePom(appDirectory.Path, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path, "../shared/worker.jar").WithMavenBuild();

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetBuildOutputJarPath(app.Resource, out _));

        Assert.Contains("../shared/worker.jar", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\artifacts\app.jar")]
    [InlineData(@"\\build-server\artifacts\app.jar")]
    [InlineData("C:app.jar")]
    public void VerifyPublish_AWindowsAbsoluteJarPathIsRejectedOnEveryPlatform(string jarPath)
    {
        // Path.IsPathRooted only applies the host's rules, so publishing an AppHost authored on Windows
        // from Linux CI used to accept these as build-relative, rewrite the backslashes for the container
        // and build an image against a path that cannot exist. The verdict has to be the same everywhere.
        using var appDirectory = new TempJavaAppDirectory();
        WritePom(appDirectory.Path, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path, jarPath).WithMavenBuild();

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.TryGetBuildOutputJarPath(app.Resource, out _));

        // Keep the authored spelling in diagnostics even when the path uses separators from another host.
        Assert.Contains(jarPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("outside the directory the build runs in", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPublish_AWindowsAbsoluteJarArtifactIsRejectedOnEveryPlatform()
    {
        using var appDirectory = new TempJavaAppDirectory();
        WritePom(appDirectory.Path, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path).WithMavenBuild().WithJarArtifact(@"C:\out\app.jar");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.JavaContainerBuild.Resolve(app.Resource, appDirectory.Path));

        Assert.Contains("outside the build context", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyPublish_AWindowsAbsoluteWrapperIsRejectedOnEveryPlatform()
    {
        using var appDirectory = new TempJavaAppDirectory();
        WritePom(appDirectory.Path, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var app = builder.AddJavaApp("api", appDirectory.Path).WithMavenBuild().WithWrapperPath(@"C:\tools\mvnw");

        var exception = Assert.Throws<DistributedApplicationException>(
            () => JavaDockerfileGenerator.JavaContainerBuild.Resolve(app.Resource, appDirectory.Path));

        Assert.Contains("outside the build context", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_ExplicitJarArtifactWinsOverTheNamedJar()
    {
        var content = await PublishDockerfileAsync(
            configureSource: source => WritePom(source, javaVersion: "21"),
            jarPath: "target/worker.jar",
            configureResource: app => app.WithMavenBuild().WithJarArtifact("target/shaded.jar"));

        Assert.Contains("cp 'target/shaded.jar' /build/app.jar", content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyPublish_MovingTheWorkingDirectoryAfterwardsFailsWithAnActionableMessage()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var elsewhere = workspace.CreateDirectory("elsewhere");

        WritePom(sourceDir.FullName, javaVersion: "21");

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.AddJavaApp("api", sourceDir.FullName)
            .WithMavenBuild()
            .WithWorkingDirectory(elsewhere.FullName);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // The build context is fixed when the resource is added and cannot move afterwards, so the
        // Dockerfile callback has to refuse rather than build an image from a directory the author no
        // longer points at. The message is what the author acts on, so assert it names both directories.
        var annotation = model.Resources
            .SelectMany(resource => resource.Annotations.OfType<DockerfileBuildAnnotation>())
            .Single();

        var context = new DockerfileFactoryContext
        {
            Services = app.Services,
            Resource = model.Resources.Single(resource => resource.Name == "api"),
            CancellationToken = TestContext.Current.CancellationToken
        };

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => annotation.DockerfileFactory!(context));

        Assert.Contains("its working directory was changed", exception.Message, StringComparison.Ordinal);
        Assert.Contains(elsewhere.FullName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(sourceDir.FullName, exception.Message, StringComparison.Ordinal);
    }

    private async Task<string> PublishDockerfileAsync(
        Action<string>? configureSource = null,
        string? jarPath = null,
        Func<IResourceBuilder<JavaAppResource>, IResourceBuilder<JavaAppResource>>? configureResource = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        configureSource?.Invoke(sourceDir.FullName);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

        var app = jarPath is null
            ? builder.AddJavaApp("api", sourceDir.FullName)
            : builder.AddJavaApp("api", sourceDir.FullName, jarPath);

        configureResource?.Invoke(app);

        builder.Build().Run();

        return await File.ReadAllTextAsync(Path.Combine(outputDir.FullName, "api.Dockerfile"), TestContext.Current.CancellationToken);
    }

    private async Task<string?> PublishBuildContextIgnoreAsync(
        Action<string>? configureSource = null,
        string? jarPath = null,
        Func<IResourceBuilder<JavaAppResource>, IResourceBuilder<JavaAppResource>>? configureResource = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var sourceDir = workspace.CreateDirectory("source");
        var outputDir = workspace.CreateDirectory("output");

        configureSource?.Invoke(sourceDir.FullName);

        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, outputDir.FullName, step: "publish-manifest");

        var app = jarPath is null
            ? builder.AddJavaApp("api", sourceDir.FullName)
            : builder.AddJavaApp("api", sourceDir.FullName, jarPath);

        configureResource?.Invoke(app);

        builder.Build().Run();

        var ignorePath = Path.Combine(outputDir.FullName, "api.Dockerfile.dockerignore");

        return File.Exists(ignorePath)
            ? await File.ReadAllTextAsync(ignorePath, TestContext.Current.CancellationToken)
            : null;
    }

    private static void WritePom(string sourceDirectory, string javaVersion, string mavenVersion = "3.9.9")
    {
        // Publishing requires a wrapper, so every project that publishes has to ship one.
        WriteWrapper(sourceDirectory, "mvnw", mavenVersion);
        File.WriteAllText(Path.Combine(sourceDirectory, "pom.xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>api</artifactId>
              <version>0.0.1-SNAPSHOT</version>
              <properties>
                <java.version>{javaVersion}</java.version>
              </properties>
            </project>
            """);
    }

    private static void WritePomProperties(string sourceDirectory, string properties, string mavenVersion = "3.9.9")
    {
        WriteWrapper(sourceDirectory, "mvnw", mavenVersion);
        File.WriteAllText(Path.Combine(sourceDirectory, "pom.xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>api</artifactId>
              <version>0.0.1-SNAPSHOT</version>
              <properties>
            {properties}
              </properties>
            </project>
            """);
    }

    private static void WritePomWithCompilerPlugin(string sourceDirectory, string javaVersion, string release, string mavenVersion = "3.9.9")
    {
        WriteWrapper(sourceDirectory, "mvnw", mavenVersion);
        File.WriteAllText(Path.Combine(sourceDirectory, "pom.xml"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <modelVersion>4.0.0</modelVersion>
              <groupId>com.example</groupId>
              <artifactId>api</artifactId>
              <version>0.0.1-SNAPSHOT</version>
              <properties>
                <java.version>{javaVersion}</java.version>
              </properties>
              <build>
                <plugins>
                  <plugin>
                    <groupId>org.apache.maven.plugins</groupId>
                    <artifactId>maven-compiler-plugin</artifactId>
                    <configuration>
                      <release>{release}</release>
                    </configuration>
                  </plugin>
                </plugins>
              </build>
            </project>
            """);
    }

    private static void WriteGradleBuild(string sourceDirectory, string contents, string gradleVersion = "8.10")
    {
        WriteWrapper(sourceDirectory, "gradlew", gradleVersion);
        File.WriteAllText(Path.Combine(sourceDirectory, "build.gradle"), contents);
    }

    private static void WriteWrapper(string sourceDirectory, string wrapperName, string? toolVersion = null)
    {
        Directory.CreateDirectory(sourceDirectory);
        File.WriteAllText(Path.Combine(sourceDirectory, wrapperName), "#!/bin/sh\nexit 0\n");

        // A real wrapper always ships the properties file that pins the tool version, and publishing
        // requires it so the distribution can be unpacked in its own image layer. The URLs match what the
        // real wrappers write, including the escaped ':' Gradle uses and the version Maven repeats.
        var gradle = wrapperName.Contains("gradle", StringComparison.OrdinalIgnoreCase);
        var supportDirectory = Path.Combine(sourceDirectory, gradle ? "gradle" : ".mvn", "wrapper");

        // Versions that impose no build JDK requirement of their own, so a test that does not care about
        // tool compatibility sees the targeted release in both stages.
        var version = toolVersion ?? (gradle ? "8.10" : "3.9.9");

        var distributionUrl = gradle
            ? $"https\\://services.gradle.org/distributions/gradle-{version}-bin.zip"
            : $"https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/{version}/apache-maven-{version}-bin.zip";

        Directory.CreateDirectory(supportDirectory);
        File.WriteAllText(
            Path.Combine(supportDirectory, gradle ? "gradle-wrapper.properties" : "maven-wrapper.properties"),
            $"distributionUrl={distributionUrl}\n");
    }
}
