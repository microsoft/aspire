// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.Go;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
    /// <param name="appDirectory">
    /// The path to the directory that acts as both the Go module root (where <c>go.mod</c> lives)
    /// and the Docker build context for <c>aspire publish</c>.
    /// </param>
    /// <param name="packagePath">
    /// The Go package to run or build, relative to <paramref name="appDirectory"/>.
    /// Defaults to <c>"."</c> (the module root itself).
    /// Use a sub-path such as <c>"./cmd/server"</c> when the main package is not at the module root
    /// (e.g. <c>api/cmd/server/main.go</c> with <c>api/go.mod</c>).
    /// This value is passed to <c>go run</c>, <c>dlv debug</c>, and <c>go build</c> consistently.
    /// </param>
    /// <param name="buildTags">Optional build tags passed to the compiler via <c>-tags</c> (e.g. <c>"netgo"</c>, <c>"integration"</c>).</param>
    /// <param name="ldFlags">Optional linker flags passed via <c>-ldflags</c> (e.g. <c>"-X main.version=1.0.0"</c>).</param>
    /// <param name="gcFlags">Optional compiler flags passed via <c>-gcflags</c> (e.g. <c>"all=-N -l"</c> to disable optimisations for Delve).</param>
    /// <param name="raceDetector">When <see langword="true"/>, enables the Go race detector by passing <c>-race</c> to <c>go run</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
    public static IResourceBuilder<GoAppResource> AddGoApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string appDirectory,
        string packagePath = ".",
        string[]? buildTags = null,
        string? ldFlags = null,
        string? gcFlags = null,
        bool raceDetector = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(packagePath);
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
                var pkg = ctx.Resource.TryGetLastAnnotation<GoPackagePathAnnotation>(out var pkgAnnotation)
                    ? pkgAnnotation.PackagePath
                    : ".";

                if (hasDelve)
                {
                    // Delve debug mode — global flags MUST precede the subcommand per the Delve CLI:
                    //   dlv --headless=true --listen=127.0.0.1:PORT --api-version=2 debug [--build-flags=...] <pkg> [-- args]
                    // See: https://www.jetbrains.com/help/go/attach-to-running-go-processes-with-debugger.html
                    ctx.Args.Add("--headless=true");
                    ctx.Args.Add($"--listen=127.0.0.1:{delveAnnotation!.Port}");
                    ctx.Args.Add("--api-version=2");
                    ctx.Args.Add("debug");

                    var buildFlags = BuildFlagsString(ctx.Resource);
                    if (buildFlags.Length > 0)
                    {
                        ctx.Args.Add($"--build-flags={buildFlags}");
                    }

                    ctx.Args.Add(pkg);

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
                    // Normal run mode: go run [-race] [-tags=...] [-ldflags=...] [-gcflags=...] <pkg> [args]
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

                    ctx.Args.Add(pkg);

                    foreach (var arg in programArgs)
                    {
                        ctx.Args.Add(arg);
                    }
                }
            })
            .WithRequiredCommand("go", "https://go.dev/dl/")
            .WithOtlpExporter()
            .WithVSCodeDebugging()
            .PublishAsDockerFile(containerBuilder =>
            {
                if (File.Exists(Path.Combine(appDirectory, "Dockerfile")))
                {
                    return;
                }

                containerBuilder.WithDockerfileBuilder(appDirectory, ctx =>
                {
                    var logger = ctx.Services.GetService<ILogger<GoAppResource>>();
                    var goVersion = GoVersionDetector.Detect(appDirectory);

                    ctx.Resource.TryGetLastAnnotation<DockerfileBaseImageAnnotation>(out var baseImageAnnotation);
                    var buildImage = baseImageAnnotation?.BuildImage ?? $"golang:{goVersion}-alpine";
                    var runtimeImage = baseImageAnnotation?.RuntimeImage ?? "alpine:latest";

                    // packagePath comes from the AddGoApp closure — ctx.Resource is the
                    // ContainerResource created by PublishAsDockerFile and does not carry
                    // the GoPackagePathAnnotation from the original GoAppResource.
                    var binaryName = ctx.Resource.Name;
                    var buildCmd = BuildDockerGoCommand(ctx.Resource, packagePath, binaryName);
                    var hasGoMod = File.Exists(Path.Combine(appDirectory, "go.mod"));
                    var hasGoSum = File.Exists(Path.Combine(appDirectory, "go.sum"));
                    var hasPrivate = ctx.Resource.TryGetLastAnnotation<GoPrivateAnnotation>(out var privateAnnotation);

                    var buildStage = ctx.Builder
                        .From(buildImage, "build")
                        .WorkDir("/app")
                        // CGO_ENABLED=0 produces a fully static binary; GOOS=linux ensures the
                        // correct target even when building on macOS or Windows hosts.
                        .Env("CGO_ENABLED", "0")
                        .Env("GOOS", "linux");

                    if (hasPrivate)
                    {
                        // ARG carries the non-sensitive username; the token comes via --mount=type=secret.
                        buildStage.Arg(privateAnnotation!.UsernameArgName);
                        // GOPRIVATE implicitly sets GONOSUMCHECK and GONOPROXY for the listed paths,
                        // so the toolchain fetches them directly rather than going through the public proxy.
                        buildStage.Env("GOPRIVATE", string.Join(",", privateAnnotation.PrivatePatterns));
                        // git is required for private module fetching over HTTPS.
                        if (buildImage.Contains("alpine", StringComparison.OrdinalIgnoreCase))
                        {
                            buildStage.Run("apk add --no-cache git");
                        }
                    }

                    if (hasGoMod)
                    {
                        buildStage.Copy("go.mod", "./");

                        if (hasGoSum)
                        {
                            buildStage.Copy("go.sum", "./");
                        }

                        if (hasPrivate)
                        {
                            // Write .netrc from ARG + secret, download modules, then remove .netrc
                            // — all in one layer so credentials never persist in the image.
                            var usernameRef = "${" + privateAnnotation!.UsernameArgName + "}";
                            var downloadCmd = string.Join(" && ",
                                $"GH_TOKEN=$(cat /run/secrets/{privateAnnotation.TokenSecretId})",
                                $"echo \"machine {privateAnnotation.AuthHost} login {usernameRef} password ${{GH_TOKEN}}\" >> $HOME/.netrc",
                                "go mod download",
                                "rm -f $HOME/.netrc");

                            buildStage.RunWithMounts(
                                downloadCmd,
                                "type=cache,target=/root/go/pkg/mod",
                                $"type=secret,id={privateAnnotation.TokenSecretId}");
                        }
                        else
                        {
                            // Cache the module download so repeated builds don't re-fetch the internet.
                            buildStage.RunWithMounts(
                                "go mod download",
                                "type=cache,target=/root/go/pkg/mod");
                        }
                    }

                    buildStage
                        .Copy(".", ".")
                        // Cache the Go build cache and the module cache across builds for fast
                        // incremental compilation inside Docker.
                        .RunWithMounts(
                            buildCmd,
                            "type=cache,target=/root/.cache/go-build",
                            "type=cache,target=/root/go/pkg/mod");

                    // Add intermediate FROM stages for any container files sources
                    // (e.g. FROM frontend AS frontend_stage).
                    ctx.Builder.AddContainerFilesStages(ctx.Resource, logger);

                    var runtimeStage = ctx.Builder.From(runtimeImage);

                    // Only use apk when the runtime image is Alpine-based.
                    // For custom images (e.g. debian:bookworm-slim) the caller
                    // is expected to supply a base image that already includes
                    // ca-certificates and tzdata, or extend the Dockerfile.
                    if (runtimeImage.Contains("alpine", StringComparison.OrdinalIgnoreCase))
                    {
                        runtimeStage
                            .Run("apk --no-cache add ca-certificates tzdata")
                            // Create a non-root user — Alpine uses addgroup/adduser.
                            .Run("addgroup -S app && adduser -S -G app app");
                    }
                    else
                    {
                        // Debian/Ubuntu and other glibc-based images use groupadd/useradd.
                        runtimeStage.Run("groupadd --system --gid 999 app && useradd --system --gid 999 --uid 999 --no-create-home app");
                    }

                    runtimeStage
                        .WorkDir("/app")
                        // Add COPY --from=<source> instructions for each container files source.
                        .AddContainerFiles(ctx.Resource, "/app", logger)
                        .CopyFrom("build", $"/app/{ctx.Resource.Name}", $"/app/{ctx.Resource.Name}")
                        .User("app")
                        .Entrypoint([$"/app/{ctx.Resource.Name}"]);
                });
            });

        if (packagePath != ".")
        {
            rb.WithAnnotation(new GoPackagePathAnnotation(packagePath), ResourceAnnotationMutationBehavior.Replace);
        }

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

        // Ensure source resources (that provide container files) build before this Go image.
        rb.WithPipelineConfiguration(context =>
        {
            if (rb.Resource.TryGetAnnotationsOfType<ContainerFilesDestinationAnnotation>(
                    out var containerFilesAnnotations))
            {
                var buildSteps = context.GetSteps(rb.Resource, WellKnownPipelineTags.BuildCompute);
                foreach (var containerFile in containerFilesAnnotations)
                {
                    buildSteps.DependsOn(context.GetSteps(containerFile.Source, WellKnownPipelineTags.BuildCompute));
                }
            }
        });

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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithAppArgs<T>(this IResourceBuilder<T> builder, params object[] args)
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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

            // Store the builder reference so WithModVendor/WithModDownload can chain after tidy.
            builder.WithAnnotation(new GoModTidyBuilderAnnotation(tidy));

            // If WithModVendor was called before WithModTidy (reverse order), retroactively
            // make the vendor sibling wait for tidy so the ordering is correct regardless of
            // which With* method the caller invokes first.
            if (builder.Resource.TryGetLastAnnotation<GoModVendorBuilderAnnotation>(out var existingVendor))
            {
                existingVendor.Sibling.WaitForCompletion(tidy);
            }

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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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

            // vendor must run after tidy: tidy updates go.mod/go.sum which vendor reads.
            if (builder.Resource.TryGetLastAnnotation<GoModTidyBuilderAnnotation>(out var tidyBuilder))
            {
                vendor.WaitForCompletion(tidyBuilder.Sibling);
            }

            // Store vendor builder so WithModDownload can chain after it.
            builder.WithAnnotation(new GoModVendorBuilderAnnotation(vendor));
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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
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

            // download must run after tidy (tidy may add/remove entries) and after
            // vendor (vendor and download both populate module state — run sequentially).
            if (builder.Resource.TryGetLastAnnotation<GoModVendorBuilderAnnotation>(out var vendorBuilder))
            {
                download.WaitForCompletion(vendorBuilder.Sibling);
            }
            else if (builder.Resource.TryGetLastAnnotation<GoModTidyBuilderAnnotation>(out var tidyBuilder))
            {
                download.WaitForCompletion(tidyBuilder.Sibling);
            }

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
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithVetTool<T>(this IResourceBuilder<T> builder)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (builder.Resource.TryGetLastAnnotation<GoVetToolAnnotation>(out _))
        {
            return builder;
        }

        builder.WithAnnotation(new GoVetToolAnnotation());

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
    /// Configures private Go module authentication for publish-time Dockerfile generation.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <param name="privatePatterns">
    /// One or more module path patterns that should bypass the public proxy and checksum database,
    /// e.g. <c>"*.mycompany.com"</c> or <c>"github.com/myorg"</c>.
    /// Passed verbatim to <c>GOPRIVATE</c>, which implicitly covers <c>GONOSUMCHECK</c> and <c>GONOPROXY</c>.
    /// </param>
    /// <param name="authHost">The Git host that requires authentication, e.g. <c>"github.com"</c>.</param>
    /// <param name="usernameArgName">
    /// The Docker build-arg name for the Git username. Defaults to <c>"GIT_USER"</c>.
    /// Pass it at build time with <c>--build-arg GIT_USER=myuser</c>.
    /// </param>
    /// <param name="tokenSecretId">
    /// The BuildKit secret ID for the Git access token. Defaults to <c>"gittoken"</c>.
    /// Pass it at build time with <c>--secret id=gittoken,src=/path/to/token</c>.
    /// </param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// Only affects the generated Dockerfile — has no effect in run mode, where the local
    /// Go toolchain picks up credentials from the developer's own <c>~/.netrc</c> or git
    /// credential helper.
    /// </para>
    /// <para>
    /// The generated build stage writes a temporary <c>.netrc</c> file from the username
    /// build-arg and the token secret, runs <c>go mod download</c>, then removes the file —
    /// all in a single layer so credentials never persist in the image.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddGoApp("api", "../go-api")
    ///        .WithGoPrivate(["github.com/myorg"], "github.com");
    /// </code>
    /// Build with:
    /// <code lang="sh">
    /// docker build --build-arg GIT_USER=myuser --secret id=gittoken,src=~/.git-token .
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<T> WithGoPrivate<T>(
        this IResourceBuilder<T> builder,
        string[] privatePatterns,
        string authHost,
        string usernameArgName = "GIT_USER",
        string tokenSecretId = "gittoken")
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(privatePatterns);
        ArgumentException.ThrowIfNullOrEmpty(authHost);
        ArgumentException.ThrowIfNullOrEmpty(usernameArgName);
        ArgumentException.ThrowIfNullOrEmpty(tokenSecretId);

        return builder.WithAnnotation(
            new GoPrivateAnnotation
            {
                PrivatePatterns = privatePatterns,
                AuthHost = authHost,
                UsernameArgName = usernameArgName,
                TokenSecretId = tokenSecretId,
            },
            ResourceAnnotationMutationBehavior.Replace);
    }

    /// <summary>
    /// Starts a headless Delve debug server so that any DAP-compatible client can attach remotely.
    /// The application is launched as
    /// <c>dlv --headless=true --listen=127.0.0.1:&lt;port&gt; --api-version=2 debug .</c>
    /// instead of <c>go run .</c>. Delve must be available on the PATH.
    /// </summary>
    /// <typeparam name="T">The type of the Go application resource.</typeparam>
    /// <param name="builder">The resource builder for the Go application.</param>
    /// <param name="port">The TCP port Delve listens on. Defaults to <c>2345</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
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
    [AspireExport]
    public static IResourceBuilder<T> WithDelveServer<T>(this IResourceBuilder<T> builder, int port = 2345)
        where T : GoAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Switch the underlying executable from "go" to "dlv" using Replace so that
        // calling WithDelveServer more than once is idempotent.
        return builder
            .WithAnnotation(
                new ExecutableAnnotation { Command = "dlv", WorkingDirectory = builder.Resource.WorkingDirectory },
                ResourceAnnotationMutationBehavior.Replace)
            .WithAnnotation(new GoDelveServerAnnotation(port), ResourceAnnotationMutationBehavior.Replace)
            .WithRequiredCommand("dlv", "https://github.com/go-delve/delve");
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
    private static string BuildDockerGoCommand(IResource resource, string packagePath = ".", string binaryName = "app")
    {
        var parts = new List<string> { "go", "build" };
        // Race detection requires CGO; the Dockerfile sets CGO_ENABLED=0 for a fully static
        // binary, so -race is intentionally excluded from publish/deploy builds.
        parts.AddRange(BuildFlagParts(resource, includeRace: false));
        parts.AddRange(["-o", $"/app/{binaryName}", packagePath]);
        return string.Join(" ", parts);
    }

    /// <summary>
    /// Builds the combined build-flags string from present annotations.
    /// Returns an empty string when no flags are set.
    /// For <c>go run</c> the flags are individual args; for <c>dlv --build-flags</c> they are combined.
    /// </summary>
    private static string BuildFlagsString(IResource resource) =>
        string.Join(" ", BuildFlagParts(resource, includeRace: true));

    /// <summary>
    /// Returns the ordered flag tokens derived from build annotations on the resource.
    /// Shared by <see cref="BuildDockerGoCommand"/> and <see cref="BuildFlagsString"/> so
    /// that flag ordering and quoting rules are defined in exactly one place.
    /// </summary>
    private static List<string> BuildFlagParts(IResource resource, bool includeRace = true)
    {
        var parts = new List<string>();

        if (includeRace && resource.TryGetLastAnnotation<GoRaceDetectorAnnotation>(out _))
        {
            parts.Add("-race");
        }

        if (resource.TryGetLastAnnotation<GoBuildTagsAnnotation>(out var tagsAnnotation))
        {
            parts.Add($"-tags={ShellQuote(string.Join(",", tagsAnnotation.Tags))}");
        }

        if (resource.TryGetLastAnnotation<GoLdFlagsAnnotation>(out var ldFlagsAnnotation))
        {
            parts.Add($"-ldflags={ShellQuote(ldFlagsAnnotation.Flags)}");
        }

        if (resource.TryGetLastAnnotation<GoGcFlagsAnnotation>(out var gcFlagsAnnotation))
        {
            parts.Add($"-gcflags={ShellQuote(gcFlagsAnnotation.Flags)}");
        }

        return parts;
    }

    /// <summary>
    /// Wraps <paramref name="value"/> in POSIX single quotes so that all shell
    /// metacharacters (<c>$</c>, <c>`</c>, <c>\</c>, <c>"</c>, <c>;</c>,
    /// <c>&amp;</c>, <c>|</c>, space, etc.) are treated as literals in Dockerfile
    /// <c>RUN</c> shell-form commands and Delve's <c>--build-flags</c> parser.
    /// Embedded single quotes are escaped with the standard POSIX technique:
    /// <c>'</c> → <c>'\''</c>.
    /// </summary>
    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''")}'";

}
