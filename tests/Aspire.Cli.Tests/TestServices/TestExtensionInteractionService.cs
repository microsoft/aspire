// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Interaction;
using Aspire.Cli.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestExtensionInteractionService(IServiceProvider serviceProvider) : IExtensionInteractionService
{
    public ConsoleOutput Console { get; set; }
    public Action<string>? DisplayErrorCallback { get; set; }
    public Action<string>? DisplaySubtleMessageCallback { get; set; }
    public Action<string>? DisplayConsoleWriteLineMessage { get; set; }
    public Action? LaunchAppHostCallback { get; set; }
    public Action? NotifyAppHostStartupCompletedCallback { get; set; }
    public Action<DashboardUrlsState>? DisplayDashboardUrlsCallback { get; set; }
    public Action<string, string?, bool>? StartDebugSessionCallback { get; set; }
    public Action<string, bool>? ConsoleDisplaySubtleMessageCallback { get; set; }

    public IExtensionBackchannel Backchannel { get; } = serviceProvider.GetRequiredService<IExtensionBackchannel>();

    public Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        return action();
    }

    public void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        action();
    }

    public Task<string> PromptForStringAsync(string promptText, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(binding?.DefaultValue ?? string.Empty);
    }

    public Task<string> PromptForFilePathAsync(string promptText, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
    {
        return PromptForStringAsync(promptText, validator, isSecret: false, required, binding, cancellationToken);
    }

    public Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull
    {
        if (!choices.Any())
        {
            throw new EmptyChoicesException($"No items available for selection: {promptText}");
        }

        return Task.FromResult(choices.First());
    }

    public Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull
    {
        if (!choices.Any())
        {
            throw new EmptyChoicesException($"No items available for selection: {promptText}");
        }

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
        DisplayErrorCallback?.Invoke(errorMessage);
    }

    public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false)
    {
    }

    public void DisplaySuccess(string message, bool allowMarkup = false)
    {
    }

    public void DisplayDashboardUrls(DashboardUrlsState dashboardUrls)
    {
        DisplayDashboardUrlsCallback?.Invoke(dashboardUrls);
    }

    public void NotifyAppHostStartupCompleted()
    {
        NotifyAppHostStartupCompletedCallback?.Invoke();
    }

    public void DisplayConsolePlainText(string message)
    {
        DisplayConsoleWriteLineMessage?.Invoke(message);
    }

    public Task StartDebugSessionAsync(string workingDirectory, string? projectFile, bool debug, DebugSessionOptions? options = null)
    {
        StartDebugSessionCallback?.Invoke(workingDirectory, projectFile, debug);
        return Task.CompletedTask;
    }

    public void WriteDebugSessionMessage(string message, bool stdout, string? textStyle)
    {
    }

    public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines)
    {
    }

    public void DisplayCancellationMessage()
    {
    }

    public Task<bool> PromptConfirmAsync(string promptText, PromptBinding<bool>? binding = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true);
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

    public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null)
    {
    }

    public void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null, int? maxWidth = null)
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

    public void DisplayRenderable(IRenderable renderable)
    {
    }

    public Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback)
    {
        return callback(_ => { });
    }

    public Action<string>? OpenEditorCallback { get; set; }

    public void OpenEditor(string projectPath)
    {
        OpenEditorCallback?.Invoke(projectPath);
    }

    public Action<int, string>? RequestAppHostAttachCallback { get; set; }

    public Task RequestAppHostAttachAsync(int processId, string projectName)
    {
        RequestAppHostAttachCallback?.Invoke(processId, projectName);
        return Task.CompletedTask;
    }

    public Action<LogLevel, string>? LogMessageCallback { get; set; }

    public void LogMessage(LogLevel logLevel, string message)
    {
        LogMessageCallback?.Invoke(logLevel, message);
    }

    public Task LaunchAppHostAsync(string projectFile, List<string> arguments, List<EnvVar> environment, bool debug)
    {
        LaunchAppHostCallback?.Invoke();
        return Task.CompletedTask;
    }

    public void ConsoleDisplaySubtleMessage(string message, bool allowMarkup = false)
    {
        ConsoleDisplaySubtleMessageCallback?.Invoke(message, allowMarkup);
    }
}
