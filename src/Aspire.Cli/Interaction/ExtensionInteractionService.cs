// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Threading.Channels;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Interaction;

internal interface IExtensionInteractionService : IInteractionService
{
    IExtensionBackchannel Backchannel { get; }
    void OpenEditor(string projectPath);
    void LogMessage(LogLevel logLevel, string message);
    Task LaunchAppHostAsync(string projectFile, List<string> arguments, List<EnvVar> environment, bool debug);
    void DisplayDashboardUrls(DashboardUrlsState dashboardUrls);
    void NotifyAppHostStartupCompleted();
    void DisplayConsolePlainText(string message);
    Task StartDebugSessionAsync(string workingDirectory, string? projectFile, bool debug, DebugSessionOptions? options = null);
    void WriteDebugSessionMessage(string message, bool stdout, string? textStyle);
    void ConsoleDisplaySubtleMessage(string message, bool allowMarkup = false);
}

internal class ExtensionInteractionService : IExtensionInteractionService
{
    private readonly ConsoleInteractionService _consoleInteractionService;
    private readonly bool _extensionPromptEnabled;
    private readonly CancellationToken _cancellationToken;
    private readonly Channel<Func<Task>> _extensionTaskChannel;

    public IExtensionBackchannel Backchannel { get; }

    public ExtensionInteractionService(ConsoleInteractionService consoleInteractionService, IExtensionBackchannel backchannel, bool extensionPromptEnabled, CancellationToken? cancellationToken = null)
    {
        _consoleInteractionService = consoleInteractionService;
        Backchannel = backchannel;
        _extensionPromptEnabled = extensionPromptEnabled;
        _cancellationToken = cancellationToken ?? CancellationToken.None;
        _extensionTaskChannel = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(async () =>
        {
            while (await _extensionTaskChannel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                try
                {
                    var taskFunction = await _extensionTaskChannel.Reader.ReadAsync().ConfigureAwait(false);
                    await taskFunction.Invoke();
                }
                catch (Exception ex) when (ex is not ExtensionOperationCanceledException)
                {
                    await Backchannel.DisplayErrorAsync(ex.Message.RemoveSpectreFormatting(), _cancellationToken);
                    _consoleInteractionService.DisplayError(ex.Message);
                }
            }
        });
    }

