// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.Utils;

/// <summary>
/// A test implementation of <see cref="IPipelineActivityReporter"/> that captures activity for test assertions.
/// </summary>
internal sealed class TestPipelineActivityReporter : IPipelineActivityReporter
{
    private readonly ITestOutputHelper _testOutputHelper;

    /// <summary>
    /// Initializes a new instance of the <see cref="TestPipelineActivityReporter"/> class.
    /// </summary>
    /// <param name="testOutputHelper">The test output helper to write to.</param>
    public TestPipelineActivityReporter(ITestOutputHelper testOutputHelper)
    {
        _testOutputHelper = testOutputHelper ?? throw new ArgumentNullException(nameof(testOutputHelper));
    }

    /// <summary>
    /// Gets a list of all steps that have been created.
    /// </summary>
    public List<string> CreatedSteps { get; } = [];

    /// <summary>
    /// Gets a list of all tasks that have been created.
    /// </summary>
    public List<(string StepTitle, string TaskStatusText)> CreatedTasks { get; } = [];

    /// <summary>
    /// Gets a list of all steps that have been completed.
    /// </summary>
    public List<(string StepTitle, string CompletionText, CompletionState CompletionState)> CompletedSteps { get; } = [];

    /// <summary>
    /// Gets a list of all tasks that have been completed.
    /// </summary>
    public List<(string TaskStatusText, string? CompletionMessage, CompletionState CompletionState)> CompletedTasks { get; } = [];

    /// <summary>
    /// Gets a list of all tasks that have been updated.
    /// </summary>
    public List<(string TaskStatusText, string StatusText)> UpdatedTasks { get; } = [];

    /// <summary>
    /// Gets a list of all log messages that have been logged.
    /// </summary>
    public List<(string StepTitle, LogLevel LogLevel, string Message)> LoggedMessages { get; } = [];

    /// <summary>
    /// Gets or sets a callback that is invoked when a step is created.
    /// </summary>
    public Action<string>? OnStepCreated { get; set; }

    /// <summary>
    /// Gets or sets a callback that is invoked when a step completes.
    /// </summary>
    public Action<string, string, CompletionState>? OnStepCompleted { get; set; }

    /// <summary>
    /// Gets a value indicating whether <see cref="CompletePublishAsync(PublishCompletionOptions?, CancellationToken)"/> has been called.
    /// </summary>
    public bool CompletePublishCalled { get; private set; }

    /// <summary>
    /// Gets the completion message passed to <see cref="CompletePublishAsync(PublishCompletionOptions?, CancellationToken)"/>.
    /// </summary>
    public string? CompletionMessage { get; private set; }

    /// <summary>
    /// Gets the completion state passed to <see cref="CompletePublishAsync(PublishCompletionOptions?, CancellationToken)"/>.
    /// </summary>
    public CompletionState? ResultCompletionState { get; private set; }

    /// <summary>
    /// Gets the pipeline summary passed to <see cref="CompletePublishAsync(PublishCompletionOptions?, CancellationToken)"/>.
    /// </summary>
    public IReadOnlyList<PipelineSummaryItem>? PipelineSummary { get; private set; }

    /// <summary>
    /// Clears all captured state to allow reuse between pipeline runs.
    /// </summary>
    public void Clear()
    {
        lock (CreatedSteps)
        {
            CreatedSteps.Clear();
        }
        lock (CreatedTasks)
        {
            CreatedTasks.Clear();
        }
        lock (CompletedSteps)
        {
            CompletedSteps.Clear();
        }
        lock (CompletedTasks)
        {
            CompletedTasks.Clear();
        }
        lock (UpdatedTasks)
        {
            UpdatedTasks.Clear();
        }
        lock (LoggedMessages)
        {
            LoggedMessages.Clear();
        }
        CompletePublishCalled = false;
        CompletionMessage = null;
        ResultCompletionState = null;
        PipelineSummary = null;
    }

