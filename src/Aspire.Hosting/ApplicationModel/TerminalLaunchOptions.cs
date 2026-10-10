// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Describes the process, initial grid, and placement of a terminal created by <see cref="TerminalService"/>.
/// </summary>
/// <example>
/// <code language="csharp">
/// var options = new TerminalLaunchOptions
/// {
///     Title = "Container shell",
///     Executable = "docker",
///     Arguments = ["exec", "-it", containerName, "/bin/sh"]
/// };
/// </code>
/// </example>
[Experimental(TerminalDiagnostics.DiagnosticId, UrlFormat = TerminalDiagnostics.UrlFormat)]
public sealed class TerminalLaunchOptions
{
    private const int DefaultColumns = 80;
    private const int DefaultRows = 24;

    /// <summary>
    /// Gets or sets the title shown on the terminal's dock tab, and in the title bar when the terminal is
    /// detached into its own window.
    /// </summary>
    /// <remarks>
    /// The title must not be empty or consist only of white-space characters.
    /// </remarks>
    public required string Title { get; set; }

    /// <summary>
    /// Gets or sets the executable to run. Resolved against <c>PATH</c> when not fully qualified.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty.</exception>
    public required string Executable
    {
        get;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the arguments passed to <see cref="Executable"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public IList<string> Arguments
    {
        get;
        set
        {
            // Validate on assignment so invalid input fails here rather than later inside the terminal library.
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
    /// The process inherits the AppHost's environment. Entries add or override variables without replacing the
    /// rest of that environment, so interactive workloads retain inherited <c>PATH</c>, <c>HOME</c>, and
    /// <c>TERM</c> values unless explicitly overridden.
    /// </remarks>
    public IDictionary<string, string> EnvironmentVariables { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets the initial number of columns. Defaults to 80.
    /// </summary>
    /// <remarks>
    /// The process starts with this grid and retains it while no viewer requests a resize.
    /// Dock and interaction dialog viewers resize the grid to fit their available space when shown.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is less than one.</exception>
    public int Columns
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            field = value;
        }
    } = DefaultColumns;

    /// <summary>
    /// Gets or sets the initial number of rows. Defaults to 24.
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

    /// <summary>
    /// Gets or sets where the terminal is displayed. Defaults to <see cref="TerminalPlacement.Dock"/>.
    /// </summary>
    /// <remarks>
    /// AppHost-owned terminals support <see cref="TerminalPlacement.Dock"/>, <see cref="TerminalPlacement.Dialog"/>,
    /// and <see cref="TerminalPlacement.None"/>. <see cref="TerminalPlacement.ResourceView"/> is reserved for
    /// terminals owned by resources and cannot be used when creating an AppHost-owned terminal.
    /// </remarks>
    public TerminalPlacement Placement { get; set; } = TerminalPlacement.Dock;
}
