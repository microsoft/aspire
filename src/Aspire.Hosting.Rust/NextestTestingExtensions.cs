// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Rust;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for collecting Rust nextest results in an Aspire application.
/// </summary>
[Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class NextestTestingExtensions
{
    /// <summary>
    /// Collects final JUnit results from a finite <c>cargo nextest run</c> executable during <c>aspire test</c>.
    /// </summary>
    /// <param name="builder">The existing Cargo executable resource builder.</param>
    /// <returns>The original resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Requires separately installed cargo-nextest 0.9.143 or later. The command must be <c>cargo</c>
    /// with arguments <c>nextest run</c>, optionally preceded by a Rust toolchain selector.
    /// Cargo workspace, package, features, target, build profile (<c>--cargo-profile</c>),
    /// working directory, environment, references, and waits remain caller-owned.
    /// This is not an adapter for AddRustApp, whose entrypoint is cargo run.
    /// </para>
    /// <para>
    /// During testing, an Aspire-owned <c>--config-file</c> replaces repository nextest configuration,
    /// without editing it. This explicitly controlled mode reserves the runner profile, report path,
    /// retries, stress runs, and fail-fast behavior. It does not load repository execution settings,
    /// setup scripts, test groups, or per-test overrides. Cargo configuration is unchanged.
    /// Ignored tests are reported as skipped; filtered-out and unexecuted tests are not inferred.
    /// Nextest does not run doctests. Only final reported outcomes contribute to the summary.
    /// </para>
    /// <para>
    /// Supports one native invocation without terminals, replicas, restarts, or retries.
    /// Missing, malformed, empty, or inconsistent reports fail collection. Nonzero execution remains
    /// authoritative even when useful results are collected. Raw stdout and stderr are retained.
    /// Cancellation does not fabricate results. Ordinary runs retain the caller's arguments and
    /// configuration without collection; the test resource is excluded from publishing.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddExecutable("rust-tests", "cargo", "../rust-api", "nextest", "run", "--workspace")
    ///     .WithNextestTesting()
    ///     .WithEnvironment("API_URL", api.GetEndpoint("http"))
    ///     .WaitFor(api);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    /// <exception cref="ArgumentException">Arguments conflict with managed nextest reporting or execution.</exception>
    /// <exception cref="DistributedApplicationException">The testing executable has an unsupported launch configuration.</exception>
    [AspireExport]
    [Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<ExecutableResource> WithNextestTesting(this IResourceBuilder<ExecutableResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithRequiredCommand("cargo", "https://www.rust-lang.org/tools/install")
            .WithRequiredCommand("cargo-nextest", "https://nexte.st/docs/installation/pre-built-binaries/")
            .ExcludeFromManifest();

        builder.WithTesting(testing =>
        {
            if (testing.OutputPath is not { } outputPath)
            {
                return;
            }
            testing.CollectResults(NextestJunitReport.CollectAsync);
            builder.ApplicationBuilder.OnBeforeStart(async (@event, cancellationToken) =>
            {
                if (builder.Resource.Command != "cargo" ||
                    builder.Resource.TryGetLastAnnotation<ReplicaAnnotation>(out var replicas) && replicas.Replicas != 1 ||
                    builder.Resource.TryGetLastAnnotation<TerminalAnnotation>(out _) ||
                    !builder.Resource.TryGetLastAnnotation<ExecutableLaunchRecipeAnnotation>(out var launch) ||
                    launch.Recipe != DirectExecutableLaunchRecipe.Instance)
                {
                    throw new DistributedApplicationException("Nextest testing supports exactly one native, non-terminal Cargo executable.");
                }
                NextestJunitReport.ValidateDirectory(outputPath, testing.RunContext!.OutputPath);
                var configPath = Path.Combine(outputPath, "nextest.toml");
                var streamOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous
                };
                if (!OperatingSystem.IsWindows())
                {
                    streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                var stream = new FileStream(configPath, streamOptions);
                await using (stream.ConfigureAwait(false))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(CreateConfiguration(
                        Path.Combine(outputPath, NextestJunitReport.FileName))), cancellationToken).ConfigureAwait(false);
                }
                // Tool config is lower priority than repository config and cannot guarantee a private
                // JUnit path. An explicit replacement config also prevents hidden per-test retry overrides.
                // https://nexte.st/docs/configuration/#hierarchical-configuration
                builder.WithArgs(async context =>
                {
                    var resolved = new List<string>();
                    foreach (var argument in context.Args)
                    {
                        var value = await context.Resource.ResolveValueAsync(context.ExecutionContext,
                            context.Logger, argument, "nextest argument", context.CancellationToken).ConfigureAwait(false);
                        resolved.Add(value?.Value ?? throw new ArgumentException("Nextest arguments cannot resolve to null."));
                    }
                    ValidateArguments(resolved);
                    context.Args.Add("--config-file=" + configPath);
                    context.Args.Add("--profile=aspire");
                    context.Args.Add("--no-fail-fast");
                    context.Args.Add("--no-tests=fail");
                    context.Args.Add("--retries=0");
                    context.Args.Add("--message-format=human");
                });
            });
            var starts = 0;
            builder.OnBeforeResourceStarted((_, _, _) =>
            {
                if (Interlocked.Increment(ref starts) != 1)
                {
                    throw new DistributedApplicationException("Nextest testing resources do not support restarts within a test run.");
                }
                return Task.CompletedTask;
            });
        });

        return builder;
    }

    internal static void ValidateArguments(IReadOnlyList<string> args)
    {
        var start = args.Count > 0 && args[0].StartsWith('+') ? 1 : 0;
        if (args.Count < start + 2 || args[start] != "nextest" || args[start + 1] != "run")
        {
            throw new ArgumentException("WithNextestTesting requires 'cargo [ +toolchain ] nextest run'.", nameof(args));
        }
        foreach (var value in args.Skip(start + 2))
        {
            var flag = value.Split('=', 2)[0];
            if (flag is "--" or "--config-file" or "--tool-config-file" or "--profile" or "--retries" or
                "--fail-fast" or "--ff" or "--no-fail-fast" or "--nff" or "--max-fail" or "--no-tests" or "--stress-count" or
                "--stress-duration" or "--message-format" or "--message-format-version" or
                "--override-version-check" or "--no-run" or "--no-capture" or "--nocapture" or
                "--archive-file" or "--cargo-metadata" or "--binaries-metadata" or "--unit-graph" or
                "--debugger" or "--tracer" or "--rerun" ||
                value.StartsWith("-R", StringComparison.Ordinal) ||
                value.StartsWith("-P", StringComparison.Ordinal))
            {
                throw new ArgumentException($"The nextest argument '{value}' is reserved by WithNextestTesting during testing.", nameof(args));
            }
        }
    }

    internal static string CreateConfiguration(string reportPath)
    {
        // TOML basic strings use escaped quotes/backslashes, e.g. "C:\\tests\\junit.xml".
        // Preserve Unicode path characters directly; JSON surrogate escapes are not valid TOML.
        var escaped = new StringBuilder();
        foreach (var character in reportPath)
        {
            escaped.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                _ when char.IsControl(character) => "\\u" + ((int)character).ToString("X4", System.Globalization.CultureInfo.InvariantCulture),
                _ => character.ToString()
            });
        }

        return $$"""
            nextest-version = "0.9.143"
            [profile.aspire]
            fail-fast = false
            retries = 0
            [profile.aspire.junit]
            path = "{{escaped}}"
            report-skipped = "ignored"
            flaky-fail-status = "failure"

            """;
    }
}
