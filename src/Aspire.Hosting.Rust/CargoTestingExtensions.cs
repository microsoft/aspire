// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for running built-in Cargo tests in an Aspire application.
/// </summary>
[Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class CargoTestingExtensions
{
    /// <summary>
    /// Includes a finite <c>cargo test</c> executable in <c>aspire test</c> using its process exit status.
    /// </summary>
    /// <param name="builder">The existing Cargo executable resource builder.</param>
    /// <returns>The original resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Requires only the Rust toolchain. No nextest installation, nightly compiler, or machine-readable
    /// test report is required. Arguments, environment, Cargo configuration, workspace/package selection,
    /// features, profiles, target selection, and test filters remain caller-owned.
    /// The command must be <c>cargo [ +toolchain ] test</c>, not AddRustApp's <c>cargo run</c>.
    /// </para>
    /// <para>
    /// The process exit status determines the resource outcome. No individual test results are published
    /// or inferred from console output. Zero published results does not mean no Rust tests ran.
    /// Default Cargo semantics include unit, integration, and documentation tests as applicable;
    /// caller-supplied options can change which tests run, including compiling only with <c>--no-run</c>.
    /// See <see href="https://doc.rust-lang.org/cargo/commands/cargo-test.html"/>.
    /// </para>
    /// <para>
    /// During testing, raw stdout and stderr are captured into the assigned private resource directory
    /// after authoritative process completion, including nonzero exits. The collector drains both streams
    /// without requiring a report or changing a failing resource into a passing one.
    /// Supports one native invocation without terminals, replicas, or restarts.
    /// Ordinary runs retain the caller's execution without capture. Publishing excludes this test resource.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddExecutable("rust-tests", "cargo", "../rust-api", "test", "--workspace")
    ///     .WithCargoTesting()
    ///     .WithEnvironment("API_URL", api.GetEndpoint("http"))
    ///     .WaitFor(api);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    /// <exception cref="ArgumentException">The command arguments do not select built-in Cargo testing.</exception>
    /// <exception cref="DistributedApplicationException">The executable has an unsupported launch configuration.</exception>
    [AspireExport]
    [Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<ExecutableResource> WithCargoTesting(this IResourceBuilder<ExecutableResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithRequiredCommand("cargo", "https://www.rust-lang.org/tools/install")
            .ExcludeFromManifest();

        builder.WithTesting(options =>
        {
            if (!options.IsTestRun)
            {
                return;
            }

            // Capture drains even after a failed exit; resource completion remains authoritative.
            // It publishes no testcase outcomes and does not interpret Cargo's human output.
            options.CollectResults(context => context.CaptureOutputAsync());
            builder.ApplicationBuilder.OnBeforeStart((_, _) =>
            {
                if (builder.Resource.Command != "cargo" ||
                    builder.Resource.TryGetLastAnnotation<ReplicaAnnotation>(out var replicas) && replicas.Replicas != 1 ||
                    builder.Resource.TryGetLastAnnotation<TerminalAnnotation>(out _) ||
                    !builder.Resource.TryGetLastAnnotation<ExecutableLaunchRecipeAnnotation>(out var launch) ||
                    launch.Recipe != DirectExecutableLaunchRecipe.Instance)
                {
                    throw new DistributedApplicationException("Cargo testing supports exactly one native, non-terminal Cargo executable.");
                }
                builder.WithArgs(async context =>
                {
                    var resolved = new List<string>();
                    foreach (var argument in context.Args)
                    {
                        var value = await context.Resource.ResolveValueAsync(context.ExecutionContext,
                            context.Logger, argument, "Cargo test argument", context.CancellationToken).ConfigureAwait(false);
                        resolved.Add(value?.Value ?? throw new ArgumentException("Cargo test arguments cannot resolve to null."));
                    }
                    ValidateArguments(resolved);
                });
                return Task.CompletedTask;
            });
            var starts = 0;
            builder.OnBeforeResourceStarted((_, _, _) =>
            {
                if (Interlocked.Increment(ref starts) != 1)
                {
                    throw new DistributedApplicationException("Cargo testing resources do not support restarts within a test run.");
                }
                return Task.CompletedTask;
            });
        });

        return builder;
    }

    internal static void ValidateArguments(IReadOnlyList<string> args)
    {
        // Preserve Cargo's "cargo [+toolchain] test [options] [filter] [-- libtest-options]" shape.
        var start = args.Count > 0 && args[0].StartsWith('+') ? 1 : 0;
        if (args.Count <= start || args[start] != "test")
        {
            throw new ArgumentException("WithCargoTesting requires 'cargo [ +toolchain ] test'.", nameof(args));
        }
    }
}