    /// <inheritdoc />
    public Task CompletePublishAsync(PublishCompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        CompletePublishCalled = true;
        CompletionMessage = options?.CompletionMessage;
        ResultCompletionState = options?.CompletionState;
        PipelineSummary = options?.PipelineSummary;
        var summaryStr = options?.PipelineSummary != null ? string.Join(", ", options.PipelineSummary.Select(item => $"{item.Key}={item.Value}")) : null;
        _testOutputHelper.WriteLine($"[CompletePublish] {options?.CompletionMessage} (State: {options?.CompletionState}) (Summary: {summaryStr})");

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    [Obsolete("Use CompletePublishAsync(PublishCompletionOptions?, CancellationToken) instead.")]
    public Task CompletePublishAsync(string? completionMessage = null, CompletionState? completionState = null, CancellationToken cancellationToken = default)
    {
        return CompletePublishAsync(new PublishCompletionOptions
        {
            CompletionMessage = completionMessage,
            CompletionState = completionState
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReportingStep> CreateStepAsync(string title, CancellationToken cancellationToken = default)
    {
        lock (CreatedSteps)
        {
            CreatedSteps.Add(title);
            OnStepCreated?.Invoke(title);
            _testOutputHelper.WriteLine($"[CreateStep] {title}");
        }

        return Task.FromResult<IReportingStep>(new TestReportingStep(this, title, _testOutputHelper));
    }

    private sealed class TestReportingStep : IReportingStep
    {
        private readonly TestPipelineActivityReporter _reporter;
        private readonly string _title;
        private readonly ITestOutputHelper _testOutputHelper;

        public TestReportingStep(TestPipelineActivityReporter reporter, string title, ITestOutputHelper testOutputHelper)
        {
            _reporter = reporter;
            _title = title;
            _testOutputHelper = testOutputHelper;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task CompleteAsync(string completionText, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
        {
            lock (_reporter.CompletedSteps)
            {
                _reporter.CompletedSteps.Add((_title, completionText, completionState));
                _reporter.OnStepCompleted?.Invoke(_title, completionText, completionState);
                _testOutputHelper.WriteLine($"  [CompleteStep:{_title}] {completionText} (State: {completionState})");
            }

            return Task.CompletedTask;
        }

        public Task<IReportingTask> CreateTaskAsync(string statusText, CancellationToken cancellationToken = default)
        {
            lock (_reporter.CreatedTasks)
            {
                _reporter.CreatedTasks.Add((_title, statusText));
                _testOutputHelper.WriteLine($"    [CreateTask:{_title}] {statusText}");
            }

            return Task.FromResult<IReportingTask>(new TestReportingTask(_reporter, statusText, _testOutputHelper));
        }

        public void Log(LogLevel logLevel, string message, bool enableMarkdown)
        {
            lock (_reporter.LoggedMessages)
            {
                _reporter.LoggedMessages.Add((_title, logLevel, message));
                _testOutputHelper.WriteLine($"    [{logLevel}:{_title}] {message}");
            }
        }

        public void Log(LogLevel logLevel, string message)
        {
            lock (_reporter.LoggedMessages)
            {
                _reporter.LoggedMessages.Add((_title, logLevel, message));
                _testOutputHelper.WriteLine($"    [{logLevel}:{_title}] {message}");
            }
        }

        public void Log(LogLevel logLevel, MarkdownString message)
        {
            ArgumentNullException.ThrowIfNull(message);
            lock (_reporter.LoggedMessages)
            {
                _reporter.LoggedMessages.Add((_title, logLevel, message.Value));
                _testOutputHelper.WriteLine($"    [{logLevel}:{_title}] {message.Value}");
            }
        }

        public Task<IReportingTask> CreateTaskAsync(MarkdownString statusText, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(statusText);
            return CreateTaskAsync(statusText.Value, cancellationToken);
        }

        public Task CompleteAsync(MarkdownString completionText, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(completionText);
            return CompleteAsync(completionText.Value, completionState, cancellationToken);
        }
    }

    private sealed class TestReportingTask : IReportingTask
    {
        private readonly TestPipelineActivityReporter _reporter;
        private readonly string _initialStatusText;
        private readonly ITestOutputHelper _testOutputHelper;

        public TestReportingTask(TestPipelineActivityReporter reporter, string initialStatusText, ITestOutputHelper testOutputHelper)
        {
            _reporter = reporter;
            _initialStatusText = initialStatusText;
            _testOutputHelper = testOutputHelper;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public Task CompleteAsync(string? completionMessage = null, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
        {
            lock (_reporter.CompletedTasks)
            {
                _reporter.CompletedTasks.Add((_initialStatusText, completionMessage, completionState));
                _testOutputHelper.WriteLine($"      [CompleteTask:{_initialStatusText}] {completionMessage} (State: {completionState})");
            }

            return Task.CompletedTask;
        }

        public Task UpdateAsync(string statusText, CancellationToken cancellationToken = default)
        {
            lock (_reporter.UpdatedTasks)
            {
                _reporter.UpdatedTasks.Add((_initialStatusText, statusText));
                _testOutputHelper.WriteLine($"      [UpdateTask:{_initialStatusText}] {statusText}");
            }

            return Task.CompletedTask;
        }

        public Task UpdateAsync(MarkdownString statusText, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(statusText);
            return UpdateAsync(statusText.Value, cancellationToken);
        }

        public Task CompleteAsync(MarkdownString completionMessage, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(completionMessage);
            return CompleteAsync(completionMessage.Value, completionState, cancellationToken);
        }
    }
}
