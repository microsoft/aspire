// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Interaction;

internal class ConsoleInteractionService : IInteractionService
{
    private static readonly Style s_exitCodeMessageStyle = new Style(foreground: Color.RoyalBlue1, background: null, decoration: Decoration.None);
    private static readonly Style s_infoMessageStyle = new Style(foreground: Color.Green, background: null, decoration: Decoration.None);
    private static readonly Style s_waitingMessageStyle = new Style(foreground: Color.Yellow, background: null, decoration: Decoration.None);
    private static readonly Style s_errorMessageStyle = new Style(foreground: Color.Red, background: null, decoration: Decoration.Bold);
    private static readonly Style s_searchHighlightStyle = new Style(foreground: Color.Black, background: Color.Cyan1, decoration: Decoration.None);

    private readonly IAnsiConsole _outConsole;
    private readonly IAnsiConsole _errorConsole;
    private readonly CliExecutionContext _executionContext;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly ILogger _stdoutLogger;
    private readonly ILogger _stderrLogger;
    private int _inStatus;

    /// <summary>
    /// Console used for human-readable messages; routes to stderr when <see cref="Console"/> is set to <see cref="ConsoleOutput.Error"/>.
    /// </summary>
    private IAnsiConsole MessageConsole => Console == ConsoleOutput.Error ? _errorConsole : _outConsole;

    // Limit logging to prompts and messages. Don't log raw text output since it may contain sensitive information.
    private ILogger MessageLogger => Console == ConsoleOutput.Error ? _stderrLogger : _stdoutLogger;

    public ConsoleOutput Console { get; set; }

    public ConsoleInteractionService(ConsoleEnvironment consoleEnvironment, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(consoleEnvironment);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _outConsole = consoleEnvironment.Out;
        _errorConsole = consoleEnvironment.Error;
        _executionContext = executionContext;
        _hostEnvironment = hostEnvironment;
        _stdoutLogger = loggerFactory.CreateLogger("Aspire.Cli.Console.Stdout");
        _stderrLogger = loggerFactory.CreateLogger("Aspire.Cli.Console.Stderr");
    }

    public async Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        if (!allowMarkup)
        {
            statusText = statusText.EscapeMarkup();
        }

        if (emoji is { } e)
        {
            statusText = ConsoleHelpers.FormatEmojiPrefix(e, MessageConsole) + statusText;
        }

        // Use atomic check-and-set to prevent nested Spectre.Console Status operations.
        // Spectre.Console throws if multiple interactive operations run concurrently.
        // If already in a status, or in debug/non-interactive mode, fall back to subtle message.
        // Also skip status display if statusText is empty (e.g., when outputting JSON)
        if (Interlocked.CompareExchange(ref _inStatus, 1, 0) != 0 ||
            _executionContext.DebugMode ||
            !_hostEnvironment.SupportsInteractiveOutput ||
            string.IsNullOrEmpty(statusText))
        {
            // Skip displaying if status text is empty (e.g., when outputting JSON)
            if (!string.IsNullOrEmpty(statusText))
            {
                // Text has already been escaped and emoji prepended, so pass as markup
                DisplaySubtleMessage(statusText, allowMarkup: true);
            }
            else
            {
                MessageLogger.LogInformation("Status: {StatusText}", statusText);
            }
            return await action();
        }

