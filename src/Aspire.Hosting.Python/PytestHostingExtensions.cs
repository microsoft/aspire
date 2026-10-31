// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Python;

#pragma warning disable ASPIRETESTING001 // Implements the experimental testing integration.

namespace Aspire.Hosting;

/// <summary>
/// Provides methods for running pytest suites in an Aspire application.
/// </summary>
[Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class PytestHostingExtensions
{
    /// <summary>
    /// Adds a pytest executable and collects its built-in JUnit XML report during <c>aspire test</c>.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The test resource name.</param>
    /// <param name="appDirectory">The test application directory, relative to the AppHost directory or absolute.</param>
    /// <returns>The Python resource builder.</returns>
    /// <remarks>
    /// <para>
    /// Runs the virtual environment's pytest executable, preserving Python dependency installation,
    /// environment variables, references, and waits. Declare pytest in the application's dependencies.
    /// Use WithArgs for pytest arguments and WithVirtualEnvironment, WithPip, or WithUv for environment setup.
    /// WithUv synchronizes the selected virtual environment before launching pytest directly.
    /// </para>
    /// <para>
    /// During testing, Aspire reserves the JUnit report destination and waits for completion before
    /// collecting results, including after a nonzero exit. Missing or invalid reports fail collection.
    /// Results count JUnit outcomes, not unique collected pytest items: setup/teardown and collection
    /// errors can produce additional outcomes with the same name. Raw suite totals remain in pytest.xml.
    /// Reports have pytest-controlled file permissions inside Aspire's private resource output directory.
    /// Only one invocation per resource is supported, without replicas, restarts, or retry plugins.
    /// </para>
    /// <para>
    /// Ordinary runs launch pytest without reporting flags or collection. This resource is excluded
    /// from publishing. Do not replace its entrypoint during testing or disable pytest's JUnit plugin.
    /// Explicit report redirection and response files are reserved during testing; the managed argument
    /// overrides report destinations in configuration and PYTEST_ADDOPTS.
    /// Every nonzero process exit fails, including no tests collected and interrupted collection.
    /// Cancellation is determined by Aspire, not inferred from pytest exit code 2.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// builder.AddPytest("api-tests", "../python-api")
    ///     .WithUv()
    ///     .WithArgs("tests", "-q")
    ///     .WithReference(api)
    ///     .WaitFor(api);
    /// </code>
    /// </example>
    /// <exception cref="ArgumentNullException">The builder or application directory is null.</exception>
    /// <exception cref="ArgumentException">The resource name or application directory is empty.</exception>
    [AspireExport]
    [Experimental("ASPIRETESTING001", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
    public static IResourceBuilder<PythonAppResource> AddPytest(
        this IDistributedApplicationBuilder builder, [ResourceName] string name, string appDirectory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDirectory);

        var resource = builder.AddPythonExecutable(name, appDirectory, "pytest")
            .WithIconName("Beaker")
            .ExcludeFromManifest();

        if (builder.ExecutionContext.IsRunMode)
        {
            builder.OnBeforeStart((_, _) =>
            {
                if (resource.Resource.TryGetLastAnnotation<PythonPackageManagerAnnotation>(out var manager) &&
                    manager.ExecutableName == "uv" &&
                    resource.Resource.TryGetLastAnnotation<PythonPackageInstallerAnnotation>(out var installer) &&
                    resource.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var environment) &&
                    environment.VirtualEnvironment is { } venv)
                {
                    // uv otherwise syncs its workspace's .venv, which can differ from the Python
                    // resource's AppHost fallback or explicit environment. No shell activation is needed.
                    // https://docs.astral.sh/uv/concepts/projects/config/#project-environment-path
                    builder.CreateResourceBuilder(installer.Resource).WithEnvironment("UV_PROJECT_ENVIRONMENT",
                        Path.GetFullPath(venv.VirtualEnvironmentPath, resource.Resource.WorkingDirectory));
                }
                return Task.CompletedTask;
            });
        }

        resource.WithTesting(testing =>
        {
            if (testing.OutputPath is not { } outputPath)
            {
                return;
            }
            testing.CollectResults(PytestJunitReport.CollectAsync);
            builder.OnBeforeStart((_, _) =>
            {
                if (resource.Resource.TryGetLastAnnotation<ReplicaAnnotation>(out var replicas) && replicas.Replicas != 1)
                {
                    throw new DistributedApplicationException("Pytest testing resources support exactly one replica.");
                }
                if (!resource.Resource.TryGetLastAnnotation<PythonEntrypointAnnotation>(out var entrypoint) ||
                    entrypoint.Type != EntrypointType.Executable || entrypoint.Entrypoint != "pytest" ||
                    !resource.Resource.TryGetLastAnnotation<PythonEnvironmentAnnotation>(out var environment) ||
                    environment.VirtualEnvironment is not { } venv ||
                    resource.Resource.Command != venv.GetExecutable("pytest"))
                {
                    throw new DistributedApplicationException("The pytest entrypoint cannot be replaced during testing.");
                }
                // Register after model definition so later WithArgs calls compose. pytest prepends
                // configuration/addopts before CLI args; the last report option wins. Insert before
                // '--', which makes subsequent tokens positional even when they resemble options.
                // https://docs.pytest.org/en/stable/how-to/output.html#creating-junitxml-format-files
                resource.WithArgs(context =>
                {
                    ValidateArguments(context.Args);
                    var separator = context.Args.IndexOf("--");
                    context.Args.Insert(separator < 0 ? context.Args.Count : separator,
                        "--junitxml=" + Path.Combine(outputPath, PytestJunitReport.FileName));
                });
                return Task.CompletedTask;
            });
            var starts = 0;
            resource.OnBeforeResourceStarted((_, _, _) =>
            {
                if (Interlocked.Increment(ref starts) != 1)
                {
                    throw new DistributedApplicationException("Pytest testing resources do not support restarts within a test run.");
                }
                return Task.CompletedTask;
            });
        });

        return resource;
    }

    internal static void ValidateArguments(IEnumerable<object> args)
    {
        foreach (var arg in args)
        {
            if (arg is null)
            {
                throw new ArgumentException("Pytest arguments cannot be null.", nameof(args));
            }
            if (arg is string value &&
                (value.StartsWith('@') || value.Split('=', 2)[0] is "--junitxml" or "--junit-xml" or
                    "--reruns" or "--reruns-delay" or "--count" or "--looponfail" or "-f"))
            {
                throw new ArgumentException($"The pytest argument '{value}' is reserved by AddPytest during testing.", nameof(args));
            }
        }
    }
}
