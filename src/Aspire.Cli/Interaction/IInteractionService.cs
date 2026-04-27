// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Utils;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Interaction;

internal interface IInteractionService
{
    Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false);
    void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false);
    Task<string> PromptForStringAsync(string promptText, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default);
    Task<string> PromptForFilePathAsync(string promptText, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default);
    public Task<bool> PromptConfirmAsync(string promptText, PromptBinding<bool>? binding = null, CancellationToken cancellationToken = default);
    Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull;
    Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default) where T : notnull;
    int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion);
    void DisplayError(string errorMessage);
    void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false);
    void DisplayPlainText(string text);
    void DisplayRawText(string text, ConsoleOutput? consoleOverride = null);
    void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null);
    void DisplayMarkupLine(string markup);
    void DisplaySuccess(string message, bool allowMarkup = false);
    void DisplaySubtleMessage(string message, bool allowMarkup = false);
    void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines);
    void DisplayRenderable(IRenderable renderable);
    Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback);
    void DisplayCancellationMessage();
    void DisplayEmptyLine();

    /// <summary>
    /// Gets or sets the default console output stream for human-readable messages.
    /// When set to <see cref="ConsoleOutput.Error"/>, display methods route output to stderr
    /// so that structured output (e.g., JSON) on stdout remains parseable.
    /// </summary>
    ConsoleOutput Console { get; set; }

    void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null);
    // The semantic type is stringly-typed because some values originate from backchannel payloads.
    // Use ConsoleLogTypes for CLI-defined values.
    void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false);
}
