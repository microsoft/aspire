#pragma warning disable ASPIREPIPELINES001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Pipelines;

/// <summary>
/// Provides loggers that forward calls to a contextual logger associated with the current pipeline step.
/// </summary>
/// <remarks>
/// This logger provider uses AsyncLocal storage to maintain the current logger context across async operations.
/// This enables pipeline steps to log through a contextual logger that can be set per execution.
/// </remarks>
internal sealed class PipelineLoggerProvider(IOptions<PipelineLoggingOptions> options) : ILoggerProvider
{
    private static readonly AsyncLocal<StepLoggerHolder?> s_currentStep = new();

    /// <summary>
    /// Gets or sets the current logger for the executing pipeline step.
    /// </summary>
    public static IReportingStep? CurrentStep
    {
        get => s_currentStep.Value?.Step;
        set
        {
            // Clear the current logger from AsyncLocal context
            s_currentStep.Value = null;

            if (value is not null)
            {
                // Use an object indirection to hold the logger in the AsyncLocal,
                // so it can be cleared in all ExecutionContexts when cleared.
                s_currentStep.Value = new StepLoggerHolder { Step = value };
            }
        }
    }

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) =>
        new StepLogger(() => CurrentStep, options.Value);

    /// <inheritdoc/>
    public void Dispose()
    {
        // No resources to dispose
    }

    /// <summary>
    /// Holds the logger instance in AsyncLocal storage.
    /// </summary>
    private sealed class StepLoggerHolder
    {
        public IReportingStep? Step;
    }

    /// <summary>
    /// A logger implementation that forwards all calls to the current contextual logger.
    /// </summary>
    /// <remarks>
    /// This logger acts as a proxy and dynamically resolves the current logger on each operation,
    /// allowing the target logger to change between calls.
    /// When logging exceptions, stack traces are only included when the configured minimum log level is Debug or Trace.
    /// </remarks>
    private sealed class StepLogger(Func<IReportingStep?> currentStepAccessor, PipelineLoggingOptions options) : ILogger
    {
        /// <inheritdoc/>
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            null;

        /// <inheritdoc/>
        public bool IsEnabled(LogLevel logLevel) =>
            true;

        /// <inheritdoc/>
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var step = currentStepAccessor();

            if (step is null)
            {
                // No current step logger; nothing to log to
                return;
            }

            // Also log to the step logger (for publishing output display)
            var message = formatter(state, exception);

            if (options.IncludeExceptionDetails && exception != null)
            {
                message = $"{message} {exception}";
            }

            step.Log(logLevel, message);
        }
    }
}