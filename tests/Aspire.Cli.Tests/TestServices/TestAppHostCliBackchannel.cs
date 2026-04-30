// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Backchannel;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAppHostBackchannel : IAppHostCliBackchannel
{
    public TaskCompletionSource? RequestStopAsyncCalled { get; set; }
    public Func<Task>? RequestStopAsyncCallback { get; set; }

    public TaskCompletionSource? GetDashboardUrlsAsyncCalled { get; set; }
    public Func<CancellationToken, Task<DashboardUrlsState>>? GetDashboardUrlsAsyncCallback { get; set; }

    public TaskCompletionSource? GetResourceStatesAsyncCalled { get; set; }
    public Func<CancellationToken, IAsyncEnumerable<RpcResourceState>>? GetResourceStatesAsyncCallback { get; set; }

    public TaskCompletionSource? GetAppHostLogEntriesAsyncCalled { get; set; }
    public Func<CancellationToken, IAsyncEnumerable<BackchannelLogEntry>>? GetAppHostLogEntriesAsyncCallback { get; set; }

    public TaskCompletionSource? ConnectAsyncCalled { get; set; }
    public Func<string, CancellationToken, Task>? ConnectAsyncCallback { get; set; }

    public TaskCompletionSource? GetPublishingActivitiesAsyncCalled { get; set; }
    public Func<CancellationToken, IAsyncEnumerable<PublishingActivity>>? GetPublishingActivitiesAsyncCallback { get; set; }

    public TaskCompletionSource? GetCapabilitiesAsyncCalled { get; set; }
    public Func<CancellationToken, Task<string[]>>? GetCapabilitiesAsyncCallback { get; set; }

    public TaskCompletionSource? GetPipelineStepsAsyncCalled { get; set; }
    public Func<string?, CancellationToken, Task<GetPipelineStepsResponse>>? GetPipelineStepsAsyncCallback { get; set; }

    public Task RequestStopAsync(CancellationToken cancellationToken)
    {
        RequestStopAsyncCalled?.SetResult();
        if (RequestStopAsyncCallback != null)
        {
            return RequestStopAsyncCallback.Invoke();
        }
        else
        {
            return Task.CompletedTask;
        }
    }

    public Task<DashboardUrlsState> GetDashboardUrlsAsync(CancellationToken cancellationToken)
    {
        GetDashboardUrlsAsyncCalled?.SetResult();
        return GetDashboardUrlsAsyncCallback != null
            ? GetDashboardUrlsAsyncCallback.Invoke(cancellationToken)
            : Task.FromResult(
                new DashboardUrlsState
                {
                    DashboardHealthy = true,
                    BaseUrlWithLoginToken = "http://localhost:5000/login?t=abcd",
                    CodespacesUrlWithLoginToken = "https://monalisa-hot-potato-vrpqrxxrx7x2rxx-5000.app.github.dev/login?t=abcd"
                });
    }

    public async IAsyncEnumerable<RpcResourceState> GetResourceStatesAsync([EnumeratorCancellation]CancellationToken cancellationToken)
    {
        GetResourceStatesAsyncCalled?.SetResult();

        if (GetResourceStatesAsyncCallback != null)
        {
            var resourceStates = GetResourceStatesAsyncCallback.Invoke(cancellationToken).ConfigureAwait(false);
            await foreach (var resourceState in resourceStates)
            {
                yield return resourceState;
            }
        }
        else
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                yield return new RpcResourceState
                {
                    Resource = "frontend",
                    Type = "Project",
                    State = "Starting",
                    Endpoints = new[] { "http://localhost:5000" },
                    Health = "Healthy"
                };
                yield return new RpcResourceState
                {
                    Resource = "backend",
                    Type = "Project",
                    State = "Running",
                    Endpoints = new[] { "http://localhost:5001" },
                    Health = "Healthy"
                };
            }
        }
    }

    public Task ConnectAsync(string socketPath, int retryCount, CancellationToken cancellationToken)
        => ConnectAsync(socketPath, autoReconnect: false, retryCount: retryCount, cancellationToken);

    public async Task ConnectAsync(string socketPath, bool autoReconnect, int retryCount, CancellationToken cancellationToken)
    {
        ConnectAsyncCalled?.SetResult();
        if (ConnectAsyncCallback != null)
        {
            await ConnectAsyncCallback.Invoke(socketPath, cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<PublishingActivity> GetPublishingActivitiesAsync([EnumeratorCancellation]CancellationToken cancellationToken)
    {
        GetPublishingActivitiesAsyncCalled?.SetResult();
        if (GetPublishingActivitiesAsyncCallback is not null)
        {
            var publishingActivities = GetPublishingActivitiesAsyncCallback.Invoke(cancellationToken).ConfigureAwait(false);

            await foreach (var activity in publishingActivities)
            {
                yield return activity;
            }
        }
        else
        {
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Step,
                Data = new PublishingActivityData
                {
                    Id = "root-step",
                    StatusText = "Publishing artifacts",
                    CompletionState = CompletionStates.InProgress,
                    StepId = null
                }
            };
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Task,
                Data = new PublishingActivityData
                {
                    Id = "child-task-1",
                    StatusText = "Generating YAML goodness",
                    CompletionState = CompletionStates.InProgress,
                    StepId = "root-step"
                }
            };
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Task,
                Data = new PublishingActivityData
                {
                    Id = "child-task-1",
                    StatusText = "Generating YAML goodness",
                    CompletionState = CompletionStates.Completed,
                    StepId = "root-step"
                }
            };
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Task,
                Data = new PublishingActivityData
                {
                    Id = "child-task-2",
                    StatusText = "Building image 1",
                    CompletionState = CompletionStates.InProgress,
                    StepId = "root-step"
                }
            };
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Task,
                Data = new PublishingActivityData
                {
                    Id = "child-task-2",
                    StatusText = "Building image 1",
                    CompletionState = CompletionStates.Completed,
                    StepId = "root-step"
                }
            };
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Task,
                Data = new PublishingActivityData
                {
                    Id = "child-task-2",
                    StatusText = "Building image 2",
                    CompletionState = CompletionStates.InProgress,
                    StepId = "root-step"
                }
            };
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Task,
                Data = new PublishingActivityData
                {
                    Id = "child-task-2",
                    StatusText = "Building image 2",
                    CompletionState = CompletionStates.Completed,
                    StepId = "root-step"
                }
            };
            yield return new PublishingActivity
            {
                Type = PublishingActivityTypes.Step,
                Data = new PublishingActivityData
                {
                    Id = "root-step",
                    StatusText = "Publishing artifacts",
                    CompletionState = CompletionStates.Completed,
                    StepId = null
                }
            };
        }
    }

    public async Task<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        GetCapabilitiesAsyncCalled?.SetResult();
        if (GetCapabilitiesAsyncCallback != null)
        {
            return await GetCapabilitiesAsyncCallback(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            return ["baseline.v2", "pipeline-steps.v1"];
        }
    }

    public async IAsyncEnumerable<BackchannelLogEntry> GetAppHostLogEntriesAsync([EnumeratorCancellation]CancellationToken cancellationToken)
    {
        GetAppHostLogEntriesAsyncCalled?.SetResult();
        if (GetAppHostLogEntriesAsyncCallback != null)
        {
            await foreach (var entry in GetAppHostLogEntriesAsyncCallback.Invoke(cancellationToken))
            {
                yield return entry;
            }
        }
    }

    public Task CompletePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task UpdatePromptResponseAsync(string promptId, PublishingPromptInputAnswer[] answers, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<CommandOutput> ExecAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        yield return new CommandOutput { Text = "test", IsErrorMessage = false, LineNumber = 0 };
    }

    public async Task<GetPipelineStepsResponse> GetPipelineStepsAsync(string? step, CancellationToken cancellationToken)
    {
        GetPipelineStepsAsyncCalled?.SetResult();
        if (GetPipelineStepsAsyncCallback is not null)
        {
            return await GetPipelineStepsAsyncCallback(step, cancellationToken).ConfigureAwait(false);
        }

        return new GetPipelineStepsResponse
        {
            Steps = [
                new PipelineStepInfo { Name = "process-parameters", Description = "Prompts for parameter values" },
                new PipelineStepInfo { Name = "build-webapi", DependsOn = ["process-parameters"], Tags = ["build-compute"] },
                new PipelineStepInfo { Name = "deploy-webapi", DependsOn = ["build-webapi"], Tags = ["deploy-compute"] }
            ]
        };
    }
}