        try
        {
            return await MessageConsole.Status()
                .Spinner(Spinner.Known.Dots3)
                .StartAsync(statusText, (context) => action());
        }
        finally
        {
            Interlocked.Exchange(ref _inStatus, 0);
        }
    }

    public void ShowStatus(string statusText, Action action, KnownEmoji? emoji = null, bool allowMarkup = false)
    {
        MessageLogger.LogInformation("Status: {StatusText}", statusText);

        if (!allowMarkup)
        {
            statusText = statusText.EscapeMarkup();
        }

        if (emoji is { } e)
        {
            statusText = ConsoleHelpers.FormatEmojiPrefix(e, MessageConsole) + statusText;
        }

        // Use atomic check-and-set to prevent nested Spectre.Console Status operations.
        // Spectre.Console throws if multiple interactive operations run concurrently.
        // If already in a status, or in debug/non-interactive mode, fall back to subtle message.
        // Also skip status display if statusText is empty (e.g., when outputting JSON)
        if (Interlocked.CompareExchange(ref _inStatus, 1, 0) != 0 ||
            _executionContext.DebugMode ||
            !_hostEnvironment.SupportsInteractiveOutput ||
            string.IsNullOrEmpty(statusText))
        {
            if (!string.IsNullOrEmpty(statusText))
            {
                // Text has already been escaped and emoji prepended, so pass as markup
                DisplaySubtleMessage(statusText, allowMarkup: true);
            }
            action();
            return;
        }

        try
        {
            MessageConsole.Status()
                .Spinner(Spinner.Known.Dots3)
                .Start(statusText, (context) => action());
        }
        finally
        {
            Interlocked.Exchange(ref _inStatus, 0);
        }
    }

    public async Task<string> PromptForStringAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promptText, nameof(promptText));

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        MessageLogger.LogInformation("Prompt: {PromptText} (default: {DefaultValue}, secret: {IsSecret})", promptText, isSecret ? "****" : defaultValue ?? "(none)", isSecret);

        var prompt = new TextPrompt<string>(promptText)
        {
            IsSecret = isSecret,
            AllowEmpty = !required
        };

        if (defaultValue is not null)
        {
            prompt.DefaultValue(defaultValue);
            prompt.ShowDefaultValue();
            prompt.DefaultValueStyle(new Style(Color.Fuchsia));
        }

        if (validator is not null)
        {
            prompt.Validate(validator);
        }

        var result = await MessageConsole.PromptAsync(prompt, cancellationToken);
        MessageLogger.LogInformation("Prompt result: {Result}", isSecret ? "****" : result);
        return result;
    }

    public Task<string> PromptForFilePathAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, CancellationToken cancellationToken = default)
    {
        return PromptForStringAsync(promptText, defaultValue, validator, isSecret: false, required, cancellationToken);
    }

    public async Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(promptText, nameof(promptText));
        ArgumentNullException.ThrowIfNull(choices, nameof(choices));
        ArgumentNullException.ThrowIfNull(choiceFormatter, nameof(choiceFormatter));

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        // Check if the choices collection is empty to avoid throwing an InvalidOperationException
        if (!choices.Any())
        {
            throw new EmptyChoicesException(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NoItemsAvailableForSelection, promptText));
        }

        MessageLogger.LogInformation("Selection prompt: {PromptText}", promptText);

        var prompt = new SelectionPrompt<T>()
            .Title(promptText)
            .UseConverter(choiceFormatter)
            .AddChoices(choices)
            .PageSize(10)
            .EnableSearch();

        prompt.SearchHighlightStyle = s_searchHighlightStyle;

        var result = await MessageConsole.PromptAsync(prompt, cancellationToken);
        MessageLogger.LogInformation("Selection result: {Result}", choiceFormatter(result));
        return result;
    }

    public async Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(promptText, nameof(promptText));
        ArgumentNullException.ThrowIfNull(choices, nameof(choices));
        ArgumentNullException.ThrowIfNull(choiceFormatter, nameof(choiceFormatter));

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        var choicesList = choices.ToList();

        if (choicesList.Count == 0)
        {
            throw new EmptyChoicesException(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NoItemsAvailableForSelection, promptText));
        }

        var preSelectedSet = preSelected is not null ? new HashSet<T>(preSelected) : null;

        MessageLogger.LogInformation("Selection prompt: {PromptText}", promptText);

        var prompt = new MultiSelectionPrompt<T>()
            .Title(promptText)
            .UseConverter(choiceFormatter)
            .PageSize(10);

        prompt.Required = !optional;

        foreach (var choice in choicesList)
        {
            var item = prompt.AddChoice(choice);
            if (preSelectedSet?.Contains(choice) == true)
            {
                item.Select();
            }
        }

        var result = await MessageConsole.PromptAsync(prompt, cancellationToken);
        MessageLogger.LogInformation("Selection results: {Results}", string.Join(", ", result.Select(choiceFormatter)));
        return result;
    }

    public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion)
    {
        var cliInformationalVersion = VersionHelper.GetDefaultTemplateVersion();

        DisplayError(InteractionServiceStrings.AppHostNotCompatibleConsiderUpgrading);
        MessageConsole.WriteLine();
        MessageConsole.MarkupLine(
            $"\t[bold]{InteractionServiceStrings.AspireHostingSDKVersion}[/]: {appHostHostingVersion.EscapeMarkup()}");
        MessageConsole.MarkupLine($"\t[bold]{InteractionServiceStrings.AspireCLIVersion}[/]: {cliInformationalVersion.EscapeMarkup()}");
        MessageConsole.MarkupLine($"\t[bold]{InteractionServiceStrings.RequiredCapability}[/]: {ex.RequiredCapability.EscapeMarkup()}");
        MessageConsole.WriteLine();
        return ExitCodeConstants.AppHostIncompatible;
    }

    public void DisplayError(string errorMessage)
    {
        DisplayMessage(KnownEmojis.CrossMark, $"[red bold]{errorMessage.EscapeMarkup()}[/]", allowMarkup: true);
    }

    public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false)
    {
        if (MessageLogger.IsEnabled(LogLevel.Information))
        {
            // Only attempt to parse/remove markup when the message is expected to contain it.
            // Plain text messages may contain characters like '[' that would be rejected by the markup parser.
            var logMessage = allowMarkup ? message.RemoveMarkup() : message;
            MessageLogger.LogInformation("{Message}", ConsoleHelpers.FormatEmojiPrefix(emoji, MessageConsole, replaceEmoji: true) + logMessage);
        }

        var displayMessage = allowMarkup ? message : message.EscapeMarkup();
        MessageConsole.MarkupLine(ConsoleHelpers.FormatEmojiPrefix(emoji, MessageConsole) + displayMessage);
    }

    public void DisplayPlainText(string message)
    {
        // Write directly to avoid Spectre.Console line wrapping
        MessageConsole.Profile.Out.Writer.WriteLine(message);
    }

    public void DisplayRawText(string text, ConsoleOutput? consoleOverride = null)
    {
        var effectiveConsole = consoleOverride ?? Console;
        // Write raw text directly to avoid console wrapping.
        // When consoleOverride is null, respect the Console setting.
        var target = effectiveConsole == ConsoleOutput.Error ? _errorConsole : _outConsole;
        target.Profile.Out.Writer.WriteLine(text);
    }

    public void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null)
    {
        var effectiveConsole = consoleOverride ?? Console;
        var target = effectiveConsole == ConsoleOutput.Error ? _errorConsole : _outConsole;
        var spectreMarkup = MarkdownToSpectreConverter.ConvertToSpectre(markdown);
        target.MarkupLine(spectreMarkup);
    }

    public void DisplayMarkupLine(string markup)
    {
        MessageConsole.MarkupLine(markup);
    }

    public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false)
    {
        var style = isErrorMessage ? s_errorMessageStyle
            : type switch
            {
                ConsoleLogTypes.Waiting => s_waitingMessageStyle,
                ConsoleLogTypes.Running => s_infoMessageStyle,
                ConsoleLogTypes.ExitCode => s_exitCodeMessageStyle,
                ConsoleLogTypes.FailedToStart => s_errorMessageStyle,
                _ => s_infoMessageStyle
            };

        var prefix = lineNumber.HasValue ? $"#{lineNumber.Value}: " : "";
        MessageConsole.WriteLine($"{prefix}{message}", style);
    }

    public void DisplaySuccess(string message, bool allowMarkup = false)
    {
        DisplayMessage(KnownEmojis.CheckMark, message, allowMarkup);
    }

    public void DisplayLines(IEnumerable<(OutputLineStream Stream, string Line)> lines)
    {
        var linesArray = lines.ToArray();

        // Special case one stderr line to include error icon.
        if (linesArray.Length == 1 && linesArray[0].Stream == OutputLineStream.StdErr)
        {
            DisplayError(linesArray[0].Line);
            return;
        }

        foreach (var (stream, line) in linesArray)
        {
            if (stream == OutputLineStream.StdOut)
            {
                MessageConsole.MarkupLine(line.EscapeMarkup());
            }
            else
            {
                MessageConsole.MarkupLine($"[red]{line.EscapeMarkup()}[/]");
            }
        }
    }

    public void DisplayRenderable(IRenderable renderable)
    {
        MessageConsole.Write(renderable);
    }

    public async Task DisplayLiveAsync(IRenderable initialRenderable, Func<Action<IRenderable>, Task> callback)
    {
        await MessageConsole.Live(initialRenderable)
            .AutoClear(false)
            .StartAsync(async ctx =>
            {
                await callback(renderable => ctx.UpdateTarget(renderable));
            });
    }

    public void DisplayCancellationMessage()
    {
        MessageConsole.WriteLine();
        DisplayMessage(KnownEmojis.StopSign, $"[teal bold]{InteractionServiceStrings.StoppingAspire}[/]", allowMarkup: true);
    }

    public async Task<bool> ConfirmAsync(string promptText, bool defaultValue = true, CancellationToken cancellationToken = default)
    {
        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        MessageLogger.LogInformation("Confirm: {PromptText} (default: {DefaultValue})", promptText, defaultValue);

        // Use [Y/n] or [y/N] convention where the capitalized letter indicates the default value.
        var yesChoice = defaultValue ? 'Y' : 'y';
        var noChoice = defaultValue ? 'n' : 'N';

        var prompt = new ConfirmationPrompt(promptText)
        {
            Yes = yesChoice,
            No = noChoice,
            ShowDefaultValue = false,
            DefaultValue = defaultValue,
        };

        var result = await MessageConsole.PromptAsync(prompt, cancellationToken);
        MessageLogger.LogInformation("Confirm result: {Result}", result);
        return result;
    }

    public void DisplaySubtleMessage(string message, bool allowMarkup = false)
    {
        MessageLogger.LogInformation("{Message}", message);
        var displayMessage = allowMarkup ? message : message.EscapeMarkup();
        MessageConsole.MarkupLine($"[dim]{displayMessage}[/]");
    }

    public void DisplayEmptyLine()
    {
        MessageConsole.WriteLine();
    }

    private const string UpdateUrl = "https://aka.ms/aspire/update";

    public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null)
    {
        // Write to stderr to avoid corrupting stdout when JSON output is used
        _errorConsole.WriteLine();
        _errorConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NewCliVersionAvailable, newerVersion.EscapeMarkup()));

        if (!string.IsNullOrEmpty(updateCommand))
        {
            _errorConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ToUpdateRunCommand, updateCommand.EscapeMarkup()));
        }

        _errorConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.MoreInfoNewCliVersion, UpdateUrl));
    }
}
