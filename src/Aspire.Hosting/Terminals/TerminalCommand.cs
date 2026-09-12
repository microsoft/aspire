// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Describes the process a terminal runs, and the grid it starts on.
/// </summary>
/// <remarks>
/// <para>
/// This is Aspire's own description of a terminal workload. It exists so terminals can be created from
/// AppHost code without the underlying terminal library (currently Hex1b) appearing in Aspire's public API.
/// Aspire translates it into whatever the implementation needs and attaches the transport itself.
/// </para>
/// <para>
/// The surface is deliberately narrow — a process, its arguments, and the environment it runs in. Hex1b can
/// also host an in-process TUI app rather than a child process, but that is not projected here because it
/// would put Hex1b's widget model into Aspire's public API, which is exactly what this type exists to avoid.
/// </para>
/// </remarks>
/// <example>
/// Shell into a running container:
/// <code>
/// var command = new TerminalCommand("docker")
/// {
///     Arguments = ["exec", "-it", containerName, "/bin/sh"]
/// };
/// </code>
/// </example>
[Experimental(TerminalDiagnostics.AppHostTerminals, UrlFormat = TerminalDiagnostics.UrlFormat)]
public sealed class TerminalCommand
{
    /// <summary>
    /// The grid a terminal starts on before any viewer attaches.
    /// </summary>
    /// <remarks>
    /// Chosen to be comfortably wider than the 80x24 default so output that assumes a modern terminal is not
    /// wrapped in the moments before a viewer attaches and negotiates the real size.
    /// </remarks>
    private const int DefaultColumns = 120;
    private const int DefaultRows = 32;

    /// <summary>
    /// Initializes a new instance of the <see cref="TerminalCommand"/> class.
    /// </summary>
    /// <param name="executable">The executable to run. Resolved against <c>PATH</c> when not fully qualified.</param>
    public TerminalCommand(string executable)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);

        Executable = executable;
    }

    /// <summary>
    /// Gets the executable to run.
    /// </summary>
    public string Executable { get; }

    /// <summary>
    /// Gets or sets the arguments passed to <see cref="Executable"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public IList<string> Arguments
    {
        get;
        set
        {
            // Validate on assignment rather than when the terminal is created. The arguments are not read until
            // TerminalService translates this command into a process, which is far enough away that a null here
            // would otherwise surface as an unattributed NullReferenceException inside terminal creation.
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = [];

    /// <summary>
    /// Gets or sets the working directory the process starts in. Defaults to the AppHost's working directory.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets environment variables applied to the process on top of the AppHost's own environment.
    /// </summary>
    /// <remarks>
    /// The process inherits the AppHost's environment and these are layered over it. That matters for
    /// interactive workloads, which generally need an inherited <c>PATH</c>, <c>HOME</c> and <c>TERM</c>
    /// to behave like a normal shell.
    /// </remarks>
    public IDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the number of columns the terminal starts with.
    /// </summary>
    /// <remarks>
    /// This is only the initial grid. A viewer that attaches renegotiates the size to fit the space it has,
    /// so this matters mainly for terminals driven by automation before anyone attaches.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is less than one.</exception>
    public int Columns
    {
        get;
        set
        {
            // A zero or negative grid is not a terminal the emulator can render into, and the failure would
            // otherwise appear deep inside the terminal library rather than at the assignment that caused it.
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = DefaultColumns;

    /// <summary>
    /// Gets or sets the number of rows the terminal starts with.
    /// </summary>
    /// <inheritdoc cref="Columns" path="/remarks"/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is less than one.</exception>
    public int Rows
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = DefaultRows;
}
