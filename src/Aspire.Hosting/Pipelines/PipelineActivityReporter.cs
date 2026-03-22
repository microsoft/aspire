// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREINTERACTION001

using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Dashboard;
using Microsoft.Extensions.Logging;
using static Aspire.Hosting.Dashboard.DashboardServiceData;

namespace Aspire.Hosting.Pipelines;

internal sealed class PipelineActivityReporter : IPipelineActivityReporter, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ReportingStep> _steps = new();
    private readonly InteractionService _interactionService;
    private readonly ILogger<PipelineActivityReporter> _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly Task _interactionServiceSubscriber;

    public PipelineActivityReporter(InteractionService interactionService, ILogger<PipelineActivityReporter> logger)
    {
        _interactionService = interactionService;
        _logger = logger;
        _interactionServiceSubscriber = Task.Run(() => SubscribeToInteractionsAsync(_cancellationTokenSource.Token));
    }

    private static string ToBackchannelCompletionState(CompletionState state) => state switch
    {
        CompletionState.InProgress => CompletionStates.InProgress,
        CompletionState.Completed => CompletionStates.Completed,
        CompletionState.CompletedWithWarning => CompletionStates.CompletedWithWarning,
        CompletionState.CompletedWithError => CompletionStates.CompletedWithError,
        _ => CompletionStates.InProgress
    };

    public async Task<IReportingStep> CreateStepAsync(string title, CancellationToken cancellationToken = default)
    {
        var step = new ReportingStep(this, Guid.NewGuid().ToString(), title);
        _steps.TryAdd(step.Id, step);

        var state = new PublishingActivity
        {
            Type = PublishingActivityTypes.Step,
            Data = new PublishingActivityData
            {
                Id = step.Id,
                StatusText = step.Title,
                CompletionState = ToBackchannelCompletionState(CompletionState.InProgress),
                StepId = null
            }
        };

        await ActivityItemUpdated.Writer.WriteAsync(state, cancellationToken).ConfigureAwait(false);
        return step;
    }

    public async Task<ReportingTask> CreateTaskAsync(ReportingStep step, string statusText, bool enableMarkdown, CancellationToken cancellationToken)
    {
        if (!_steps.TryGetValue(step.Id, out var parentStep))
        {
            throw new InvalidOperationException($"Step with ID '{step.Id}' does not exist.");
        }

        lock (parentStep)
        {
            if (parentStep.CompletionState != CompletionState.InProgress)
            {
                throw new InvalidOperationException($"Cannot create task for step '{step.Id}' because the step is already complete.");
            }
        }

        var task = new ReportingTask(Guid.NewGuid().ToString(), step.Id, statusText, parentStep);

        // Add task to parent step
        parentStep.AddTask(task);

        var state = new PublishingActivity
        {
            Type = PublishingActivityTypes.Task,
            Data = new PublishingActivityData
            {
                Id = task.Id,
                StatusText = statusText,
                CompletionState = ToBackchannelCompletionState(CompletionState.InProgress),
                StepId = step.Id,
                EnableMarkdown = enableMarkdown
            }
        };

        await ActivityItemUpdated.Writer.WriteAsync(state, cancellationToken).ConfigureAwait(false);
        return task;
    }

    public async Task CompleteStepAsync(ReportingStep step, string completionText, CompletionState completionState, bool enableMarkdown, CancellationToken cancellationToken)
    {
        lock (step)
        {
            // If the step is already in a terminal state, this is a noop (idempotent)
            if (step.CompletionState != CompletionState.InProgress)
            {
                return;
            }

            step.CompletionState = completionState;
            step.CompletionText = completionText;
        }

        var state = new PublishingActivity
        {
            Type = PublishingActivityTypes.Step,
            Data = new PublishingActivityData
            {
                Id = step.Id,
                StatusText = completionText,
                CompletionState = ToBackchannelCompletionState(completionState),
                StepId = null,
                EnableMarkdown = enableMarkdown
            }
        };

        await ActivityItemUpdated.Writer.WriteAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateTaskAsync(ReportingTask task, string statusText, bool enableMarkdown, CancellationToken cancellationToken)
    {
        if (!_steps.TryGetValue(task.StepId, out var parentStep))
        {
            throw new InvalidOperationException($"Parent step with ID '{task.StepId}' does not exist.");
        }

        lock (parentStep)
        {
            if (parentStep.CompletionState != CompletionState.InProgress)
            {
                throw new InvalidOperationException($"Cannot update task '{task.Id}' because its parent step '{task.StepId}' is already complete.");
            }

            task.StatusText = statusText;
        }

        var state = new PublishingActivity
        {
            Type = PublishingActivityTypes.Task,
            Data = new PublishingActivityData
            {
                Id = task.Id,
                StatusText = statusText,
                CompletionState = ToBackchannelCompletionState(CompletionState.InProgress),
                StepId = task.StepId,
                EnableMarkdown = enableMarkdown
            }
        };

        await ActivityItemUpdated.Writer.WriteAsync(state, cancellationToken).ConfigureAwait(false);
    }

    internal void Log(ReportingStep step, LogLevel logLevel, string message, bool enableMarkdown)
    {
        if (!_steps.TryGetValue(step.Id, out var parentStep))
        {
            return;
        }

        lock (parentStep)
        {
            if (parentStep.CompletionState != CompletionState.InProgress)
            {
                return;
            }
        }

        var state = new PublishingActivity
        {
            Type = PublishingActivityTypes.Log,
            Data = new PublishingActivityData
            {
                Id = Guid.NewGuid().ToString(),
                StatusText = message,
                CompletionState = ToBackchannelCompletionState(CompletionState.Completed),
                StepId = step.Id,
                LogLevel = logLevel.ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                EnableMarkdown = enableMarkdown
            }
        };

        ActivityItemUpdated.Writer.TryWrite(state);
    }

    public async Task CompleteTaskAsync(ReportingTask task, CompletionState completionState, string? completionMessage, bool enableMarkdown, CancellationToken cancellationToken)
    {
        if (!_steps.TryGetValue(task.StepId, out var parentStep))
        {
            throw new InvalidOperationException($"Parent step with ID '{task.StepId}' does not exist.");
        }

        // If the task is already in a terminal state, this is a noop (idempotent)
        if (task.CompletionState != CompletionState.InProgress)
        {
            return;
        }

        lock (parentStep)
        {
            if (parentStep.CompletionState != CompletionState.InProgress)
            {
                throw new InvalidOperationException($"Cannot complete task '{task.Id}' because its parent step '{task.StepId}' is already complete.");
            }

            task.CompletionState = completionState;
            task.CompletionMessage = completionMessage ?? string.Empty;
        }

        var state = new PublishingActivity
        {
            Type = PublishingActivityTypes.Task,
            Data = new PublishingActivityData
            {
                Id = task.Id,
                StatusText = task.StatusText,
                CompletionState = ToBackchannelCompletionState(completionState),
                StepId = task.StepId,
                CompletionMessage = completionMessage,
                EnableMarkdown = enableMarkdown
            }
        };

        await ActivityItemUpdated.Writer.WriteAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompletePublishAsync(PublishCompletionOptions? options = null, CancellationToken cancellationToken = default)
    {
        // Use provided state or aggregate from all steps
        var finalState = options?.CompletionState ?? CalculateOverallAggregatedState();

        var state = new PublishingActivity
        {
            Type = PublishingActivityTypes.PublishComplete,
            Data = new PublishingActivityData
            {
                Id = PublishingActivityTypes.PublishComplete,
                StatusText = options?.CompletionMessage ?? finalState switch
                {
                    CompletionState.Completed => "Pipeline completed successfully",
                    CompletionState.CompletedWithWarning => "Pipeline completed with warnings",
                    CompletionState.CompletedWithError => "Pipeline completed with errors",
                    _ => "Pipeline completed"
                },
                CompletionState = ToBackchannelCompletionState(finalState),
                PipelineSummary = options?.PipelineSummary?.Select(item => new BackchannelPipelineSummaryItem
                {
                    Key = item.Key,
                    Value = item.Value,
                    EnableMarkdown = item.EnableMarkdown
                }).ToList()
            }
        };

        await ActivityItemUpdated.Writer.WriteAsync(state, cancellationToken).ConfigureAwait(false);
    }

    [Obsolete("Use CompletePublishAsync(PublishCompletionOptions?, CancellationToken) instead.")]
    public Task CompletePublishAsync(string? completionMessage = null, CompletionState? completionState = null, CancellationToken cancellationToken = default)
    {
        return CompletePublishAsync(new PublishCompletionOptions
        {
            CompletionMessage = completionMessage,
            CompletionState = completionState
        }, cancellationToken);
    }

    /// <summary>
    /// Calculates the overall completion state by aggregating all steps.
    /// </summary>
    private CompletionState CalculateOverallAggregatedState()
    {
        if (_steps.IsEmpty)
        {
            return CompletionState.Completed;
        }

        var maxState = CompletionState.InProgress;
        foreach (var step in _steps.Values)
        {
            var stepState = step.CompletionState;
            if ((int)stepState > (int)maxState)
            {
                maxState = stepState;
            }
        }
        return maxState;
    }

    private async Task SubscribeToInteractionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var interaction in _interactionService.SubscribeInteractionUpdates(cancellationToken).ConfigureAwait(false))
            {
                await WriteInteractionUpdateToClientAsync(interaction, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
    }

    private async Task WriteInteractionUpdateToClientAsync(Interaction interaction, CancellationToken cancellationToken)
    {
        if (interaction.State == Interaction.InteractionState.InProgress)
        {
            if (interaction.InteractionInfo is Interaction.InputsInteractionInfo inputsInfo && inputsInfo.Inputs.Count > 0)
            {
                // Find all the inputs that are depended on.
                // These inputs value changing will cause the interaction to be sent to the server.
                var updateStateOnChangeInputs = inputsInfo.Inputs
                    .SelectMany(i => i.DynamicLoading?.DependsOnInputs ?? [])
                    .ToList();

                var promptInputs = inputsInfo.Inputs.Select(input => new PublishingPromptInput
                {
                    Name = input.Name,
                    Label = input.EffectiveLabel,
                    InputType = input.InputType.ToString(),
                    Required = input.Required,
                    Options = input.Options,
                    Value = input.Value,
                    ValidationErrors = input.ValidationErrors,
                    AllowCustomChoice = input.AllowCustomChoice,
                    UpdateStateOnChange = updateStateOnChangeInputs.Any(i => string.Equals(i, input.Name, StringComparisons.InteractionInputName)),
                    Loading = input.DynamicLoadingState?.Loading ?? false,
                    Disabled = input.Disabled
                }).ToList();

                var activity = new PublishingActivity
                {
                    Type = PublishingActivityTypes.Prompt,
                    Data = new PublishingActivityData
                    {
                        Id = interaction.InteractionId.ToString(CultureInfo.InvariantCulture),
                        StatusText = interaction.Message ?? $"{interaction.Title}: ",
                        CompletionState = ToBackchannelCompletionState(CompletionState.InProgress),
                        Inputs = promptInputs
                    }
                };

                await ActivityItemUpdated.Writer.WriteAsync(activity, cancellationToken).ConfigureAwait(false);
            }
            // Handle notification interaction types (PromptNotificationAsync)
            else if (interaction.InteractionInfo is Interaction.NotificationInteractionInfo)
            {
                var promptInputs = new List<PublishingPromptInput>
                {
                    new PublishingPromptInput
                    {
                        Name = "confirm",
                        Label = "Confirm",
                        InputType = "Boolean",
                        Required = true,
                        Options = null,
                        Value = null,
                        ValidationErrors = null
                    }
                };

                var activity = new PublishingActivity
                {
                    Type = PublishingActivityTypes.Prompt,
                    Data = new PublishingActivityData
                    {
                        Id = interaction.InteractionId.ToString(CultureInfo.InvariantCulture),
                        StatusText = interaction.Message ?? $"{interaction.Title}: ",
                        CompletionState = ToBackchannelCompletionState(CompletionState.InProgress),
                        Inputs = promptInputs
                    }
                };

                await ActivityItemUpdated.Writer.WriteAsync(activity, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task CompleteInteractionAsync(string promptId, PublishingPromptInputAnswer[]? responses, bool updateResponse = false, CancellationToken cancellationToken = default)
    {
        if (int.TryParse(promptId, CultureInfo.InvariantCulture, out var interactionId))
        {
            await _interactionService.ProcessInteractionFromClientAsync(interactionId,
                (interaction, serviceProvider, logger) =>
                {
                    if (interaction.InteractionInfo is Interaction.InputsInteractionInfo inputsInfo)
                    {
                        var options = (InputsDialogInteractionOptions)interaction.Options;

                        // Set values for all inputs if we have responses
                        if (responses is not null)
                        {
                            var dtos = new List<InputDto>();
                            for (var i = 0; i < responses.Length; i++)
                            {
                                var responseAnswer = responses[i];
                                InteractionInput? matchingInput;

                                if (responseAnswer.Name != null)
                                {
                                    if (!inputsInfo.Inputs.TryGetByName(responseAnswer.Name, out matchingInput))
                                    {
                                        _logger.LogWarning("Unable to match answer with name '{InputName}' to an input.", responseAnswer.Name);
                                        continue;
                                    }
                                }
                                else
                                {
                                    matchingInput = inputsInfo.Inputs[i];
                                }

                                dtos.Add(new InputDto(matchingInput.Name, responseAnswer.Value ?? "", matchingInput.InputType));
                            }

                            DashboardServiceData.ProcessInputs(
                                serviceProvider,
                                logger,
                                inputsInfo,
                                dtos,
                                dependencyChange: updateResponse,
                                interaction.CancellationToken);
                        }

                        return new InteractionCompletionState
                        {
                            Complete = !updateResponse,
                            State = inputsInfo.Inputs
                        };
                    }
                    else if (interaction.InteractionInfo is Interaction.NotificationInteractionInfo)
                    {
                        // Handle notification interactions with boolean result
                        var result = false;
                        if (responses is not null && responses.Length > 0)
                        {
                            // Parse the boolean value from the first response
                            result = bool.TryParse(responses[0].Value, out var parsedValue) && parsedValue;
                        }

                        return new InteractionCompletionState
                        {
                            Complete = true,
                            State = result
                        };
                    }

                    return new InteractionCompletionState
                    {
                        Complete = true,
                        State = null
                    };
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_cancellationTokenSource.IsCancellationRequested)
        {
            _cancellationTokenSource.Cancel();
        }

        try
        {
            await _interactionServiceSubscriber.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }

        _cancellationTokenSource.Dispose();
    }

    internal Channel<PublishingActivity> ActivityItemUpdated { get; } = Channel.CreateUnbounded<PublishingActivity>();
}
