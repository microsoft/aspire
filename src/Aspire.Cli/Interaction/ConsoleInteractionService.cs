// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Interaction;

internal class ConsoleInteractionService : IInteractionService
{
    private static readonly Style s_exitCodeMessageStyle = new Style(foreground: Color.RoyalBlue1, background: null, decoration: Decoration.None);
    private static readonly Style s_infoMessageStyle = new Style(foreground: Color.Green, background: null, decoration: Decoration.None);
    private static readonly Style s_waitingMessageStyle = new Style(foreground: Color.Yellow, background: null, decoration: Decoration.None);
    private static readonly Style s_errorMessageStyle = new Style(foreground: Color.Red, background: null, decoration: Decoration.Bold);
    private static readonly Style s_searchHighlightStyle = new Style(foreground: Color.Black, background: Color.Cyan1, decoration: Decoration.None);

    private readonly IAnsiConsole _ansiConsole;
    private readonly CliExecutionContext _executionContext;
    private readonly ICliHostEnvironment _hostEnvironment;

    public ConsoleInteractionService(IAnsiConsole ansiConsole, CliExecutionContext executionContext, ICliHostEnvironment hostEnvironment)
    {
        ArgumentNullException.ThrowIfNull(ansiConsole);
        ArgumentNullException.ThrowIfNull(executionContext);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        _ansiConsole = ansiConsole;
        _executionContext = executionContext;
        _hostEnvironment = hostEnvironment;
    }

    public async Task<T> ShowStatusAsync<T>(string statusText, Func<Task<T>> action)
    {
        // In debug mode or non-interactive environments, avoid interactive progress as it conflicts with debug logging
        if (_executionContext.DebugMode || !_hostEnvironment.SupportsInteractiveOutput)
        {
            DisplaySubtleMessage(statusText);
            return await action();
        }
        
        return await _ansiConsole.Status()
            .Spinner(Spinner.Known.Dots3)
            .StartAsync(statusText, (context) => action());
    }

    public void ShowStatus(string statusText, Action action)
    {
        // In debug mode or non-interactive environments, avoid interactive progress as it conflicts with debug logging
        if (_executionContext.DebugMode || !_hostEnvironment.SupportsInteractiveOutput)
        {
            DisplaySubtleMessage(statusText);
            action();
            return;
        }
        
        _ansiConsole.Status()
            .Spinner(Spinner.Known.Dots3)
            .Start(statusText, (context) => action());
    }

    public async Task<string> PromptForStringAsync(string promptText, string? defaultValue = null, Func<string, ValidationResult>? validator = null, bool isSecret = false, bool required = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(promptText, nameof(promptText));

        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

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

        return await _ansiConsole.PromptAsync(prompt, cancellationToken);
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

        var prompt = new SelectionPrompt<T>()
            .Title(promptText)
            .UseConverter(choiceFormatter)
            .AddChoices(choices)
            .PageSize(10)
            .EnableSearch();
        
        prompt.SearchHighlightStyle = s_searchHighlightStyle;

        return await _ansiConsole.PromptAsync(prompt, cancellationToken);
    }

    public async Task<IReadOnlyList<T>> PromptForSelectionsAsync<T>(string promptText, IEnumerable<T> choices, Func<T, string> choiceFormatter, CancellationToken cancellationToken = default) where T : notnull
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

        var prompt = new MultiSelectionPrompt<T>()
            .Title(promptText)
            .UseConverter(choiceFormatter)
            .AddChoices(choices)
            .PageSize(10);

