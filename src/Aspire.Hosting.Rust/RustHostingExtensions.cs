// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREFILESYSTEM001
#pragma warning disable ASPIREDOCKERFILEBUILDER001
#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREPIPELINES003

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ApplicationModel.Docker;
using Aspire.Hosting.Pipelines;
using Aspire.Hosting.Publishing;
using Aspire.Hosting.Rust;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Rust applications to an <see cref="IDistributedApplicationBuilder"/>.
/// </summary>
public static class RustHostingExtensions
{
    /// <summary>
    /// Adds a Rust application to the application model.
    /// </summary>
    /// <param name="builder">The <see cref="IDistributedApplicationBuilder"/> to add the resource to.</param>
    /// <param name="name">The name of the resource.</param>
    /// <param name="appDirectory">The working directory for cargo and the Docker build context used when publishing.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// The resource runs <c>cargo run</c> in <paramref name="appDirectory"/>. Cargo discovers the manifest
    /// from that directory by default; use <c>WithCargoManifestPath</c> to select another manifest. Cargo
    /// requires the two kinds of argument to be separated by <c>--</c>, so they are configured separately:
    /// <c>WithCargoArgs</c> adds arguments for cargo itself (before the separator) and <c>WithArgs</c> adds
    /// arguments for the application (after it).
    /// </para>
    /// <para>
    /// Debugging is wired up automatically. In VS Code the resource is built with <c>cargo build</c> and
    /// the resulting binary is launched under a native debugger, so the cargo arguments are applied to
    /// the build rather than to <c>cargo run</c>.
    /// </para>
    /// <para>
    /// Aspire configures the OTLP endpoint and development certificate environment variables. The Rust
    /// application must still enable the transport and TLS features required by its OpenTelemetry SDK and
    /// load native trust roots when using the development certificate. Rust does not read a port from the
    /// environment on its own, so bind to the port named by <c>WithHttpEndpoint(env: ...)</c> rather than a
    /// hard-coded one.
    /// </para>
    /// <para>
    /// When publishing, a multi-stage Dockerfile is generated that builds the crate inside the container;
    /// the crate is never compiled on the host. If the app directory already contains a <c>Dockerfile</c>,
    /// that file is used instead. Call <c>WithDockerfileBaseImage</c> once with both arguments to override
    /// the build and runtime base images together; each call replaces the previous image configuration.
    /// </para>
    /// </remarks>
    /// <example>
    /// Add a Rust application to the app host and expose an HTTP endpoint:
    /// <code language="csharp">
    /// var builder = DistributedApplication.CreateBuilder(args);
    ///
    /// builder.AddRustApp("api", "../rust-api")
    ///        .WithHttpEndpoint(env: "PORT")
    ///        .WithCargoReleaseBuild();
    ///
    /// builder.Build().Run();
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<RustAppResource> AddRustApp(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        appDirectory = Path.GetFullPath(appDirectory, builder.AppHostDirectory);
        var resource = new RustAppResource(name, appDirectory);

        // TryAdd so a test (or a caller who wants to answer from a cached manifest) can substitute its own
        // reader by registering one before or after AddRustApp.
        builder.Services.TryAddSingleton<ICargoMetadataReader, CargoMetadataReader>();

        var resourceBuilder = builder.AddResource(resource)
            .WithRequiredCommand("cargo", "https://www.rust-lang.org/tools/install")
            .WithRustDefaults()
            .WithCargoArgs(context => AddInitialCargoArgs(resource, builder.ExecutionContext, context.Args))
            .WithArgs(async context =>
            {
                // Resolve the cargo arguments once and record them: the debug launch configuration
                // reuses this list rather than invoking the user's callbacks a second time.
                var cargoArgs = new List<string>();

                foreach (var annotation in resource.Annotations.OfType<RustCargoArgsCallbackAnnotation>())
                {
                    await annotation.Callback(new RustCargoArgsCallbackContext(resource, cargoArgs, context.CancellationToken)).ConfigureAwait(false);
                }

                resource.ResolvedCargoArgs = cargoArgs;
            })
            .WithLaunchToolArgs(context =>
            {
                var cargoArgs = resource.ResolvedCargoArgs
                    ?? throw new InvalidOperationException(
                        $"Cargo arguments for resource '{resource.Name}' have not been resolved yet. " +
                        "The launch tool arguments must be created after the resource's arguments are evaluated.");

                // No validation is performed on these arguments: every value is passed through raw for
                // cargo itself to accept or reject. Nothing here inspects what they contain, so only the
                // WithCargo* options feed the executable-path and Dockerfile resolution — a flag that
                // arrives as a raw string through WithCargoArgs is not parsed back out. Doing so would be
                // a second, subtly-different implementation of cargo's own argument handling that could
                // never be complete, since a WithArgs callback can append arguments after this point.
                context.Args.Add("run");
                foreach (var cargoArg in cargoArgs)
                {
                    context.Args.Add(cargoArg);
                }

                context.Args.Add("--");
            }, ownedByLaunchConfigurationType: "rust")
            .WithVSCodeDebugging()
            .PublishAsDockerFile();

        // The generated image copies files out of each container files source, so those sources have to be
        // built first. PublishAsDockerFile removes the Rust resource from the model, but the container it
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

        if (builder.ExecutionContext.IsPublishMode)
        {
            if (!builder.TryCreateResourceBuilder<ContainerResource>(resource.Name, out var containerBuilder)
                || !containerBuilder.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var provisionalDockerfile))
            {
                throw new InvalidOperationException(
                    $"The published Rust app '{resource.Name}' was not converted to a Dockerfile container resource.");
            }

            var publishState = new RustPublishState();
            containerBuilder.WithContainerBuildOptions(context =>
            {
                if (publishState.TargetPlatform is { } targetPlatform)
                {
                    context.TargetPlatform = targetPlatform;
                }
            });

            builder.OnBeforeStart((_, _) =>
            {
                FinalizePublishDockerfile(builder, resource, provisionalDockerfile, publishState);
                return Task.CompletedTask;
            });
        }

