// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Configures a resource's output destination and framework-neutral test result collectors.
/// </summary>
/// <remarks>
/// Configuration runs immediately when <c>WithTesting</c> is called, including ordinary runs.
/// Collectors run only during <c>aspire test</c>. Register collectors here; do not execute them.
/// The output destination is a host filesystem path. Containers must explicitly mount it.
/// </remarks>
/// <example>
/// <code lang="C#">
/// tests.WithTesting(options =>
/// {
///     if (options.OutputPath is { } outputPath)
///     {
///         tests.WithEnvironment("TEST_OUTPUT_DIRECTORY", outputPath);
///     }
///     options.CollectResults(context =>
///     {
///         context.PublishResult(new TestResult { Name = "example", Outcome = TestOutcome.Passed });
///         return Task.CompletedTask;
///     });
/// });
/// </code>
/// </example>
[AspireExport]
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class TestingOptions
{
    private readonly List<Func<ResourceTestingContext, Task>> _collectors = [];
    private readonly object _gate = new();
    private bool _configured;

    internal TestingOptions(TestRunContext? runContext, string resourceName, string? outputPath)
    {
        RunContext = runContext;
        ResourceName = resourceName;
        OutputPath = outputPath;
    }

    /// <summary>
    /// Gets the containing test run context, or null outside an <c>aspire test</c> invocation.
    /// </summary>
    [AspireExport]
    public TestRunContext? RunContext { get; }

    /// <summary>
    /// Gets whether the CLI supplied a test run output destination for this AppHost.
    /// </summary>
    [AspireExport]
    public bool IsTestRun => RunContext is not null;

    /// <summary>
    /// Gets the logical resource name whose testing behavior is being configured.
    /// </summary>
    [AspireExport]
    public string ResourceName { get; }

    /// <summary>
    /// Gets the absolute host path to this resource's private output directory, or null outside testing.
    /// </summary>
    [AspireExport]
    public string? OutputPath { get; }

    /// <summary>
    /// Registers a callback that publishes framework-neutral results for this resource.
    /// </summary>
    /// <param name="collector">The asynchronous result collector.</param>
    /// <remarks>
    /// Callbacks receive a resource-specific context separate from these configuration options.
    /// Collectors start alongside resource execution and may use
    /// <see cref="ResourceTestingContext.WaitForCompletionAsync"/> before reading final artifacts.
    /// Multiple collectors run concurrently. Registration is only allowed during immediate configuration.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The collector is null.</exception>
    /// <exception cref="InvalidOperationException">Configuration has already completed.</exception>
    [AspireExport]
    public void CollectResults(Func<ResourceTestingContext, Task> collector)
    {
        ArgumentNullException.ThrowIfNull(collector);
        lock (_gate)
        {
            if (_configured)
            {
                throw new InvalidOperationException("Test collectors must be registered during testing configuration.");
            }
            _collectors.Add(collector);
        }
    }

    internal Func<ResourceTestingContext, Task>[] Collectors { get; private set; } = [];

    internal void CompleteConfiguration()
    {
        lock (_gate)
        {
            _configured = true;
            Collectors = _collectors.ToArray();
        }
    }
}
