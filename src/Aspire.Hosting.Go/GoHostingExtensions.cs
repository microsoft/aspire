// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001

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
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <remarks>
    /// <para>
    /// This method executes the Go application using <c>go run .</c>. The Go toolchain resolves the
    /// entry point from the package in <paramref name="appDirectory"/>.
    /// </para>
    /// <para>
    /// Go applications automatically have VS Code debugging support enabled via Delve.
    /// Use <see cref="WithBuildTags{T}"/> to pass build constraints, <see cref="WithLdFlags{T}"/>
    /// to pass linker flags, and <see cref="WithAppArgs{T}"/> to pass runtime program arguments.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a Go API to the application model:
    /// <code lang="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddGoApp("api", "../go-api")
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
        string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(appDirectory);

        appDirectory = Path.GetFullPath(appDirectory, builder.AppHostDirectory);
        var resource = new GoAppResource(name, appDirectory);

        return builder.AddResource(resource)
            .WithArgs(ctx =>
            {
                var programArgs = ctx.Resource.TryGetLastAnnotation<GoAppArgsAnnotation>(out var argsAnnotation)
                    ? argsAnnotation.Args
                    : Array.Empty<string>();

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
            .WithVSCodeDebugging();
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
    /// Specifies Go build tags to pass to <c>go run</c> via <c>-tags</c>.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <param name="tags">One or more build tags (e.g., <c>"integration"</c>, <c>"netgo"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Specifies Go build tags passed via -tags")]
    public static IResourceBuilder<T> WithBuildTags<T>(this IResourceBuilder<T> builder, params string[] tags)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tags);

        return builder.WithAnnotation(new GoBuildTagsAnnotation(tags), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Specifies linker flags to pass to <c>go run</c> via <c>-ldflags</c>.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <param name="flags">The linker flags string (e.g., <c>"-X main.version=1.0.0"</c>).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Specifies Go linker flags passed via -ldflags")]
    public static IResourceBuilder<T> WithLdFlags<T>(this IResourceBuilder<T> builder, string flags)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(flags);

        return builder.WithAnnotation(new GoLdFlagsAnnotation(flags), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Enables the Go race detector by passing <c>-race</c> to <c>go run</c>.
    /// When used with <see cref="WithDelveServer{T}"/>, <c>-race</c> is forwarded via <c>--build-flags</c>.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Enables the Go race detector via -race")]
    public static IResourceBuilder<T> WithRaceDetector<T>(this IResourceBuilder<T> builder)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithAnnotation(new GoRaceDetectorAnnotation(), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Specifies compiler flags to pass to <c>go run</c> via <c>-gcflags</c>.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <param name="flags">The compiler flags string (e.g., <c>"all=-N -l"</c> to disable optimisations for Delve).</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Specifies Go compiler flags passed via -gcflags")]
    public static IResourceBuilder<T> WithGcFlags<T>(this IResourceBuilder<T> builder, string flags)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(flags);
        return builder.WithAnnotation(new GoGcFlagsAnnotation(flags), ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Runs <c>go mod tidy</c> before starting the application, ensuring <c>go.sum</c> is up to date.
    /// The main application waits for the tidy step to complete successfully before launching.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Runs go mod tidy before starting the application to ensure go.sum is up to date")]
    public static IResourceBuilder<T> WithTidy<T>(this IResourceBuilder<T> builder)
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
                $"{builder.Resource.Name}-tidy", "go", builder.Resource.WorkingDirectory);

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
    public static IResourceBuilder<T> WithVendor<T>(this IResourceBuilder<T> builder)
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
                $"{builder.Resource.Name}-vendor", "go", builder.Resource.WorkingDirectory);

            var vendor = builder.ApplicationBuilder
                .AddResource(vendorResource)
                .WithArgs("mod", "vendor")
                .ExcludeFromManifest();

            builder.WaitForCompletion(vendor);
        }

        return builder;
    }

    /// <summary>
    /// Runs <c>go vet ./...</c> before starting the application to catch static analysis issues.
    /// The main application waits for the lint step to complete successfully before launching.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    [AspireExport(Description = "Runs go vet ./... before starting the application to catch static analysis issues")]
    public static IResourceBuilder<T> WithVet<T>(this IResourceBuilder<T> builder)
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
            var lintResource = new ExecutableResource(
                $"{builder.Resource.Name}-vet", "go", builder.Resource.WorkingDirectory);

            var lint = builder.ApplicationBuilder
                .AddResource(lintResource)
                .WithArgs("vet", "./...")
                .ExcludeFromManifest();

            builder.WaitForCompletion(lint);
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