        return resourceBuilder;
    }

    /// <summary>
    /// Adds command-line arguments to the cargo command used by a Rust application.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="args">The cargo arguments to append before <c>--</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Arguments are forwarded to cargo verbatim and are not interpreted. Publishing and debugging work out
    /// which file cargo produces from the <c>WithCargo*</c> options alone, so a target selection that has a
    /// dedicated method — <c>WithCargoBinTarget</c>, <c>WithCargoExample</c>, <c>WithCargoPackage</c>,
    /// <c>WithCargoProfile</c>, <c>WithCargoReleaseBuild</c> and <c>WithCargoTarget</c> — has to go through
    /// it rather than being passed here.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoArgs<T>(this IResourceBuilder<T> builder, params string[] args)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        return builder.WithCargoArgs(context =>
        {
            foreach (var arg in args)
            {
                context.Args.Add(arg);
            }
        });
    }

    /// <summary>
    /// Adds command-line arguments to the cargo command used by a Rust application.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback that computes cargo arguments at execution time.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use the string[] overload instead.</remarks>
    [AspireExportIgnore(Reason = "Callback-based cargo arguments are not expressible in polyglot app hosts.")]
    public static IResourceBuilder<T> WithCargoArgs<T>(this IResourceBuilder<T> builder, Action<RustCargoArgsCallbackContext> callback)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        return builder.WithCargoArgs(context =>
        {
            callback(context);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Adds command-line arguments to the cargo command used by a Rust application.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="callback">A callback that computes cargo arguments at execution time.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <remarks>This method is not available in polyglot app hosts. Use the string[] overload instead.</remarks>
    [AspireExportIgnore(Reason = "Callback-based cargo arguments are not expressible in polyglot app hosts.")]
    public static IResourceBuilder<T> WithCargoArgs<T>(this IResourceBuilder<T> builder, Func<RustCargoArgsCallbackContext, Task> callback)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        var annotation = new RustCargoArgsCallbackAnnotation(callback);
        return builder.WithAnnotation(annotation);
    }

    /// <summary>
    /// Configures the Rust application to run using release optimization.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="releaseBuild"><see langword="true"/> to add <c>--release</c>; otherwise <see langword="false"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Publishing builds an optimized image by default, so pass <see langword="false"/> to opt a published
    /// image out of <c>--release</c>.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoReleaseBuild<T>(this IResourceBuilder<T> builder, bool releaseBuild = true)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        GetOrAddCargoOptions(builder).ReleaseBuild = releaseBuild;
        return builder;
    }

    /// <summary>
    /// Configures the Rust application to build and run with the exact dependency versions recorded in
    /// <c>Cargo.lock</c>.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="locked"><see langword="true"/> to add <c>--locked</c>; otherwise <see langword="false"/>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Passed to cargo as <c>--locked</c>, which fails the build rather than updating <c>Cargo.lock</c>.
    /// Publishing already adds this whenever the crate has a lock file, so a published image cannot silently
    /// pick up dependency versions that were never committed; pass <see langword="false"/> to opt out.
    /// See https://doc.rust-lang.org/cargo/commands/cargo-build.html#manifest-options
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoLocked<T>(this IResourceBuilder<T> builder, bool locked = true)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        GetOrAddCargoOptions(builder).Locked = locked;
        return builder;
    }

    /// <summary>
    /// Adds cargo features for the Rust application.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="features">The features to enable.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>Repeated calls accumulate features in call order.</remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoFeatures<T>(this IResourceBuilder<T> builder, params string[] features)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(features);

        var options = GetOrAddCargoOptions(builder);
        options.Features = options.Features is { } existingFeatures
            ? [.. existingFeatures, .. features]
            : [.. features];
        return builder;
    }

    /// <summary>
    /// Configures the binary target to run for Rust applications that declare more than one.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="binName">The binary target name, as declared by <c>[[bin]] name</c> in Cargo.toml.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Passed to cargo as <c>--bin</c>. Debugging and publishing also use it to work out which file cargo
    /// produces, so a package with several binaries must select one here (or set <c>default-run</c> in
    /// Cargo.toml).
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoBinTarget<T>(this IResourceBuilder<T> builder, string binName)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(binName);

        GetOrAddCargoOptions(builder).BinTarget = binName;
        return builder;
    }

    /// <summary>
    /// Configures an example target to run instead of a binary.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="exampleName">The example name, as declared by a file or directory under <c>examples/</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Passed to cargo as <c>--example</c>. Cargo writes examples to <c>target/&lt;profile&gt;/examples/</c>,
    /// and debugging and publishing both follow that layout.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoExample<T>(this IResourceBuilder<T> builder, string exampleName)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(exampleName);

        GetOrAddCargoOptions(builder).Example = exampleName;
        return builder;
    }

    /// <summary>
    /// Configures the workspace package to build and run.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="packageName">The cargo package name.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Passed to cargo as <c>--package</c>. Required when the crate directory is a workspace whose default
    /// members include more than one package with a binary target, because the binary to run would otherwise
    /// be ambiguous. Library-only members are ignored, so an app crate beside library crates needs nothing.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoPackage<T>(this IResourceBuilder<T> builder, string packageName)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);

        GetOrAddCargoOptions(builder).Package = packageName;
        return builder;
    }

    /// <summary>
    /// Configures the target triple cargo builds for.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="target">The target triple, for example <c>x86_64-unknown-linux-musl</c>.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Passed to cargo as <c>--target</c>. Cargo writes a cross-compiled binary to
    /// <c>target/&lt;triple&gt;/&lt;profile&gt;/</c>, and the generated Dockerfile follows that layout and adds
    /// the target's standard library to the build image with <c>rustup target add</c>.
    /// <para>
    /// Aspire-generated Dockerfiles map native Linux x86_64, aarch64, 32-bit ARM, and 32-bit x86 targets to
    /// Docker Linux platforms. Docker's <c>linux/arm</c> platform represents the ARMv7 variant. The default
    /// build and runtime images support x86_64 and aarch64 musl targets. A 32-bit musl target needs a custom
    /// build image but can use the default runtime image. Other ABIs require custom build and runtime images
    /// configured together in one <c>WithDockerfileBaseImage</c> call because later calls replace the previous
    /// image configuration.
    /// </para>
    /// <para>
    /// Custom images opt out of default-image compatibility checks, but the target must still map to a
    /// supported native Docker Linux platform. A custom build image must already contain any linker or
    /// native dependencies the target needs; <c>WithDockerfileBaseImage</c> changes images but does not
    /// install cross-compilation tooling. Other targets require an authored Dockerfile for publishing.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoTarget<T>(this IResourceBuilder<T> builder, string target)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        GetOrAddCargoOptions(builder).Target = target;
        return builder;
    }

    /// <summary>
    /// Configures the <c>Cargo.toml</c> cargo builds from.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="manifestPath">The path to the manifest, absolute or relative to the app directory.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Passed to cargo as <c>--manifest-path</c>. Cargo otherwise discovers the manifest by searching upwards
    /// from the app directory, which is what most apps want, so this is only needed to point at a manifest
    /// somewhere else — for example the crate of one workspace member when the app directory is the
    /// workspace root.
    /// <para>
    /// Publishing copies the app directory into the container image and rewrites the manifest path to match,
    /// so the manifest has to live inside the app directory and the path has to be relative to it. An absolute
    /// path is accepted when running and rejected when publishing.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoManifestPath<T>(this IResourceBuilder<T> builder, string manifestPath)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        GetOrAddCargoOptions(builder).ManifestPath = manifestPath;
        return builder;
    }

    /// <summary>
    /// Configures the named cargo profile to build with.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="profileName">The profile name, for example <c>dev</c>, <c>release</c>, or a custom profile.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// Passed to cargo as <c>--profile</c>, which takes precedence over <c>WithCargoReleaseBuild</c> because
    /// cargo rejects <c>--profile</c> and <c>--release</c> together.
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoProfile<T>(this IResourceBuilder<T> builder, string profileName)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        GetOrAddCargoOptions(builder).Profile = profileName;
        return builder;
    }

    // Gets the resource's existing RustCargoOptionsAnnotation, or creates and attaches a new one. Callers mutate
    // the returned instance's properties directly rather than adding a new annotation per call, so repeated
    // WithCargo* calls (in any order) all end up configuring the same shared annotation instance.
    private static RustCargoOptionsAnnotation GetOrAddCargoOptions<T>(IResourceBuilder<T> builder)
        where T : RustAppResource
    {
        if (!builder.Resource.TryGetLastAnnotation<RustCargoOptionsAnnotation>(out var options))
        {
            options = new RustCargoOptionsAnnotation();
            builder.WithAnnotation(options);
        }

        return options;
    }

    private static void AddInitialCargoArgs(
        RustAppResource resource,
        DistributedApplicationExecutionContext executionContext,
        IList<string> args)
    {
        // A resource that called no WithCargo* method still takes the publish defaults below, so carry on
        // with an empty set of options rather than returning.
        var options = resource.TryGetLastAnnotation<RustCargoOptionsAnnotation>(out var cargoOptions)
            ? cargoOptions
            : new RustCargoOptionsAnnotation();

        if (options.Features is { Count: > 0 } features)
        {
            args.Add("--features");
            args.Add(string.Join(",", features));
        }

        if (options.BinTarget is { } binTarget)
        {
            args.Add("--bin");
            args.Add(binTarget);
        }

        if (options.Example is { } example)
        {
            args.Add("--example");
            args.Add(example);
        }

        if (options.Package is { } package)
        {
            args.Add("--package");
            args.Add(package);
        }

        if (options.ManifestPath is { } manifestPath)
        {
            args.Add("--manifest-path");
            args.Add(manifestPath);
        }

        if (options.Target is { } target)
        {
            args.Add("--target");
            args.Add(target);
        }

        if (options.Locked == true)
        {
            args.Add("--locked");
        }

        // Cargo rejects --profile and --release together, so an explicit profile wins.
        if (options.Profile is { } profile)
        {
            args.Add("--profile");
            args.Add(profile);
        }
        else if (options.ReleaseBuild == true)
        {
            args.Add("--release");
        }

        if (executionContext.IsRunMode)
        {
            return;
        }

        // The defaults below apply to publishing only. Run mode leaves cargo's own defaults alone: a debug
        // build is what a developer iterating on the app wants, and a lock file that needs updating should
        // update rather than fail. A published image is the opposite on both counts.

        // --locked fails the build rather than writing a lock file, so a published image can only build the
        // dependency versions that were committed. It is only safe to add when a lock file actually exists;
        // cargo errors out with "the lock file needs to be updated but --locked was passed" otherwise, which
        // would break publishing for crates that deliberately do not commit one (libraries, mostly).
        if (options.Locked is null && HasLockFile(resource.WorkingDirectory, options.ManifestPath))
        {
            args.Add("--locked");
        }

        // Cargo rejects --release alongside --profile, so a resource that named a profile is already
        // optimized as it asked to be. An explicit `false` means the image deliberately does without.
        if (options.Profile is null && options.ReleaseBuild is null)
        {
            args.Add("--release");
        }
    }

    // Cargo keeps a single lock file per workspace, next to the root manifest, which sits at or above the
    // package being built. Publishing requires that root to be inside the directory cargo runs in, since the
    // container build copies nothing else, so searching from the manifest up to the working directory covers
    // every layout publishing supports.
    // See https://doc.rust-lang.org/cargo/guide/cargo-toml-vs-cargo-lock.html
    private static bool HasLockFile(string workingDirectory, string? manifestPath)
    {
        // Trailing separators are load bearing here. Path.GetFullPath("../rust-api/") keeps the separator
        // while Path.GetDirectoryName returns the same directory without one, so comparing the two spellings
        // for equality never matches and the walk would climb past the app directory and find an unrelated
        // repository lock file that is not in the Docker build context.
        workingDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));

        // A relative manifest path is resolved the same way cargo resolves it: against the directory the
        // process is launched in.
        var directory = manifestPath is { } path
            ? Path.GetDirectoryName(Path.GetFullPath(path, workingDirectory))
            : workingDirectory;

        // A manifest outside the app directory starts the walk outside it, so containment is checked before
        // each probe rather than only after one, which also stops the walk at the app directory itself.
        // Path.GetDirectoryName only keeps a trailing separator for a filesystem root, which IsAtOrBelow
        // handles, so no further trimming is needed inside the loop.
        while (directory is { } candidate && IsAtOrBelow(candidate, workingDirectory))
        {
            if (File.Exists(Path.Combine(candidate, "Cargo.lock")))
            {
                return true;
            }

            directory = Path.GetDirectoryName(candidate);
        }

        return false;
    }

    private static bool IsAtOrBelow(string candidate, string directory)
    {
        if (string.Equals(candidate, directory, StringComparison.Ordinal))
        {
            return true;
        }

        // A filesystem root already ends in a separator, so appending another would build a prefix that no
        // path starts with.
        var prefix = Path.EndsInDirectorySeparator(directory)
            ? directory
            : directory + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.Ordinal);
    }

    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithDebugSupport(
            async context =>
            {
                // DCP resolves the resource's arguments before it asks for the launch configuration
                // (ExecutableCreator.CreateObjectAsync builds the args, then invokes this producer),
                // so the resolved cargo arguments are reused here. That keeps the debug build identical
                // to the run command and means user cargo argument callbacks run exactly once per launch.
                var resource = (RustAppResource)context.Resource;
                var cargoArgs = resource.ResolvedCargoArgs
                    ?? throw new InvalidOperationException(
                        $"Cargo arguments for resource '{resource.Name}' have not been resolved yet. " +
                        "The debug launch configuration must be created after the resource's arguments are evaluated.");

                var workingDirectory = Path.GetFullPath(resource.WorkingDirectory);
                var executablePath = await ResolveDebugExecutablePathAsync(
                    resource,
                    workingDirectory,
                    builder.ApplicationBuilder.ExecutionContext,
                    context.EnvironmentVariables,
                    context.CancellationToken).ConfigureAwait(false);

                return new RustLaunchConfiguration
                {
                    Mode = context.Mode,
                    WorkingDirectory = workingDirectory,
                    Cargo = new RustCargoLaunchTarget
                    {
                        // The same cargo arguments run mode uses, so any target selection the user made
                        // (`--bin`, `--example`, `--package`) narrows the debug build the same way it
                        // narrows `cargo run`.
                        Args = ["build", .. cargoArgs],
                        ExecutablePath = executablePath
                    }
                };
            },
            "rust");
    }

    // Works out the file the debug build will produce, so the debugger can run a plain `cargo build` and
    // launch the result instead of parsing cargo's JSON artifact stream to find it.
    //
    // This is the same resolution publishing uses, against the same cargo metadata, so the debugged process
    // and the published container run the same binary. It is also strictly better than reading the build's
    // artifacts: `cargo build` ignores `default-run` and therefore reports every binary in the package,
    // whereas metadata reports `default-run` itself and so matches what `cargo run` launches.
    private static async Task<string> ResolveDebugExecutablePathAsync(
        RustAppResource resource,
        string workingDirectory,
        DistributedApplicationExecutionContext executionContext,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var options = resource.TryGetLastAnnotation<RustCargoOptionsAnnotation>(out var cargoOptions)
            ? cargoOptions
            : new RustCargoOptionsAnnotation();

        var metadata = await executionContext.Services.GetRequiredService<ICargoMetadataReader>()
            .ReadAsync(workingDirectory, options.ManifestPath, resource.Name, environment, cancellationToken)
            .ConfigureAwait(false);

        var target = RustCargoTargetResolver.Resolve(metadata, options, executionContext, resource.Name);

        // CARGO_BUILD_TARGET selects a target the same way --target does, and cargo then writes the binary
        // under an extra triple directory. Cargo metadata does not report the value, so it is read from the
        // resource environment here. WithCargoTarget still wins because a command-line --target beats both
        // the environment and .cargo/config.toml.
        // A `[build] target` in .cargo/config.toml is not resolved: cargo metadata does not report it either
        // and reading it needs the unstable `cargo config get`, so that layout is left to
        // https://github.com/microsoft/aspire/issues/18956.
        // See https://doc.rust-lang.org/cargo/reference/config.html#buildtarget
        if (target.Target is null && environment.TryGetValue("CARGO_BUILD_TARGET", out var buildTarget) && buildTarget.Length > 0)
        {
            target = target with { Target = buildTarget };
        }

        return target.GetExecutablePath(metadata.TargetDirectory);
    }

    private static void FinalizePublishDockerfile(
        IDistributedApplicationBuilder applicationBuilder,
        RustAppResource resource,
        DockerfileBuildAnnotation provisionalDockerfile,
        RustPublishState publishState)
    {
        if (!applicationBuilder.TryCreateResourceBuilder<ContainerResource>(resource.Name, out var containerBuilder))
        {
            throw new InvalidOperationException(
                $"The published Rust app '{resource.Name}' was not converted to a container resource.");
        }

        var annotations = containerBuilder.Resource.Annotations;
        if (!containerBuilder.Resource.TryGetLastAnnotation<DockerfileBuildAnnotation>(out var activeDockerfile)
            || !ReferenceEquals(activeDockerfile, provisionalDockerfile))
        {
            // PublishAsDockerFile is idempotent, so callers can replace the integration's provisional
            // Dockerfile or factory after AddRustApp returns. That later configuration owns its context,
            // generated content, platform, and target handling.
            return;
        }

        ContainerTargetPlatform? targetPlatform = null;

        annotations.Remove(provisionalDockerfile);

        var workingDirectory = Path.GetFullPath(resource.WorkingDirectory);

        // The app model remains mutable after AddRustApp returns. Resolve both the authored-Dockerfile
        // decision and the build context at the final model hook so a later WithWorkingDirectory changes
        // the directory cargo inspects and the directory Docker copies together.
        if (File.Exists(Path.Combine(workingDirectory, "Dockerfile")))
        {
            containerBuilder.WithAnnotation(
                new DockerfileBuildAnnotation(
                    workingDirectory,
                    Path.Combine(workingDirectory, "Dockerfile"),
                    provisionalDockerfile.Stage),
                ResourceAnnotationMutationBehavior.Replace);
        }
        else
        {
            targetPlatform = ResolveContainerTargetPlatform(resource, containerBuilder.Resource);
            publishState.TargetPlatform = targetPlatform;
            containerBuilder.WithAnnotation(
                CreateGeneratedDockerfileAnnotation(
                    applicationBuilder,
                    resource,
                    workingDirectory,
                    provisionalDockerfile.Stage),
                ResourceAnnotationMutationBehavior.Replace);
        }

        var dockerfile = annotations.OfType<DockerfileBuildAnnotation>().Last();
        foreach (var buildArgument in provisionalDockerfile.BuildArguments)
        {
            dockerfile.BuildArguments[buildArgument.Key] = buildArgument.Value;
        }

        foreach (var buildSecret in provisionalDockerfile.BuildSecrets)
        {
            dockerfile.BuildSecrets[buildSecret.Key] = buildSecret.Value;
        }

        dockerfile.ImageName = provisionalDockerfile.ImageName ?? dockerfile.ImageName;
        dockerfile.ImageTag = provisionalDockerfile.ImageTag ?? dockerfile.ImageTag;
        dockerfile.HasEntrypoint = provisionalDockerfile.HasEntrypoint;
        dockerfile.BuildContextIgnoreContent = provisionalDockerfile.BuildContextIgnoreContent;

        if (targetPlatform is { } platform)
        {
            var cargoTarget = resource.TryGetLastAnnotation<RustCargoOptionsAnnotation>(out var options)
                ? options.Target
                : null;

            containerBuilder.WithContainerBuildOptions(context =>
            {
                if (context.TargetPlatform is { } configuredPlatform && configuredPlatform != platform)
                {
                    throw new DistributedApplicationException(
                        $"The Rust app '{resource.Name}' targets '{cargoTarget}', " +
                        $"which requires container target platform '{platform}', but WithContainerBuildOptions selected '{configuredPlatform}'.");
                }

                // The earlier Rust callback establishes the mapped default before caller callbacks run.
                // Reapply it here only after confirming a later callback did not choose a conflicting platform.
                context.TargetPlatform = platform;
            });
        }
    }

    private static DockerfileBuildAnnotation CreateGeneratedDockerfileAnnotation(
        IDistributedApplicationBuilder applicationBuilder,
        RustAppResource resource,
        string workingDirectory,
        string? stage)
    {
        // Replacing the integration-owned provisional annotation directly avoids rerunning WithDockerfileFactory,
        // which would replace caller-owned pipeline annotations and append duplicate pipeline configuration.
        var dockerfilePath = applicationBuilder.ExecutionContext.Services
            .GetRequiredService<IFileSystemService>()
            .TempDirectory.CreateTempFile("Dockerfile").Path;

        return new DockerfileBuildAnnotation(workingDirectory, dockerfilePath, stage)
        {
            DockerfileFactory = async factoryContext =>
            {
                var dockerfileBuilder = new DockerfileBuilder();
                var callbackContext = new DockerfileBuilderCallbackContext(
                    factoryContext.Resource,
                    dockerfileBuilder,
                    factoryContext.Services,
                    factoryContext.CancellationToken);

                await RustDockerfileGenerator.WriteAsync(resource, callbackContext).ConfigureAwait(false);

                using var stream = new MemoryStream();
                using var writer = new StreamWriter(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    leaveOpen: true)
                {
                    NewLine = "\n"
                };

                await dockerfileBuilder.WriteAsync(writer, factoryContext.CancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(factoryContext.CancellationToken).ConfigureAwait(false);

                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        };
    }

    private static ContainerTargetPlatform? ResolveContainerTargetPlatform(
        RustAppResource resource,
        ContainerResource container)
    {
        var target = resource.TryGetLastAnnotation<RustCargoOptionsAnnotation>(out var options)
            ? options.Target
            : null;

        if (target is null)
        {
            return null;
        }

        // Built-in Rust Linux targets use <architecture>-<vendor>-linux-<environment>, for example
        // armv7-unknown-linux-musleabihf. Custom target JSON paths and non-Linux targets do not carry enough
        // information to choose a native Docker platform and therefore need an authored Dockerfile.
        var targetParts = target.Split('-');
        if (targetParts.Length < 4 || !string.Equals(targetParts[2], "linux", StringComparison.Ordinal))
        {
            throw CreateUnsupportedContainerTargetException(resource, target);
        }

        var architecture = targetParts[0];
        // ContainerTargetPlatform.LinuxArm emits Docker's canonical linux/arm platform, which containerd
        // normalizes to the v7 variant. See https://github.com/containerd/platforms/blob/main/platforms.go.
        var platform = architecture switch
        {
            "x86_64" => ContainerTargetPlatform.LinuxAmd64,
            "aarch64" => ContainerTargetPlatform.LinuxArm64,
            "i386" or "i486" or "i586" or "i686" => ContainerTargetPlatform.Linux386,
            "arm" or "armv4t" or "armv5te" or "armv7" or "thumbv7neon" => ContainerTargetPlatform.LinuxArm,
            _ => throw CreateUnsupportedContainerTargetException(resource, target)
        };

        var targetEnvironment = targetParts[3];
        // The official Rust image index used by the default build stage publishes amd64 and arm64 images,
        // but not Docker's 32-bit arm or 386 platforms. Those architectures therefore need a custom build
        // image even when the default Alpine runtime image already supports the target platform.
        var defaultBuildImageCompatible = platform is ContainerTargetPlatform.LinuxAmd64 or ContainerTargetPlatform.LinuxArm64
            && string.Equals(targetEnvironment, "musl", StringComparison.Ordinal);
        var defaultRuntimeImageCompatible = platform == ContainerTargetPlatform.LinuxArm
            ? targetEnvironment is "musleabi" or "musleabihf"
            : string.Equals(targetEnvironment, "musl", StringComparison.Ordinal);

        var baseImages = container.Annotations.OfType<DockerfileBaseImageAnnotation>().LastOrDefault()
            ?? resource.Annotations.OfType<DockerfileBaseImageAnnotation>().LastOrDefault();
        var customBuildImageConfigured = baseImages?.BuildImage is not null;
        var customRuntimeImageConfigured = baseImages?.RuntimeImage is not null;

        if (!defaultBuildImageCompatible && !defaultRuntimeImageCompatible
            && (!customBuildImageConfigured || !customRuntimeImageConfigured))
        {
            throw new DistributedApplicationException(
                $"The Rust app '{resource.Name}' targets '{target}', which is not compatible with the default " +
                "musl build and runtime images. Configure both images in a single " +
                "WithDockerfileBaseImage(buildImage: ..., runtimeImage: ...) call before publishing; later calls replace " +
                "the previous configuration.");
        }

        if (!defaultBuildImageCompatible && !customBuildImageConfigured)
        {
            throw new DistributedApplicationException(
                $"The Rust app '{resource.Name}' targets '{target}', but the default Rust build image does not support " +
                $"container target platform '{platform}'. Configure buildImage with WithDockerfileBaseImage before publishing.");
        }

        if (!defaultRuntimeImageCompatible && !customRuntimeImageConfigured)
        {
            throw new DistributedApplicationException(
                $"The Rust app '{resource.Name}' targets '{target}', but the default Rust runtime image is not compatible " +
                "with its target ABI. Configure runtimeImage with WithDockerfileBaseImage before publishing.");
        }

        return platform;
    }

    private static DistributedApplicationException CreateUnsupportedContainerTargetException(
        RustAppResource resource,
        string target)
        => new(
            $"The Rust app '{resource.Name}' targets '{target}', which cannot be mapped to a supported Docker Linux " +
            "container platform. Generated Rust containers support native Linux x86_64, aarch64, 32-bit ARM, " +
            "and 32-bit x86 targets. Use an authored Dockerfile to publish this target.");

    private sealed class RustPublishState
    {
        public ContainerTargetPlatform? TargetPlatform { get; set; }
    }

    // OTLP export plus certificate trust so outbound TLS calls made by the app pick up the dev/test
    // certificate bundle. Certificate trust needs nothing Rust-specific: the app host already exports
    // SSL_CERT_DIR (and SSL_CERT_FILE, for the scopes that replace the system store rather than add to it),
    // which is what OpenSSL and rustls-native-certs read.
    private static IResourceBuilder<RustAppResource> WithRustDefaults(this IResourceBuilder<RustAppResource> builder)
        => builder.WithOtlpExporter();
}

#pragma warning restore ASPIREEXTENSION001
#pragma warning restore ASPIREFILESYSTEM001
#pragma warning restore ASPIREDOCKERFILEBUILDER001
#pragma warning restore ASPIREPIPELINES003
