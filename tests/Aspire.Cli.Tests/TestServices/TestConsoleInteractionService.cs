// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Spectre.Console;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestConsoleInteractionService : IInteractionService
{
    public ConsoleOutput Console { get; set; }
    public Action<string>? DisplayErrorCallback { get; set; }
    public Action<string>? DisplaySubtleMessageCallback { get; set; }
    public Action<string>? DisplayConsoleWriteLineMessage { get; set; }
    public Func<string, bool, bool>? ConfirmCallback { get; set; }
    public Action<string>? ShowStatusCallback { get; set;  }
    
    /// <summary>
    /// Callback for capturing selection prompts in tests. Uses non-generic IEnumerable and object
    /// to work with the generic PromptForSelectionAsync&lt;T&gt; method regardless of T's type.
    /// This allows tests to inspect what choices are presented without knowing the generic type at compile time.
    /// </summary>
    public Func<string, IEnumerable, Func<object, string>, CancellationToken, object>? PromptForSelectionCallback { get; set; }

    public Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action)
    {
        ShowStatusCallback?.Invoke(statusText);
        return action();
    }

    public void ShowStatus(string statusText, Action action)
    {
        action();
    }

    public Task<string> PromptForStringAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(defaultValue ?? string.Empty);
    }

    public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken = default) where T : notnull
    {
        if (!choices.Any())
        {
            throw new EmptyChoicesException($"No items available for selection: {promptText}");
        }

        if (PromptForSelectionCallback is not null)
        {
            // Invoke the callback - casting is safe here because:
            // 1. 'choices' is IEnumerable<T>, and we cast items to T when calling choiceFormatter
            // 2. 'result' comes from the callback which receives 'choices', so it must be of type T
            // 3. These casts are for test infrastructure only, not production code
            var result = PromptForSelectionCallback(promptText, choices, o => choiceFormatter((T)o), cancellationToken);
            return Task.FromResult((T)result);
        }

        return Task.FromResult(choices.First());
    }

    public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken = default) where T : notnull
    {
        if (!choices.Any())
        {
            throw new EmptyChoicesException($"No items available for selection: {promptText}");
        }

        return Task.FromResult<IReadOnlyList<T>>(choices.ToList());
    }

    public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion)
    {
        return 0;
    }

    public void DisplayError(string errorMessage)
    {
        DisplayErrorCallback?.Invoke(errorMessage);
    }

    public Action<string, string>? DisplayMessageCallback { get; set; }

    public void DisplayMessage(string emojiName, string message)
    {
        DisplayMessageCallback?.Invoke(emojiName, message);
    }

    public void DisplaySuccess(string message)
    {
    }

    public void DisplayLines(IEnumerable<(string Stream, string Line)> lines)
    {
    }

    public void DisplayCancellationMessage()
    {
    }

    public Task<bool> ConfirmAsync(string promptText, bool defaultValue = true, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ConfirmCallback != null? ConfirmCallback(promptText, defaultValue) : defaultValue);
    }

    public void DisplaySubtleMessage(string message, bool escapeMarkup = true)
    {
        DisplaySubtleMessageCallback?.Invoke(message);
    }

    public void DisplayEmptyLine()
    {
    }

    public void DisplayPlainText(string text)
    {
    }

    public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null)
    {
    }

    public void DisplayMarkdown(string markdown)
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

    public Action<string>? DisplayVersionUpdateNotificationCallback { get; set; }

    public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null)
    {
        DisplayVersionUpdateNotificationCallback?.Invoke(newerVersion);
    }
}
