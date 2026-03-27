// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Spectre.Console;

namespace Aspire.Cli.Diagnostics;

/// <summary>
/// Writes startup error messages to the console before DI services are available.
/// On disposal, displays the log file path so the user can investigate.
/// </summary>
internal interface IStartupErrorWriter : IDisposable
{
    /// <summary>
    /// Writes a plain text line to stderr, optionally prefixed with an emoji.
    /// </summary>
    void WriteLine(string message, KnownEmoji? emoji = null);

    /// <summary>
    /// Writes a Spectre Console markup string to stderr, optionally prefixed with an emoji.
    /// </summary>
    void WriteMarkup(string markup, KnownEmoji? emoji = null);
}

/// <summary>
/// Default implementation that writes to an <see cref="IAnsiConsole"/> targeting stderr.
/// </summary>
internal sealed class StartupErrorWriter : IStartupErrorWriter
{
    private readonly IAnsiConsole _errorConsole;
    private readonly string _logFilePath;
    private bool _hasOutput;

    public StartupErrorWriter(string logFilePath)
    {
        _logFilePath = logFilePath;
        _errorConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Error)
        });
    }

    public void WriteLine(string message, KnownEmoji? emoji = null)
    {
        WriteMarkup($"[red bold]{message.EscapeMarkup()}[/]", emoji ?? KnownEmojis.CrossMark);
    }

    public void WriteMarkup(string markup, KnownEmoji? emoji = null)
    {
        _hasOutput = true;
        var prefix = emoji is not null ? ConsoleHelpers.FormatEmojiPrefix(emoji.Value, _errorConsole) : string.Empty;
        _errorConsole.MarkupLine(prefix + markup);
    }

    public void Dispose()
    {
        if (!_hasOutput || !File.Exists(_logFilePath))
        {
            return;
        }

        var prefix = ConsoleHelpers.FormatEmojiPrefix(KnownEmojis.PageFacingUp, _errorConsole);
        _errorConsole.MarkupLine(prefix + string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.SeeLogsAt, _logFilePath.EscapeMarkup()));
    }
}
