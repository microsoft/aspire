// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001

#pragma warning disable ASPIRECERTIFICATES001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003
#pragma warning disable ASPIREEXTENSION001 // WithDebugSupport and WithLaunchToolArgs are experimental but used internally for debug support.

using System.Diagnostics.CodeAnalysis;
using System.Formats.Asn1;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Java;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Java applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static partial class JavaHostingExtensions
{
    private const string JavaToolOptions = "JAVA_TOOL_OPTIONS";

    // The icon Java resources and their build steps show in the dashboard, matching
    // CommunityToolkit.Aspire.Hosting.Java so migrating users see the same thing.
    private const string JavaIconName = "DrinkCoffee";

    internal static readonly string s_defaultMavenWrapper =
        JavaBuildToolResolver.GetDefaultWrapperName(JavaBuildTool.Maven, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));
    internal static readonly string s_defaultGradleWrapper =
        JavaBuildToolResolver.GetDefaultWrapperName(JavaBuildTool.Gradle, RuntimeInformation.IsOSPlatform(OSPlatform.Windows));

    /// <summary>The directory Quarkus's default packaging writes under the build tool's output directory.</summary>
    internal const string QuarkusFastJarDirectory = "quarkus-app";

    /// <summary>The runnable artifact inside <see cref="QuarkusFastJarDirectory"/>.</summary>
    internal const string QuarkusRunJarName = "quarkus-run.jar";

    /// <summary>
    /// Adds a Java application to the application model, launched with <c>java</c>.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The application directory. Relative paths are resolved against the AppHost directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="appDirectory"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">No launch mode was configured. Raised when the resource starts, not when this is called.</exception>
    /// <remarks>
    /// Combine with <see cref="WithMavenGoal{T}(IResourceBuilder{T}, string, string[])"/> or
    /// <see cref="WithGradleTask{T}(IResourceBuilder{T}, string, string[])"/> to run the application through a build tool,
    /// or use the overload that accepts a <c>jarPath</c> to run a prebuilt JAR with <c>java -jar</c>.
    /// Exactly one of those three launch modes must be configured.
    /// </remarks>
    /// <example>
    /// Run a Spring Boot application through the Maven wrapper:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddJavaApp("catalog", "../catalog")
    ///        .WithMavenGoal("spring-boot:run")
    ///        .WithHttpEndpoint(env: "SERVER_PORT")
    ///        .WithHttpHealthCheck("/actuator/health");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> AddJavaApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        // Accept both slash styles so an AppHost authored on Windows resolves the same paths on Linux.
        appDirectory = PathNormalizer.NormalizePathForCurrentPlatform(
            Path.Combine(builder.AppHostDirectory, appDirectory));

        var resource = new JavaAppResource(name, appDirectory);

        var resourceBuilder = builder.AddResource(resource)
            .WithIconName(JavaIconName)
            .WithRequiredCommand("java", "https://adoptium.net/")
            // Declared as launch tool arguments rather than through WithArgs for two reasons: they are
            // pinned ahead of any caller-supplied WithArgs no matter the order the builder methods are
            // called in, and they are omitted when an IDE launches the resource through a "java" launch
            // configuration, which starts the JVM directly instead of going through the build tool.
            .WithLaunchToolArgs(ctx => AddLaunchArgs(resource, ctx), ownedByLaunchConfigurationType: "java")
            .WithOtlpExporter()
            // Requested explicitly because the JVM's trust store setting replaces the default certificate
            // authorities instead of adding to them, so the bundle has to carry the system roots too.
            .WithCertificateTrustScope(CertificateTrustScope.System)
            .WithCertificateTrustConfiguration(JavaCertificateTrustCallback)
            .WithVSCodeDebugging()
            .PublishAsDockerFile(containerBuilder =>
            {
                // An authored Dockerfile in the application directory is the author's deployment contract.
                // Generating over it would silently discard base image pins, extra runtime packages, and
                // anything else the project depends on.
                if (File.Exists(Path.Combine(appDirectory, "Dockerfile")))
                {
                    resource.Annotations.Add(new JavaAuthoredDockerfileAnnotation());
                    return;
                }

                containerBuilder.WithDockerfileBuilder(
                    appDirectory,
                    ctx =>
                    {
                        // The build context was fixed when PublishAsDockerFile ran, which is during
                        // AddJavaApp, and DockerfileBuildAnnotation.ContextPath cannot be changed
                        // afterwards. A later WithWorkingDirectory therefore moves where the application
                        // runs without moving what is uploaded to the daemon, and the image would be built
                        // from the original directory. Saying so is far better than producing an image
                        // whose sources come from somewhere the author no longer points at.
                        if (!ArePathsEquivalent(resource.WorkingDirectory, appDirectory))
                        {
                            throw new DistributedApplicationException(
                                $"Java application '{resource.Name}' cannot be published because its working " +
                                $"directory was changed to '{resource.WorkingDirectory}' after the container " +
                                $"build context was set to '{appDirectory}'. Pass the directory to " +
                                "AddJavaApp instead of calling WithWorkingDirectory afterwards.");
                        }

                        JavaDockerfileGenerator.Write(resource, appDirectory, ctx);
                    });
            });

        // The generated image copies files out of each container files source, so those sources have to be
        // built first. PublishAsDockerFile removes the Java resource from the model, but the container it
        // substitutes shares this annotation collection, so the callback still runs; the step lookup matches
        // on resource name and therefore finds the substituted container's build steps.
        resourceBuilder.WithPipelineConfiguration(context =>
        {
            if (resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(out var containerFilesAnnotations))
            {
                var buildSteps = context.GetSteps(resource, WellKnownPipelineTags.BuildCompute);
                foreach (var containerFile in containerFilesAnnotations)
                {
                    buildSteps.DependsOn(context.GetSteps(containerFile.Source, WellKnownPipelineTags.BuildCompute));
                }
            }
        });

        return resourceBuilder;
    }

    /// <summary>
    /// Adds a Java application that runs a prebuilt JAR with <c>java -jar</c>.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The application directory. Relative paths are resolved against the AppHost directory.</param>
    /// <param name="jarPath">The path to the JAR file to execute. Relative paths are resolved against <paramref name="appDirectory"/>.</param>
    /// <param name="args">Arguments passed to the Java application after the JAR path.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="args"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/>, <paramref name="appDirectory"/>, or <paramref name="jarPath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <example>
    /// Build the JAR with Maven, then run it:
    /// <code language="csharp">
    /// builder.AddJavaApp("worker", "../worker", "target/worker.jar")
    ///        .WithMavenBuild();
    /// </code>
    /// </example>
    [AspireExport("addJavaAppWithJar")]
    public static IResourceBuilder<JavaAppResource> AddJavaApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string appDirectory,
        string jarPath,
        params string[] args)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);
        ArgumentNullException.ThrowIfNull(args);

        var rb = builder.AddJavaApp(name, appDirectory);

        rb.WithAnnotation(
            // Keep the authored text in the model so diagnostics can quote the path the user recognizes.
            // Run and publish normalize only when crossing into their execution environment instead of
            // baking the AppHost's path semantics into a value that also targets a Linux container.
            new JavaJarPathAnnotation(jarPath),
            ResourceAnnotationMutationBehavior.Replace);

        if (args.Length > 0)
        {
            rb.WithArgs(args);
        }

        return rb;
    }

    /// <summary>
    /// Adds a Java application that runs from an existing container image.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="image">The container image that runs the application, for example <c>mycompany/catalog</c>.</param>
    /// <param name="tag">The image tag. Defaults to the image's <c>latest</c> tag.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="image"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <remarks>
    /// Use this when the image is built elsewhere — by a separate CI pipeline, or by a team that ships the
    /// application as a container. Aspire runs the image as-is and never rebuilds it, so the JAR, the JDK,
    /// and any OpenTelemetry agent all come from the image. Use
    /// <see cref="AddJavaApp(IDistributedApplicationBuilder, string, string)"/> instead when Aspire should
    /// build and run the application from source.
    /// <para>
    /// No endpoint is declared, because the port the image listens on is a property of the image. Add one
    /// with <c>WithHttpEndpoint(targetPort: 8080)</c>, using whichever port the application binds — 8080
    /// for a default Spring Boot or Quarkus image.
    /// </para>
    /// </remarks>
    /// <example>
    /// Run a published Spring Boot image and give it a database:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var db = builder.AddPostgres("pg").AddDatabase("catalogdb");
    ///
    /// builder.AddJavaContainer("catalog", "mycompany/catalog", "1.4.0")
    ///        .WithHttpEndpoint(targetPort: 8080)
    ///        .WithReference(db)
    ///        .WithJvmArgs("-Xmx512m");
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<JavaContainerResource> AddJavaContainer(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string image,
        string? tag = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        var resource = new JavaContainerResource(name);

        return builder.AddResource(resource)
            .WithImage(image, tag)
            .WithIconName(JavaIconName)
            .WithOtlpExporter()
            // Requested explicitly because the JVM's trust store setting replaces the default certificate
            // authorities instead of adding to them, so the bundle has to carry the system roots too.
            .WithCertificateTrustScope(CertificateTrustScope.System)
            .WithCertificateTrustConfiguration(JavaCertificateTrustCallback);
    }

    /// <summary>
    /// Adds a Spring Boot application to the application model, built and launched with its own Maven or Gradle wrapper.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The application directory, containing <c>pom.xml</c>, <c>build.gradle</c>, <c>build.gradle.kts</c>, <c>settings.gradle</c>, or <c>settings.gradle.kts</c>. Relative paths are resolved against the AppHost directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="appDirectory"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The directory contains neither a Maven nor a Gradle build file, or contains both.</exception>
    /// <remarks>
    /// This is <see cref="AddJavaApp(IDistributedApplicationBuilder, string, string)"/> with the common
    /// Spring Boot configuration already applied: the build tool is detected when the resource starts, the
    /// application is launched through that tool's Spring Boot plugin (<c>spring-boot:run</c> or <c>bootRun</c>),
    /// and an HTTP endpoint is declared through <c>SERVER_PORT</c>, which is the environment
    /// variable Spring Boot reads for its listening port. Everything else is the same, so any <c>With…</c> method
    /// that works on <see cref="AddJavaApp(IDistributedApplicationBuilder, string, string)"/> works here too.
    /// <para>
    /// The launch goal compiles the application itself, so Aspire does not add a separate build resource before
    /// it. Publishing still packages the application with tests skipped (<c>-DskipTests</c> for Maven,
    /// <c>-x test</c> for Gradle). Call <see cref="WithMavenBuild{T}(IResourceBuilder{T}, string[])"/> or
    /// <see cref="WithGradleBuild{T}(IResourceBuilder{T}, string[])"/> afterwards to customize those package arguments.
    /// The one thing that does add a build resource is
    /// <see cref="WithOtelAgent{T}(IResourceBuilder{T})"/> with a build-produced agent, which cannot be
    /// loaded until a build has written it.
    /// </para>
    /// <para>
    /// No health check is added. <c>/actuator/health</c> only exists when the application depends on
    /// <c>spring-boot-starter-actuator</c>, and adding it unconditionally would leave applications without that
    /// dependency permanently unhealthy and silently stall every <c>WaitFor</c> on them. Add
    /// <c>WithHttpHealthCheck("/actuator/health")</c> when the actuator is present.
    /// </para>
    /// </remarks>
    /// <example>
    /// Two Spring Boot services and a database:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var db = builder.AddPostgres("pg").AddDatabase("catalogdb");
    ///
    /// var catalog = builder.AddSpringBootApp("catalog", "../catalog")
    ///                      .WithReference(db);
    ///
    /// builder.AddSpringBootApp("orders", "../orders")
    ///        .WithReference(catalog)
    ///        .WaitFor(catalog);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> AddSpringBootApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        var resourceBuilder = builder.AddJavaApp(name, appDirectory)
            .WithDetectedBuildTool(
                mavenBuildArgs: ["-B", "-ntp", "-DskipTests", "package"],
                mavenLaunchArgs: ["spring-boot:run"],
                gradleBuildArgs: ["build", "-x", "test"],
                gradleLaunchArgs: ["bootRun"]);

        // Spring Boot reads SERVER_PORT for its listening port, so the port Aspire allocates reaches the
        // application without any code in the application. No targetPort is pinned: these are host
        // processes rather than containers, so a fixed target port is a real port on the machine and two
        // Spring Boot services both asking for 8080 would collide.
        return resourceBuilder.WithHttpEndpoint(env: "SERVER_PORT");
    }

    /// <summary>
    /// Adds a Quarkus application to the application model, built and launched with its own Maven or Gradle wrapper.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The application directory, containing <c>pom.xml</c>, <c>build.gradle</c>, <c>build.gradle.kts</c>, <c>settings.gradle</c>, or <c>settings.gradle.kts</c>. Relative paths are resolved against the AppHost directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="appDirectory"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The directory contains neither a Maven nor a Gradle build file, or contains both.</exception>
    /// <remarks>
    /// The build tool is detected when the resource starts, the application runs in Quarkus dev mode
    /// (<c>quarkus:dev</c> or <c>quarkusDev</c>) so live coding works, and an HTTP endpoint is declared through
    /// <c>QUARKUS_HTTP_PORT</c>, the environment variable Quarkus reads for its listening port. Everything else
    /// behaves like <see cref="AddJavaApp(IDistributedApplicationBuilder, string, string)"/>.
    /// The dev-mode goal compiles the application itself, so Aspire does not add a separate build resource before it.
    /// There are two exceptions. <see cref="WithOtelAgent{T}(IResourceBuilder{T})"/> with a build-produced agent
    /// cannot be loaded until a build has written it, and a debug session launches the packaged fast JAR rather
    /// than the dev-mode wrapper, which no build has written on a clean checkout.
    /// <para>
    /// Quarkus Dev Services are left enabled but do not activate for anything Aspire supplies: Dev Services only
    /// start a container when the corresponding configuration is missing, and a <c>WithReference</c> to a database
    /// or broker provides it. That means Aspire's resources are used rather than a second set started underneath.
    /// </para>
    /// <para>
    /// <c>QUARKUS_PROFILE</c> is set to <c>dev</c> in run mode. Dev mode already selects that profile; setting it
    /// explicitly means a debugger, which launches the packaged application rather than the dev-mode wrapper,
    /// resolves the same <c>%dev.</c> configuration the application would see when run normally.
    /// </para>
    /// <para>
    /// In run mode the application is bound to all interfaces. Quarkus enables Host header validation
    /// whenever it binds a localhost name, and that filter rejects the hostname Aspire publishes, which
    /// makes the endpoint link in the dashboard return <c>400</c>. Published output is left alone, where
    /// the application already binds all interfaces.
    /// </para>
    /// <para>
    /// No health check is added. <c>/q/health</c> only exists when the application depends on
    /// <c>quarkus-smallrye-health</c>, and adding it unconditionally would leave applications without that
    /// extension permanently unhealthy and silently stall every <c>WaitFor</c> on them. Add
    /// <c>WithHttpHealthCheck("/q/health")</c> when the extension is present.
    /// </para>
    /// </remarks>
    /// <example>
    /// A Quarkus service backed by a database Aspire provides:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// var db = builder.AddPostgres("pg").AddDatabase("inventorydb");
    ///
    /// builder.AddQuarkusApp("inventory", "../inventory")
    ///        .WithReference(db)
    ///        .WaitFor(db);
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<JavaAppResource> AddQuarkusApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        var resourceBuilder = builder.AddJavaApp(name, appDirectory)
            .WithAnnotation(new JavaQuarkusAnnotation(), ResourceAnnotationMutationBehavior.Replace)
            .WithDetectedBuildTool(
                mavenBuildArgs: ["-B", "-ntp", "-DskipTests", "package"],
                mavenLaunchArgs: ["quarkus:dev"],
                gradleBuildArgs: ["build", "-x", "test"],
                gradleLaunchArgs: ["quarkusDev"]);

        // Called after the Quarkus and build-tool annotations are in place, because both decide whether a
        // build has to run before the IDE launches the fast JAR it will attach to.
        EnsureBuildRunsBeforeLaunch(resourceBuilder);

        // Declared before the run-mode block so the Host validation configuration below can name this
        // endpoint's host. QUARKUS_HTTP_PORT is the variable Quarkus reads for its listening port.
        resourceBuilder = resourceBuilder.WithHttpEndpoint(env: "QUARKUS_HTTP_PORT");

        if (builder.ExecutionContext.IsRunMode)
        {
            resourceBuilder.WithEnvironment("QUARKUS_PROFILE", "dev");

            // Quarkus turns on its Host header validation filter whenever quarkus.http.host holds a
            // localhost name, which is the dev-mode default. The filter compares only the host portion of
            // the request authority, lowercased, against an exact set, so it rejects the
            // "<resource>.dev.localhost" hostname Aspire publishes with a bare 400 and no body: the
            // endpoint link shown in the dashboard fails while the same request to 127.0.0.1 succeeds.
            // See io.quarkus.vertx.http.runtime.HostValidationFilter.
            //
            // Binding all interfaces is what suppresses the filter, because it only auto-enables for a
            // localhost bind. The targeted alternative, naming the hostname in
            // quarkus.http.host-validation.allowed-hosts, cannot be delivered: the Quarkus Gradle plugin
            // re-exports QUARKUS_* environment variables to the dev JVM as system properties by replacing
            // every underscore with a dot, so a property whose name contains a dash arrives as
            // "quarkus.http.host.validation.allowed.hosts" and is silently ignored. Aspire also has no
            // access to the published hostname here - the endpoint's Host resolves to the bind address.
            //
            // Only run mode is configured, and only ever on a developer machine. A published container
            // already binds all interfaces by default, so nothing needs to be said about it there.
            resourceBuilder.WithEnvironment("QUARKUS_HTTP_HOST", "0.0.0.0");

            // Quarkus dev mode starts an "observability" Dev Service when an application depends on
            // quarkus-opentelemetry and no exporter endpoint is configured in the application itself. That
            // Dev Service pulls grafana/otel-lgtm (roughly 600 MB), starts it through Testcontainers, and
            // then *overrides* the exporter configuration to point at the container it just started:
            //
            //   Dev Service Lgtm started, config: {quarkus.otel.exporter.otlp.endpoint=http://localhost:51845, ...}
            //
            // Aspire is already the observability stack here, so that override sends every span and metric
            // somewhere the Aspire dashboard cannot see, leaves an orphaned container behind, and costs a
            // large image pull on first run. Turning the Dev Service off lets the exporter configuration
            // below win, which is what an Aspire user expects.
            // See https://quarkus.io/guides/observability-devservices-lgtm.
            resourceBuilder.WithEnvironment("QUARKUS_OBSERVABILITY_ENABLED", "false");
        }

        // The quarkus-opentelemetry extension does not read the standard OTEL_* environment variables that
        // Aspire sets. It reads its own quarkus.otel.* configuration, so an application with the extension
        // compiled in silently keeps its default endpoint and fails every export:
        //
        //   WARNING [io.quarkus.opentelemetry.runtime.exporter.otlp.sender.VertxGrpcSender]
        //     Failed to export . The request could not be executed.
        //     Full error message: Connection refused: localhost/127.0.0.1:4317
        //
        // SmallRye Config maps QUARKUS_OTEL_EXPORTER_OTLP_ENDPOINT onto quarkus.otel.exporter.otlp.endpoint,
        // so mirroring the values Aspire already resolved is enough to point the extension at the dashboard.
        // See https://quarkus.io/guides/opentelemetry-tracing#create-the-configuration.
        //
        // The callback runs after the one WithOtlpExporter installed in AddJavaApp, so the OTEL_* entries are
        // already present and carry the resolved endpoint reference rather than a literal. An application that
        // does not use the extension ignores these, at the cost of a "unrecognized configuration key" warning.
        //
        // This covers `aspire run`. It cannot cover a published image, because there the compute environment
        // supplies OTEL_* from a callback it appends while preparing the deployment target — after every
        // callback the AppHost registered — so there is nothing here to copy. A SmallRye config expression
        // would sidestep the ordering, but it cannot be passed as an environment variable: Docker Compose
        // interpolates '${...}' in its own file and rejects SmallRye's '${VAR:default}' form outright
        // ("invalid interpolation format"). A deployed application therefore maps the value in its own
        // application.properties, which the README documents and both playgrounds do.
        resourceBuilder.WithEnvironment(context =>
        {
            MirrorOtelVariable(context, KnownOtelConfigNames.ExporterOtlpEndpoint, "QUARKUS_OTEL_EXPORTER_OTLP_ENDPOINT");
            MirrorOtelVariable(context, KnownOtelConfigNames.ExporterOtlpProtocol, "QUARKUS_OTEL_EXPORTER_OTLP_PROTOCOL");
            MirrorOtelVariable(context, KnownOtelConfigNames.ExporterOtlpHeaders, "QUARKUS_OTEL_EXPORTER_OTLP_HEADERS");
            MirrorOtelVariable(context, KnownOtelConfigNames.ResourceAttributes, "QUARKUS_OTEL_RESOURCE_ATTRIBUTES");
            MirrorOtelVariable(context, KnownOtelConfigNames.ServiceName, "QUARKUS_OTEL_SERVICE_NAME");
            MirrorOtelVariable(context, KnownOtelConfigNames.BspScheduleDelay, "QUARKUS_OTEL_BSP_SCHEDULE_DELAY");
            MirrorOtelVariable(context, KnownOtelConfigNames.BlrpScheduleDelay, "QUARKUS_OTEL_BLRP_SCHEDULE_DELAY");
            MirrorOtelVariable(context, KnownOtelConfigNames.MetricExportInterval, "QUARKUS_OTEL_METRIC_EXPORT_INTERVAL");
            MirrorOtelVariable(context, KnownOtelConfigNames.TracesSampler, "QUARKUS_OTEL_TRACES_SAMPLER");
        });

        return resourceBuilder;
    }

    /// <summary>
    /// Copies an OpenTelemetry environment variable Aspire already resolved to the name Quarkus reads it under.
    /// </summary>
    /// <remarks>
    /// The value is copied by reference rather than converted to a string: several of these are endpoint
    /// references or DCP templates that only resolve once the resource starts.
    /// </remarks>
    private static void MirrorOtelVariable(EnvironmentCallbackContext context, string standardName, string quarkusName)
    {
        if (context.EnvironmentVariables.TryGetValue(standardName, out var value))
        {
            context.EnvironmentVariables[quarkusName] = value;
        }
    }

    /// <summary>
    /// Requires an application directory to declare a Maven or Gradle project.
    /// </summary>
    /// <remarks>
    /// Detection is shared with publishing so the same project files cannot select different tools in each path.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The directory has no build file, or has both Maven and Gradle files.</exception>
    private static JavaBuildTool RequireBuildTool(string workingDirectory, string resourceName)
    {
        if (JavaBuildToolResolver.Detect(
                workingDirectory,
                resourceName,
                static message => new InvalidOperationException(message)) is not { } tool)
        {
            throw new InvalidOperationException(
                $"Directory '{workingDirectory}' contains no pom.xml, build.gradle, build.gradle.kts, settings.gradle, or settings.gradle.kts, " +
                $"so the build tool for resource '{resourceName}' cannot be detected. " +
                $"Check the path, or use AddJavaApp for an application laid out differently.");
        }

        return tool;
    }

    private static IResourceBuilder<T> WithDetectedBuildTool<T>(
        this IResourceBuilder<T> builder,
        string[] mavenBuildArgs,
        string[] mavenLaunchArgs,
        string[] gradleBuildArgs,
        string[] gradleLaunchArgs)
        where T : JavaAppResource
    {
        builder.WithAnnotation(
            new JavaDetectedBuildToolAnnotation(
                mavenBuildArgs,
                mavenLaunchArgs,
                gradleBuildArgs,
                gradleLaunchArgs),
            ResourceAnnotationMutationBehavior.Replace);

        // Maven and Gradle both launch through the platform's command interpreter. The wrapper and its
        // arguments are resolved later, after the complete AppHost has had a chance to supply an override.
        builder.WithCommand(WrapperCommand());

        return builder.OnBeforeResourceStarted((resource, _, _) =>
        {
            var (tool, configuration) = ResolveDetectedBuildTool(resource);

            // Explicit WithMaven*/WithGradle* calls identify the tool without disk detection and must
            // keep their authored arguments. The deferred defaults only fill the missing half.
            if (!resource.HasAnnotationOfType<JavaBuildToolAnnotation>())
            {
                builder.WithAnnotation(
                    new JavaBuildToolAnnotation(tool, configuration.LaunchArgs),
                    ResourceAnnotationMutationBehavior.Replace);
            }

            if (!resource.HasAnnotationOfType<JavaBuildStepAnnotation>())
            {
                builder.WithAnnotation(
                    new JavaBuildStepAnnotation(ResourceName: null, tool, configuration.BuildArgs),
                    ResourceAnnotationMutationBehavior.Replace);
            }

            ValidateWrapperExists(resource, tool);
            return Task.CompletedTask;
        });
    }

    private static (JavaBuildTool Tool, (string[] BuildArgs, string[] LaunchArgs) Configuration) ResolveDetectedBuildTool(
        JavaAppResource resource)
    {
        var annotation = resource.Annotations.OfType<JavaDetectedBuildToolAnnotation>().Single();
        var tool = TryResolveConfiguredBuildTool(resource, out var configuredTool)
            ? configuredTool
            : RequireBuildTool(resource.WorkingDirectory, resource.Name);

        return (tool, annotation.GetConfiguration(tool));
    }

    private static bool TryResolveConfiguredBuildTool(JavaAppResource resource, out JavaBuildTool tool)
    {
        if (resource.TryGetLastAnnotation<JavaBuildStepAnnotation>(out var buildStep))
        {
            tool = buildStep.Tool;
            return true;
        }

        if (resource.TryGetLastAnnotation<JavaBuildToolAnnotation>(out var launch))
        {
            tool = launch.Tool;
            return true;
        }

        if (resource.HasAnnotationOfType<JavaDetectedBuildToolAnnotation>())
        {
            tool = RequireBuildTool(resource.WorkingDirectory, resource.Name);
            return true;
        }

        tool = default;
        return false;
    }

    /// <summary>
    /// Launches the Java application through a Maven goal instead of <c>java</c>, for example <c>spring-boot:run</c>.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="goal">The Maven goal to execute.</param>
    /// <param name="args">Additional arguments passed to the Maven wrapper after the goal.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="args"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="goal"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The application is already configured to run a prebuilt JAR or a Gradle task.</exception>
    /// <remarks>
    /// The wrapper defaults to <c>mvnw</c> (<c>mvnw.cmd</c> on Windows) in the resource's working directory
    /// and can be overridden with <see cref="WithWrapperPath{T}(IResourceBuilder{T}, string)"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithMavenGoal<T>(
        this IResourceBuilder<T> builder,
        string goal,
        params string[] args) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(goal);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithBuildToolLaunch(JavaBuildTool.Maven, goal, args, nameof(WithMavenGoal));
    }

    /// <summary>
    /// Launches the Java application through a Gradle task instead of <c>java</c>, for example <c>bootRun</c>.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="task">The Gradle task to execute.</param>
    /// <param name="args">Additional arguments passed to the Gradle wrapper after the task.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="args"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="task"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The application is already configured to run a prebuilt JAR or a Maven goal.</exception>
    /// <remarks>
    /// The wrapper defaults to <c>gradlew</c> (<c>gradlew.bat</c> on Windows) in the resource's working
    /// directory and can be overridden with <see cref="WithWrapperPath{T}(IResourceBuilder{T}, string)"/>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithGradleTask<T>(
        this IResourceBuilder<T> builder,
        string task,
        params string[] args) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithBuildToolLaunch(JavaBuildTool.Gradle, task, args, nameof(WithGradleTask));
    }

    private static IResourceBuilder<T> WithBuildToolLaunch<T>(
        this IResourceBuilder<T> builder,
        JavaBuildTool tool,
        string goalOrTask,
        string[] args,
        string methodName) where T : JavaAppResource
    {
        // A prebuilt JAR and a build-tool launch are mutually exclusive: the build tool decides what to
        // run, so -jar would be ignored. CommunityToolkit rejects both combinations; this port only
        // rejected the Gradle half, letting a Maven+JAR application silently drop its JAR.
        if (builder.Resource.HasAnnotationOfType<JavaJarPathAnnotation>())
        {
            throw new InvalidOperationException(
                $"{methodName} cannot be used when a JAR path has been specified. Use either the " +
                $"{nameof(AddJavaApp)} overload that takes a jarPath, or {methodName}, not both.");
        }

        if (builder.Resource.TryGetLastAnnotation<JavaBuildToolAnnotation>(out var existing) && existing.Tool != tool)
        {
            throw new InvalidOperationException(
                $"{methodName} cannot be used when the application is already configured to launch with " +
                $"{existing.Tool}. A Java application is launched by a single build tool.");
        }

        if (builder.Resource.TryGetLastAnnotation<JavaBuildStepAnnotation>(out var buildStep) && buildStep.Tool != tool)
        {
            throw new InvalidOperationException(
                $"{methodName} cannot be used when the application is already configured to build with " +
                $"{buildStep.Tool}. A Java application is built and launched by a single build tool.");
        }

        builder.WithAnnotation(
            new JavaBuildToolAnnotation(tool, args.Length > 0 ? [goalOrTask, .. args] : [goalOrTask]),
            ResourceAnnotationMutationBehavior.Replace);

        // The launch goal now compiles the application, so the build resource is redundant — unless
        // something outside the launch goal needs the build's output before the application starts.
        if (buildStep is not null
            && builder.ApplicationBuilder.ExecutionContext.IsRunMode
            && !RequiresBuildBeforeLaunch(builder))
        {
            RemoveRunBuildResource(builder, buildStep);
        }

        // Set the command in every execution context. Setting it only in run mode left publish emitting
        // "java" as the command while the goal was still contributed as an argument, producing the
        // uninvokable command line "java spring-boot:run".
        return builder
            .WithCommand(ResolveWrapperInvocation(builder.Resource, tool).Command)
            .WithDeferredWrapperValidation(tool);
    }

    /// <summary>
    /// Runs a Maven build before the Java application starts.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="args">Arguments passed to the Maven wrapper. Defaults to <c>clean package</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="args"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application is already configured to build with Gradle.</exception>
    /// <remarks>
    /// When the application launches with <see cref="WithMavenGoal{T}(IResourceBuilder{T}, string, string[])"/>,
    /// that goal performs the local compilation, so these arguments normally configure publishing without adding a
    /// second run-mode build. Otherwise, the build step is a child resource that the application waits for.
    /// No child is created when publishing because the generated container image performs the build.
    /// <para>
    /// This runs a build before the application starts. To launch the application <em>through</em> Maven,
    /// use <see cref="WithMavenGoal{T}(IResourceBuilder{T}, string, string[])"/> instead.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithMavenBuild<T>(
        this IResourceBuilder<T> builder,
        params string[] args) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithJavaBuildStep(
            JavaBuildTool.Maven,
            buildResourceName: $"{builder.Resource.Name}-maven-build",
            buildArgs: args.Length > 0 ? args : ["clean", "package"]);
    }

    /// <summary>
    /// Runs a Gradle build before the Java application starts.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="args">Arguments passed to the Gradle wrapper. Defaults to <c>clean build</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="args"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The application is already configured to build with Maven.</exception>
    /// <remarks>
    /// When the application launches with <see cref="WithGradleTask{T}(IResourceBuilder{T}, string, string[])"/>,
    /// that task performs the local compilation, so these arguments normally configure publishing without adding a
    /// second run-mode build. Otherwise, the build step is a child resource that the application waits for.
    /// No child is created when publishing because the generated container image performs the build.
    /// <para>
    /// This runs a build before the application starts. To launch the application <em>through</em> Gradle,
    /// use <see cref="WithGradleTask{T}(IResourceBuilder{T}, string, string[])"/> instead.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithGradleBuild<T>(
        this IResourceBuilder<T> builder,
        params string[] args) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithJavaBuildStep(
            JavaBuildTool.Gradle,
            buildResourceName: $"{builder.Resource.Name}-gradle-build",
            buildArgs: args.Length > 0 ? args : ["clean", "build"]);
    }

    private static IResourceBuilder<T> WithJavaBuildStep<T>(
        this IResourceBuilder<T> builder,
        JavaBuildTool tool,
        string buildResourceName,
        string[] buildArgs)
        where T : JavaAppResource
    {
        // Building with both tools would produce two artifacts and leave the container build with no way
        // to choose between them, so it is rejected the same way conflicting launch modes are.
        if (builder.Resource.TryGetLastAnnotation<JavaBuildStepAnnotation>(out var existing) && existing.Tool != tool)
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' is already configured to build with {existing.Tool}. " +
                $"Call either WithMavenBuild or WithGradleBuild, not both.");
        }

        if (builder.Resource.TryGetLastAnnotation<JavaBuildToolAnnotation>(out var launchTool) && launchTool.Tool != tool)
        {
            throw new InvalidOperationException(
                $"Resource '{builder.Resource.Name}' is already configured to launch with {launchTool.Tool}. " +
                "A Java application is built and launched by a single build tool.");
        }

        // A launch goal such as spring-boot:run or bootRun compiles the application on its way to running
        // it, so a build resource in front of it would only repeat work. That holds only while the launch
        // goal is what actually starts the application and nothing else needs the build's output first.
        var createRunResource = builder.ApplicationBuilder.ExecutionContext.IsRunMode
            && (RequiresBuildBeforeLaunch(builder)
                || launchTool is null && !builder.Resource.HasAnnotationOfType<JavaDetectedBuildToolAnnotation>());

        // Recorded in every execution context: in publish mode there is no build-step resource, but the
        // generated Dockerfile still runs this tool and these arguments to produce the deployable JAR.
        builder.WithAnnotation(
            new JavaBuildStepAnnotation(
                createRunResource ? buildResourceName : null,
                tool,
                buildArgs),
            ResourceAnnotationMutationBehavior.Replace);

        if (!createRunResource)
        {
            return builder;
        }
        // Calling the same method twice must not add a second resource under the same name. The
        // annotation was just replaced, and the arguments are read from it on every run, so the existing
        // resource already reflects the new arguments.
        //
        // The check is on ResourceName rather than on the annotation, because an earlier call can record
        // a build step without creating a resource: createRunResource is false when the launch goal
        // already compiles the application. If something later makes the build mandatory - a relative
        // WithOtelAgent path, for instance, whose agent JAR the build has to produce before launch -
        // this call is the one that has to create it. Returning early on the annotation alone left the
        // new annotation naming a resource that was never added, and the application then failed at
        // startup loading an agent that nothing had built.
        if (existing?.ResourceName is not null)
        {
            return builder;
        }

        var resource = builder.Resource;
        var wrapperInvocation = ResolveWrapperInvocation(resource, tool);
        var buildResource = new JavaBuildResource(buildResourceName, wrapperInvocation.Command, resource.WorkingDirectory, tool);

        var buildBuilder = builder.ApplicationBuilder.AddResource(buildResource)
            .WithArgs(ctx =>
            {
                // Resolved on every evaluation rather than captured, because WithWrapperPath can replace
                // the wrapper after this resource exists and the leading argument has to follow it.
                foreach (var leadingArg in ResolveWrapperInvocation(resource, tool).LeadingArgs)
                {
                    ctx.Args.Add(leadingArg);
                }

                if (resource.TryGetLastAnnotation<JavaBuildStepAnnotation>(out var buildStep))
                {
                    foreach (var arg in buildStep.Args)
                    {
                        ctx.Args.Add(arg);
                    }
                }
            })
            .WithIconName(JavaIconName)
            .WithParentRelationship(resource)
            .ExcludeFromManifest()
            // The build step runs before the application, so without this a missing wrapper would first
            // surface as this resource failing to exec, rather than as the actionable message.
            .OnBeforeResourceStarted((_, _, _) =>
            {
                ValidateWrapperExists(resource, tool);
                return Task.CompletedTask;
            });

        return builder.WaitForCompletion(buildBuilder);
    }

    private static void RemoveRunBuildResource<T>(
        IResourceBuilder<T> builder,
        JavaBuildStepAnnotation buildStep)
        where T : JavaAppResource
    {
        if (buildStep.ResourceName is not { } buildResourceName)
        {
            return;
        }

        var buildResource = builder.ApplicationBuilder.Resources
            .OfType<ExecutableResource>()
            .FirstOrDefault(resource => string.Equals(resource.Name, buildResourceName, StringComparisons.ResourceName));

        if (buildResource is null)
        {
            return;
        }

        // The child and both dependency annotations were added as one unit by WithJavaBuildStep. Removing
        // all three prevents a launch goal configured later from leaving a dangling wait on a resource
        // that no longer runs.
        builder.ApplicationBuilder.Resources.Remove(buildResource);

        foreach (var annotation in builder.Resource.Annotations
            .Where(annotation =>
                annotation is WaitAnnotation wait && ReferenceEquals(wait.Resource, buildResource)
                || annotation is ResourceRelationshipAnnotation relationship
                    && ReferenceEquals(relationship.Resource, buildResource))
            .ToArray())
        {
            builder.Resource.Annotations.Remove(annotation);
        }

        builder.WithAnnotation(
            buildStep with { ResourceName = null },
            ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Overrides the build tool wrapper script path, for repositories whose wrapper is not in the
    /// default location or does not use the default name.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The <see cref="IResourceBuilder{T}"/> to configure.</param>
    /// <param name="wrapperPath">The path to the wrapper script, absolute or relative to the resource's working directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="wrapperPath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <remarks>
    /// May be called before or after the build tool is configured. A later call re-points anything that
    /// already resolved the default wrapper, so the result does not depend on the order of builder calls.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithWrapperPath<T>(
        this IResourceBuilder<T> builder,
        string wrapperPath) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(wrapperPath);

        var resolvedWrapperPath = PathNormalizer.NormalizePathForCurrentPlatform(
            Path.Combine(builder.Resource.WorkingDirectory, wrapperPath));

        builder.WithAnnotation(new WrapperAnnotation(resolvedWrapperPath), ResourceAnnotationMutationBehavior.Replace);

        // Re-point anything that already captured the default wrapper. Without this, calling
        // WithWrapperPath after WithMavenGoal was silently ignored.
        if (builder.Resource.TryGetLastAnnotation<JavaBuildToolAnnotation>(out var buildTool))
        {
            builder.WithCommand(ResolveWrapperInvocation(builder.Resource, buildTool.Tool).Command);
        }

        foreach (var buildStep in builder.Resource.Annotations.OfType<JavaBuildStepAnnotation>())
        {
            // Null outside run mode, where no build-step resource is created.
            if (buildStep.ResourceName is not { } buildStepName)
            {
                continue;
            }

            var buildResource = builder.ApplicationBuilder.Resources
                .OfType<ExecutableResource>()
                .FirstOrDefault(r => string.Equals(r.Name, buildStepName, StringComparisons.ResourceName));

            if (buildResource is not null)
            {
                builder.ApplicationBuilder.CreateResourceBuilder(buildResource)
                    .WithCommand(ResolveWrapperInvocation(builder.Resource, buildStep.Tool).Command);
            }
        }

        return builder;
    }

    /// <summary>
    /// Sets the main class an IDE launches when running or debugging this application. Has no effect on
    /// how Aspire starts the process, which is decided by the JAR path, Maven goal, or Gradle task.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="mainClass">The fully qualified name of the class declaring <c>main</c>, for example <c>com.example.Application</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="mainClass"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <remarks>
    /// Only affects IDE execution. When omitted, the IDE resolves the main class from the project's build
    /// files; set it explicitly when a project declares more than one class with a <c>main</c> method.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithMainClass<T>(
        this IResourceBuilder<T> builder,
        string mainClass) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(mainClass);

        return builder.WithAnnotation(new JavaMainClassAnnotation(mainClass), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Selects the JAR the generated container image runs, for projects whose build produces more than one.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder for the Java application.</param>
    /// <param name="jarPath">The path to the JAR produced by the build, relative to the application directory, for example <c>target/app.jar</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="jarPath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <remarks>
    /// Only affects publishing, and only when the application is built in the image. Without it the
    /// container build selects the single JAR that is not a <c>-plain</c>, <c>-sources</c>, or
    /// <c>-javadoc</c> artifact, and fails the build if that is ambiguous.
    /// <para>
    /// This takes precedence over the JAR named by the <c>jarPath</c> overload of
    /// <see cref="AddJavaApp(IDistributedApplicationBuilder, string, string, string, string[])"/>, so a
    /// resource can run one JAR locally and publish another. It has no effect on an application published
    /// from a prebuilt JAR, because nothing is built in the image for it to select from.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithJarArtifact<T>(
        this IResourceBuilder<T> builder,
        string jarPath) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(jarPath);

        return builder.WithAnnotation(new JavaJarArtifactAnnotation(jarPath), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Adds arguments to the Java Virtual Machine that runs the application.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="args">The JVM arguments, for example <c>-Xmx512m</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="args"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Arguments are passed through the <c>JAVA_TOOL_OPTIONS</c> environment variable, which the JVM reads
    /// however it was started — <c>java -jar</c>, a Maven goal, a Gradle task, or a container image's own
    /// entrypoint, including the JVM those build tools fork. Values containing spaces are quoted, because
    /// the JVM splits this variable on whitespace.
    /// <para>
    /// This is also how a container image that already carries the OpenTelemetry Java agent turns it on,
    /// since <see cref="WithOtelAgent{T}(IResourceBuilder{T}, string)"/> copies an agent from the build
    /// context and so applies only to applications Aspire itself launches or builds:
    /// <c>WithJvmArgs("-javaagent:/app/opentelemetry-javaagent.jar")</c>.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithJvmArgs<T>(
        this IResourceBuilder<T> builder,
        params string[] args) where T : IJavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            return builder;
        }

        return builder.WithEnvironment(context =>
        {
            // Keep JAVA_TOOL_OPTIONS as the single JVM-argument source even when an IDE owns the launch.
            // DCP passes the resource environment to that JVM, so also emitting vmArgs would apply
            // single-instance options such as -javaagent twice and can double-instrument the application.
            AppendJavaToolOptions(context.EnvironmentVariables, args);
        });
    }

    /// <summary>
    /// Runs the application with the OpenTelemetry Java agent from the location the build tool writes it to.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The resource has no Maven or Gradle build configured, so the agent location cannot be inferred. Raised when the application model is built, not when this is called.</exception>
    /// <remarks>
    /// The agent is expected at <c>target/agent/opentelemetry-javaagent.jar</c> for Maven and
    /// <c>build/agent/opentelemetry-javaagent.jar</c> for Gradle — the conventional output directory of each tool
    /// with an <c>agent</c> subdirectory. The build has to put it there; nothing is downloaded. With Maven, copy it
    /// with <c>maven-dependency-plugin</c>'s <c>copy</c> goal bound to <c>process-resources</c>; with Gradle,
    /// declare the agent in its own configuration and add a <c>Copy</c> task that <c>compileJava</c> depends on.
    /// <para>
    /// May be called before or after the build tool is configured: which directory the agent is read from is
    /// decided when the application model is built, so the result does not depend on the order of builder calls.
    /// </para>
    /// <para>
    /// Because the build writes the agent, Aspire runs that build as its own resource in run mode and holds
    /// the application until it finishes — including for Spring Boot and Quarkus, whose launch goals would
    /// otherwise be the only build. This is required rather than an optimization: <c>JAVA_TOOL_OPTIONS</c> is
    /// read by every JVM started beneath the resource, and the first of those is the wrapper's own, so an
    /// agent the build has not written yet kills that JVM during VM initialization with "Error opening zip
    /// file or JAR manifest missing" before the launch goal runs.
    /// </para>
    /// <para>
    /// Use <see cref="WithOtelAgent{T}(IResourceBuilder{T}, string)"/> when the agent lives anywhere else, including
    /// when it is committed to the repository or supplied by the container base image.
    /// </para>
    /// </remarks>
    [AspireExport("withOtelAgentDefaultPath")]
    public static IResourceBuilder<T> WithOtelAgent<T>(
        this IResourceBuilder<T> builder) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Deliberately not resolved here. WithMavenBuild may not have been called yet, and an eager read
        // would make .WithOtelAgent().WithMavenBuild() throw while .WithMavenBuild().WithOtelAgent()
        // worked - exactly the order dependence WithWrapperPath goes out of its way to avoid.
        return builder.WithOtelAgentCore(agentPath: null);
    }

    /// <summary>
    /// The agent path to use, resolving the build tool's conventional location when none was authored.
    /// </summary>
    /// <remarks>
    /// The launch tool is not consulted, only the build: a resource can be launched from a prebuilt JAR and
    /// still be built by Maven, and it is the build that decides whether the agent lands in <c>target</c> or
    /// <c>build</c>.
    /// </remarks>
    internal static string ResolveOtelAgentPath(IResource resource, JavaOtelAgentAnnotation annotation)
    {
        if (annotation.AgentPath is { } authored)
        {
            return authored;
        }

        JavaBuildTool tool;

        if (resource.TryGetLastAnnotation<JavaBuildStepAnnotation>(out var buildStep))
        {
            tool = buildStep.Tool;
        }
        else if (resource is JavaAppResource app && app.HasAnnotationOfType<JavaDetectedBuildToolAnnotation>())
        {
            tool = ResolveDetectedBuildTool(app).Tool;
        }
        else
        {
            throw new InvalidOperationException(
                $"Resource '{resource.Name}' has no Maven or Gradle build configured, so the OpenTelemetry agent location cannot be inferred. " +
                $"Call WithMavenBuild or WithGradleBuild, or pass the agent path to WithOtelAgent.");
        }

        var outputDirectory = tool is JavaBuildTool.Gradle ? "build" : "target";

        return Path.Combine(outputDirectory, "agent", "opentelemetry-javaagent.jar");
    }

    /// <summary>
    /// Runs the application with the OpenTelemetry Java agent so it exports traces, metrics, and logs to Aspire.
    /// </summary>
    /// <typeparam name="T">The Java application resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="agentPath">The path to the <c>opentelemetry-javaagent.jar</c> file.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="agentPath"/> is <see langword="null"/>, empty, or whitespace.</exception>
    /// <remarks>
    /// The agent is not downloaded. Obtain it as a build dependency, or from
    /// https://github.com/open-telemetry/opentelemetry-java-instrumentation/releases, and point this at the
    /// resulting file. The OTLP exporter is configured by <c>AddJavaApp</c> regardless of whether an agent
    /// is used, so call this only when you want the agent's automatic instrumentation.
    /// <para>
    /// A relative <paramref name="agentPath"/> is resolved against the application directory and made absolute
    /// when running locally. This is required, not cosmetic: <c>JAVA_TOOL_OPTIONS</c> is inherited by every JVM
    /// started beneath the resource, and build tools start JVMs whose working directory is not the application
    /// directory. The Gradle daemon, for example, starts from its own distribution directory, so a relative
    /// <c>-javaagent:</c> path fails to resolve and the daemon dies during VM initialization with
    /// "Error opening zip file or JAR manifest missing".
    /// </para>
    /// <para>
    /// A relative path names a file the build produces, so Aspire runs that build as its own resource in run
    /// mode and holds the application until it finishes. An absolute path names a file that exists
    /// independently of the build, so no build resource is added.
    /// </para>
    /// <para>
    /// In publish mode a relative path is rewritten to the location the generated Dockerfile copies the
    /// agent to, because the path has to be interpreted inside the container rather than on the build
    /// machine. An absolute path is emitted unchanged, since it cannot have come from the build context
    /// and must be supplied by the base image or a mount.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithOtelAgent<T>(
        this IResourceBuilder<T> builder,
        string agentPath) where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentPath);

        return builder.WithOtelAgentCore(agentPath);
    }

    private static IResourceBuilder<T> WithOtelAgentCore<T>(
        this IResourceBuilder<T> builder,
        string? agentPath) where T : JavaAppResource
    {
        // Recorded so the container build can copy the agent forward. The environment variable alone
        // would leave a published image pointing at a JAR that is not in it.
        var isFirstCall = !builder.Resource.HasAnnotationOfType<JavaOtelAgentAnnotation>();
        builder.WithAnnotation(new JavaOtelAgentAnnotation(agentPath), ResourceAnnotationMutationBehavior.Replace);

        EnsureBuildRunsBeforeLaunch(builder);

        // Callbacks accumulate even though the annotation replaces, so registering one per call would
        // put a -javaagent: entry per call into JAVA_TOOL_OPTIONS and start the JVM with several agents.
        // Only the first call registers, and it reads the replaced annotation so it sees the last path.
        if (!isFirstCall)
        {
            return builder;
        }

        return builder.WithEnvironment(context =>
        {
            if (!builder.Resource.TryGetLastAnnotation<JavaOtelAgentAnnotation>(out var agent))
            {
                return;
            }

            var authored = ResolveOtelAgentPath(builder.Resource, agent);

            string resolved;

            if (context.ExecutionContext.IsRunMode)
            {
                resolved = Path.GetFullPath(Path.Combine(builder.Resource.WorkingDirectory, authored));
            }
            else if (JavaDockerfileGenerator.TryGetBuildProducedAgentPath(builder.Resource, out _))
            {
                // /app/agent.jar is where the generated Dockerfile copies the agent to. When the
                // developer wrote the Dockerfile, nothing put it there, and a JVM told to load an agent
                // that is not in the image dies during VM initialization with "Error opening zip file or
                // JAR manifest missing" — which says nothing about the cause.
                if (builder.Resource.HasAnnotationOfType<JavaAuthoredDockerfileAnnotation>())
                {
                    throw new DistributedApplicationException(
                        $"Java application '{builder.Resource.Name}' cannot be published because it uses " +
                        $"the Dockerfile in '{builder.Resource.WorkingDirectory}' and its OpenTelemetry " +
                        $"agent path '{authored}' is relative to the build output. Aspire copies a " +
                        $"build-produced agent into the image only in the Dockerfile it generates. Copy " +
                        $"the agent in your Dockerfile and pass its path inside the image to " +
                        $"{nameof(WithOtelAgent)}, for example WithOtelAgent(\"/opt/otel/javaagent.jar\").");
                }

                resolved = JavaDockerfileGenerator.ContainerAgentPath;
            }
            else
            {
                resolved = authored;
            }

            AppendJavaToolOptions(context.EnvironmentVariables, [$"-javaagent:{resolved}"]);
        });
    }

    /// <summary>
    /// Adds the build whose output the application needs before it starts, when there is one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two things need a build in front of a launch goal that would otherwise compile as it runs.
    /// </para>
    /// <para>
    /// A build-produced OpenTelemetry agent is the first. <c>JAVA_TOOL_OPTIONS</c> is read by every JVM
    /// started beneath the resource, and for a Maven or Gradle launch the first of those is the wrapper's
    /// own. A <c>-javaagent:</c> naming a file the build has not written yet therefore kills that JVM
    /// during VM initialization with "Error opening zip file or JAR manifest missing", before the launch
    /// goal that would have produced the agent ever runs. The build has to be a resource of its own so it
    /// runs in a JVM that is not carrying the agent.
    /// </para>
    /// <para>
    /// A Quarkus resource handed to an IDE is the second: the IDE starts it from the fast JAR the build
    /// packages rather than from the dev-mode goal. See <see cref="IdeLaunchesAPackagedArtifact"/>.
    /// </para>
    /// </remarks>
    private static void EnsureBuildRunsBeforeLaunch<T>(IResourceBuilder<T> builder)
        where T : JavaAppResource
    {
        // Publish resolves the agent to the path the generated Dockerfile copies it to, the image build
        // runs the packaging command itself, and no IDE launches anything, so there is no resource to add.
        if (!builder.ApplicationBuilder.ExecutionContext.IsRunMode
            || !RequiresBuildBeforeLaunch(builder))
        {
            return;
        }

        // Naming the resource and resolving its wrapper both need the tool, which is unknown when the
        // agent is configured before the build tool is. That ordering stays supported: the later
        // WithMavenBuild, WithGradleBuild, WithMavenGoal, or WithGradleTask call reaches
        // WithJavaBuildStep, which adds the resource once RequiresBuildBeforeLaunch is true.
        if (!TryResolveConfiguredBuildTool(builder.Resource, out var tool))
        {
            return;
        }

        var buildArgs = builder.Resource.TryGetLastAnnotation<JavaBuildStepAnnotation>(out var buildStep)
            ? buildStep.Args
            : builder.Resource.HasAnnotationOfType<JavaDetectedBuildToolAnnotation>()
                ? ResolveDetectedBuildTool(builder.Resource).Configuration.BuildArgs
                : DefaultBuildArgs(tool);

        builder.WithJavaBuildStep(
            tool,
            buildResourceName: $"{builder.Resource.Name}-{(tool is JavaBuildTool.Gradle ? "gradle" : "maven")}-build",
            buildArgs: buildArgs);
    }

    /// <summary>
    /// The packaging arguments <see cref="WithMavenBuild{T}(IResourceBuilder{T}, string[])"/> and
    /// <see cref="WithGradleBuild{T}(IResourceBuilder{T}, string[])"/> default to.
    /// </summary>
    private static string[] DefaultBuildArgs(JavaBuildTool tool) =>
        tool is JavaBuildTool.Gradle ? ["clean", "build"] : ["clean", "package"];

    /// <summary>
    /// Whether something other than the launch goal needs the build's output before the application
    /// starts, which makes a build resource mandatory even for a goal that compiles as it runs.
    /// </summary>
    private static bool RequiresBuildBeforeLaunch<T>(IResourceBuilder<T> builder)
        where T : JavaAppResource
        => HasBuildProducedOtelAgent(builder.Resource)
            || IdeLaunchesAPackagedArtifact(builder);

    /// <summary>
    /// Whether this run hands the resource to an IDE that starts it from an artifact the build produces
    /// rather than from the launch goal.
    /// </summary>
    /// <remarks>
    /// Only Quarkus is in this position. Its entry point lives in the fast JAR's boot classpath rather
    /// than in the project, so <see cref="ResolveEntryPointForIde"/> has to hand the debug adapter
    /// <c>quarkus-app/quarkus-run.jar</c> — and on a clean checkout no build has written it. The adapter's
    /// response to being given no entry point is to ask which of the workspace's main classes to start,
    /// a prompt nobody who has not read the AppHost can answer, so the build has to run first.
    /// <para>
    /// Spring Boot needs nothing here: the adapter starts it from the classpath the Java language server
    /// already compiled, so a build would only delay the session.
    /// </para>
    /// </remarks>
    private static bool IdeLaunchesAPackagedArtifact<T>(IResourceBuilder<T> builder)
        where T : JavaAppResource
        => builder.Resource.HasAnnotationOfType<JavaQuarkusAnnotation>()
            && builder.Resource.SupportsDebugging(builder.ApplicationBuilder.Configuration, out _);

    /// <summary>
    /// Whether the resource's OpenTelemetry agent is one its own build writes, rather than one the machine
    /// or the base image already provides.
    /// </summary>
    private static bool HasBuildProducedOtelAgent(JavaAppResource resource)
    {
        if (!resource.TryGetLastAnnotation<JavaOtelAgentAnnotation>(out var agent))
        {
            return false;
        }

        // The default location is under the build tool's output directory, so it is always build-produced.
        // An authored absolute path names a file that exists independently of the build; a relative one is
        // resolved against the application directory, which is where the build writes.
        return agent.AgentPath is not { } authored || !JavaDockerfileGenerator.IsPathRootedOnAnyPlatform(authored);
    }

    /// <summary>
    /// Contributes the arguments that turn the resource's command into a complete invocation:
    /// <c>-jar &lt;path&gt;</c> for a prebuilt JAR, or the goal/task for a build tool launch.
    /// </summary>
    private static void AddLaunchArgs(JavaAppResource resource, CommandLineArgsCallbackContext ctx)
    {
        JavaBuildTool tool;
        string[] args;

        if (resource.TryGetLastAnnotation<JavaBuildToolAnnotation>(out var buildTool))
        {
            tool = buildTool.Tool;
            args = buildTool.Args;
        }
        else if (resource.HasAnnotationOfType<JavaDetectedBuildToolAnnotation>())
        {
            var detected = ResolveDetectedBuildTool(resource);
            tool = detected.Tool;
            args = detected.Configuration.LaunchArgs;
        }
        else
        {
            tool = default;
            args = [];
        }

        if (args.Length > 0)
        {
            foreach (var leadingArg in ResolveWrapperInvocation(resource, tool).LeadingArgs)
            {
                ctx.Args.Add(leadingArg);
            }

            foreach (var arg in args)
            {
                ctx.Args.Add(arg);
            }

            return;
        }

        if (resource.TryGetLastAnnotation<JavaJarPathAnnotation>(out var jar))
        {
            ctx.Args.Add("-jar");
            ctx.Args.Add(NormalizeJarPathForJava(jar.JarPath));

            return;
        }

        // Reached when AddJavaApp was called without a jar path and without a Maven goal or Gradle task.
        // The resource would otherwise start as a bare "java" with no arguments, which prints the JVM
        // usage text and exits.
        throw new InvalidOperationException(
            $"Java application '{resource.Name}' has no launch mode configured. Call {nameof(WithMavenGoal)} " +
            $"or {nameof(WithGradleTask)} to run it through a build tool, or use the {nameof(AddJavaApp)} " +
            "overload that takes a jarPath to run a prebuilt JAR.");
    }

    private static string NormalizeJarPathForJava(string path)
    {
        // Java accepts '/' on Windows, so one target-neutral form preserves authored forward slashes
        // while also making a Windows-authored relative path usable on Linux and macOS.
        return path.Replace('\\', '/');
    }

    /// <summary>
    /// Resolves the wrapper script for <paramref name="tool"/>, honouring an override set by
    /// <see cref="WithWrapperPath{T}(IResourceBuilder{T}, string)"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wrapper is required; a globally installed <c>mvn</c> or <c>gradle</c> is deliberately not used as
    /// a fallback. The wrapper pins the exact tool version in the repository, so the AppHost, CI, and the
    /// published container image all build with the same one. Falling back to whatever happens to be on
    /// <c>PATH</c> would make the build depend on each developer's machine and silently change behaviour
    /// when that version differs, which is precisely what the wrapper exists to prevent.
    /// </para>
    /// <para>
    /// Existence is deliberately not checked here. This runs while the AppHost is still being authored,
    /// and <see cref="WithWrapperPath{T}(IResourceBuilder{T}, string)"/> is documented as usable after the
    /// build tool is configured — so a project whose only wrapper is a custom one would otherwise fail
    /// inside <c>WithMavenGoal</c>/<c>WithGradleTask</c>, before the override could be applied.
    /// <see cref="ValidateWrapperExists"/> performs the check once the configuration is final.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The command that launches a build tool wrapper, together with any arguments that must precede
    /// the wrapper's own.
    /// </summary>
    /// <remarks>
    /// On Unix the wrapper is invoked through <c>sh</c> instead of being executed directly. Git does not
    /// record an executable bit on Windows, so a repository committed from there checks out <c>mvnw</c>
    /// and <c>gradlew</c> as mode 644 and executing them fails with "permission denied". Both are POSIX
    /// shell scripts, so <c>sh</c> runs them either way. The container build already does this for the
    /// same reason (see <see cref="JavaDockerfileGenerator"/>), and run mode has to match or an identical
    /// checkout fails on Linux and macOS while succeeding inside the image.
    /// <para>
    /// On Windows the wrappers are the <c>mvnw.cmd</c> and <c>gradlew.bat</c> batch files, which <c>sh</c>
    /// cannot run. They are launched through the command interpreter rather than directly, because a
    /// batch file started with redirected stdout can silently produce no output — the same constraint
    /// <c>NpmRunner</c> hits with <c>npm.cmd</c>, and the same one
    /// <c>JavaAppHostToolchainResolver.GetToolInvocation</c> handles for Java AppHosts.
    /// </para>
    /// </remarks>
    private static (string Command, string[] LeadingArgs) ResolveWrapperInvocation(JavaAppResource resource, JavaBuildTool tool)
        => WrapperInvocationFor(
            JavaBuildToolResolver.ResolveWrapperPath(resource, tool, OperatingSystem.IsWindows()),
            resource.WorkingDirectory,
            OperatingSystem.IsWindows());

    /// <inheritdoc cref="ResolveWrapperInvocation" />
    internal static (string Command, string[] LeadingArgs) WrapperInvocationFor(string wrapperPath, string workingDirectory, bool isWindows)
    {
        if (!isWindows)
        {
            return ("sh", [wrapperPath]);
        }

        // Passing the wrapper as a path relative to the resource's working directory keeps it short and
        // usually free of spaces, which matters because cmd.exe strips quotes in a way that does not
        // match how arguments are escaped for it: when the *first* token on the line is quoted, cmd
        // removes that quote and the last one on the line, mangling everything in between.
        var relativeWrapperPath = Path.GetRelativePath(workingDirectory, wrapperPath);

        // "call" makes that unreachable rather than merely unlikely. A wrapper reached through a
        // directory with a space in its name — WithWrapperPath("../build tools/mvnw.cmd") — is quoted
        // when the command line is built, and quoting the first token is exactly what triggers the
        // stripping. With "call" ahead of it the first character is never a quote, so the rule cannot
        // apply, and "call" is how a batch file is meant to be invoked from another anyway: it returns
        // control and propagates the wrapper's exit code.
        // See the quote-processing rules printed by `cmd /?`.
        return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/c", "call", relativeWrapperPath]);
    }

    private static string WrapperCommand()
        => OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
            : "sh";

    /// <summary>
    /// Throws when the wrapper the resource will launch is not on disk.
    /// </summary>
    /// <remarks>
    /// Deferred to resource start so that the whole AppHost has been authored first: only then is it
    /// known whether a <see cref="WithWrapperPath{T}(IResourceBuilder{T}, string)"/> override supplied
    /// the wrapper that the default location lacks.
    /// </remarks>
    private static void ValidateWrapperExists(JavaAppResource resource, JavaBuildTool tool)
    {
        var wrapperPath = JavaBuildToolResolver.ResolveWrapperPath(resource, tool, OperatingSystem.IsWindows());

        if (File.Exists(wrapperPath))
        {
            return;
        }

        if (resource.HasAnnotationOfType<WrapperAnnotation>())
        {
            throw new DistributedApplicationException(
                $"Java application '{resource.Name}' has no wrapper at '{wrapperPath}'. That path came " +
                $"from {nameof(WithWrapperPath)} and is resolved relative to the application's working " +
                $"directory '{resource.WorkingDirectory}'.");
        }

        var wrapperName = JavaBuildToolResolver.GetDefaultWrapperName(tool, OperatingSystem.IsWindows());

        throw new DistributedApplicationException(
            $"Java application '{resource.Name}' has no {wrapperName} in '{resource.WorkingDirectory}' " +
            $"or in the build root above it. Aspire runs Java applications through the project's own " +
            $"wrapper so that every build uses the tool version the repository pins. Generate one with " +
            $"{GenerateWrapperCommand(tool)}, or point at an existing wrapper with {nameof(WithWrapperPath)}.");
    }

    /// <summary>
    /// Arranges for the resource's wrapper to be validated once its configuration is final.
    /// </summary>
    private static IResourceBuilder<T> WithDeferredWrapperValidation<T>(
        this IResourceBuilder<T> builder,
        JavaBuildTool tool) where T : JavaAppResource
    {
        // WithMavenGoal and WithMavenBuild both want this, and either may be called more than once, so
        // the subscription is registered at most once per tool.
        if (builder.Resource.Annotations.OfType<JavaWrapperValidationAnnotation>().Any(a => a.Tool == tool))
        {
            return builder;
        }

        builder.WithAnnotation(new JavaWrapperValidationAnnotation(tool));

        return builder.OnBeforeResourceStarted((resource, _, _) =>
        {
            ValidateWrapperExists(resource, tool);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The command that adds a wrapper to an existing project, named in the error raised when one is missing.
    /// </summary>
    internal static string GenerateWrapperCommand(JavaBuildTool tool) => tool switch
    {
        // -N keeps the goal from recursing into the modules of a multi-module build, which would litter
        // every module with a wrapper that only the root needs.
        JavaBuildTool.Maven => "'mvn -N wrapper:wrapper'",
        JavaBuildTool.Gradle => "'gradle wrapper'",
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null)
    };

    /// <summary>
    /// Appends <paramref name="values"/> to the <c>JAVA_TOOL_OPTIONS</c> environment variable, preserving
    /// whatever is already there.
    /// </summary>
    /// <remarks>
    /// The existing value may be any expression Aspire supports — a plain string, a
    /// <see cref="ReferenceExpression"/>, a parameter, or an endpoint reference — so a non-string value is
    /// folded into a new <see cref="ReferenceExpression"/> rather than being read as a string. An earlier
    /// string-only implementation silently discarded non-string values.
    /// </remarks>
    private static void AppendJavaToolOptions(Dictionary<string, object> environmentVariables, string[] values)
    {
        var appended = string.Join(' ', values.Select(QuoteIfNeeded));

        if (!environmentVariables.TryGetValue(JavaToolOptions, out var existing) || existing is null)
        {
            environmentVariables[JavaToolOptions] = appended;
            return;
        }

        environmentVariables[JavaToolOptions] = existing switch
        {
            string s when string.IsNullOrEmpty(s) => appended,
            string s => $"{s} {appended}",
            ReferenceExpression re => ReferenceExpression.Create($"{re} {appended}"),
            IValueProvider valueProvider when existing is IManifestExpressionProvider manifestProvider
                => ReferenceExpression.Create($"{new ComposableValue(valueProvider, manifestProvider)} {appended}"),
            // Anything else is a plain value (a number, a bool, a string-convertible object) that the
            // environment layer would format the same way.
            _ => $"{existing} {appended}"
        };
    }

    /// <summary>
    /// Pairs the two facets Aspire needs to compose a value into a <see cref="ReferenceExpression"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ReferenceExpression.ExpressionInterpolatedStringHandler.AppendFormatted{T}(T)"/> is
    /// constrained to a single type implementing both interfaces, which a value typed as <c>object</c>
    /// cannot satisfy without this adapter.
    /// </remarks>
    private sealed class ComposableValue(IValueProvider valueProvider, IManifestExpressionProvider manifestExpressionProvider)
        : IValueProvider, IManifestExpressionProvider
    {
        public string ValueExpression => manifestExpressionProvider.ValueExpression;

        public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken) => valueProvider.GetValueAsync(cancellationToken);
    }

    /// <summary>
    /// Quotes a JVM option whose value contains whitespace.
    /// </summary>
    /// <remarks>
    /// The JVM tokenizes <c>JAVA_TOOL_OPTIONS</c> on whitespace, so an unquoted
    /// <c>-Djavax.net.ssl.trustStore=C:\Users\First Last\AppData\...\bundle.p12</c> arrives as two
    /// unrelated options and TLS fails with no useful diagnostic. Only the value after the first
    /// <c>=</c> is quoted, because the JVM does not accept a quoted <c>-Dkey=value</c> as a whole.
    /// <para>
    /// An option that already contains a quote is passed through untouched. Quoting it again would turn
    /// an author's own <c>-Dmsg="hi there"</c> into <c>-Dmsg=""hi there""</c>, and deciding whether the
    /// existing quotes already cover the whitespace needs a real shell-style parser. Handing the option
    /// back unchanged leaves quoting to the author who introduced it, which is the only reading that
    /// cannot corrupt an already-correct value.
    /// </para>
    /// </remarks>
    private static string QuoteIfNeeded(string option)
    {
        if (!option.Any(char.IsWhiteSpace) || option.Contains('"'))
        {
            return option;
        }

        var separatorIndex = option.IndexOf('=');

        return separatorIndex < 0
            ? $"\"{option}\""
            : $"{option[..(separatorIndex + 1)]}\"{option[(separatorIndex + 1)..]}\"";
    }

    /// <summary>
    /// Builds a PKCS#12 trust store containing Aspire's development certificate and any configured
    /// certificate authorities, and points the JVM at it through <c>JAVA_TOOL_OPTIONS</c>.
    /// </summary>
    /// <remarks>
    /// The JVM ignores the <c>SSL_CERT_DIR</c> and <c>SSL_CERT_FILE</c> variables Aspire sets for other
    /// languages, so without this a Java application fails to export telemetry over HTTPS with
    /// <c>PKIX path building failed</c>. See https://github.com/CommunityToolkit/Aspire/issues/1517.
    /// <para>
    /// <c>javax.net.ssl.trustStore</c> <em>replaces</em> the JVM's trust anchors rather than adding to
    /// them: once it is set, <c>cacerts</c> is not consulted at all. Aspire therefore has to request
    /// <see cref="CertificateTrustScope.System"/> so the generated bundle carries the system roots
    /// alongside the development certificate. If the scope is still
    /// <see cref="CertificateTrustScope.Append"/>, the bundle would contain only Aspire's own
    /// certificates, and pointing the JVM at it would strip every public CA — breaking outbound HTTPS
    /// from the application and, because <c>JAVA_TOOL_OPTIONS</c> is inherited by the build tool's JVM,
    /// breaking Maven Central and Gradle distribution downloads too. In that case the override is
    /// skipped instead.
    /// </para>
    /// </remarks>
    private static async Task JavaCertificateTrustCallback(CertificateTrustConfigurationCallbackAnnotationContext ctx)
    {
        if (ctx.Scope == CertificateTrustScope.Append)
        {
            var resourceLoggerService = ctx.ExecutionContext.Services.GetRequiredService<ResourceLoggerService>();
            resourceLoggerService.GetLogger(ctx.Resource).LogInformation(
                "Certificate trust scope is set to 'Append', but the JVM's trust store setting replaces the default " +
                "certificate authorities rather than adding to them. Skipping the trust store override so the JVM " +
                "keeps trusting its built-in certificate authorities.");
            return;
        }

        var bundlePath = ctx.CreateCustomBundle((certificates, ct) =>
        {
            var pkcs12Builder = new Pkcs12Builder();
            var safeContents = new Pkcs12SafeContents();

            // Oracle/OpenJDK trusted cert bag attribute OID. Without it the JDK reads the entries as
            // key entry candidates rather than trustedCertEntry, and the store trusts nothing.
            // See sun.security.pkcs12.PKCS12KeyStore in the OpenJDK sources.
            var trustAnchorOid = new Oid("2.16.840.1.113894.746875.1.1");
            var asnWriter = new AsnWriter(AsnEncodingRules.DER);
            asnWriter.WriteObjectIdentifier("2.5.29.37.0"); // anyExtendedKeyUsage
            var trustAnchorValue = asnWriter.Encode();

            for (var i = 0; i < certificates.Count; i++)
            {
                // Re-import the public part only so no private key can reach the trust store.
                using var publicCert = X509CertificateLoader.LoadCertificate(certificates[i].Export(X509ContentType.Cert));
                var certBag = safeContents.AddCertificate(publicCert);
                certBag.Attributes.Add(
                    new CryptographicAttributeObject(
                        trustAnchorOid,
                        new AsnEncodedDataCollection(new AsnEncodedData(trustAnchorOid, trustAnchorValue))));
            }

            pkcs12Builder.AddSafeContentsUnencrypted(safeContents);

            // Sealed with an empty password on purpose. The MAC still protects integrity, and a trust
            // store holds only public certificates, so a password would protect nothing while appearing
            // in JAVA_TOOL_OPTIONS — which the dashboard shows unmasked, which every process the build
            // tool forks inherits, and which the JVM itself echoes to stderr as
            // "Picked up JAVA_TOOL_OPTIONS: ...".
            pkcs12Builder.SealWithMac(string.Empty, HashAlgorithmName.SHA256, iterationCount: 2048);

            return Task.FromResult(pkcs12Builder.Encode());
        });

        var bundlePathValue = await bundlePath.GetValueAsync(ctx.CancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(bundlePathValue))
        {
            return;
        }

        AppendJavaToolOptions(
            ctx.EnvironmentVariables,
            [
                $"-Djavax.net.ssl.trustStore={bundlePathValue}",
                "-Djavax.net.ssl.trustStoreType=PKCS12"
            ]);
    }

    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder)
        where T : JavaAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithDebugSupport(
            mode =>
            {
                var (mainClass, classPaths) = ResolveEntryPointForIde(builder.Resource);

                return new JavaLaunchConfiguration
                {
                    Mode = mode,
                    JavaExec = TryResolveJavaExecutableForIde(builder.Resource),
                    WorkingDirectory = builder.Resource.WorkingDirectory,
                    MainClass = mainClass,
                    ClassPaths = classPaths,
                    // projectName scopes the adapter's entry point resolution to this resource's own
                    // project. It is sent alongside mainClass rather than only as a fallback: given
                    // mainClass alone the adapter searches every project in the workspace, and a class
                    // that turns up in more than one fails the launch outright with
                    // "Main class ... isn't unique in the workspace". That happens whenever a directory
                    // is covered both by its own build file and by another project's source root, which
                    // is easy to arrange by accident and impossible to diagnose from the error.
                    //
                    // The name is read out of pom.xml or settings.gradle rather than derived from the
                    // resource name, so it is the name m2e and Buildship import the project under.
                    //
                    // It is omitted when explicit class paths are supplied, because such a resource runs
                    // from a prebuilt archive rather than from a project the language server compiled,
                    // so there may be no imported project to scope to.
                    ProjectName = classPaths is { Length: > 0 }
                        ? null
                        : TryResolveIdeProjectName(builder.Resource),
                    BuildTool = TryResolveConfiguredBuildTool(builder.Resource, out var buildTool)
                        ? buildTool.ToString().ToLowerInvariant()
                        : null
                };
            },
            "java");
    }

    private static string? TryResolveJavaExecutableForIde(JavaAppResource resource)
    {
        if (resource.HasAnnotationOfType<JavaBuildToolAnnotation>()
            || resource.HasAnnotationOfType<JavaDetectedBuildToolAnnotation>())
        {
            return null;
        }

        return IsFullyQualifiedJavaExecutable(resource.Command) ? resource.Command : null;
    }

    private static bool IsFullyQualifiedJavaExecutable(string command)
    {
        if (!Path.IsPathFullyQualified(command))
        {
            return false;
        }

        var fileName = Path.GetFileName(command);
        return string.Equals(fileName, "java", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "java.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Determines what the IDE should launch: the entry point class and, when the resource runs a
    /// prebuilt JAR, the classpath that contains it.
    /// </summary>
    /// <remarks>
    /// A JAR is never sent as the main class. The Java debug adapter documents that attribute as a
    /// fully qualified class name or a <c>.java</c> source path, so it does not open archives; passing
    /// a JAR path made the adapter fail to resolve an entry point. Instead the archive goes on the
    /// classpath and its manifest's <c>Main-Class</c> becomes the main class, which is exactly what
    /// <c>java -jar</c> does and works for Spring Boot fat JARs too (their manifest names
    /// <c>JarLauncher</c>, the same class <c>java -jar</c> would run).
    /// </remarks>
    private static (string? MainClass, string[]? ClassPaths) ResolveEntryPointForIde(JavaAppResource resource)
    {
        // An explicit WithMainClass always wins, including over a JAR's manifest, so a resource that
        // ships a launcher manifest can still be debugged at its real entry point.
        var explicitMainClass = resource.TryGetLastAnnotation<JavaMainClassAnnotation>(out var mainClass)
            ? mainClass.MainClass
            : null;

        if (!resource.TryGetLastAnnotation<JavaJarPathAnnotation>(out var jar))
        {
            if (explicitMainClass is not null)
            {
                return (explicitMainClass, null);
            }

            // Quarkus is the exception to the rule below. Its entry point lives in the fast JAR's boot
            // classpath rather than in the project, so the language server's classpath cannot start it and
            // the archive has to be supplied. Breakpoints still bind, because the debugger maps loaded
            // classes back to project sources by name.
            if (TryGetQuarkusRunJar(resource) is { } quarkusRunJar)
            {
                return (TryReadJarManifestMainClass(quarkusRunJar), [quarkusRunJar]);
            }

            // A Maven or Gradle resource deliberately sends no classpath: the language server already
            // knows the project's, and supplying one built from the JAR would bind breakpoints to
            // compiled classes instead of the source the user is editing.
            return (TryDiscoverProjectMainClass(resource), null);
        }

        // Resolved to an absolute path because the adapter reads the archive before the debuggee's
        // working directory exists. Normalize first because a Windows-authored relative path otherwise
        // names a literal backslash-containing file when the AppHost runs on Linux or macOS.
        var jarPath = Path.GetFullPath(Path.Combine(resource.WorkingDirectory, NormalizeJarPathForJava(jar.JarPath)));

        return (explicitMainClass ?? TryReadJarManifestMainClass(jarPath), [jarPath]);
    }

    /// <summary>
    /// Recovers the entry point of a Maven or Gradle resource from the JAR its build produced, or
    /// returns <see langword="null"/> when no single application JAR is there to read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a Spring Boot resource launched through <c>spring-boot:run</c> or <c>bootRun</c>
    /// sends no main class, and <c>vscjava.vscode-java-debug</c> responds by asking the user to pick one
    /// from every main class in the workspace. That prompt appears on each launch, offers classes
    /// belonging to other resources, and cannot be answered correctly by anyone who has not read the
    /// AppHost — so the entry point is resolved here instead.
    /// </para>
    /// <para>
    /// The build output is the right place to look because it is the only artifact that names the entry
    /// point without parsing a build file: the Spring Boot plugins record it while repackaging, and a
    /// plain JAR carries it as <c>Main-Class</c>.
    /// </para>
    /// </remarks>
    private static string? TryDiscoverProjectMainClass(JavaAppResource resource)
    {
        if (!TryResolveConfiguredBuildTool(resource, out var buildTool))
        {
            return null;
        }

        // Both tools have a single conventional output directory, and neither lets the AppHost know
        // about a redirected one without parsing the build file.
        var outputDirectory = buildTool switch
        {
            JavaBuildTool.Maven => Path.Combine(resource.WorkingDirectory, "target"),
            JavaBuildTool.Gradle => Path.Combine(resource.WorkingDirectory, "build", "libs"),
            _ => null
        };

        if (outputDirectory is null || !Directory.Exists(outputDirectory))
        {
            return null;
        }

        string[] jars;

        try
        {
            jars = Directory.GetFiles(outputDirectory, "*.jar", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // A build can leave several archives behind: Maven publishes -sources and -javadoc alongside the
        // application, and the Gradle Spring Boot plugin writes the unrepackaged classes to -plain.jar.
        // Only one of them is the application, and guessing between two candidates would be worse than
        // letting the adapter resolve the class itself.
        var applicationJars = jars
            .Where(jarPath => !IsAuxiliaryJar(jarPath))
            .ToArray();

        if (applicationJars.Length != 1)
        {
            return null;
        }

        if (TryReadJarManifest(applicationJars[0]) is not { } manifest)
        {
            return null;
        }

        // Spring Boot repackaging points Main-Class at its own launcher and moves the application's
        // entry point to Start-Class, so Start-Class is what a debugger should start.
        // https://docs.spring.io/spring-boot/specification/executable-jar/launching.html
        if (manifest.TryGetValue("Start-Class", out var startClass) && !string.IsNullOrWhiteSpace(startClass))
        {
            return startClass;
        }

        if (!manifest.TryGetValue("Main-Class", out var entryPoint) || string.IsNullOrWhiteSpace(entryPoint))
        {
            return null;
        }

        // A launcher is only startable with the fat JAR on the classpath, which is exactly what this
        // path does not send. Reporting it would launch a JVM that fails with ClassNotFoundException.
        return entryPoint.StartsWith("org.springframework.boot.loader.", StringComparison.Ordinal)
            ? null
            : entryPoint;
    }

    /// <summary>
    /// Gets the Quarkus fast JAR the build produced, or <see langword="null"/> when the resource is not a
    /// Quarkus application or has not been built yet.
    /// </summary>
    /// <remarks>
    /// Quarkus's default packaging leaves the runnable artifact at <c>quarkus-app/quarkus-run.jar</c> under the
    /// build tool's output directory. The JAR that sits directly in that output directory is the plain,
    /// unrunnable one — it has no <c>Main-Class</c> — so the ordinary discovery path finds nothing to report and
    /// the debugger would fall back to asking the user which application to start.
    /// See https://quarkus.io/guides/maven-tooling#fast-jar.
    /// </remarks>
    private static string? TryGetQuarkusRunJar(JavaAppResource resource)
    {
        if (!resource.HasAnnotationOfType<JavaQuarkusAnnotation>()
            || !TryResolveConfiguredBuildTool(resource, out var buildTool))
        {
            return null;
        }

        var runJar = Path.Combine(
            resource.WorkingDirectory,
            buildTool is JavaBuildTool.Gradle ? "build" : "target",
            QuarkusFastJarDirectory,
            QuarkusRunJarName);

        return File.Exists(runJar) ? Path.GetFullPath(runJar) : null;
    }

    /// <summary>
    /// Compares two directory paths for equivalence, tolerating a trailing separator and, on Windows and
    /// macOS, differences in case that the file system itself ignores.
    /// </summary>
    private static bool ArePathsEquivalent(string left, string right)
        => string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static bool IsAuxiliaryJar(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);

        return fileName.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("-plain.jar", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves the name the Java language server imported this resource's project under, so the debug
    /// adapter can scope entry point resolution to it instead of searching the whole workspace.
    /// Returns <see langword="null"/> when the imported name cannot be predicted with confidence.
    /// </summary>
    private static string? TryResolveIdeProjectName(JavaAppResource resource)
    {
        if (!TryResolveConfiguredBuildTool(resource, out var buildTool))
        {
            return null;
        }

        var declaredName = buildTool switch
        {
            JavaBuildTool.Maven => TryReadMavenArtifactId(Path.Combine(resource.WorkingDirectory, "pom.xml")),
            JavaBuildTool.Gradle => TryReadGradleProjectName(resource.WorkingDirectory),
            _ => null
        };

        if (declaredName is null)
        {
            return null;
        }

        // The declared name is only used when the project directory is named the same, because that is
        // the case where the language server is known to import the project under it. The two are not
        // always the same: a Gradle build declaring `rootProject.name = 'javaspringboot-apphost'` inside
        // a folder named JavaSpringBoot.AppHost.Java is imported as
        // "javaspringboot-apphost-JavaSpringBoot.AppHost.Java", appending the directory to keep the name
        // unambiguous.
        //
        // Guessing wrong is worse than not guessing. Without a project name the adapter resolves the
        // entry point across the whole workspace, which succeeds whenever that entry point is unique;
        // with a name no project answers to, every launch fails.
        var directoryName = new DirectoryInfo(resource.WorkingDirectory).Name;

        return string.Equals(declaredName, directoryName, StringComparison.Ordinal) ? declaredName : null;
    }

    /// <summary>
    /// Reads a POM's own <c>artifactId</c>, which is the name the language server imports a Maven
    /// project under.
    /// </summary>
    private static string? TryReadMavenArtifactId(string pomPath)
    {
        if (!File.Exists(pomPath))
        {
            return null;
        }

        try
        {
            var document = XDocument.Load(pomPath);

            // Only a direct child of <project> identifies this module. Nearly every Spring Boot POM also
            // declares <parent><artifactId>spring-boot-starter-parent</artifactId></parent>, and a
            // descendant search would find that one first and name the wrong project.
            //
            // Matched on LocalName because a POM may or may not declare the Maven namespace:
            //   <project xmlns="http://maven.apache.org/POM/4.0.0"> ... <artifactId>catalog</artifactId>
            var artifactId = document.Root?
                .Elements()
                .FirstOrDefault(element => element.Name.LocalName == "artifactId")?
                .Value;

            return string.IsNullOrWhiteSpace(artifactId) ? null : artifactId.Trim();
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads <c>rootProject.name</c> from a Gradle settings file, falling back to the directory name
    /// that Gradle itself defaults a project's name to.
    /// </summary>
    private static string? TryReadGradleProjectName(string workingDirectory)
    {
        foreach (var fileName in (ReadOnlySpan<string>)["settings.gradle", "settings.gradle.kts"])
        {
            var settingsPath = Path.Combine(workingDirectory, fileName);

            if (!File.Exists(settingsPath))
            {
                continue;
            }

            try
            {
                // Groovy and the Kotlin DSL differ only in quoting:
                //   rootProject.name = 'orders'
                //   rootProject.name = "orders"
                if (GradleRootProjectNameRegex().Match(File.ReadAllText(settingsPath)) is { Success: true } match)
                {
                    return match.Groups["name"].Value;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to the directory name.
            }
        }

        // https://docs.gradle.org/current/userguide/multi_project_builds.html — a build that does not
        // name itself takes the name of the directory containing it.
        var directoryName = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return string.IsNullOrEmpty(directoryName) ? null : directoryName;
    }

    [GeneratedRegex(@"rootProject\.name\s*=\s*['""](?<name>[^'""]+)['""]")]
    private static partial Regex GradleRootProjectNameRegex();

    /// <summary>
    /// Reads <c>Main-Class</c> from a JAR's manifest, or returns <see langword="null"/> when the archive
    /// is missing, unreadable, or declares no entry point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Failures are non-fatal on purpose. The JAR is usually produced by a build step that runs before
    /// launch, but a user can start a debug session against a stale or half-written archive. Returning
    /// null lets the IDE resolve the entry point from the project instead of failing the whole run.
    /// </para>
    /// <para>
    /// <c>META-INF/MANIFEST.MF</c> is a line-oriented <c>Name: value</c> format where each line is
    /// limited to 72 bytes and longer values continue on the next line with a single leading space,
    /// which the space must be stripped from. Names are case-insensitive. For example:
    /// </para>
    /// <code>
    /// Manifest-Version: 1.0
    /// Main-Class: com.example.catalog.averylongpackagename.that.wraps.Catalo
    ///  gApplication
    /// </code>
    /// <para>
    /// See https://docs.oracle.com/en/java/javase/25/docs/specs/jar/jar.html#jar-manifest.
    /// </para>
    /// </remarks>
    private static string? TryReadJarManifestMainClass(string jarPath)
    {
        if (TryReadJarManifest(jarPath) is not { } manifest)
        {
            return null;
        }

        return manifest.TryGetValue("Main-Class", out var mainClass) && !string.IsNullOrWhiteSpace(mainClass)
            ? mainClass
            : null;
    }

    /// <summary>
    /// Reads the main section of a JAR's manifest, or returns <see langword="null"/> when the archive is
    /// missing, unreadable, or carries no manifest.
    /// </summary>
    private static Dictionary<string, string>? TryReadJarManifest(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);

            // The JAR specification requires this exact name, but archive entry lookup is
            // case-sensitive while some tools write the directory in a different case.
            var manifestEntry = archive.Entries.FirstOrDefault(
                entry => string.Equals(entry.FullName, "META-INF/MANIFEST.MF", StringComparison.OrdinalIgnoreCase));

            if (manifestEntry is null)
            {
                return null;
            }

            using var reader = new StreamReader(manifestEntry.Open());

            // Attribute names are case-insensitive per the specification.
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? name = null;
            var value = new StringBuilder();

            while (reader.ReadLine() is { } line)
            {
                // A blank line ends the main section. Everything after it describes individual archive
                // entries and would overwrite the main attributes with per-entry ones of the same name.
                if (line.Length == 0)
                {
                    break;
                }

                // Values longer than 72 bytes continue on the following line behind a single space,
                // which belongs to the encoding rather than the value.
                if (line[0] == ' ')
                {
                    if (name is not null)
                    {
                        value.Append(line, 1, line.Length - 1);
                    }

                    continue;
                }

                Commit();

                var separator = line.IndexOf(':');

                if (separator < 0)
                {
                    name = null;
                    continue;
                }

                name = line[..separator];
                value.Append(line[(separator + 1)..].TrimStart());
            }

            Commit();

            return attributes;

            void Commit()
            {
                if (name is not null && value.Length > 0)
                {
                    attributes[name] = value.ToString();
                }

                name = null;
                value.Clear();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

#pragma warning restore ASPIREPIPELINES003
#pragma warning restore ASPIREPIPELINES001
#pragma warning restore ASPIREDOCKERFILEBUILDER001
