// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Java;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for collecting Maven Surefire JUnit test results.
/// </summary>
[Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class JUnitTestingExtensions
{
    internal const string ReportEnvironmentVariable = "ASPIRE_JUNIT_REPORT_DIRECTORY";

    /// <summary>
    /// Collects standard Maven Surefire JUnit XML results from a Java test resource during <c>aspire test</c>.
    /// </summary>
    /// <param name="builder">A single-module Java resource configured with <c>WithMavenGoal("test")</c>.</param>
    /// <returns>The original resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Configure Surefire's <c>reportsDirectory</c> as <c>${env.ASPIRE_JUNIT_REPORT_DIRECTORY}</c>
    /// in a Maven profile activated by that environment variable. Outside testing the variable is
    /// not set by this helper, so the project's ordinary report configuration remains in effect.
    /// Aspire does not install JUnit or Maven, rewrite the POM, or replace the project's wrapper.
    /// Gradle, Failsafe, reactor builds, retries, terminals, replicas and resource restarts are not supported.
    /// </para>
    /// <para>
    /// A fresh private directory is assigned before launch, independently of the runtime collector.
    /// After completion, including nonzero exits, the collector stages top-level <c>TEST-*.xml</c>
    /// reports into the logical resource output directory and publishes their test cases.
    /// Missing, malformed, inconsistent or unsupported reports fail collection without inventing tests.
    /// Failures map to Failed, errors to Error, skipped cases to Skipped, and other cases to Passed.
    /// No richer outcomes are inferred from exception messages. Process failure remains authoritative.
    /// The AppHost file-system service removes runner reports on disposal; staged reports remain.
    /// Ordinary runs retain the existing command and do not collect reports. Publishing excludes this resource.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddJavaApp("java-tests", "../java-tests")
    ///     .WithMavenGoal("test")
    ///     .WithJUnitTesting()
    ///     .WithEnvironment("SERVICE_URL", service.GetEndpoint("http"))
    ///     .WaitFor(service);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder is null.</exception>
    /// <exception cref="DistributedApplicationException">The test-mode launcher configuration is unsupported.</exception>
    [AspireExport]
    [Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<JavaAppResource> WithJUnitTesting(this IResourceBuilder<JavaAppResource> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.ExcludeFromManifest();
        if (!builder.Resource.TryGetLastAnnotation<JUnitTestingAnnotation>(out var state))
        {
            state = new JUnitTestingAnnotation();
            builder.WithAnnotation(state);
            if (builder.ApplicationBuilder.ExecutionContext.IsRunMode &&
                builder.ApplicationBuilder.Configuration["ASPIRE_TEST_OUTPUT_PATH"] is not null)
            {
                var launchTools = builder.Resource.Annotations.OfType<LaunchToolArgsCallbackAnnotation>().LastOrDefault();
                builder.ApplicationBuilder.OnBeforeStart((evt, _) =>
                {
                    ValidateLaunch(builder.Resource);
                    if (launchTools is null ||
                        !ReferenceEquals(launchTools, builder.Resource.Annotations.OfType<LaunchToolArgsCallbackAnnotation>().LastOrDefault()))
                    {
                        throw new DistributedApplicationException("The Java launch tool arguments cannot be replaced during JUnit testing.");
                    }
                    state.ReportDirectory ??= evt.Services.GetRequiredService<IFileSystemService>()
                        .TempDirectory.CreateTempSubdirectory("aspire-junit-").Path;
                    builder.WithEnvironment(ReportEnvironmentVariable, state.ReportDirectory);
                    builder.WithArgs(context =>
                    {
                        // Java's wrapper/goal prefix is evaluated separately from WithArgs.
                        // Validate only the ordinary tail here, after all authored callbacks.
                        var invocation = JavaHostingExtensions.WrapperInvocationFor(
                            JavaBuildToolResolver.ResolveWrapperPath(builder.Resource, JavaBuildTool.Maven, OperatingSystem.IsWindows()),
                            builder.Resource.WorkingDirectory, OperatingSystem.IsWindows());
                        if (builder.Resource.Command != invocation.Command)
                        {
                            throw new DistributedApplicationException("JUnit testing requires the existing native Maven wrapper test launch.");
                        }
                        ValidateArguments(context.Args);
                    });
                    return Task.CompletedTask;
                });
                var starts = 0;
                builder.OnBeforeResourceStarted((_, _, _) =>
                {
                    if (Interlocked.Increment(ref starts) != 1)
                    {
                        throw new DistributedApplicationException("JUnit testing resources do not support restarts within a test run.");
                    }
                    return Task.CompletedTask;
                });
            }
        }
        builder.WithTesting(async context =>
        {
            // A collector can start before prestart hooks. The launch owns configuration;
            // completion establishes when its report directory is ready to harvest.
            await context.WaitForCompletionAsync().ConfigureAwait(false);
            await SurefireJUnitReport.CollectAsync(context, state.ReportDirectory ??
                throw new InvalidOperationException("The JUnit report directory was not initialized before execution.")).ConfigureAwait(false);
        });

        return builder;
    }

    private static void ValidateLaunch(JavaAppResource resource)
    {
        if (!resource.TryGetLastAnnotation<JavaBuildToolAnnotation>(out var launch) ||
            launch.Tool != JavaBuildTool.Maven || launch.Args.FirstOrDefault() != "test" ||
            resource.HasAnnotationOfType<JavaBuildStepAnnotation>() ||
            resource.TryGetLastAnnotation<ReplicaAnnotation>(out var replicas) && replicas.Replicas != 1 ||
            resource.HasAnnotationOfType<TerminalAnnotation>() ||
            !resource.TryGetLastAnnotation<ExecutableLaunchRecipeAnnotation>(out var recipe) ||
            recipe.Recipe != DirectExecutableLaunchRecipe.Instance)
        {
            throw new DistributedApplicationException("JUnit testing supports one native Maven 'test' launch without a separate build resource.");
        }
        ValidateArguments(launch.Args.Skip(1).Cast<object>());
        var pom = XDocument.Load(Path.Combine(resource.WorkingDirectory, "pom.xml"));
        if (pom.Root is not { } root || root.Name.LocalName != "project" ||
            root.Descendants().Any(element => element.Name.LocalName == "modules"))
        {
            throw new DistributedApplicationException("JUnit testing supports single-module Maven projects only.");
        }
    }

    internal static void ValidateArguments(IEnumerable<object> arguments)
    {
        foreach (var argument in arguments)
        {
            if (argument is not string value ||
                !(value.StartsWith("-D", StringComparison.Ordinal) && value.Length > 2 ||
                  value.StartsWith("-P", StringComparison.Ordinal) && value.Length > 2 ||
                  value is "-q" or "--quiet" or "-B" or "--batch-mode" or "-e" or "--errors" or
                      "-X" or "--debug" or "-ntp" or "--no-transfer-progress" or "-o" or "--offline" or
                      "-U" or "--update-snapshots"))
            {
                throw new ArgumentException($"Unsupported JUnit Maven argument '{argument}'. Use a single test goal with attached -D/-P options or logging/offline flags.", nameof(arguments));
            }
        }
    }

    private sealed class JUnitTestingAnnotation : IResourceAnnotation
    {
        public string? ReportDirectory { get; set; }
    }
}
