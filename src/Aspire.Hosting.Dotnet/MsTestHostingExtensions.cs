// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet;

#pragma warning disable ASPIRETESTING001 // Implements the experimental testing integration.

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for adding executable MSTest projects to an Aspire application.
/// </summary>
[Experimental(MsTestDiagnostics.DiagnosticId, UrlFormat = MsTestDiagnostics.UrlFormat)]
public static class MsTestHostingExtensions
{
    /// <summary>
    /// Adds an MSTest project and collects its TRX results during <c>aspire test</c>.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The test resource name.</param>
    /// <param name="projectPath">An explicit .csproj path, relative to the AppHost directory or absolute.</param>
    /// <param name="options">Optional build selection and runner arguments.</param>
    /// <returns>The test resource builder.</returns>
    /// <remarks>
    /// <para>
    /// The project must be an executable using the MSTest Microsoft.Testing.Platform runner with
    /// Microsoft.Testing.Extensions.TrxReport registered. MSTest.Sdk's Default profile includes it.
    /// Aspire does not modify the project or its dependencies. Legacy VSTest libraries are not supported.
    /// </para>
    /// <para>
    /// Execution uses <c>dotnet run --project &lt;path&gt; --no-launch-profile --</c>, building independently
    /// of AddDotnetProject's coordinated builds. Select one framework for multi-targeted projects.
    /// During testing, the runner writes <c>results.trx</c> into the resource's private output directory.
    /// Collection waits for process completion, including failures, and missing or invalid reports fail the run.
    /// Reports and attachments remain in that directory; their file permissions are controlled by the runner.
    /// Only one process invocation per resource is supported during testing, without replicas or restarts.
    /// </para>
    /// <para>
    /// Ordinary runs launch the project without Aspire reporting flags or collection.
    /// The resource is excluded from publishing. Environment, references, endpoints, and waits compose normally.
    /// Additional WithArgs arguments belong to the runner, not dotnet; reporting flags and response files are reserved.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddMsTest("api-tests", "../Api.Tests/Api.Tests.csproj")
    ///     .WithReference(api)
    ///     .WaitFor(api);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder or project path is null.</exception>
    /// <exception cref="ArgumentException">A name, path, option, or runner argument is invalid.</exception>
    /// <exception cref="FileNotFoundException">The project does not exist.</exception>
    [AspireExport]
    [Experimental(MsTestDiagnostics.DiagnosticId, UrlFormat = MsTestDiagnostics.UrlFormat)]
    public static IResourceBuilder<MsTestResource> AddMsTest(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name,
        string projectPath,
        MsTestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        if (!string.Equals(Path.GetExtension(projectPath), ".csproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("AddMsTest requires an explicit executable MSTest .csproj path.", nameof(projectPath));
        }
        projectPath = Path.GetFullPath(projectPath, builder.AppHostDirectory);
        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("The MSTest project does not exist.", projectPath);
        }
        if (options?.Configuration is { } configuration)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(configuration);
        }
        if (options?.TargetFramework is { } framework)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(framework);
        }
        var runnerArgs = options?.Args?.ToArray() ?? [];
        ValidateRunnerArguments(runnerArgs);
        var launchArgs = new List<object> { "run", "--project", projectPath, "--no-launch-profile" };
        if (options?.Configuration is { } selectedConfiguration)
        {
            launchArgs.AddRange(["--configuration", selectedConfiguration]);
        }
        if (options?.TargetFramework is { } selectedFramework)
        {
            launchArgs.AddRange(["--framework", selectedFramework]);
        }
        launchArgs.Add("--");

        var resource = builder.AddResource(new MsTestResource(name, Path.GetDirectoryName(projectPath)!))
            .WithIconName("Beaker")
            .WithRequiredCommand("dotnet", "https://dotnet.microsoft.com/download")
            .WithOtlpExporterIfEndpointAvailable(OtlpProtocol.HttpProtobuf)
            .ExcludeFromManifest()
            .WithArgs(context =>
            {
                foreach (var arg in launchArgs.Concat(runnerArgs))
                {
                    context.Args.Add(arg);
                }
            });

        resource.WithTesting(testing =>
        {
            if (testing.OutputPath is not { } outputPath)
            {
                return;
            }
            testing.CollectResults(MsTestTrxReport.CollectAsync);
            // Append after model definition so WithArgs remains composable, but cannot redirect
            // the owned report. MTP report filenames can be absolute paths:
            // https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-test-reports
            builder.OnBeforeStart((_, _) =>
            {
                if (resource.Resource.TryGetLastAnnotation<ReplicaAnnotation>(out var replicas) && replicas.Replicas != 1)
                {
                    throw new DistributedApplicationException("MSTest testing resources support exactly one replica.");
                }
                resource.WithArgs(context =>
                {
                    if (!context.Args.Take(launchArgs.Count).SequenceEqual(launchArgs))
                    {
                        throw new DistributedApplicationException("The MSTest dotnet launch arguments cannot be replaced during testing.");
                    }
                    ValidateRunnerArguments(context.Args.Skip(launchArgs.Count));
                    foreach (var arg in new[] { "--report-trx", "--results-directory", outputPath,
                        "--report-trx-filename", MsTestTrxReport.FileName })
                    {
                        context.Args.Add(arg);
                    }
                });
                return Task.CompletedTask;
            });
            var starts = 0;
            resource.OnBeforeResourceStarted((_, _, _) =>
            {
                if (Interlocked.Increment(ref starts) != 1)
                {
                    throw new DistributedApplicationException("MSTest testing resources do not support restarts within a test run.");
                }
                return Task.CompletedTask;
            });
        });

        return resource;
    }

    internal static void ValidateRunnerArguments(IEnumerable<object> args)
    {
        foreach (var arg in args)
        {
            if (arg is null)
            {
                throw new ArgumentException("MSTest runner arguments cannot be null.", nameof(args));
            }
            if (arg is string value &&
                (value.StartsWith('@') || value.StartsWith("--retry-", StringComparison.Ordinal) || value.Split('=', 2)[0] is
                    "--report-trx" or "--report-trx-filename" or "--results-directory" or "--server"))
            {
                throw new ArgumentException($"The MSTest runner argument '{value}' is reserved by AddMsTest.", nameof(args));
            }
        }
    }
}
