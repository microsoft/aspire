// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Go;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for running standard Go tests in an Aspire application.
/// </summary>
[Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class GoTestingExtensions
{
    /// <summary>
    /// Adds a finite Go test executable and collects its JSON results during <c>aspire test</c>.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The test resource name.</param>
    /// <param name="appDirectory">The module or workspace directory, relative to the AppHost directory or absolute.</param>
    /// <param name="packagePatterns">The package patterns to test. Defaults to <c>./...</c>.</param>
    /// <returns>The Go test resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Runs <c>go test -json -count=1</c> during testing and <c>go test</c> during ordinary runs.
    /// Use WithArgs for build/test flags and an optional final <c>-args</c> tail. Testing reserves
    /// reporting, cache, execution-wrapper, benchmark, and sustained-fuzzing flags, including GOFLAGS.
    /// Module/workspace selection and build/module caches remain Go-owned; no tidy or tool installation runs.
    /// </para>
    /// <para>
    /// Named top-level invocations contribute to the common summary. Subtest outcomes and hierarchy
    /// remain in parent messages and raw JSON; repeated names receive invocation ordinals.
    /// Package/build failures and incomplete output fail collection without inventing test cases.
    /// Valid zero-test package outcomes contribute zero results. Partial named outcomes survive later errors.
    /// Standard Go tests only export telemetry when explicitly instrumented.
    /// </para>
    /// <para>
    /// This run-only resource supports one native process invocation, without terminals, replicas,
    /// restarts, or retries. Do not replace the executable or reporting flags during testing.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddGoTest("api-tests", "../go-api")
    ///     .WithArgs("-tags=integration", "-run=TestAPI")
    ///     .WithReference(api)
    ///     .WaitFor(api);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    /// <exception cref="ArgumentException">A required name, path, or package pattern is empty.</exception>
    [AspireExport]
    [Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<GoTestResource> AddGoTest(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory,
        string[]? packagePatterns = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);
        var packages = packagePatterns is null ? ["./..."] : packagePatterns.ToArray();
        if (packages.Length == 0 || packages.Any(p => string.IsNullOrWhiteSpace(p) || p.StartsWith('-')))
        {
            throw new ArgumentException("At least one non-option Go package pattern is required.", nameof(packagePatterns));
        }
        var resource = builder.AddResource(new GoTestResource(name, Path.GetFullPath(appDirectory, builder.AppHostDirectory)))
            .WithRequiredCommand("go", "https://go.dev/dl/")
            .WithIconName("Beaker")
            .WithOtlpExporter()
            .ExcludeFromManifest();

        resource.WithTesting(testing =>
        {
            builder.OnBeforeStart((_, _) =>
            {
                if (testing.IsTestRun &&
                    (resource.Resource.Command != "go" ||
                     resource.Resource.TryGetLastAnnotation<ReplicaAnnotation>(out var replicas) && replicas.Replicas != 1 ||
                     resource.Resource.TryGetLastAnnotation<TerminalAnnotation>(out _) ||
                     !resource.Resource.TryGetLastAnnotation<ExecutableLaunchRecipeAnnotation>(out var launch) ||
                     launch.Recipe != DirectExecutableLaunchRecipe.Instance))
                {
                    throw new DistributedApplicationException("Go testing supports exactly one native, non-terminal go process.");
                }
                resource.WithArgs(async context =>
                {
                    if (testing.IsTestRun)
                    {
                        var resolved = new List<object>();
                        foreach (var argument in context.Args)
                        {
                            var value = await context.Resource.ResolveValueAsync(context.ExecutionContext,
                                context.Logger, argument, "Go test argument", context.CancellationToken).ConfigureAwait(false);
                            resolved.Add(value?.Value ?? throw new ArgumentException("Go test arguments cannot resolve to null."));
                        }
                        ValidateArguments(resolved);
                    }
                    var separator = context.Args.IndexOf("-args");
                    var insertion = separator < 0 ? context.Args.Count : separator;
                    foreach (var package in packages)
                    {
                        context.Args.Insert(insertion++, package);
                    }
                    context.Args.Insert(0, "test");
                    if (testing.IsTestRun)
                    {
                        context.Args.Insert(1, "-json");
                        context.Args.Insert(2, "-count=1");
                    }
                });
                if (testing.IsTestRun)
                {
                    // Register after model definition so later WithEnvironment calls, including
                    // deferred GOFLAGS parameters, are present before the strict settings probe.
                    resource.WithEnvironment(async context =>
                    {
                        var flags = context.EnvironmentVariables.GetValueOrDefault("GOFLAGS");
                        var resolved = flags is null ? null : (await context.Resource.ResolveValueAsync(
                            context.ExecutionContext, context.Logger, flags, "GOFLAGS", context.CancellationToken).ConfigureAwait(false))?.Value;
                        var effective = await GoRunDebugging.ReadGoFlagsAsync(context, resolved, throwOnError: true).ConfigureAwait(false);
                        ValidateArguments(GoRunDebugging.SplitFlags(effective).Cast<object>());
                    });
                }
                return Task.CompletedTask;
            });
            if (!testing.IsTestRun)
            {
                return;
            }
            testing.CollectResults(async context =>
            {
                await context.CaptureOutputAsync().ConfigureAwait(false);
                var stream = new FileStream(Path.Combine(context.OutputPath, "stdout.log"),
                    FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (stream.ConfigureAwait(false))
                {
                    await GoTestJsonReport.ReadAsync(stream, context.PublishResult, context.CancellationToken).ConfigureAwait(false);
                }
            });
            var starts = 0;
            resource.OnBeforeResourceStarted((_, _, _) =>
            {
                if (Interlocked.Increment(ref starts) != 1)
                {
                    throw new DistributedApplicationException("Go testing resources do not support restarts within a test run.");
                }
                return Task.CompletedTask;
            });
        });

        return resource;
    }

    internal static void ValidateArguments(IEnumerable<object> args)
    {
        var tail = false;
        foreach (var argument in args)
        {
            if (argument is not string value)
            {
                throw new ArgumentException("Go testing arguments must be literal strings.", nameof(args));
            }
            if (value == "-args")
            {
                tail = true;
                continue;
            }
            var flag = value.Split('=', 2)[0].TrimStart('-');
            if (flag.StartsWith("test.", StringComparison.Ordinal))
            {
                flag = flag[5..];
            }
            if (value.StartsWith('-') &&
                (flag is "json" or "count" or "c" or "list" or "exec" or "bench" or "fuzz" or "v" ||
                 !tail && value == "--"))
            {
                throw new ArgumentException($"The Go argument '{value}' is reserved by AddGoTest during testing.", nameof(args));
            }
        }
    }
}
