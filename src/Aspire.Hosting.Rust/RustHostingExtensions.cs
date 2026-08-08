// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001
#pragma warning disable ASPIREDOCKERFILEBUILDER001

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
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
    /// <param name="appDirectory">The directory containing the Rust application files.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// The resource runs <c>cargo run</c> in <paramref name="appDirectory"/>, which must contain a
    /// <c>Cargo.toml</c>. Cargo requires the two kinds of argument to be separated by <c>--</c>, so they
    /// are configured separately: <c>WithCargoArgs</c> adds arguments for cargo itself (before the
    /// separator) and <c>WithArgs</c> adds arguments for the application (after it).
    /// </para>
    /// <para>
    /// Debugging is wired up automatically. In VS Code the resource is built with <c>cargo build</c> and
    /// the resulting binary is launched under a native debugger, so the cargo arguments are applied to
    /// the build rather than to <c>cargo run</c>.
    /// </para>
    /// <para>
    /// OTLP export and dev certificate trust are configured by default. Rust does not read a port from
    /// the environment on its own, so bind to the port named by <c>WithHttpEndpoint(env: ...)</c> rather
    /// than a hard-coded one.
    /// </para>
    /// <para>
    /// When publishing, a multi-stage Dockerfile is generated that builds the crate inside the container;
    /// the crate is never compiled on the host. If the app directory already contains a <c>Dockerfile</c>,
    /// that file is used instead. Call <c>WithDockerfileBaseImage</c> to override the build and runtime
    /// base images.
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

        return builder.AddResource(resource)
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
                    await annotation.Callback(new RustCargoArgsCallbackContext(cargoArgs, context.CancellationToken)).ConfigureAwait(false);
                }

                resource.ResolvedCargoArgs = cargoArgs;

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
            })
            // Must be registered after the cargo args above, otherwise the debug args filter has
            // nothing to strip. See https://github.com/microsoft/aspire/issues/18929
            .WithVSCodeDebugging()
            .PublishAsDockerFile(containerBuilder =>
            {
                // A hand-written Dockerfile always wins: the generated one is a convenience for crates that
                // do not have one, not something that should silently shadow the user's own container build.
                if (File.Exists(Path.Combine(resource.WorkingDirectory, "Dockerfile")))
                {
                    return;
                }

                containerBuilder.WithDockerfileBuilder(
                    resource.WorkingDirectory,
                    context => RustDockerfileGenerator.WriteAsync(resource, context));
            });
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
    /// Configures cargo features for the Rust application.
    /// </summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="features">The features to enable.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{T}"/> for chaining.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    [AspireExport]
    public static IResourceBuilder<T> WithCargoFeatures<T>(this IResourceBuilder<T> builder, params string[] features)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(features);

        GetOrAddCargoOptions(builder).Features = features;
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
    /// the target to the build image with <c>rustup target add</c>. Pairing the triple with base images that
    /// can build and run the result is the caller's: a glibc (<c>-gnu</c>) triple needs glibc images, and a
    /// triple for another architecture needs a cross-linker in the build image, both of which
    /// <c>WithDockerfileBaseImage</c> supplies.
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
        workingDirectory = Path.GetFullPath(workingDirectory);

        // A relative manifest path is resolved the same way cargo resolves it: against the directory the
        // process is launched in.
        var directory = manifestPath is { } path
            ? Path.GetDirectoryName(Path.GetFullPath(path, workingDirectory))
            : workingDirectory;

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory, "Cargo.lock")))
            {
                return true;
            }

            if (string.Equals(directory, workingDirectory, StringComparison.Ordinal))
            {
                return false;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    [Experimental("ASPIREEXTENSION001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    internal static IResourceBuilder<T> WithVSCodeDebugging<T>(this IResourceBuilder<T> builder)
        where T : RustAppResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithDebugSupport(
            async (mode, cancellationToken) =>
            {
                // DCP resolves the resource's arguments before it asks for the launch configuration
                // (ExecutableCreator.CreateObjectAsync builds the args, then invokes this annotator),
                // so the resolved cargo arguments are reused here. That keeps the debug build identical
                // to the run command and means user cargo argument callbacks run exactly once per launch.
                var cargoArgs = builder.Resource.ResolvedCargoArgs
                    ?? throw new InvalidOperationException(
                        $"Cargo arguments for resource '{builder.Resource.Name}' have not been resolved yet. " +
                        "The debug launch configuration must be created after the resource's arguments are evaluated.");

                var workingDirectory = Path.GetFullPath(builder.Resource.WorkingDirectory);
                var executablePath = await ResolveDebugExecutablePathAsync(
                    builder.Resource,
                    workingDirectory,
                    builder.ApplicationBuilder.ExecutionContext,
                    cancellationToken).ConfigureAwait(false);

                return new RustLaunchConfiguration
                {
                    Mode = mode,
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
            "rust",
            static context =>
            {
                if (context.Args is not [string runCommand, ..] || runCommand != "run")
                {
                    return;
                }

                context.Args.RemoveAt(0);

                while (context.Args is [string arg, ..])
                {
                    context.Args.RemoveAt(0);
                    if (arg == "--")
                    {
                        break;
                    }
                }
            });
    }

    // Works out the file the debug build will produce, so the debugger can run a plain `cargo build` and
    // launch the result instead of parsing cargo's JSON artifact stream to find it.
    //
    // This is the same resolution publishing uses, against the same cargo metadata, so the debugged process
    // and the published container run the same binary. It is also strictly better than reading the build's
    // artifacts: `cargo build` ignores `default-run` and therefore reports every binary in the package,
    // whereas metadata reports `default-run` itself and so matches what `cargo run` launches.
    private static async Task<string> ResolveDebugExecutablePathAsync(RustAppResource resource, string workingDirectory, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        // The crate layout is fixed for the lifetime of the app host, so the first successful resolution is
        // reused. Without this, every launch configuration request would pay for another `cargo metadata`.
        if (resource.ResolvedDebugExecutablePath is { } cached)
        {
            return cached;
        }

        await resource.DebugExecutablePathGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (resource.ResolvedDebugExecutablePath is { } resolvedWhileWaiting)
            {
                return resolvedWhileWaiting;
            }

            var executablePath = await ResolveDebugExecutablePathCoreAsync(resource, workingDirectory, executionContext, cancellationToken).ConfigureAwait(false);
            resource.ResolvedDebugExecutablePath = executablePath;

            return executablePath;
        }
        finally
        {
            resource.DebugExecutablePathGate.Release();
        }
    }

    private static async Task<string> ResolveDebugExecutablePathCoreAsync(RustAppResource resource, string workingDirectory, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        var options = resource.TryGetLastAnnotation<RustCargoOptionsAnnotation>(out var cargoOptions)
            ? cargoOptions
            : new RustCargoOptionsAnnotation();

        // The resource's own environment, resolved the same way DCP resolves it before launching the
        // process, so cargo sees exactly what the app will see.
        //
        // This is a second evaluation of the environment callbacks: DCP has already resolved them into
        // exe.Spec.Env by the time it asks for the launch configuration, but the launch configuration
        // producer is only handed the debug mode, so the resolved values are not reachable from here.
        // Aspire.Hosting.Maui resolves the environment the same way, for the same reason, when it
        // materialises environment variables into an MSBuild targets file.
        // Remove once the producer receives the resolved configuration:
        // https://github.com/microsoft/aspire/issues/18956
        var environment = (await ExecutionConfigurationBuilder.Create(resource)
            .WithEnvironmentVariablesConfig()
            .BuildAsync(executionContext, cancellationToken: cancellationToken).ConfigureAwait(false))
            .EnvironmentVariables
            .ToDictionary(StringComparer.Ordinal);

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

    // OTLP export plus certificate trust so outbound TLS calls made by the app pick up the dev/test
    // certificate bundle. Certificate trust needs nothing Rust-specific: the app host already exports
    // SSL_CERT_DIR (and SSL_CERT_FILE, for the scopes that replace the system store rather than add to it),
    // which is what OpenSSL and rustls-native-certs read.
    private static IResourceBuilder<RustAppResource> WithRustDefaults(this IResourceBuilder<RustAppResource> builder)
        => builder.WithOtlpExporter();
}

#pragma warning restore ASPIREEXTENSION001
#pragma warning restore ASPIREDOCKERFILEBUILDER001
