// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.InternalTesting;
using Aspire.Cli.Backchannel;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestExtensionBackchannel : IExtensionBackchannel
{
    public TaskCompletionSource? ConnectAsyncCalled { get; set; }
    public Action? ConnectAsyncCallback { get; set; }

    public TaskCompletionSource? DisplayMessageAsyncCalled { get; set; }
    public Func<string, string, Task>? DisplayMessageAsyncCallback { get; set; }

    public TaskCompletionSource? DisplaySuccessAsyncCalled { get; set; }
    public Func<string, Task>? DisplaySuccessAsyncCallback { get; set; }

    public TaskCompletionSource? DisplaySubtleMessageAsyncCalled { get; set; }
    public Func<string, Task>? DisplaySubtleMessageAsyncCallback { get; set; }

    public TaskCompletionSource? DisplayErrorAsyncCalled { get; set; }
    public Func<string, Task>? DisplayErrorAsyncCallback { get; set; }

    public TaskCompletionSource? DisplayEmptyLineAsyncCalled { get; set; }
    public Func<Task>? DisplayEmptyLineAsyncCallback { get; set; }

    public TaskCompletionSource? DisplayIncompatibleVersionErrorAsyncCalled { get; set; }
    public Func<string, string, Task>? DisplayIncompatibleVersionErrorAsyncCallback { get; set; }

    public TaskCompletionSource? DisplayCancellationMessageAsyncCalled { get; set; }
    public Func<Task>? DisplayCancellationMessageAsyncCallback { get; set; }

    public TaskCompletionSource? DisplayLinesAsyncCalled { get; set; }
    public Func<IEnumerable<DisplayLineState>, Task>? DisplayLinesAsyncCallback { get; set; }

    public TaskCompletionSource? DisplayDashboardUrlsAsyncCalled { get; set; }
    public Func<DashboardUrlsState, Task>? DisplayDashboardUrlsAsyncCallback { get; set; }

    public TaskCompletionSource? ShowStatusAsyncCalled { get; set; }
    public Func<string?, Task>? ShowStatusAsyncCallback { get; set; }

    public TaskCompletionSource? PromptForSelectionAsyncCalled { get; set; }

    public TaskCompletionSource? PromptForSelectionsAsyncCalled { get; set; }

    public TaskCompletionSource? ConfirmAsyncCalled { get; set; }
    public Func<string, bool, Task<bool>>? ConfirmAsyncCallback { get; set; }

    public TaskCompletionSource? PromptForStringAsyncCalled { get; set; }
    public Func<string, string?, Func<string, ValidationResult>?, bool, Task<string>>? PromptForStringAsyncCallback { get; set; }

    public TaskCompletionSource? PromptForSecretStringAsyncCalled { get; set; }
    public Func<string, Func<string, ValidationResult>?, bool, Task<string>>? PromptForSecretStringAsyncCallback { get; set; }

    public TaskCompletionSource? PromptForFilePathAsyncCalled { get; set; }
    public Func<string, string?, bool, Task<string?>>? PromptForFilePathAsyncCallback { get; set; }

    public TaskCompletionSource? OpenEditorAsyncCalled { get; set; }
    public Func<string, Task>? OpenEditorAsyncCallback { get; set; }

    public TaskCompletionSource? LogMessageAsyncCalled { get; set; }
    public Func<LogLevel, string, Task>? LogMessageAsyncCallback { get; set; }

    public TaskCompletionSource? GetCapabilitiesAsyncCalled { get; set; }
    public Func<CancellationToken, Task<string[]>>? GetCapabilitiesAsyncCallback { get; set; }

    public TaskCompletionSource? HasCapabilityAsyncCalled { get; set; }
    public Func<string, CancellationToken, Task<bool>>? HasCapabilityAsyncCallback { get; set; }

    public TaskCompletionSource? LaunchAppHostAsyncCalled { get; set; }
    public Func<string, List<string>, List<EnvVar>, bool, Task>? LaunchAppHostAsyncCallback { get; set; }

    public TaskCompletionSource? NotifyAppHostStartupCompletedAsyncCalled { get; set; }

    public TaskCompletionSource? StartDebugSessionAsyncCalled { get; set; }
    public Func<string, string?, bool, DebugSessionOptions?, Task>? StartDebugSessionAsyncCallback { get; set; }

    public TaskCompletionSource? DisplayPlainTextAsyncCalled { get; set; }
    public Func<string, Task>? DisplayPlainTextAsyncCallback { get; set; }

    public TaskCompletionSource? WriteDebugSessionMessageAsyncCalled { get; set; }
    public Func<string, bool, string?, Task>? WriteDebugSessionMessageAsyncCallback { get; set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ConnectAsyncCalled?.SetResult();
        ConnectAsyncCallback?.Invoke();
        return Task.CompletedTask;
    }

    public Task DisplayMessageAsync(string emojiName, string message, CancellationToken cancellationToken)
    {
        DisplayMessageAsyncCalled?.SetResult();
        return DisplayMessageAsyncCallback?.Invoke(emojiName, message) ?? Task.CompletedTask;
    }

    public Task DisplaySuccessAsync(string message, CancellationToken cancellationToken)
    {
        DisplaySuccessAsyncCalled?.SetResult();
        return DisplaySuccessAsyncCallback?.Invoke(message) ?? Task.CompletedTask;
    }

    public Task DisplaySubtleMessageAsync(string message, CancellationToken cancellationToken)
    {
        DisplaySubtleMessageAsyncCalled?.SetResult();
        return DisplaySubtleMessageAsyncCallback?.Invoke(message) ?? Task.CompletedTask;
    }

    public Task DisplayErrorAsync(string errorMessage, CancellationToken cancellationToken)
    {
        DisplayErrorAsyncCalled?.SetResult();
        return DisplayErrorAsyncCallback?.Invoke(errorMessage) ?? Task.CompletedTask;
    }

    public Task DisplayEmptyLineAsync(CancellationToken cancellationToken)
    {
        DisplayEmptyLineAsyncCalled?.SetResult();
        return DisplayEmptyLineAsyncCallback?.Invoke() ?? Task.CompletedTask;
    }

    public Task DisplayIncompatibleVersionErrorAsync(string appHostHostingVersion, string errorMessage, CancellationToken cancellationToken)
    {
        DisplayIncompatibleVersionErrorAsyncCalled?.SetResult();
        return DisplayIncompatibleVersionErrorAsyncCallback?.Invoke(appHostHostingVersion, errorMessage) ?? Task.CompletedTask;
    }

    public Task DisplayCancellationMessageAsync(CancellationToken cancellationToken)
    {
        DisplayCancellationMessageAsyncCalled?.SetResult();
        return DisplayCancellationMessageAsyncCallback?.Invoke() ?? Task.CompletedTask;
    }

    public Task DisplayLinesAsync(IEnumerable<DisplayLineState> lines, CancellationToken cancellationToken)
    {
        DisplayLinesAsyncCalled?.SetResult();
        return DisplayLinesAsyncCallback?.Invoke(lines) ?? Task.CompletedTask;
    }

    public Task DisplayDashboardUrlsAsync(DashboardUrlsState dashboardUrls, CancellationToken cancellationToken)
    {
        DisplayDashboardUrlsAsyncCalled?.SetResult();
        return DisplayDashboardUrlsAsyncCallback?.Invoke(dashboardUrls) ?? Task.CompletedTask;
    }

    public Task ShowStatusAsync(string? status, CancellationToken cancellationToken)
    {
        ShowStatusAsyncCalled?.SetResult();
        return ShowStatusAsyncCallback?.Invoke(status) ?? Task.CompletedTask;
    }

    public Task<string?> PromptForFilePathAsync(string promptText, string? defaultValue, bool directory, CancellationToken cancellationToken)
    {
        PromptForFilePathAsyncCalled?.SetResult();
        return PromptForFilePathAsyncCallback != null
            ? PromptForFilePathAsyncCallback.Invoke(promptText, defaultValue, directory)
            : Task.FromResult(defaultValue);
    }

    public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken) where T : notnull
    {
        PromptForSelectionAsyncCalled?.SetResult();

        if (!choices.Any())
        {
            throw new InvalidOperationException($"No items available for selection: {promptText}");
        }

        return Task.FromResult(choices.First());
    }

    public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken) where T : notnull
    {
        PromptForSelectionsAsyncCalled?.SetResult();

        if (!choices.Any())
        {
            throw new InvalidOperationException($"No items available for selection: {promptText}");
        }

        return Task.FromResult<IReadOnlyList<T>>(choices.ToList());
    }

    public Task<bool> ConfirmAsync(string promptText, bool defaultValue = true, CancellationToken cancellationToken = default)
    {
        ConfirmAsyncCalled?.SetResult();
        return ConfirmAsyncCallback != null
            ? ConfirmAsyncCallback.Invoke(promptText, defaultValue)
            : Task.FromResult(true);
    }

    public Task<string> PromptForStringAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool required = false, CancellationToken cancellationToken = default)
    {
        PromptForStringAsyncCalled?.SetResult();
        return PromptForStringAsyncCallback != null
            ? PromptForStringAsyncCallback.Invoke(promptText, defaultValue, validator, required)
            : Task.FromResult(defaultValue ?? string.Empty);
    }

    public Task<string> PromptForSecretStringAsync(string promptText, Func<string, ValidationResult>? validator = null, bool required = false, CancellationToken cancellationToken = default)
    {
        PromptForSecretStringAsyncCalled?.SetResult();
        return PromptForSecretStringAsyncCallback != null
            ? PromptForSecretStringAsyncCallback.Invoke(promptText, validator, required)
            : Task.FromResult(string.Empty);
    }

    public Task OpenEditorAsync(string projectPath, CancellationToken cancellationToken)
    {
        OpenEditorAsyncCalled?.SetResult();
        return OpenEditorAsyncCallback != null
            ? OpenEditorAsyncCallback.Invoke(projectPath)
            : Task.CompletedTask;
    }

    public Task LogMessageAsync(LogLevel logLevel, string message, CancellationToken cancellationToken)
    {
        LogMessageAsyncCalled?.SetResult();
        return LogMessageAsyncCallback != null
            ? LogMessageAsyncCallback.Invoke(logLevel, message)
            : Task.CompletedTask;
    }

    public Task<string[]> GetCapabilitiesAsync(CancellationToken cancellationToken)
    {
        GetCapabilitiesAsyncCalled?.SetResult();
        return GetCapabilitiesAsyncCallback != null
            ? GetCapabilitiesAsyncCallback.Invoke(cancellationToken)
            : Task.FromResult(Array.Empty<string>());
    }

    public async Task<bool> HasCapabilityAsync(string capability, CancellationToken cancellationToken)
    {
        HasCapabilityAsyncCalled?.SetResult();
        if (HasCapabilityAsyncCallback != null)
        {
            return await HasCapabilityAsyncCallback.Invoke(capability, cancellationToken);
        }

        // Default behavior: check if capability is in the capabilities returned by GetCapabilitiesAsync
        var capabilities = await GetCapabilitiesAsync(cancellationToken).DefaultTimeout();
        return capabilities.Contains(capability);
    }

    public Task LaunchAppHostAsync(string projectPath, List<string> arguments, List<EnvVar> envVars, bool debug, CancellationToken cancellationToken)
    {
        LaunchAppHostAsyncCalled?.SetResult();
        return LaunchAppHostAsyncCallback != null
            ? LaunchAppHostAsyncCallback.Invoke(projectPath, arguments, envVars, debug)
            : Task.CompletedTask;
    }

    public Task NotifyAppHostStartupCompletedAsync(CancellationToken cancellationToken)
    {
        NotifyAppHostStartupCompletedAsyncCalled?.SetResult();
        return Task.CompletedTask;
    }

    public Task StartDebugSessionAsync(string workingDirectory, string? projectFile, bool debug,
        DebugSessionOptions? options, CancellationToken cancellationToken)
    {
        StartDebugSessionAsyncCalled?.SetResult();
        return StartDebugSessionAsyncCallback != null
            ? StartDebugSessionAsyncCallback.Invoke(workingDirectory, projectFile, debug, options)
            : Task.CompletedTask;
    }

    public Task DisplayPlainTextAsync(string text, CancellationToken cancellationToken)
    {
        DisplayPlainTextAsyncCalled?.SetResult();
        return DisplayPlainTextAsyncCallback != null
            ? DisplayPlainTextAsyncCallback.Invoke(text)
            : Task.CompletedTask;
    }

    public Task WriteDebugSessionMessageAsync(string message, bool stdout, string? textStyle, CancellationToken cancellationToken)
    {
        WriteDebugSessionMessageAsyncCalled?.SetResult();
        return WriteDebugSessionMessageAsyncCallback != null
            ? WriteDebugSessionMessageAsyncCallback.Invoke(message, stdout, textStyle)
            : Task.CompletedTask;
    }
}
