// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Utils;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestInteractionService : IInteractionService
{
    private readonly Queue<(string Response, ResponseType Type)> _responses = new();
    private bool _shouldCancel;

    public ConsoleOutput Console { get; set; }

    // Callback hooks
    public Action<string>? DisplaySubtleMessageCallback { get; set; }
    public Action<string>? DisplayConsoleWriteLineMessage { get; set; }
    public Func<string, bool, bool>? ConfirmCallback { get; set; }
    public Action<string>? ShowStatusCallback { get; set; }
    public Action<string>? DisplayVersionUpdateNotificationCallback { get; set; }

    /// <summary>
    /// Callback for capturing selection prompts in tests. Uses non-generic IEnumerable and object
    /// to work with the generic PromptForSelectionAsync&lt;T&gt; method regardless of T's type.
    /// This allows tests to inspect what choices are presented without knowing the generic type at compile time.
    /// </summary>
    public Func<string, IEnumerable, Func<object, string>, CancellationToken, object>? PromptForSelectionCallback { get; set; }

    /// <summary>
    /// Callback for capturing multi-selection prompts in tests without compile-time knowledge of T.
    /// </summary>
    public Func<string, IEnumerable, Func<object, string>, CancellationToken, IReadOnlyList<object>>? PromptForSelectionsCallback { get; set; }

    // Call tracking
    public List<StringPromptCall> StringPromptCalls { get; } = [];
    public List<BooleanPromptCall> BooleanPromptCalls { get; } = [];
    public List<string> DisplayedErrors { get; } = [];
    public List<(KnownEmoji Emoji, string Message)> DisplayedMessages { get; } = [];

    // Response queue setup methods
    public void SetupStringPromptResponse(string response) => _responses.Enqueue((response, ResponseType.String));
    public void SetupSelectionResponse(string response) => _responses.Enqueue((response, ResponseType.Selection));
    public void SetupBooleanResponse(bool response) => _responses.Enqueue((response.ToString().ToLowerInvariant(), ResponseType.Boolean));
    public void SetupCancellationResponse() => _shouldCancel = true;

    public void SetupSequentialResponses(params (string Response, ResponseType Type)[] responses)
    {
        foreach (var (response, type) in responses)
        {
            _responses.Enqueue((response, type));
        }
    }

    public Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        ShowStatusCallback?.Invoke(statusText);
        return action();
    }

    public void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        action();
    }

    public Task<string> PromptForStringAsync(string promptText, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
    {
        StringPromptCalls.Add(new StringPromptCall(promptText, binding?.DefaultValue, isSecret));

        if (_shouldCancel || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        if (_responses.TryDequeue(out var response))
        {
            return Task.FromResult(response.Response);
        }

        return Task.FromResult(binding?.DefaultValue ?? string.Empty);
    }

    public Task<string> PromptForFilePathAsync(string promptText, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
    {
        return PromptForStringAsync(promptText, validator, isSecret: false, required, binding, cancellationToken);
    }

    public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull
    {
        if (_shouldCancel || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        if (!choices.Any())
        {
            throw new EmptyChoicesException($"No items available for selection: {promptText}");
        }

        if (PromptForSelectionCallback is not null)
        {
            var result = PromptForSelectionCallback(promptText, choices, o => choiceFormatter((T)o), cancellationToken);
            return Task.FromResult((T)result);
        }

        if (_responses.TryDequeue(out var response))
        {
            var matchingChoice = choices.FirstOrDefault(c => choiceFormatter(c) == response.Response || c.ToString() == response.Response);
            if (matchingChoice is not null)
            {
                return Task.FromResult(matchingChoice);
            }
        }

        return Task.FromResult(choices.First());
    }

    public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull
    {
        if (_shouldCancel || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        if (!choices.Any())
        {
            throw new EmptyChoicesException($"No items available for selection: {promptText}");
        }

        if (PromptForSelectionsCallback is not null)
        {
            var result = PromptForSelectionsCallback(promptText, choices, o => choiceFormatter((T)o), cancellationToken);
            return Task.FromResult<IReadOnlyList<T>>(result.Cast<T>().ToList());
        }

        _ = _responses.TryDequeue(out _);

        // In tests, return pre-selected items if provided, otherwise all items
        if (preSelected is not null)
        {
            return Task.FromResult<IReadOnlyList<T>>(preSelected.ToList());
        }

        return Task.FromResult<IReadOnlyList<T>>(choices.ToList());
    }

    public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion)
    {
        return 0;
    }

    public void DisplayError(string errorMessage)
    {
        DisplayedErrors.Add(errorMessage);
    }

    public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false)
    {
        DisplayedMessages.Add((emoji, message));
    }

    public void DisplaySuccess(string message, bool allowMarkup = false)
    {
    }

    public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines)
    {
    }

    public void DisplayCancellationMessage()
    {
    }

    public Task<bool> ConfirmAsync(string promptText, PromptBinding<bool>? binding = null, CancellationToken cancellationToken = default)
    {
        var defaultValue = binding?.DefaultValue ?? false;
        BooleanPromptCalls.Add(new BooleanPromptCall(promptText, defaultValue));

        if (_shouldCancel || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException();
        }

        if (ConfirmCallback is not null)
        {
            return Task.FromResult(ConfirmCallback(promptText, defaultValue));
        }

        if (_responses.TryDequeue(out var response))
        {
            return Task.FromResult(bool.Parse(response.Response));
        }

        return Task.FromResult(defaultValue);
    }

    public void DisplaySubtleMessage(string message, bool allowMarkup = false)
    {
        DisplaySubtleMessageCallback?.Invoke(message);
    }

    public void DisplayEmptyLine()
    {
    }

    public void DisplayPlainText(string text)
    {
    }

    public Action<string>? DisplayRawTextCallback { get; set; }

    public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null)
    {
        DisplayRawTextCallback?.Invoke(text);
    }

    public void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null)
    {
    }

    public void DisplayMarkupLine(string markup)
    {
    }

    public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false)
    {
        var output = $"[{(isErrorMessage ? "Error" : type ?? "Info")}] {message} (Line: {lineNumber})";
        DisplayConsoleWriteLineMessage?.Invoke(output);
    }

    public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null)
    {
        DisplayVersionUpdateNotificationCallback?.Invoke(newerVersion);
    }

    public void DisplayRenderable(IRenderable renderable)
    {
    }

    public Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback)
    {
        return callback(_ => { });
    }
}

internal enum ResponseType
{
    String,
    Selection,
    Boolean
}

internal sealed record StringPromptCall(string PromptText, string? DefaultValue, bool IsSecret);
internal sealed record SelectionPromptCall<T>(string PromptText, IEnumerable<T> Choices, Func<T, string> ChoiceFormatter);
internal sealed record BooleanPromptCall(string PromptText, bool DefaultValue);