        var result = await _ansiConsole.PromptAsync(prompt, cancellationToken);
        return result;
    }

    public int DisplayIncompatibleVersionError(AppHostIncompatibleException ex, string appHostHostingVersion)
    {
        var cliInformationalVersion = VersionHelper.GetDefaultTemplateVersion();

        DisplayError(InteractionServiceStrings.AppHostNotCompatibleConsiderUpgrading);
        Console.WriteLine();
        _ansiConsole.MarkupLine(
            $"\t[bold]{InteractionServiceStrings.AspireHostingSDKVersion}[/]: {appHostHostingVersion}");
        _ansiConsole.MarkupLine($"\t[bold]{InteractionServiceStrings.AspireCLIVersion}[/]: {cliInformationalVersion}");
        _ansiConsole.MarkupLine($"\t[bold]{InteractionServiceStrings.RequiredCapability}[/]: {ex.RequiredCapability}");
        Console.WriteLine();
        return ExitCodeConstants.AppHostIncompatible;
    }

    public void DisplayError(string errorMessage)
    {
        DisplayMessage("cross_mark", $"[red bold]{errorMessage.EscapeMarkup()}[/]");
    }

    public void DisplayMessage(string emoji, string message)
    {
        // This is a hack to deal with emoji of different size. We write the emoji then move the cursor to aboslute column 4
        // on the same line before writing the message. This ensures that the message starts at the same position regardless
        // of the emoji used. I'm not OCD .. you are!
        _ansiConsole.Markup($":{emoji}:");
        _ansiConsole.Write("\u001b[4G");
        _ansiConsole.MarkupLine(message);
    }

    public void DisplayPlainText(string message)
    {
        _ansiConsole.WriteLine(message);
    }

    public void DisplayMarkdown(string markdown)
    {
        var spectreMarkup = MarkdownToSpectreConverter.ConvertToSpectre(markdown);
        _ansiConsole.MarkupLine(spectreMarkup);
    }

    public void WriteConsoleLog(string message, int? lineNumber = null, string? type = null, bool isErrorMessage = false)
    {
        var style = isErrorMessage ? s_errorMessageStyle
            : type switch
            {
                "waiting" => s_waitingMessageStyle,
                "running" => s_infoMessageStyle,
                "exitCode" => s_exitCodeMessageStyle,
                "failedToStart" => s_errorMessageStyle,
                _ => s_infoMessageStyle
            };

        var prefix = lineNumber.HasValue ? $"#{lineNumber.Value}: " : "";
        _ansiConsole.WriteLine($"{prefix}{message}", style);
    }

    public void DisplaySuccess(string message)
    {
        DisplayMessage("check_mark", message);
    }

    public void DisplayLines(IEnumerable<(string Stream, string Line)> lines)
    {
        foreach (var (stream, line) in lines)
        {
            if (stream == "stdout")
            {
                _ansiConsole.MarkupLineInterpolated($"{line.EscapeMarkup()}");
            }
            else
            {
                _ansiConsole.MarkupLineInterpolated($"[red]{line.EscapeMarkup()}[/]");
            }
        }
    }

    public void DisplayCancellationMessage()
    {
        _ansiConsole.WriteLine();
        DisplayMessage("stop_sign", $"[teal bold]{InteractionServiceStrings.StoppingAspire}[/]");
    }

    public Task<bool> ConfirmAsync(string promptText, bool defaultValue = true, CancellationToken cancellationToken = default)
    {
        if (!_hostEnvironment.SupportsInteractiveInput)
        {
            throw new InvalidOperationException(InteractionServiceStrings.InteractiveInputNotSupported);
        }

        return _ansiConsole.ConfirmAsync(promptText, defaultValue, cancellationToken);
    }

    public void DisplaySubtleMessage(string message, bool escapeMarkup = true)
    {
        var displayMessage = escapeMarkup ? message.EscapeMarkup() : message;
        _ansiConsole.MarkupLine($"[dim]{displayMessage}[/]");
    }

    public void DisplayEmptyLine()
    {
        _ansiConsole.WriteLine();
    }

    private const string UpdateUrl = "https://aka.ms/aspire/update";

    public void DisplayVersionUpdateNotification(string newerVersion, string? updateCommand = null)
    {
        _ansiConsole.WriteLine();
        _ansiConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.NewCliVersionAvailable, newerVersion));
        
        if (!string.IsNullOrEmpty(updateCommand))
        {
            _ansiConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ToUpdateRunCommand, updateCommand));
        }
        
        _ansiConsole.MarkupLine(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.MoreInfoNewCliVersion, UpdateUrl));
    }
}