    public async Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.ShowStatusAsync(statusText.RemoveSpectreFormatting(), _cancellationToken));
        Debug.Assert(result);

        try
        {
            return await _consoleInteractionService.ShowStatusAsync(statusText, action, emoji, allowMarkup).ConfigureAwait(false);
        }
        finally
        {
            // Clear the IDE status indicator even if the action threw, to avoid leaving it spinning indefinitely.
            result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.ShowStatusAsync(null, _cancellationToken));
            Debug.Assert(result);
        }
    }

    public void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.ShowStatusAsync(statusText.RemoveSpectreFormatting(), _cancellationToken));
        Debug.Assert(result);

        try
        {
            _consoleInteractionService.ShowStatus(statusText, action, emoji, allowMarkup);
        }
        finally
        {
            // Clear the IDE status indicator even if the action threw, to avoid leaving it spinning indefinitely.
            result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.ShowStatusAsync(null, _cancellationToken));
            Debug.Assert(result);
        }
    }

    public async Task<string> PromptForStringAsync(string promptText, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
    {
        // Check binding first — if a CLI arg was explicitly provided, return it immediately
        // without prompting through either the extension or console path.
        var (wasProvided, value, _) = PromptBinding.Resolve(binding);
        if (wasProvided && value is not null)
        {
            return value;
        }

        if (_extensionPromptEnabled)
        {
            var tcs = new TaskCompletionSource<string>();

            await _extensionTaskChannel.Writer.WriteAsync(async () =>
            {
                try
                {
                    string result;
                    if (isSecret)
                    {
                        // Check if extension supports the new secret prompts capability
                        var hasSecretPromptsCapability = await Backchannel.HasCapabilityAsync(KnownCapabilities.SecretPrompts, _cancellationToken).ConfigureAwait(false);

                        if (hasSecretPromptsCapability)
                        {
                            // Use the new dedicated secret prompt method (no default value for secrets)
                            result = await Backchannel.PromptForSecretStringAsync(promptText.RemoveSpectreFormatting(), validator, required, _cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            // Fallback to regular prompt for older extension versions
                            result = await Backchannel.PromptForStringAsync(promptText.RemoveSpectreFormatting(), binding?.DefaultValue, validator, required, _cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        result = await Backchannel.PromptForStringAsync(promptText.RemoveSpectreFormatting(), binding?.DefaultValue, validator, required, _cancellationToken).ConfigureAwait(false);
                    }

                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, cancellationToken).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        else
        {
            return await _consoleInteractionService.PromptForStringAsync(promptText, validator, isSecret, required, binding, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> PromptForFilePathAsync(string promptText, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
    {
        var (wasProvided, value, _) = PromptBinding.Resolve(binding);
        if (wasProvided && value is not null)
        {
            return value;
        }

        if (_extensionPromptEnabled)
        {
            var hasFilePickersCapability = await Backchannel.HasCapabilityAsync(KnownCapabilities.FilePickers, _cancellationToken).ConfigureAwait(false);

            if (hasFilePickersCapability)
            {
                var tcs = new TaskCompletionSource<string?>();

                await _extensionTaskChannel.Writer.WriteAsync(async () =>
                {
                    try
                    {
                        var result = await Backchannel.PromptForFilePathAsync(promptText.RemoveSpectreFormatting(), binding?.DefaultValue, directory, _cancellationToken).ConfigureAwait(false);
                        tcs.SetResult(result);
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }, cancellationToken).ConfigureAwait(false);

                var picked = await tcs.Task.ConfigureAwait(false);

                if (picked is null)
                {
                    throw new ExtensionOperationCanceledException(promptText);
                }

                if (validator is not null)
                {
                    var validationResult = validator(picked);

                    if (!validationResult.Successful)
                    {
                        var errorMessage = validationResult.Message ?? "Invalid selection.";
                        DisplayError(errorMessage);
                        throw new InvalidOperationException(errorMessage);
                    }
                }

                return picked;
            }

            // Fall back to string prompt for older extensions without file picker support
            return await PromptForStringAsync(promptText, validator, isSecret: false, required, binding, cancellationToken).ConfigureAwait(false);
        }

        return await _consoleInteractionService.PromptForFilePathAsync(promptText, validator, directory, required, binding, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ConfirmAsync(string promptText, PromptBinding<bool>? binding = null, CancellationToken cancellationToken = default)
    {
        var (wasProvided, value, _) = PromptBinding.Resolve(binding);
        if (wasProvided)
        {
            return value;
        }

        if (_extensionPromptEnabled)
        {
            var tcs = new TaskCompletionSource<bool>();

            await _extensionTaskChannel.Writer.WriteAsync(async () =>
            {
                try
                {
                    var result = await Backchannel.ConfirmAsync(promptText.RemoveSpectreFormatting(), binding?.DefaultValue ?? false, _cancellationToken).ConfigureAwait(false);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                    if (ex is not ExtensionOperationCanceledException)
                    {
                        DisplayError(ex.Message);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        else
        {
            return await _consoleInteractionService.ConfirmAsync(promptText, binding, cancellationToken);
        }
    }

    public async Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter,
        PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull
    {
        var (wasProvided, value, _) = PromptBinding.Resolve(binding);
        if (wasProvided && value is not null)
        {
            return _consoleInteractionService.MatchChoiceOrThrow(value, binding!, choices, choiceFormatter);
        }

        if (_extensionPromptEnabled)
        {
            var tcs = new TaskCompletionSource<T>();

            await _extensionTaskChannel.Writer.WriteAsync(async () =>
            {
                try
                {
                    var result = await Backchannel.PromptForSelectionAsync(promptText.RemoveSpectreFormatting(), choices, choiceFormatter, _cancellationToken).ConfigureAwait(false);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                    if (ex is not ExtensionOperationCanceledException)
                    {
                        DisplayError(ex.Message);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        else
        {
            return await _consoleInteractionService.PromptForSelectionAsync(promptText, choices, choiceFormatter, binding, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter,
        IEnumerable<T>? preSelected = null, bool optional = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull
    {
        var (wasProvided, value, _) = PromptBinding.Resolve(binding);
        if (wasProvided && value is not null)
        {
            return _consoleInteractionService.MatchChoicesOrThrow(value, binding!, choices, choiceFormatter);
        }

        if (_extensionPromptEnabled)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<T>>();

            await _extensionTaskChannel.Writer.WriteAsync(async () =>
            {
                try
                {
                    // Note: The extension backchannel protocol does not yet support preSelected items.
                    // Pre-selected items are applied only when falling back to the console interaction service.
                    var result = await Backchannel.PromptForSelectionsAsync(promptText.RemoveSpectreFormatting(), choices, choiceFormatter, _cancellationToken).ConfigureAwait(false);
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                    if (ex is not ExtensionOperationCanceledException)
                    {
                        DisplayError(ex.Message);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);

            return await tcs.Task.ConfigureAwait(false);
        }
        else
        {
            return await _consoleInteractionService.PromptForSelectionsAsync(promptText, choices, choiceFormatter, preSelected, optional, binding, cancellationToken);
        }
    }

    public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingSdkVersion)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayIncompatibleVersionErrorAsync(ex.RequiredCapability, appHostHostingSdkVersion, _cancellationToken));
        Debug.Assert(result);

        return _consoleInteractionService.DisplayIncompatibleVersionError(ex, appHostHostingSdkVersion);
    }

    public void DisplayError(string errorMessage)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayErrorAsync(errorMessage.RemoveSpectreFormatting(), _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayError(errorMessage);
    }

    public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayMessageAsync(emoji.Name, message.RemoveSpectreFormatting(), _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayMessage(emoji, message, allowMarkup);
    }

    public void DisplaySuccess(string message, bool allowMarkup = false)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplaySuccessAsync(message.RemoveSpectreFormatting(), _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplaySuccess(message, allowMarkup);
    }

    public void DisplaySubtleMessage(string message, bool allowMarkup = false)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplaySubtleMessageAsync(message.RemoveSpectreFormatting(), _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplaySubtleMessage(message, allowMarkup);
    }

    public void ConsoleDisplaySubtleMessage(string message, bool allowMarkup = false)
    {
        _consoleInteractionService.DisplaySubtleMessage(message, allowMarkup);
    }

    public void DisplayDashboardUrls(DashboardUrlsState dashboardUrls)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayDashboardUrlsAsync(dashboardUrls, _cancellationToken));
        Debug.Assert(result);
    }

    public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayLinesAsync(lines.Select(line => new DisplayLineState(
            line.Stream == OutputLineStream.StdOut ? "stdout" : "stderr",
            line.Line.RemoveSpectreFormatting())), _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayLines(lines);
    }

    public void DisplayCancellationMessage()
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayCancellationMessageAsync(_cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayCancellationMessage();
    }

    public void DisplayEmptyLine()
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayEmptyLineAsync(_cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayEmptyLine();
    }

    public void OpenEditor(string path)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.OpenEditorAsync(path, _cancellationToken));
        Debug.Assert(result);
    }

    public void DisplayPlainText(string text)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayPlainTextAsync(text, _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayPlainText(text);
    }

    public ConsoleOutput Console
    {
        get => _consoleInteractionService.Console;
        set => _consoleInteractionService.Console = value;
    }

    public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.DisplayPlainTextAsync(text, _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayRawText(text, consoleOverride);
    }

    public void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null)
    {
        // Send raw markdown to extension (it can handle markdown natively)
        // Convert to Spectre markup for console display
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.LogMessageAsync(LogLevel.Information, markdown, _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayMarkdown(markdown, consoleOverride);
    }

    public void DisplayMarkupLine(string markup)
    {
        // Strip markup for backchannel, display as-is to console
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.LogMessageAsync(LogLevel.Information, markup.RemoveSpectreFormatting(), _cancellationToken));
        Debug.Assert(result);
        _consoleInteractionService.DisplayMarkupLine(markup);
    }

    public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null)
    {
        _consoleInteractionService.DisplayVersionUpdateNotification(newerVersion, updateCommand);
    }

    public void DisplayRenderable(IRenderable renderable)
    {
        _consoleInteractionService.DisplayRenderable(renderable);
    }

    public Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback)
    {
        return _consoleInteractionService.DisplayLiveAsync(initialRenderable, callback);
    }

    public void LogMessage(LogLevel logLevel, string message)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.LogMessageAsync(logLevel, message.RemoveSpectreFormatting(), _cancellationToken));
        Debug.Assert(result);
    }

    public Task LaunchAppHostAsync(string projectFile, List<string> arguments, List<EnvVar> environment, bool debug)
    {
        return Backchannel.LaunchAppHostAsync(projectFile, arguments, environment, debug, _cancellationToken);
    }

    public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false)
    {
        _consoleInteractionService.WriteConsoleLog(message, lineNumber, type, isErrorMessage);
    }

    public void NotifyAppHostStartupCompleted()
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.NotifyAppHostStartupCompletedAsync(_cancellationToken));
        Debug.Assert(result);
    }

    public void DisplayConsolePlainText(string message)
    {
        _consoleInteractionService.DisplayPlainText(message);
    }

    public Task StartDebugSessionAsync(string workingDirectory, string? projectFile, bool debug, DebugSessionOptions? options = null)
    {
        return Backchannel.StartDebugSessionAsync(workingDirectory, projectFile, debug, options, _cancellationToken);
    }

    public void WriteDebugSessionMessage(string message, bool stdout, string? textStyle)
    {
        var result = _extensionTaskChannel.Writer.TryWrite(() => Backchannel.WriteDebugSessionMessageAsync(message.RemoveSpectreFormatting(), stdout, textStyle, _cancellationToken));
        Debug.Assert(result);
    }
}
