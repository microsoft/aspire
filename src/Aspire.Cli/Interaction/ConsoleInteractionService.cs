// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Diagnostics;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Cli.Utils.Markdown;
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

    internal const string AllChoice = "all";
    internal const string NoneChoice = "none";

    private readonly IAnsiConsole _outConsole;
    private readonly IAnsiConsole _errorConsole;
    private readonly CliExecutionContext _executionContext;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly ILogger _stdoutLogger;
    private readonly ILogger _stderrLogger;
    private readonly ConsoleLogBufferContext _logBufferContext;
    private int _inStatus;

    /// <summary>
    /// Console used for human-readable messages; routes to stderr when <see cref="Console"/> is set to <see cref="ConsoleOutput.Error"/>.
    /// </summary>
    private IAnsiConsole MessageConsole => GetConsoleOutput(null);

    // Limit logging to prompts and messages. Don't log raw text output since it may contain sensitive information.
    private ILogger MessageLogger => GetLogger(null);

    private IAnsiConsole GetConsoleOutput(ConsoleOutput? consoleOverride) => (consoleOverride ?? Console) switch
    {
        ConsoleOutput.Error => _errorConsole,
        _ => _outConsole
    };

    private ILogger GetLogger(ConsoleOutput? consoleOverride) => (consoleOverride ?? Console) switch
    {
        ConsoleOutput.Error => _stderrLogger,
        _ => _stdoutLogger
    };

    public ConsoleOutput Console { get; set; }

    public bool SupportsLinks => MessageConsole.Profile.Capabilities.Links;

    public ConsoleInteractionService(ConsoleEnvironment consoleEnvironment, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment, ILoggerFactory loggerFactory, ConsoleLogBufferContext logBufferContext)
    {
        ArgumentNullException.ThrowIfNull(consoleEnvironment);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(logBufferContext);
        _outConsole = consoleEnvironment.Out;
        _errorConsole = consoleEnvironment.Error;
        _executionContext = executionContext;
        _hostEnvironment = hostEnvironment;
        _stdoutLogger = loggerFactory.CreateLogger($"Aspire.Cli.Console.{CliLogFormat.Categories.Stdout}");
        _stderrLogger = loggerFactory.CreateLogger($"Aspire.Cli.Console.{CliLogFormat.Categories.Stderr}");
        _logBufferContext = logBufferContext;
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
        // Also skip status display if statusText is empty (e.g., when outputting JSON).
        // IMPORTANT: CompareExchange must be evaluated last so that short-circuit evaluation
        // skips the swap when an earlier condition forces the fallback path. Otherwise the
        // swap would set _inStatus to 1 but the try/finally that resets it would never run,
        // permanently disabling interactive status for the lifetime of the service.
        if (_executionContext.DebugMode ||
            !_hostEnvironment.SupportsInteractiveOutput ||
            string.IsNullOrEmpty(statusText) ||
            Interlocked.CompareExchange(ref _inStatus, 1, 0) != 0)
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

    public async Task<T> ShowDynamicStatusAsync<T>(string initialStatusText, Func<Action<string>, Task<T>> action, KnownEmoji? emoji = null)
    {
        var emojiPrefix = emoji is { } e ? ConsoleHelpers.FormatEmojiPrefix(e, MessageConsole) : string.Empty;
        var initialDisplayText = emojiPrefix + initialStatusText.EscapeMarkup();

        // Mirrors ShowStatusAsync: prevent nested Spectre.Console Status operations, skip when debug/non-interactive,
        // and treat empty text as "no status UI". The fallback path still drives the action so progress logic runs;
        // we just hand it an updater that emits subtle messages instead of mutating a live spinner.
        // IMPORTANT: CompareExchange must be evaluated last so that short-circuit evaluation skips the swap when
        // an earlier condition forces the fallback path; otherwise _inStatus would be left set to 1.
        if (_executionContext.DebugMode ||
            !_hostEnvironment.SupportsInteractiveOutput ||
            string.IsNullOrEmpty(initialStatusText) ||
            Interlocked.CompareExchange(ref _inStatus, 1, 0) != 0)
        {
            if (!string.IsNullOrEmpty(initialStatusText))
            {
                DisplaySubtleMessage(initialDisplayText, allowMarkup: true);
            }
            else
            {
                MessageLogger.LogInformation("Status: {StatusText}", initialStatusText);
            }

            return await action(text =>
            {
                if (!string.IsNullOrEmpty(text))
                {
                    DisplaySubtleMessage(emojiPrefix + text.EscapeMarkup(), allowMarkup: true);
                }
            });
        }

        try
        {
            return await MessageConsole.Status()
                .Spinner(Spinner.Known.Dots3)
                .StartAsync(initialDisplayText, context => action(text => context.Status = emojiPrefix + text.EscapeMarkup()));
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
        // Also skip status display if statusText is empty (e.g., when outputting JSON).
        // IMPORTANT: CompareExchange must be evaluated last so that short-circuit evaluation skips the swap when
        // an earlier condition forces the fallback path; otherwise _inStatus would be left set to 1.
        if (_executionContext.DebugMode ||
            !_hostEnvironment.SupportsInteractiveOutput ||
            string.IsNullOrEmpty(statusText) ||
            Interlocked.CompareExchange(ref _inStatus, 1, 0) != 0)
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

    public async Task<string> PromptForStringAsync(string promptText, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promptText, nameof(promptText));

        var (wasProvided, value, defaultValue) = PromptBinding.Resolve(binding);
        if (wasProvided && value is not null)
        {
            ValidateResolvedStringValue(value, required, validator, binding!.SymbolDisplayName);
            return value;
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            if (binding != null)
            {
                if (binding.NonInteractiveDefaultValue != null)
                {
                    ValidateResolvedStringValue(binding.NonInteractiveDefaultValue, required, validator, binding.SymbolDisplayName);
                    return binding.NonInteractiveDefaultValue;
                }

                ThrowNonInteractiveError(binding.SymbolDisplayName);
            }

            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        // Buffer console logs while interactive prompts are active so
        // background debug output doesn't drown the prompt UI.
        using var promptScope = _logBufferContext.BeginInteractivePromptScope();

        MessageLogger.LogInformation("Prompt: {PromptText} (default: {DefaultValue}, secret: {IsSecret})", promptText, isSecret ? "****" : defaultValue ?? "(none)", isSecret);

        var displayDefaultValue = defaultValue?.EscapeMarkup();

        var prompt = new TextPrompt<string>(promptText)
        {
            IsSecret = isSecret,
            AllowEmpty = !required
        };

        if (displayDefaultValue != null)
        {
            prompt.DefaultValue(displayDefaultValue);
            prompt.ShowDefaultValue();
            prompt.DefaultValueStyle(new Style(Color.Fuchsia));
        }

        if (validator is not null)
        {
            prompt.Validate(validator);
        }

        var result = await MessageConsole.PromptAsync(prompt, cancellationToken);
        MessageLogger.LogInformation("Prompt result: {Result}", isSecret ? "****" : result);
        if (defaultValue is not null && string.Equals(result, displayDefaultValue, StringComparison.Ordinal))
        {
            return defaultValue;
        }

        return result;
    }

    public Task<string> PromptForFilePathAsync(string promptText, Func<string, ValidationResult>? validator = null, bool directory = false, bool required = false, PromptBinding<string?>? binding = null, CancellationToken cancellationToken = default)
    {
        return PromptForStringAsync(promptText, validator, isSecret: false, required, binding, cancellationToken);
    }

    public async Task<T> PromptForSelectionAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, PromptBinding<string?>? binding = null, bool echoSelected = true, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(promptText, nameof(promptText));
        ArgumentNullException.ThrowIfNull(choices, nameof(choices));
        ArgumentNullException.ThrowIfNull(choiceFormatter, nameof(choiceFormatter));

        // Materialize once to avoid re-enumerating the choices enumerable.
        var choicesList = choices as IReadOnlyList<T> ?? choices.ToList();

        var (wasProvided, value, defaultValue) = PromptBinding.Resolve(binding);
        if (wasProvided && value is not null)
        {
            return MatchChoiceOrThrow(value, binding!, choicesList, choiceFormatter);
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            if (binding != null)
            {
                if (binding.NonInteractiveDefaultValue != null)
                {
                    return MatchChoiceOrThrow(binding.NonInteractiveDefaultValue, binding, choicesList, choiceFormatter);
                }

                ThrowNonInteractiveError(binding.SymbolDisplayName);
            }

            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        // Check if the choices collection is empty to avoid throwing an InvalidOperationException
        if (choicesList.Count == 0)
        {
            throw new EmptyChoicesException(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NoItemsAvailableForSelection, promptText));
        }

        // Buffer console logs while interactive prompts are active so
        // background debug output doesn't drown the prompt UI.
        using var promptScope = _logBufferContext.BeginInteractivePromptScope();

        MessageLogger.LogInformation("Selection prompt: {PromptText}", promptText);

        var prompt = new SelectionPrompt<T>()
            .Title(promptText)
            .UseConverter(choiceFormatter)
            .AddChoices(choicesList)
            .PageSize(10)
            .EnableSearch();

        prompt.SearchHighlightStyle = s_searchHighlightStyle;

        var result = await MessageConsole.PromptAsync(prompt, cancellationToken);
        MessageLogger.LogInformation("Selection result: {Result}", choiceFormatter(result));

        // The SelectionPrompt clears its display after the user selects.
        // Echo the prompt text and selected value so the user can see what was chosen.
        if (echoSelected)
        {
            MessageConsole.MarkupLine($"{promptText} {choiceFormatter(result)}");
        }

        return result;
    }

    public async Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, IEnumerable<T>? preSelected = null, bool optional = false, PromptBinding<string?>? binding = null, bool echoSelected = true, CancellationToken cancellationToken = default) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(promptText, nameof(promptText));
        ArgumentNullException.ThrowIfNull(choices, nameof(choices));
        ArgumentNullException.ThrowIfNull(choiceFormatter, nameof(choiceFormatter));

        // Materialize once to avoid re-enumerating the choices enumerable.
        var choicesList = choices as IReadOnlyList<T> ?? choices.ToList();

        var (wasProvided, value, defaultValue) = PromptBinding.Resolve(binding);
        if (wasProvided && value is not null)
        {
            return MatchChoicesOrThrow(value, binding!, choicesList, choiceFormatter);
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            if (binding != null)
            {
                if (binding.NonInteractiveDefaultValue != null)
                {
                    return MatchChoicesOrThrow(binding.NonInteractiveDefaultValue, binding, choicesList, choiceFormatter);
                }

                ThrowNonInteractiveError(binding.SymbolDisplayName);
            }

            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        if (choicesList.Count == 0)
        {
            throw new EmptyChoicesException(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NoItemsAvailableForSelection, promptText));
        }

        // Buffer console logs while interactive prompts are active so
        // background debug output doesn't drown the prompt UI.
        using var promptScope = _logBufferContext.BeginInteractivePromptScope();

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

        // The MultiSelectionPrompt clears its display after the user selects.
        // Echo the prompt text and selected values so the user can see what was chosen.
        if (echoSelected)
        {
            if (result.Count == 0)
            {
                MessageConsole.MarkupLine($"{promptText} [dim](none)[/]");
            }
            else
            {
                MessageConsole.MarkupLine(promptText);
                foreach (var item in result)
                {
                    MessageConsole.MarkupLine($"  - {choiceFormatter(item)}");
                }
            }
        }

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
        return CliExitCodes.AppHostIncompatible;
    }

    public void DisplayError(string errorMessage, bool allowMarkup = false)
    {
        var formatted = allowMarkup ? errorMessage : errorMessage.EscapeMarkup();
        // Always write errors to stderr so callers can capture them separately from stdout.
        WriteEmojiMessage(_errorConsole, _stderrLogger, KnownEmojis.CrossMark, $"[red bold]{formatted}[/]", allowMarkup: true);
    }

    public void DisplayMessage(KnownEmoji emoji, string message, bool allowMarkup = false, ConsoleOutput? consoleOverride = null)
    {
        WriteEmojiMessage(GetConsoleOutput(consoleOverride), GetLogger(consoleOverride), emoji, message, allowMarkup);
    }

    private static void WriteEmojiMessage(IAnsiConsole target, ILogger logger, KnownEmoji emoji, string message, bool allowMarkup)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            // Only attempt to parse/remove markup when the message is expected to contain it.
            // Plain text messages may contain characters like '[' that would be rejected by the markup parser.
            var logMessage = allowMarkup ? message.RemoveMarkup() : message;
            logger.LogInformation("{Message}", ConsoleHelpers.FormatEmojiPrefix(emoji, target, replaceEmoji: true) + logMessage);
        }

        var displayMessage = allowMarkup ? message : message.EscapeMarkup();

        // Use a grid to keep the icon in a fixed first column so long text wraps
        // without pushing under the emoji prefix.
        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.Columns[0].NoWrap = true;
        grid.Columns[0].Padding = new Padding(0);
        grid.Columns[1].Padding = new Padding(0);

        grid.AddRow(
            new Markup(ConsoleHelpers.FormatEmojiPrefix(emoji, target)),
            new Markup(displayMessage));

        target.Write(grid);
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

    public void DisplayMarkdown(string markdown, ConsoleOutput? consoleOverride = null, int? maxWidth = null)
    {
        var effectiveConsole = consoleOverride ?? Console;
        if (ShouldDisplayMarkdownAsPlainText(effectiveConsole))
        {
            var plainText = MarkdownToSpectreConverter.ConvertToPlainText(markdown);
            DisplayRawText(plainText, effectiveConsole);
            return;
        }

        var target = effectiveConsole == ConsoleOutput.Error ? _errorConsole : _outConsole;
        var originalWidth = target.Profile.Width;
        if (maxWidth is not null)
        {
            target.Profile.Width = Math.Min(originalWidth, maxWidth.Value);
        }

        try
        {
            var renderable = MarkdownToSpectreConverter.ConvertToRenderable(markdown);
            target.Write(renderable);

            // A row automatically includes a newline, so we don't need to call WriteLine after writing the renderable.
            if (renderable is not Rows)
            {
                target.WriteLine();
            }
        }
        finally
        {
            if (maxWidth is not null)
            {
                target.Profile.Width = originalWidth;
            }
        }
    }

    private bool ShouldDisplayMarkdownAsPlainText(ConsoleOutput effectiveConsole)
    {
        if (!_hostEnvironment.SupportsInteractiveOutput)
        {
            return true;
        }

        return effectiveConsole == ConsoleOutput.Error
            ? System.Console.IsErrorRedirected
            : System.Console.IsOutputRedirected;
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
        DisplayMessage(KnownEmojis.CheckMarkButton, message, allowMarkup);
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

    public void DisplayCancellationMessage(ConsoleOutput? consoleOverride = null)
    {
        GetConsoleOutput(consoleOverride).WriteLine();
        DisplayMessage(KnownEmojis.StopSign, $"[teal bold]{InteractionServiceStrings.StoppingAspire}[/]", allowMarkup: true, consoleOverride: consoleOverride);
    }

    public async Task<bool> PromptConfirmAsync(string promptText, PromptBinding<bool>? binding = null, CancellationToken cancellationToken = default)
    {
        var (wasProvided, value, defaultValue) = PromptBinding.Resolve(binding);
        if (wasProvided)
        {
            return value;
        }

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            if (binding is not null)
            {
                if (binding.HasExplicitDefault)
                {
                    return binding.NonInteractiveDefaultValue;
                }

                ThrowNonInteractiveError(binding.SymbolDisplayName);
            }

            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        // Buffer console logs while interactive prompts are active so
        // background debug output doesn't drown the prompt UI.
        using var promptScope = _logBufferContext.BeginInteractivePromptScope();

        // When no binding is provided, default to true (matching the historical behavior
        // where the old ConfirmAsync signature had defaultValue = true).
        if (binding is null)
        {
            defaultValue = true;
        }

        MessageLogger.LogInformation("Confirm: {PromptText} (default: {DefaultValue})", promptText, defaultValue);

        // Use [Y/n] or [y/N] convention where the capitalized letter indicates the default value.
        var yesChoice = defaultValue ? 'Y' : 'y';
        var noChoice = defaultValue ? 'n' : 'N';

        var result = await PromptConfirmWithSingleKeyAsync(promptText, yesChoice, noChoice, defaultValue, cancellationToken);
        MessageLogger.LogInformation("Confirm result: {Result}", result);
        return result;
    }

    private async Task<bool> PromptConfirmWithSingleKeyAsync(string promptText, char yesChoice, char noChoice, bool defaultValue, CancellationToken cancellationToken)
    {
        MessageConsole.Markup(promptText);
        MessageConsole.Markup($" [blue][[{yesChoice}/{noChoice}]][/]: ");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await MessageConsole.Input.ReadKeyAsync(intercept: true, cancellationToken) is not { } key)
            {
                continue;
            }

            if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
            {
                MessageConsole.WriteLine((defaultValue ? yesChoice : noChoice).ToString()); // Echo the default choice
                return defaultValue;
            }

            if (char.ToLowerInvariant(key.KeyChar) == char.ToLowerInvariant(yesChoice))
            {
                MessageConsole.WriteLine(key.KeyChar.ToString());
                return true;
            }

            if (char.ToLowerInvariant(key.KeyChar) == char.ToLowerInvariant(noChoice))
            {
                MessageConsole.WriteLine(key.KeyChar.ToString());
                return false;
            }
        }
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

        _errorConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.MoreInfoNewCliVersion, MarkupHelpers.SafeLink(this, UpdateUrl)));
    }

    internal static T? MatchChoice<T>(string value, IEnumerable<T> choices, Func<T, string> choiceFormatter) where T : notnull
    {
        return choices.FirstOrDefault(c => string.Equals(choiceFormatter(c), value, StringComparison.OrdinalIgnoreCase))
            ?? choices.FirstOrDefault(c => string.Equals(c.ToString(), value, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<T>? MatchChoices<T>(string commaSeparatedValues, IReadOnlyList<T> choices, Func<T, string> choiceFormatter) where T : notnull
    {
        if (string.Equals(commaSeparatedValues, AllChoice, StringComparison.OrdinalIgnoreCase))
        {
            return choices;
        }

        if (string.Equals(commaSeparatedValues, NoneChoice, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var values = commaSeparatedValues.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matched = new List<T>();
        foreach (var val in values)
        {
            var match = MatchChoice(val, choices, choiceFormatter);
            if (match is null)
            {
                return null; // Signal that matching failed
            }
            // MatchChoice returns the instance from the choices list via FirstOrDefault,
            // so reference equality is correct here for deduplication.
            if (!matched.Contains(match))
            {
                matched.Add(match);
            }
        }
        return matched;
    }

    [DoesNotReturn]
    private void ThrowNonInteractiveError(string symbolDisplayName)
    {
        DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveOptionRequired, symbolDisplayName));
        throw new NonInteractiveException(symbolDisplayName);
    }

    internal void ValidateResolvedStringValue(string value, bool required, Func<string, ValidationResult>? validator, string symbolDisplayName)
    {
        if (required && string.IsNullOrEmpty(value))
        {
            ThrowNonInteractiveError(symbolDisplayName);
        }

        if (validator is not null)
        {
            var result = validator(value);
            if (!result.Successful)
            {
                DisplayError(result.Message ?? string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveInvalidValue, value, symbolDisplayName));
                throw new NonInteractiveException(symbolDisplayName);
            }
        }
    }

    [DoesNotReturn]
    internal void ThrowNonInteractiveInvalidValue<T>(string value, string symbolDisplayName, IEnumerable<T> choices, Func<T, string> choiceFormatter) where T : notnull
    {
        DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveInvalidValue, value, symbolDisplayName));
        var availableChoices = string.Join(", ", choices.Select(c => choiceFormatter(c)));
        DisplaySubtleMessage(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NonInteractiveAvailableValues, availableChoices));
        throw new NonInteractiveException(symbolDisplayName);
    }

    internal T MatchChoiceOrThrow<T>(string value, PromptBinding<string?> binding, IEnumerable<T> choices, Func<T, string> choiceFormatter) where T : notnull
    {
        var match = MatchChoice(value, choices, choiceFormatter);
        if (match is not null)
        {
            return match;
        }

        ThrowNonInteractiveInvalidValue(value, binding.SymbolDisplayName, choices, choiceFormatter);
        return default; // unreachable
    }

    internal IReadOnlyList<T> MatchChoicesOrThrow<T>(string value, PromptBinding<string?> binding, IEnumerable<T> choices, Func<T, string> choiceFormatter) where T : notnull
    {
        var choicesList = choices.ToList();
        var matched = MatchChoices(value, choicesList, choiceFormatter);
        if (matched is not null)
        {
            return matched;
        }

        ThrowNonInteractiveInvalidValue(value, binding.SymbolDisplayName, choicesList, choiceFormatter);
        return default; // unreachable
    }
}
