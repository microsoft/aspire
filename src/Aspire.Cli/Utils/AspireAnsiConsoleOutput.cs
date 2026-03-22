// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.Extensions.Configuration;
using Spectre.Console;

namespace Aspire.Cli.Utils;

/// <summary>
/// Custom console output that handles width detection in CI environments and respects ASPIRE_CONSOLE_WIDTH.
/// </summary>
internal sealed class AspireAnsiConsoleOutput : IAnsiConsoleOutput
{
    private readonly TextWriter _writer;
    private readonly IConfiguration _configuration;
    private int? _width;

    /// <inheritdoc/>
    public TextWriter Writer => _writer;

    /// <inheritdoc/>
    public bool IsTerminal
    {
        get
        {
            if (IsStandardOut(_writer))
            {
                return !Console.IsOutputRedirected;
            }

            if (IsStandardError(_writer))
            {
                return !Console.IsErrorRedirected;
            }

            return false;
        }
    }

    /// <inheritdoc/>
    public int Width
    {
        get => _width ?? GetConfiguredWidth();
        set => _width = value;
    }

    /// <inheritdoc/>
    public int Height => GetSafeHeight();

    /// <summary>
    /// Initializes a new instance of the <see cref="AspireAnsiConsoleOutput"/> class.
    /// </summary>
    /// <param name="writer">The text writer for console output.</param>
    /// <param name="configuration">The configuration for reading environment variables.</param>
    public AspireAnsiConsoleOutput(TextWriter writer, IConfiguration configuration)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    private int GetConfiguredWidth()
    {
        // Check if explicit width override is set via ASPIRE_CONSOLE_WIDTH
        var consoleWidthOverride = _configuration["ASPIRE_CONSOLE_WIDTH"];
        if (!string.IsNullOrEmpty(consoleWidthOverride) && 
            int.TryParse(consoleWidthOverride, out var width) && 
            width > 0)
        {
            // Cap at reasonable maximum to prevent performance issues
            return Math.Min(width, 500);
        }

        // Get width from console, automatically handling CI environment defaults
        return GetSafeWidth();
    }

    /// <inheritdoc/>
    public void SetEncoding(Encoding encoding)
    {
        if (IsStandardOut(_writer) || IsStandardError(_writer))
        {
            Console.OutputEncoding = encoding;
        }
    }

    private static bool IsStandardOut(TextWriter writer)
    {
        try
        {
            return writer == Console.Out;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsStandardError(TextWriter writer)
    {
        try
        {
            return writer == Console.Error;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private int GetSafeWidth()
    {
        try
        {
            var width = Console.BufferWidth;
            if (width == 0)
            {
                // Return default width for non-terminal environments
                // In CI environments, 80 columns is too narrow, use 160 instead
                return IsTerminal ? 80 : 160;
            }

            // When BufferWidth returns 80 (historical default) but we're not in a real terminal,
            // use 160 to avoid awkward line breaks. Some CI systems report 80 even without a terminal.
            if (width == 80 && !IsTerminal)
            {
                return 160;
            }

            return width;
        }
        catch (IOException)
        {
            // When console is redirected (CI environments), use a wider default (160)
            // to avoid awkward line breaks in logs
            return 160;
        }
    }

    private static int GetSafeHeight()
    {
        try
        {
            var height = Console.WindowHeight;
            if (height == 0)
            {
                return 24;
            }

            return height;
        }
        catch (IOException)
        {
            return 24;
        }
    }
}
