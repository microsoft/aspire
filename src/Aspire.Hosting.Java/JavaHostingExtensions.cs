// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Java;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Java applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class JavaHostingExtensions
{
    /// <summary>
    /// Adds a Java application to the application model. The Java runtime must be available on the PATH.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory for the Java application.</param>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> AddJavaApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);

        workingDirectory = Path.GetFullPath(workingDirectory, builder.AppHostDirectory);
        var resource = new JavaAppResource(name, workingDirectory);

        var rb = builder.AddResource(resource)
            .WithArgs(ctx =>
            {
                // If we have a JAR path, use -jar option
                if (resource.JarPath is not null)
                {
                    ctx.Args.Add("-jar");
                    ctx.Args.Add(resource.JarPath);
                }

                // Add JVM arguments
                if (ctx.Resource.TryGetLastAnnotation<JvmArgsAnnotation>(out var jvmArgs))
                {
                    foreach (var arg in jvmArgs.Args)
                    {
                        ctx.Args.Add(arg);
                    }
                }
            })
            .WithOtlpExporter();

        return rb;
    }

    /// <summary>
    /// Adds a Java application with a pre-existing JAR file.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory for the Java application.</param>
    /// <param name="jarPath">The path to the JAR file to execute.</param>
    /// <param name="args">Optional arguments to pass to the Java application.</param>
    [AspireExport("addJavaAppWithJar")]
    public static IResourceBuilder<JavaAppResource> AddJavaApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string workingDirectory,
        string jarPath,
        string[]? args = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        ArgumentException.ThrowIfNullOrEmpty(jarPath);

        workingDirectory = Path.GetFullPath(workingDirectory, builder.AppHostDirectory);
        jarPath = Path.GetFullPath(jarPath, workingDirectory);

        var resource = new JavaAppResource(name, workingDirectory, jarPath);

        var rb = builder.AddResource(resource)
            .WithArgs(ctx =>
            {
                // If we have a JAR path, use -jar option
                if (resource.JarPath is not null)
                {
                    ctx.Args.Add("-jar");
                    ctx.Args.Add(resource.JarPath);
                }

                // Add application arguments
                if (args is not null)
                {
                    foreach (var arg in args)
                    {
                        ctx.Args.Add(arg);
                    }
                }

                // Add JVM arguments
                if (ctx.Resource.TryGetLastAnnotation<JvmArgsAnnotation>(out var jvmArgs))
                {
                    foreach (var arg in jvmArgs.Args)
                    {
                        ctx.Args.Add(arg);
                    }
                }
            })
            .WithOtlpExporter();

        return rb;
    }

    /// <summary>
    /// Adds a Maven goal to be executed before the Java application starts.
    /// </summary>
    /// <typeparam name="T">The type of the Java application resource.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="goal">The Maven goal to execute.</param>
    [AspireExport]
    public static IResourceBuilder<T> WithMavenGoal<T>(this IResourceBuilder<T> builder, string goal)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(goal);

        var resource = new MavenBuildResource($"{builder.Resource.Name}-maven-goal", builder.Resource.WorkingDirectory, [goal]);

        var build = builder.ApplicationBuilder
            .AddResource(resource)
            .WithArgs(ctx =>
            {
                ctx.Args.Add(resource.Args);
            })
            .ExcludeFromManifest();

        builder.WaitForCompletion(build);

        return builder;
    }

    /// <summary>
    /// Adds a Gradle task to be executed before the Java application starts.
    /// </summary>
    /// <typeparam name="T">The type of the Java application resource.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="task">The Gradle task to execute (e.g., "build", "bootJar").</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport]
    public static IResourceBuilder<T> WithGradleTask<T>(this IResourceBuilder<T> builder, string task)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(task);

        var resource = new GradleBuildResource($"{builder.Resource.Name}-gradle-task", builder.Resource.WorkingDirectory, [task]);

        var build = builder.ApplicationBuilder
            .AddResource(resource)
            .WithArgs(ctx =>
            {
                ctx.Args.Add(resource.Args);
            })
            .ExcludeFromManifest();

        builder.WaitForCompletion(build);

        return builder;
    }

    /// <summary>
    /// Adds Maven build support to the Java application.
    /// </summary>
    /// <typeparam name="T">The type of the Java application resource.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="goals">The Maven goals to execute (defaults to "package").</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method configures the Java application to use Maven for building.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithMavenBuild<T>(this IResourceBuilder<T> builder, string[]? goals = null)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var args = goals ?? ["package"];
        var resource = new MavenBuildResource($"{builder.Resource.Name}-maven-build", builder.Resource.WorkingDirectory, [.. args]);

        var build = builder.ApplicationBuilder
            .AddResource(resource)
            .WithArgs(ctx =>
            {
                ctx.Args.Add(resource.Args);
            })
            .ExcludeFromManifest();

        builder.WaitForCompletion(build);

        // Store the build tool annotation
        builder.WithAnnotation(new JavaBuildToolAnnotation
        {
            BuildTool = JavaBuildTool.Maven,
            WrapperPath = "mvnw",
            Args = args
        });

        return builder;
    }

    /// <summary>
    /// Adds Gradle build support to the Java application.
    /// </summary>
    /// <typeparam name="T">The type of the Java application resource.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="tasks">The Gradle tasks to execute (defaults to "build").</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// This method configures the Java application to use Gradle for building.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithGradleBuild<T>(this IResourceBuilder<T> builder, string[]? tasks = null)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var args = tasks ?? ["build"];
        var resource = new GradleBuildResource($"{builder.Resource.Name}-gradle-build", builder.Resource.WorkingDirectory, [.. args]);

        var build = builder.ApplicationBuilder
            .AddResource(resource)
            .WithArgs(ctx =>
            {
                ctx.Args.Add(resource.Args);
            })
            .ExcludeFromManifest();

        builder.WaitForCompletion(build);

        // Store the build tool annotation
        builder.WithAnnotation(new JavaBuildToolAnnotation
        {
            BuildTool = JavaBuildTool.Gradle,
            WrapperPath = "gradlew",
            Args = args
        });

        return builder;
    }

    /// <summary>
    /// Sets the wrapper path for Maven or Gradle.
    /// </summary>
    /// <typeparam name="T">The type of the Java application resource.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="wrapperPath">The path to the wrapper script.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport]
    public static IResourceBuilder<T> WithWrapperPath<T>(this IResourceBuilder<T> builder, string wrapperPath)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(wrapperPath);

        return builder.WithAnnotation(new WrapperAnnotation { WrapperPath = wrapperPath }, ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds JVM arguments to the Java application.
    /// </summary>
    /// <typeparam name="T">The type of the Java application resource.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="args">The JVM arguments to add.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport]
    public static IResourceBuilder<T> WithJvmArgs<T>(this IResourceBuilder<T> builder, params string[] args)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithAnnotation(new JvmArgsAnnotation(args), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Configures the Java OpenTelemetry agent for the Java application.
    /// </summary>
    /// <typeparam name="T">The type of the Java application resource.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="agentPath">Optional path to the OpenTelemetry Java agent JAR file.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    [AspireExport]
    public static IResourceBuilder<T> WithOtelAgent<T>(this IResourceBuilder<T> builder, string? agentPath = null)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (agentPath is not null)
        {
            // Add javaagent argument
            return builder.WithJvmArgs($"-javaagent:{agentPath}");
        }

        // For self-instrumented apps, just ensure OTLP exporter is configured
        return builder.WithOtlpExporter();
    }
}

/// <summary>
/// Stores JVM arguments for a Java application.
/// </summary>
internal sealed class JvmArgsAnnotation(string[] args) : IResourceAnnotation
{
    /// <summary>
    /// Gets the JVM arguments.
    /// </summary>
    public string[] Args { get; } = args;
}
