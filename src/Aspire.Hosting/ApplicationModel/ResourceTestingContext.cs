// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Publishes collected test results for one logical resource into the containing test run.
/// </summary>
/// <remarks>
/// The resource output path is established internally before startup and made available to the
/// runtime collector. Results are accepted only while the collector is active.
/// Framework integrations are responsible for reading and translating their own result formats.
/// </remarks>
/// <example>
/// <code lang="C#">
/// tests.WithTesting(context =>
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
    private Task? _capture;

    internal Func<ResourceTestingContext, CancellationToken, Task>? CaptureOutputCallback { get; init; }

    internal ResourceTestingContext(TestingContext testingContext, string resourceName, string outputPath,
        Action<TestResult> publish, Task<bool> resourceCompletion, CancellationToken cancellationToken)
    {
        TestingContext = testingContext;
        ResourceName = resourceName;
        OutputPath = outputPath;
        _publish = publish;
        _resourceCompletion = resourceCompletion;
        CancellationToken = cancellationToken;
    }

    /// <summary>
    /// Gets the context for the containing testing invocation.
    /// </summary>
    [AspireExport]
    public TestingContext TestingContext { get; }

    /// <summary>
    /// Gets the logical resource name associated with every result published through this context.
    /// </summary>
    [AspireExport]
    public string ResourceName { get; }

    /// <summary>
    /// Gets the absolute host path to this resource's private output directory.
    /// </summary>
    /// <remarks>
    /// Named after the logical resource directly beneath the run directory and shared by its replicas.
    /// Configure runner report paths independently and stage artifacts here during collection.
    /// Repeated <c>WithTesting</c> calls preserve this destination.
    /// </remarks>
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
    /// <param name="cancellationToken">An optional token that cancels only this wait, in addition to test-run cancellation.</param>
    /// <returns>True if resource execution passed; otherwise false. Published test results do not affect this value.</returns>
    /// <remarks>
    /// Collection starts alongside execution. Use this helper before harvesting files written at
    /// process completion. It shares the run's completion tracking, handles resources that already
    /// finished, and waits for late exit-code notifications. Collector completion itself is not awaited.
    /// The default token still observes <see cref="CancellationToken"/>, including command deadlines
    /// and interruption. A supplied token cannot disable run cancellation, and cancelling this wait
    /// does not cancel the shared resource-completion task or other waiters.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Collection has been cancelled.</exception>
    [AspireExport]
    public Task<bool> WaitForCompletionAsync(CancellationToken cancellationToken = default)
    {
        CancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        if (!cancellationToken.CanBeCanceled || cancellationToken == CancellationToken)
        {
            return _resourceCompletion.WaitAsync(CancellationToken);
        }
        if (!CancellationToken.CanBeCanceled)
        {
            return _resourceCompletion.WaitAsync(cancellationToken);
        }

        return WaitWithLinkedCancellationAsync(cancellationToken);
    }

    private async Task<bool> WaitWithLinkedCancellationAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, cancellationToken);
        return await _resourceCompletion.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the completed executable's raw standard output and standard error into this resource's output directory.
    /// </summary>
    /// <returns>A task that completes only after both output streams reach EOF and their files are closed.</returns>
    /// <remarks>
    /// <para>
    /// Waits for authoritative process completion, including nonzero exits, then replays DCP's durable,
    /// timestamp-free stdout and stderr separately into <c>stdout.log</c> and <c>stderr.log</c>.
    /// Creates <c>output.complete</c> only after both streams drain. Partial files remain on errors or cancellation.
    /// Files are exclusively created with private permissions (0600 on Unix) in the run's private directory.
    /// </para>
    /// <para>
    /// Supports exactly one native executable invocation, without terminals, replicas, restarts, or retries.
    /// Integrations must prevent restarts before launch. Concurrent/repeated calls share the same task,
    /// including a faulted task; capture never reopens or overwrites the owned files.
    /// No dashboard backlog limits, timestamp formatting, or stdout/stderr merging apply.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="csharp">
    /// options.CollectResults(async context =>
    /// {
    ///     await context.CaptureOutputAsync();
    ///     var output = Path.Combine(context.OutputPath, "stdout.log");
    /// });
    /// </code>
    /// </example>
    /// <exception cref="NotSupportedException">The resource does not have supported native executable output.</exception>
    /// <exception cref="InvalidOperationException">Collection has completed or the output directory is unsafe.</exception>
    /// <exception cref="IOException">Owned output artifacts already exist or cannot be written.</exception>
    /// <exception cref="OperationCanceledException">Collection has been cancelled.</exception>
    [AspireExport]
    [Experimental(TestingDiagnostics.DiagnosticId, UrlFormat = TestingDiagnostics.UrlFormat)]
    public Task CaptureOutputAsync()
    {
        lock (_gate)
        {
            CancellationToken.ThrowIfCancellationRequested();
            if (_closed)
            {
                throw new InvalidOperationException("Output cannot be captured after collection completes.");
            }
            return _capture ??= CaptureAsync();
        }
    }

    private async Task CaptureAsync()
    {
        await WaitForCompletionAsync().ConfigureAwait(false);
        var capture = CaptureOutputCallback ??
            throw new NotSupportedException("Raw process output capture is unavailable in this testing context.");
        await capture(this, CancellationToken).ConfigureAwait(false);
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
