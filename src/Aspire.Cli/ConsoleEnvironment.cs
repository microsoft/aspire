// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Spectre.Console;

namespace Aspire.Cli;

/// <summary>
/// Provides access to console output streams for the CLI.
/// </summary>
internal sealed class ConsoleEnvironment
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConsoleEnvironment"/> class.
    /// </summary>
    /// <param name="output">The console for standard output.</param>
    /// <param name="error">The console for standard error.</param>
    public ConsoleEnvironment(IAnsiConsole output, IAnsiConsole error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        Out = output;
        Error = error;
    }

    /// <summary>
    /// Gets the console for standard output.
    /// </summary>
    public IAnsiConsole Out { get; }

    /// <summary>
    /// Gets the console for standard error.
    /// </summary>
    public IAnsiConsole Error { get; }
}
