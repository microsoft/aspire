// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aspire.Hosting.Tests.Pipelines;

[Trait("Partition", "4")]
public class PipelineLoggerProviderTests
{
    [Fact]
    public void CurrentLogger_WhenNotSet_ReturnsNull()
    {
        // Arrange & Act
        var currentStep = PipelineLoggerProvider.CurrentStep;

        // Assert
        Assert.Null(currentStep);
    }

    [Fact]
    public void CurrentLogger_WhenSetToValidLogger_ReturnsSetLogger()
    {
        // Arrange
        var testStep = new FakeReportingStep();

        try
        {
            // Act
            PipelineLoggerProvider.CurrentStep = testStep;
            var retrievedStep = PipelineLoggerProvider.CurrentStep;

            // Assert
            Assert.Same(testStep, retrievedStep);
        }
        finally
        {
            // Cleanup
            PipelineLoggerProvider.CurrentStep = null;
        }
    }

    [Fact]
    public void CurrentLogger_WhenSetToNull_ReturnsNullLogger()
    {
        // Arrange
        var testStep = new FakeReportingStep();
        PipelineLoggerProvider.CurrentStep = testStep;

        try
        {
            // Act
            PipelineLoggerProvider.CurrentStep = null;
            var retrievedStep = PipelineLoggerProvider.CurrentStep;

            // Assert
            Assert.Null(retrievedStep);
        }
        finally
        {
            // Cleanup
            PipelineLoggerProvider.CurrentStep = null;
        }
    }

    [Fact]
    public void CreateLogger_ReturnsValidLogger()
    {
        // Arrange
        var options = Options.Create(new PipelineLoggingOptions());
        var provider = new PipelineLoggerProvider(options);

        // Act
        var logger = provider.CreateLogger("TestCategory");

        // Assert
        Assert.NotNull(logger);
    }

    [Fact]
    public async Task CurrentLogger_IsAsyncLocal_IsolatedBetweenTasks()
    {
        // Arrange
        var fakeStep1 = new FakeReportingStep();
        var fakeStep2 = new FakeReportingStep();
        var options = Options.Create(new PipelineLoggingOptions());
        var provider = new PipelineLoggerProvider(options);

        // Act
        var task1 = Task.Run(async () =>
        {
            PipelineLoggerProvider.CurrentStep = fakeStep1;
            var logger = provider.CreateLogger("Task1");

            logger.LogInformation("Task 1 message");
        });

        var task2 = Task.Run(async () =>
        {
            PipelineLoggerProvider.CurrentStep = fakeStep2;
            var logger = provider.CreateLogger("Task2");

            logger.LogInformation("Task 2 message");
        });

        await Task.WhenAll(task1, task2);

        // Assert
        var logs1 = fakeStep1.LoggedMessages;
        var logEntry1 = Assert.Single(logs1);
        Assert.Equal(LogLevel.Information, logEntry1.Level);
        Assert.Equal("Task 1 message", logEntry1.Message);

        var logs2 = fakeStep2.LoggedMessages;
        var logEntry2 = Assert.Single(logs2);
        Assert.Equal(LogLevel.Information, logEntry2.Level);
        Assert.Equal("Task 2 message", logEntry2.Message);
    }
}

public class FakeReportingStep : IReportingStep
{
    public List<(LogLevel Level, string Message)> LoggedMessages { get; } = [];

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public Task<IReportingTask> CreateTaskAsync(string statusText, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<IReportingTask> CreateTaskAsync(MarkdownString statusText, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task CompleteAsync(string statusText, CompletionState completionState, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task CompleteAsync(MarkdownString completionText, CompletionState completionState = CompletionState.Completed, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task FailAsync(string errorText, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

#pragma warning disable CS0618 // Type or member is obsolete
    public void Log(LogLevel logLevel, string message, bool enableMarkdown = false)
    {
        LoggedMessages.Add((logLevel, message));
    }
#pragma warning restore CS0618

    public void Log(LogLevel logLevel, string message)
    {
        LoggedMessages.Add((logLevel, message));
    }

    public void Log(LogLevel logLevel, MarkdownString message)
    {
        ArgumentNullException.ThrowIfNull(message);
        LoggedMessages.Add((logLevel, message.Value));
    }
}