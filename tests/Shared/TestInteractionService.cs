// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Hosting.Terminals;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates.
#pragma warning disable ASPIRETERMINAL001 // Test consumer of the experimental AppHost terminal API.

internal enum InteractionType
{
    Input,
    Inputs,
    MessageBox,
    Notification,
    Progress,
    Terminal
}

internal sealed record InteractionData(InteractionType Type, string Title, string? Message, InteractionInputCollection Inputs, InteractionOptions? Options, CancellationToken CancellationToken, TaskCompletionSource<object> CompletionTcs)
{
    public AspireTerminal? Terminal { get; init; }
}

internal sealed class TestInteractionService : IInteractionService
{
    public Channel<InteractionData> Interactions { get; } = Channel.CreateUnbounded<InteractionData>();

    public bool IsAvailable { get; set; } = true;

    public Task<InteractionResult<bool>> PromptConfirmationAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, string inputLabel, string placeHolder, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, InteractionInput input, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var data = new InteractionData(InteractionType.Input, title, message, new InteractionInputCollection([input]), options, cancellationToken, new TaskCompletionSource<object>());
        Interactions.Writer.TryWrite(data);
        var result = (InteractionResult<InteractionInput>)await data.CompletionTcs.Task;
        return result;
    }

    public async Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(string title, string? message, IReadOnlyList<InteractionInput> inputs, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var data = new InteractionData(InteractionType.Inputs, title, message, new InteractionInputCollection(inputs), options, cancellationToken, new TaskCompletionSource<object>());
        Interactions.Writer.TryWrite(data);
        var result = (InteractionResult<InteractionInputCollection>)await data.CompletionTcs.Task;

        // Convert the result to use InteractionInputCollection
        if (result.Canceled)
        {
            return InteractionResult.Cancel<InteractionInputCollection>();
        }

        return InteractionResult.Ok(new InteractionInputCollection(result.Data));
    }

    public async Task<InteractionResult<bool>> PromptNotificationAsync(string title, string message, NotificationInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var data = new InteractionData(InteractionType.Notification, title, message, new InteractionInputCollection([]), options, cancellationToken, new TaskCompletionSource<object>());
        Interactions.Writer.TryWrite(data);
        return (InteractionResult<bool>)await data.CompletionTcs.Task;
    }

    public async Task<InteractionResult<bool>> PromptMessageBoxAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var data = new InteractionData(InteractionType.MessageBox, title, message, new InteractionInputCollection([]), options, cancellationToken, new TaskCompletionSource<object>());
        Interactions.Writer.TryWrite(data);
        return (InteractionResult<bool>)await data.CompletionTcs.Task;
    }

    public bool PromptProgressCalled { get; private set; }

    public async Task<InteractionResult<bool>> PromptProgressAsync(string message, ProgressInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        PromptProgressCalled = true;

        return await PromptWorkAsync(
            InteractionType.Progress, options?.Title, message, options, terminal: null,
            options?.Work is { } work ? token => work(new ProgressContext { CancellationToken = token }) : null,
            cancellationToken).ConfigureAwait(false);
    }

    public Task<InteractionResult<bool>> PromptTerminalAsync(string message, AspireTerminal terminal, TerminalInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return PromptWorkAsync(
            InteractionType.Terminal, options?.Title, message, options, terminal,
            options?.Work is { } work ? token => work(new TerminalContext { CancellationToken = token }) : null,
            cancellationToken);
    }

    private async Task<InteractionResult<bool>> PromptWorkAsync(
        InteractionType type, string? title, string message, InteractionOptions? options, AspireTerminal? terminal,
        Func<CancellationToken, Task>? work, CancellationToken cancellationToken)
    {
        if (work is not null)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var data = new InteractionData(type, title ?? string.Empty, message, new InteractionInputCollection([]), options, cancellationToken, new TaskCompletionSource<object>())
            {
                Terminal = terminal
            };
            Interactions.Writer.TryWrite(data);

            // Run the work and handle button clicks (CompletionTcs) canceling the work.
            var workTask = work(cts.Token);
            var completionTask = data.CompletionTcs.Task;

            var finished = await Task.WhenAny(workTask, completionTask).ConfigureAwait(false);
            if (finished == completionTask)
            {
                // Button was clicked — cancel the work.
                cts.Cancel();
                try
                {
                    await workTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                return InteractionResult.Cancel<bool>();
            }

            await workTask.ConfigureAwait(false);
            return InteractionResult.Ok(true);
        }

        return InteractionResult.Ok(true);
    }
}
