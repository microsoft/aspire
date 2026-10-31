// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Publishes collected test results for one logical resource into the containing test run.
/// </summary>
/// <remarks>
/// The resource output path is established before startup and is shared with
/// <see cref="TestingOptions.OutputPath"/>. Results are accepted only while the collector is active.
/// Framework integrations are responsible for reading and translating their own result formats.
/// </remarks>
/// <example>
/// <code lang="C#">
/// options.CollectResults(context =>
/// {
///     context.PublishResult(new TestResult { Name = "example", Outcome = TestOutcome.Passed });
///     return Task.CompletedTask;
/// });
/// </code>
/// </example>
[AspireExport]
[Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
public sealed class ResourceTestingContext
{
    private readonly Action<TestResult> _publish;
    private readonly Task<bool> _resourceCompletion;
    private readonly object _gate = new();
    private bool _closed;

    internal ResourceTestingContext(TestRunContext runContext, string resourceName, string outputPath,
        Action<TestResult> publish, Task<bool> resourceCompletion, CancellationToken cancellationToken)
    {
        RunContext = runContext;
        ResourceName = resourceName;
        OutputPath = outputPath;
        _publish = publish;
        _resourceCompletion = resourceCompletion;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the context for the containing test run.
    /// </summary>
    [AspireExport]
    public TestRunContext RunContext { get; }

    /// <summary>
    /// Gets the logical resource name associated with every result published through this context.
    /// </summary>
    [AspireExport]
    public string ResourceName { get; }

    /// <summary>
    /// Gets the absolute host path to this resource's private output directory.
    /// </summary>
    [AspireExport]
    public string OutputPath { get; }

    /// <summary>
    /// Gets the token used to cancel collection.
    /// </summary>
    [AspireExport]
    public CancellationToken CancellationToken { get; }

    /// <summary>
    /// Waits until every replica of this resource has reached an authoritative terminal outcome.
    /// </summary>
    /// <returns>True if resource execution passed; otherwise false. Published test results do not affect this value.</returns>
    /// <remarks>
    /// Collection starts alongside execution. Use this helper before harvesting files written at
    /// process completion. It shares the run's completion tracking, handles resources that already
    /// finished, and waits for late exit-code notifications. Collector completion itself is not awaited.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Collection has been cancelled.</exception>
    [AspireExport]
    public Task<bool> WaitForCompletionAsync()
    {
        CancellationToken.ThrowIfCancellationRequested();
        return _resourceCompletion.WaitAsync(CancellationToken);
    }

    /// <summary>
    /// Publishes an observed test result for this resource.
    /// </summary>
    /// <param name="result">The framework-neutral result.</param>
    /// <remarks>
    /// Results are associated with the resource by the context. Publishing does not infer any
    /// other test results, and a passing result cannot override a resource execution failure.
    /// </remarks>
    /// <exception cref="ArgumentNullException">The result is null.</exception>
    /// <exception cref="ArgumentException">The result name, outcome, or duration is invalid.</exception>
    /// <exception cref="InvalidOperationException">Collection has completed.</exception>
    /// <exception cref="OperationCanceledException">Collection has been cancelled.</exception>
    [AspireExport]
    public void PublishResult(TestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(result.Name);
        if (!Enum.IsDefined(result.Outcome) || result.Duration < TimeSpan.Zero)
        {
            throw new ArgumentException("The test outcome must be known and its duration must not be negative.", nameof(result));
        }

        lock (_gate)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (_closed)
            {
                throw new InvalidOperationException("Results cannot be published after test collection completes.");
            }
            _publish(result);
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
        }
    }
}
