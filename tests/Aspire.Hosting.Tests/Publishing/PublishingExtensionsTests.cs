// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001

using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Tests.Publishing;

[Trait("Partition", "4")]
public class PublishingExtensionsTests
{
    private readonly InteractionService _interactionService = PublishingActivityReporterTests.CreateInteractionService();

    [Fact]
    public async Task PublishingStepExtensions_CreateTask_WorksCorrectly()
    {
        // Arrange
        var reporter = CreatePublishingReporter();
        await using var step = await reporter.CreateStepAsync("Test Step", CancellationToken.None);

        // Act
        var task = await step.CreateTaskAsync("Initial status", CancellationToken.None);

        // Assert
        Assert.NotNull(task);
        await using var stepInternal = Assert.IsType<ReportingStep>(step);
        var taskInternal = Assert.IsType<ReportingTask>(task);
        Assert.Equal(stepInternal.Id, taskInternal.StepId);
        Assert.Equal("Initial status", taskInternal.StatusText);
        Assert.Equal(CompletionState.InProgress, taskInternal.CompletionState);
    }

    [Fact]
    public async Task PublishingStepExtensions_Succeed_WorksCorrectly()
    {
        // Arrange
        var reporter = CreatePublishingReporter();
        await using var step = await reporter.CreateStepAsync("Test Step", CancellationToken.None);

        // Act
        var result = await step.SucceedAsync("Success message", CancellationToken.None);

        // Assert
        Assert.Equal(step, result);
        // Cast to internal type to verify internal state for testing
        await using var stepInternal = Assert.IsType<ReportingStep>(step);
        Assert.NotEqual(CompletionState.InProgress, stepInternal.CompletionState);
        Assert.Equal("Success message", stepInternal.CompletionText);
    }

    [Fact]
    public async Task PublishingTaskExtensions_UpdateStatus_WorksCorrectly()
    {
        // Arrange
        var reporter = CreatePublishingReporter();
        await using var step = await reporter.CreateStepAsync("Test Step", CancellationToken.None);
        var task = await step.CreateTaskAsync("Initial status", CancellationToken.None);

        // Act
        var result = await task.UpdateStatusAsync("Updated status", CancellationToken.None);

        // Assert
        Assert.Equal(task, result);

        // Cast to internal type to verify internal state for testing
        var taskInternal = Assert.IsType<ReportingTask>(task);
        Assert.Equal("Updated status", taskInternal.StatusText);
    }

    [Fact]
    public async Task PublishingTaskExtensions_Succeed_WorksCorrectly()
    {
        // Arrange
        var reporter = CreatePublishingReporter();
        await using var step = await reporter.CreateStepAsync("Test Step", CancellationToken.None);
        var task = await step.CreateTaskAsync("Initial status", CancellationToken.None);

        // Act
        var result = await task.SucceedAsync("Success message", CancellationToken.None);

        // Assert
        Assert.Equal(task, result);
        var taskInternal = Assert.IsType<ReportingTask>(task);
        Assert.Equal(CompletionState.Completed, taskInternal.CompletionState);
        Assert.Equal("Success message", taskInternal.CompletionMessage);
    }

    [Fact]
    public async Task PublishingTaskExtensions_Warn_WorksCorrectly()
    {
        // Arrange
        var reporter = CreatePublishingReporter();
        await using var step = await reporter.CreateStepAsync("Test Step", CancellationToken.None);
        var task = await step.CreateTaskAsync("Initial status", CancellationToken.None);

        // Act
        var result = await task.WarnAsync("Warning message", CancellationToken.None);

        // Assert
        Assert.Equal(task, result);
        var taskInternal = Assert.IsType<ReportingTask>(task);
        Assert.Equal(CompletionState.CompletedWithWarning, taskInternal.CompletionState);
        Assert.Equal("Warning message", taskInternal.CompletionMessage);
    }

    [Fact]
    public async Task PublishingTaskExtensions_Fail_WorksCorrectly()
    {
        // Arrange
        var reporter = CreatePublishingReporter();
        await using var step = await reporter.CreateStepAsync("Test Step", CancellationToken.None);
        var task = await step.CreateTaskAsync("Initial status", CancellationToken.None);

        // Act
        await task.FailAsync("Error message", CancellationToken.None);

        // Assert
        var taskInternal = Assert.IsType<ReportingTask>(task);
        Assert.Equal(CompletionState.CompletedWithError, taskInternal.CompletionState);
        Assert.Equal("Error message", taskInternal.CompletionMessage);
    }

    private PipelineActivityReporter CreatePublishingReporter()
    {
        return new PipelineActivityReporter(_interactionService, NullLogger<PipelineActivityReporter>.Instance);
    }
}
