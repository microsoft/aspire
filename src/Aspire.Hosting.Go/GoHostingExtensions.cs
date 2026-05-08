// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREDOCKERFILEBUILDER001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Go;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Go applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class GoHostingExtensions
{
    /// <summary>
    /// Adds a Go application to the application model. The Go toolchain must be available on the PATH.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The path to the directory containing the Go application (must contain <c>go.mod</c>).</param>
    /// <param name="buildTags">Optional build tags passed to the compiler via <c>-tags</c> (e.g. <c>"netgo"</c>, <c>"integration"</c>).</param>
    /// <param name="ldFlags">Optional linker flags passed via <c>-ldflags</c> (e.g. <c>"-X main.version=1.0.0"</c>).</param>
    /// <param name="gcFlags">Optional compiler flags passed via <c>-gcflags</c> (e.g. <c>"all=-N -l"</c> to disable optimisations for Delve).</param>
    /// <param name="raceDetector">When <see langword="true"/>, enables the Go race detector by passing <c>-race</c> to <c>go run</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method executes the Go application using <c>go run .</c>. The Go toolchain resolves the
    /// entry point from the package in <paramref name="appDirectory"/>.
    /// </para>
    /// <para>
    /// Go applications automatically have VS Code debugging support enabled via Delve.
    /// Use <see cref="WithModTidy{T}"/>, <see cref="WithModVendor{T}"/>, or <see cref="WithModDownload{T}"/>
    /// to manage module dependencies before startup, and <see cref="WithVetTool{T}"/> to run static analysis.
    /// Use <see cref="WithAppArgs{T}"/> to pass runtime program arguments, and
    /// <see cref="WithDelveServer{T}"/> to enable remote debugging via a headless Delve server.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a Go API to the application model with build tags and linker flags:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddGoApp("api", "../go-api",
    ///            buildTags: ["netgo"],
    ///            ldFlags: "-X main.version=1.0.0")
    ///        .WithHttpEndpoint(port: 8080)
    ///        .WithExternalHttpEndpoints();
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a Go application resource")]
    public static IResourceBuilder<GoAppResource> AddGoApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string appDirectory,
        string[]? buildTags = null,
        string? ldFlags = null,
        string? gcFlags = null,
        bool raceDetector = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);

        appDirectory = Path.GetFullPath(appDirectory, builder.AppHostDirectory);
        var resource = new GoAppResource(name, appDirectory);

        var rb = builder.AddResource(resource)
            .WithArgs(ctx =>
            {
                var programArgs = ctx.Resource.TryGetLastAnnotation<GoAppArgsAnnotation>(out var argsAnnotation)
                    ? argsAnnotation.Args
                    : [];

                var hasDelve = ctx.Resource.TryGetLastAnnotation<GoDelveServerAnnotation>(out var delveAnnotation);

                if (hasDelve)
                {
                    // Delve debug mode — global flags MUST precede the subcommand per the Delve CLI:
                    //   dlv --headless=true --listen=:PORT --api-version=2 debug [--build-flags=...] . [-- args]
                    // See: https://www.jetbrains.com/help/go/attach-to-running-go-processes-with-debugger.html
                    ctx.Args.Add("--headless=true");
                    ctx.Args.Add($"--listen=:{delveAnnotation!.Port}");
                    ctx.Args.Add("--api-version=2");
                    ctx.Args.Add("debug");

                    var buildFlags = BuildFlagsString(ctx.Resource);
                    if (buildFlags.Length > 0)
                    {
                        ctx.Args.Add($"--build-flags={buildFlags}");
                    }

                    ctx.Args.Add(".");

                    if (programArgs.Length > 0)
                    {
                        ctx.Args.Add("--");
                        foreach (var arg in programArgs)
                        {
                            ctx.Args.Add(arg);
                        }
                    }
                }
                else
                {
                    // Normal run mode: go run [-race] [-tags=...] [-ldflags=...] [-gcflags=...] . [args]
                    ctx.Args.Add("run");

                    if (ctx.Resource.TryGetLastAnnotation<GoRaceDetectorAnnotation>(out _))
                    {
                        ctx.Args.Add("-race");
                    }

                    if (ctx.Resource.TryGetLastAnnotation<GoBuildTagsAnnotation>(out var tagsAnnotation))
                    {
                        ctx.Args.Add($"-tags={string.Join(",", tagsAnnotation.Tags)}");
                    }

                    if (ctx.Resource.TryGetLastAnnotation<GoLdFlagsAnnotation>(out var ldFlagsAnnotation))
                    {
                        ctx.Args.Add($"-ldflags={ldFlagsAnnotation.Flags}");
                    }

                    if (ctx.Resource.TryGetLastAnnotation<GoGcFlagsAnnotation>(out var gcFlagsAnnotation))
                    {
                        ctx.Args.Add($"-gcflags={gcFlagsAnnotation.Flags}");
                    }

                    ctx.Args.Add(".");

                    foreach (var arg in programArgs)
                    {
                        ctx.Args.Add(arg);
                    }
                }
            })
            .WithVSCodeDebugging()
            .PublishAsDockerFile(containerBuilder =>
            {
                if (File.Exists(Path.Combine(appDirectory, "Dockerfile")))
                {
                    return;
                }

                containerBuilder.WithDockerfileBuilder(appDirectory, ctx =>
                {
                    var goVersion = GoVersionDetector.Detect(appDirectory);

                    ctx.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);
                    var buildImage = baseImageAnnotation?.BuildImage ?? $"golang:{goVersion}-alpine";
                    var runtimeImage = baseImageAnnotation?.RuntimeImage ?? "alpine:latest";

                    var buildCmd = BuildDockerGoCommand(ctx.Resource);

                    ctx.Builder
                        .From(buildImage, "build")
                        .WorkDir("/app")
                        .Copy("go.mod", "go.sum", "./")
                        .Run("go mod download")
                        .Copy(".", ".")
                        .Run(buildCmd);

                    ctx.Builder
                        .From(runtimeImage)
                        .Run("apk --no-cache add ca-certificates tzdata")
                        .WorkDir("/app")
                        .CopyFrom("build", "/app/server", "/app/server")
                        .Entrypoint(["/app/server"]);
                });
            });

        if (buildTags is { Length: > 0 })
        {
            rb.WithAnnotation(new GoBuildTagsAnnotation(buildTags), ResourceAnnotationMutationBehavior.Replace);
        }

        if (ldFlags is not null)
        {
            rb.WithAnnotation(new GoLdFlagsAnnotation(ldFlags), ResourceAnnotationMutationBehavior.Replace);
        }

        if (gcFlags is not null)
        {
            rb.WithAnnotation(new GoGcFlagsAnnotation(gcFlags), ResourceAnnotationMutationBehavior.Replace);
        }

        if (raceDetector)
        {
            rb.WithAnnotation(new GoRaceDetectorAnnotation(), ResourceAnnotationMutationBehavior.Replace);
        }

        return rb;
    }

    /// <summary>
    /// Passes extra arguments to the Go program at runtime.
    /// In normal run mode they appear after <c>go run .</c>; in Delve mode after the <c>--</c> separator.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <param name="args">The program arguments (e.g., <c>"serve"</c>, <c>"--config"</c>, <c>"prod.yaml"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Passes extra arguments to the Go program at runtime (after go run . in normal mode, or after -- in Delve mode)")]
    public static IResourceBuilder<T> WithAppArgs<T>(this IResourceBuilder<T> builder, params string[] args)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);
        return builder.WithAnnotation(new GoAppArgsAnnotation(args), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Runs <c>go mod tidy</c> before starting the application, ensuring <c>go.sum</c> is up to date.
    /// The main application waits for the tidy step to complete successfully before launching.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Runs go mod tidy before starting the application to ensure go.sum is up to date")]
    public static IResourceBuilder<T> WithModTidy<T>(this IResourceBuilder<T> builder)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Guard against duplicate resource creation if called more than once.
        if (builder.Resource.TryGetLastAnnotation<GoModTidyAnnotation>(out _))
        {
            return builder;
        }

        builder.WithAnnotation(new GoModTidyAnnotation());

        // Only create the setup resource in run mode; it has no meaning during publish.
        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            var tidyResource = new ExecutableResource(
                $"{builder.Resource.Name}-mod-tidy", "go", builder.Resource.WorkingDirectory);

            var tidy = builder.ApplicationBuilder
                .AddResource(tidyResource)
                .WithArgs("mod", "tidy", "-e")
                .ExcludeFromManifest();

            builder.WaitForCompletion(tidy);
        }

        return builder;
    }

    /// <summary>
    /// Runs <c>go mod vendor</c> before starting the application, caching all module dependencies
    /// in the local <c>vendor/</c> directory.
    /// The main application waits for the vendor step to complete successfully before launching.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Runs go mod vendor before starting the application to cache module dependencies locally")]
    public static IResourceBuilder<T> WithModVendor<T>(this IResourceBuilder<T> builder)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.TryGetLastAnnotation<GoModVendorAnnotation>(out _))
        {
            return builder;
        }

        builder.WithAnnotation(new GoModVendorAnnotation());

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            var vendorResource = new ExecutableResource(
                $"{builder.Resource.Name}-mod-vendor", "go", builder.Resource.WorkingDirectory);

            var vendor = builder.ApplicationBuilder
                .AddResource(vendorResource)
                .WithArgs("mod", "vendor")
                .ExcludeFromManifest();

            builder.WaitForCompletion(vendor);
        }

        return builder;
    }

    /// <summary>
    /// Runs <c>go mod download</c> before starting the application, pre-fetching all module
    /// dependencies into the local module cache without modifying <c>go.sum</c>.
    /// The main application waits for the download step to complete successfully before launching.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Runs go mod download before starting the application to pre-fetch module dependencies into the local cache")]
    public static IResourceBuilder<T> WithModDownload<T>(this IResourceBuilder<T> builder)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.TryGetLastAnnotation<GoModDownloadAnnotation>(out _))
        {
            return builder;
        }

        builder.WithAnnotation(new GoModDownloadAnnotation());

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            var downloadResource = new ExecutableResource(
                $"{builder.Resource.Name}-mod-download", "go", builder.Resource.WorkingDirectory);

            var download = builder.ApplicationBuilder
                .AddResource(downloadResource)
                .WithArgs("mod", "download")
                .ExcludeFromManifest();

            builder.WaitForCompletion(download);
        }

        return builder;
    }

    /// <summary>
    /// Runs <c>go vet ./...</c> before starting the application to catch static analysis issues.
    /// The main application waits for the vet step to complete successfully before launching.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Runs go vet ./... before starting the application to catch static analysis issues")]
    public static IResourceBuilder<T> WithVetTool<T>(this IResourceBuilder<T> builder)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.TryGetLastAnnotation<GoLintAnnotation>(out _))
        {
            return builder;
        }

        builder.WithAnnotation(new GoLintAnnotation());

        if (builder.ApplicationBuilder.ExecutionContext.IsRunMode)
        {
            var vetResource = new ExecutableResource(
                $"{builder.Resource.Name}-vet-tool", "go", builder.Resource.WorkingDirectory);

            var vet = builder.ApplicationBuilder
                .AddResource(vetResource)
                .WithArgs("vet", "./...")
                .ExcludeFromManifest();

            builder.WaitForCompletion(vet);
        }

        return builder;
    }

    /// <summary>
    /// Starts a headless Delve debug server so that any DAP-compatible client can attach remotely.
    /// The application is launched as
    /// <c>dlv --headless=true --listen=:&lt;port&gt; --api-version=2 debug .</c>
    /// instead of <c>go run .</c>. Delve must be available on the PATH.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <param name="port">The TCP port Delve listens on. Defaults to <c>2345</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Delve is the only Go debugger; both GoLand and VS Code use it under the hood, just in
    /// different modes:
    /// </para>
    /// <list type="bullet">
    /// <item>
    ///   <term>GoLand</term>
    ///   <description>Create a <em>Go Remote</em> run configuration pointing at
    ///   <c>localhost:&lt;port&gt;</c> and start it after the resource has started.</description>
    /// </item>
    /// <item>
    ///   <term>VS Code (attach mode)</term>
    ///   <description>Add a <c>"request": "attach"</c> entry to <c>launch.json</c> with
    ///   <c>"mode": "remote"</c>, <c>"host": "localhost"</c>, and <c>"port": &lt;port&gt;</c>,
    ///   then start it after the resource has started.</description>
    /// </item>
    /// </list>
    /// <para>
    /// VS Code users who do not need GoLand compatibility can rely on the automatic VS Code
    /// debugging support that <see cref="AddGoApp"/> enables by default — no change to the
    /// application command is required in that case.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddGoApp("api", "../go-api")
    ///        .WithDelveServer(port: 2345);
    /// </code>
    /// </example>
    [AspireExport(Description = "Starts a headless Delve server for remote debugging (GoLand, VS Code attach, any DAP client)")]
    public static IResourceBuilder<T> WithDelveServer<T>(this IResourceBuilder<T> builder, int port = 2345)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Switch the underlying executable from "go" to "dlv".
        builder.Resource.Annotations.Add(new ExecutableAnnotation { Command = "dlv", WorkingDirectory = builder.Resource.WorkingDirectory });
        return builder.WithAnnotation(new GoDelveServerAnnotation(port), ResourceAnnotationMutationBehavior.Replace);
    }

    [System.Diagnostics.CodeAnalysis.Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        var workingDirectory = Path.GetFullPath(builder.Resource.WorkingDirectory);

        return builder.WithDebugSupport(
            mode => new GoLaunchConfiguration
            {
                Program = workingDirectory,
                Mode = mode,
                WorkingDirectory = workingDirectory
            },
            "go");
    }

    /// <summary>
    /// Builds the <c>go build</c> command for the generated Dockerfile, propagating any
    /// build-time flags that were set on the resource via <see cref="AddGoApp"/>.
    /// </summary>
    private static string BuildDockerGoCommand(IResource resource)
    {
        var parts = new List<string> { "go", "build" };

        if (resource.TryGetLastAnnotation<GoRaceDetectorAnnotation>(out _))
        {
            parts.Add("-race");
        }

        if (resource.TryGetLastAnnotation<GoBuildTagsAnnotation>(out var tagsAnnotation))
        {
            parts.Add($"-tags={string.Join(",", tagsAnnotation.Tags)}");
        }

        if (resource.TryGetLastAnnotation<GoLdFlagsAnnotation>(out var ldFlagsAnnotation))
        {
            parts.Add($"-ldflags={ldFlagsAnnotation.Flags}");
        }

        if (resource.TryGetLastAnnotation<GoGcFlagsAnnotation>(out var gcFlagsAnnotation))
        {
            parts.Add($"-gcflags={gcFlagsAnnotation.Flags}");
        }

        parts.AddRange(["-o", "/app/server", "."]);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Builds the combined build-flags string from present annotations.
    /// Returns an empty string when no flags are set.
    /// For <c>go run</c> the flags are individual args; for <c>dlv --build-flags</c> they are combined.
    /// </summary>
    private static string BuildFlagsString(IResource resource)
    {
        var parts = new List<string>();

        if (resource.TryGetLastAnnotation<GoRaceDetectorAnnotation>(out _))
        {
            parts.Add("-race");
        }

        if (resource.TryGetLastAnnotation<GoBuildTagsAnnotation>(out var tagsAnnotation))
        {
            parts.Add($"-tags={string.Join(",", tagsAnnotation.Tags)}");
        }

        if (resource.TryGetLastAnnotation<GoLdFlagsAnnotation>(out var ldFlagsAnnotation))
        {
            parts.Add($"-ldflags={ldFlagsAnnotation.Flags}");
        }

        if (resource.TryGetLastAnnotation<GoGcFlagsAnnotation>(out var gcFlagsAnnotation))
        {
            parts.Add($"-gcflags={gcFlagsAnnotation.Flags}");
        }

        return string.Join(" ", parts);
    }
}
