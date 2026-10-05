// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Java;

/// <summary>
/// Identifies the build tool that launches a <see cref="JavaAppResource"/>.
/// </summary>
internal enum JavaBuildTool
{
    /// <summary>Apache Maven, invoked through the <c>mvnw</c> wrapper.</summary>
    Maven,

    /// <summary>Gradle, invoked through the <c>gradlew</c> wrapper.</summary>
    Gradle
}

/// <summary>
/// Records that a Java application is launched through a build tool wrapper rather than through <c>java -jar</c>.
/// </summary>
/// <param name="tool">The build tool that launches the application.</param>
/// <param name="args">The arguments to pass to the build tool (the goal or task name, plus any extra arguments).</param>
internal sealed class JavaBuildToolAnnotation(JavaBuildTool tool, string[] args) : IResourceAnnotation
{
    /// <summary>
    /// The build tool that launches the application.
    /// </summary>
    public JavaBuildTool Tool { get; } = tool;

    /// <summary>
    /// The arguments to pass to the build tool.
    /// </summary>
    public string[] Args { get; } = args;
}

/// <summary>
/// Records how a <see cref="JavaAppResource"/> is built before it runs.
/// </summary>
/// <remarks>
/// Recorded in every execution context, not only where the build actually runs. In run mode the
/// <see cref="ResourceName"/> lets <c>WithWrapperPath</c> re-point a build step that was created before
/// the override was supplied, which is what makes the builder calls order-independent. In publish mode
/// there is no build-step resource, but the tool and arguments still describe how to produce a deployable
/// JAR and are what the generated Dockerfile runs.
/// </remarks>
/// <param name="ResourceName">The name of the build-step resource, or <see langword="null"/> outside run mode.</param>
/// <param name="Tool">The build tool that produces the artifact.</param>
/// <param name="Args">The arguments passed to the build tool.</param>
internal sealed record JavaBuildStepAnnotation(string? ResourceName, JavaBuildTool Tool, string[] Args) : IResourceAnnotation;

/// <summary>
/// Defers choosing between Maven and Gradle until the application is about to start.
/// </summary>
/// <remarks>
/// Spring Boot and Quarkus helpers support both tools. Keeping both configurations here lets those helpers
/// build the model without touching the application directory, while run and publish still resolve the
/// same project markers through <see cref="JavaBuildToolResolver"/>.
/// </remarks>
/// <param name="MavenBuildArgs">Arguments that package the application with Maven.</param>
/// <param name="MavenLaunchArgs">Arguments that launch the application with Maven.</param>
/// <param name="GradleBuildArgs">Arguments that package the application with Gradle.</param>
/// <param name="GradleLaunchArgs">Arguments that launch the application with Gradle.</param>
internal sealed record JavaDetectedBuildToolAnnotation(
    string[] MavenBuildArgs,
    string[] MavenLaunchArgs,
    string[] GradleBuildArgs,
    string[] GradleLaunchArgs) : IResourceAnnotation
{
    /// <summary>
    /// Returns the build and launch arguments for <paramref name="tool"/>.
    /// </summary>
    internal (string[] BuildArgs, string[] LaunchArgs) GetConfiguration(JavaBuildTool tool) => tool switch
    {
        JavaBuildTool.Maven => (MavenBuildArgs, MavenLaunchArgs),
        JavaBuildTool.Gradle => (GradleBuildArgs, GradleLaunchArgs),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null)
    };
}

/// <summary>
/// Records the OpenTelemetry Java agent configured by <c>WithOtelAgent</c>.
/// </summary>
/// <remarks>
/// The environment variable alone is not enough to reproduce the agent in a container. A relative agent
/// path names a file produced by the build, which only exists in the Dockerfile's build stage, so the
/// runtime stage has to copy it forward and the environment variable has to point at where it landed.
/// Without this the published container starts a JVM pointing at an agent JAR that is not in the image
/// and dies during VM initialization.
/// </remarks>
/// <param name="AgentPath">
/// The agent path exactly as authored, before any resolution, or <see langword="null"/> when the caller
/// asked for the location the build tool writes the agent to. That location is resolved on demand rather
/// than when the annotation is added, so <c>WithOtelAgent()</c> and <c>WithMavenBuild()</c> can be called
/// in either order.
/// </param>
internal sealed record JavaOtelAgentAnnotation(string? AgentPath) : IResourceAnnotation;

/// <summary>
/// Records that publishing found a Dockerfile the developer wrote and left it alone.
/// </summary>
/// <remarks>
/// The rest of the integration assumes it produced the image, and so knows where the build put things.
/// An authored Dockerfile is the developer's contract instead, so anything that would otherwise point
/// at a generated layout has to notice and say so rather than guess.
/// </remarks>
internal sealed record JavaAuthoredDockerfileAnnotation : IResourceAnnotation;

/// <summary>
/// Records that a Java application is a Quarkus application, which packages differently from every other
/// build the integration supports.
/// </summary>
/// <remarks>
/// Quarkus's default packaging is the "fast JAR": the runnable artifact is <c>quarkus-app/quarkus-run.jar</c>
/// and it is useless on its own, because its manifest <c>Class-Path</c> points at sibling <c>lib</c>,
/// <c>app</c>, and <c>quarkus</c> directories. Publishing has to carry that whole directory rather than a
/// single file, and the plain JAR the build leaves in the output directory alongside it has no
/// <c>Main-Class</c> at all. Neither is discoverable from the build file at publish time, when nothing has
/// been built yet, so it is recorded when the resource is added.
/// See https://quarkus.io/guides/maven-tooling#fast-jar.
/// </remarks>
internal sealed class JavaQuarkusAnnotation : IResourceAnnotation;
