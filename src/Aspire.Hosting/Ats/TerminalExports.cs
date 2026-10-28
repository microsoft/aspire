// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable ASPIRETERMINAL001 // ATS adapters for the experimental terminal API.

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for AppHost terminal creation, lifetime, and automation.
/// </summary>
internal static class TerminalExports
{
    /// <summary>
    /// Gets the terminal service from the AppHost service provider.
    /// </summary>
    [AspireExport]
    public static TerminalService GetTerminalService(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<TerminalService>();
    }

    /// <summary>
    /// Creates a terminal without starting it. The caller owns its lifetime; dock terminals can be left
    /// running until AppHost shutdown, and showing a terminal in a dialog does not transfer ownership.
    /// </summary>
    [AspireExport]
    public static AspireTerminal CreateTerminal(this TerminalService terminalService, CreateTerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(terminalService);
        ArgumentNullException.ThrowIfNull(options);

        return terminalService.CreateTerminal(options.ToOptions());
    }

    /// <summary>
    /// Gets an existing terminal by ID, or null if it is not found.
    /// </summary>
    [AspireExport]
    public static AspireTerminal? GetTerminal(this TerminalService terminalService, string terminalId)
    {
        ArgumentNullException.ThrowIfNull(terminalService);

        return terminalService.TryGetTerminal(terminalId, out var terminal) ? terminal : null;
    }

    /// <summary>
    /// Starts the terminal workload. Resource-owned workloads are started by their resource.
    /// </summary>
    [AspireExport("AspireTerminal.start", MethodName = "start")]
    public static void Start(this AspireTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        terminal.Start();
    }

    /// <summary>
    /// Reveals a dock terminal and selects its tab. Other placements are unaffected.
    /// </summary>
    [AspireExport("AspireTerminal.show", MethodName = "show")]
    public static void Show(this AspireTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        terminal.Show();
    }

    /// <summary>
    /// Sends text as though it had been typed into the terminal.
    /// </summary>
    [AspireExport("AspireTerminal.sendText", MethodName = "sendText")]
    public static Task SendText(this AspireTerminal terminal, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        return terminal.SendTextAsync(text, cancellationToken);
    }

    /// <summary>
    /// Sends a named key with optional Control, Alt, and Shift modifiers.
    /// </summary>
    [AspireExport("AspireTerminal.sendKey", MethodName = "sendKey")]
    public static Task SendKey(this AspireTerminal terminal, TerminalKeyName key, TerminalKeyModifiers? modifiers = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        var terminalKey = ToKey(key);
        if (modifiers?.Ctrl == true)
        {
            terminalKey = AspireTerminalKey.Ctrl(terminalKey);
        }
        if (modifiers?.Alt == true)
        {
            terminalKey = AspireTerminalKey.Alt(terminalKey);
        }
        if (modifiers?.Shift == true)
        {
            terminalKey = AspireTerminalKey.Shift(terminalKey);
        }

        return terminal.SendKeyAsync(terminalKey, cancellationToken);
    }

    /// <summary>
    /// Waits for text on the terminal screen. Timeout is in milliseconds and defaults to 30 seconds.
    /// </summary>
    [AspireExport("AspireTerminal.waitForText", MethodName = "waitForText")]
    public static Task WaitForText(this AspireTerminal terminal, string text, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        return terminal.WaitForTextAsync(text, timeout, cancellationToken);
    }

    /// <summary>
    /// Gets the current terminal screen text, with lines separated by newlines.
    /// </summary>
    [AspireExport("AspireTerminal.getScreenText", MethodName = "getScreenText")]
    public static string GetScreenText(this AspireTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        return terminal.GetScreenText();
    }

    /// <summary>
    /// Releases the terminal handle, stopping the workload only when the AppHost owns it.
    /// </summary>
    [AspireExport("AspireTerminal.dispose", MethodName = "dispose")]
    public static async Task Dispose(this AspireTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        await terminal.DisposeAsync().ConfigureAwait(false);
    }

    // Keep the mapping in Aspire terms: Hex1b key identities and escape sequences are implementation details.
    internal static AspireTerminalKey ToKey(TerminalKeyName key) => key switch
    {
        TerminalKeyName.A => AspireTerminalKey.A,
        TerminalKeyName.B => AspireTerminalKey.B,
        TerminalKeyName.C => AspireTerminalKey.C,
        TerminalKeyName.D => AspireTerminalKey.D,
        TerminalKeyName.E => AspireTerminalKey.E,
        TerminalKeyName.F => AspireTerminalKey.F,
        TerminalKeyName.G => AspireTerminalKey.G,
        TerminalKeyName.H => AspireTerminalKey.H,
        TerminalKeyName.I => AspireTerminalKey.I,
        TerminalKeyName.J => AspireTerminalKey.J,
        TerminalKeyName.K => AspireTerminalKey.K,
        TerminalKeyName.L => AspireTerminalKey.L,
        TerminalKeyName.M => AspireTerminalKey.M,
        TerminalKeyName.N => AspireTerminalKey.N,
        TerminalKeyName.O => AspireTerminalKey.O,
        TerminalKeyName.P => AspireTerminalKey.P,
        TerminalKeyName.Q => AspireTerminalKey.Q,
        TerminalKeyName.R => AspireTerminalKey.R,
        TerminalKeyName.S => AspireTerminalKey.S,
        TerminalKeyName.T => AspireTerminalKey.T,
        TerminalKeyName.U => AspireTerminalKey.U,
        TerminalKeyName.V => AspireTerminalKey.V,
        TerminalKeyName.W => AspireTerminalKey.W,
        TerminalKeyName.X => AspireTerminalKey.X,
        TerminalKeyName.Y => AspireTerminalKey.Y,
        TerminalKeyName.Z => AspireTerminalKey.Z,
        TerminalKeyName.D0 => AspireTerminalKey.D0,
        TerminalKeyName.D1 => AspireTerminalKey.D1,
        TerminalKeyName.D2 => AspireTerminalKey.D2,
        TerminalKeyName.D3 => AspireTerminalKey.D3,
        TerminalKeyName.D4 => AspireTerminalKey.D4,
        TerminalKeyName.D5 => AspireTerminalKey.D5,
        TerminalKeyName.D6 => AspireTerminalKey.D6,
        TerminalKeyName.D7 => AspireTerminalKey.D7,
        TerminalKeyName.D8 => AspireTerminalKey.D8,
        TerminalKeyName.D9 => AspireTerminalKey.D9,
        TerminalKeyName.F1 => AspireTerminalKey.F1,
        TerminalKeyName.F2 => AspireTerminalKey.F2,
        TerminalKeyName.F3 => AspireTerminalKey.F3,
        TerminalKeyName.F4 => AspireTerminalKey.F4,
        TerminalKeyName.F5 => AspireTerminalKey.F5,
        TerminalKeyName.F6 => AspireTerminalKey.F6,
        TerminalKeyName.F7 => AspireTerminalKey.F7,
        TerminalKeyName.F8 => AspireTerminalKey.F8,
        TerminalKeyName.F9 => AspireTerminalKey.F9,
        TerminalKeyName.F10 => AspireTerminalKey.F10,
        TerminalKeyName.F11 => AspireTerminalKey.F11,
        TerminalKeyName.F12 => AspireTerminalKey.F12,
        TerminalKeyName.Enter => AspireTerminalKey.Enter,
        TerminalKeyName.Tab => AspireTerminalKey.Tab,
        TerminalKeyName.Escape => AspireTerminalKey.Escape,
        TerminalKeyName.Backspace => AspireTerminalKey.Backspace,
        TerminalKeyName.Delete => AspireTerminalKey.Delete,
        TerminalKeyName.Insert => AspireTerminalKey.Insert,
        TerminalKeyName.Space => AspireTerminalKey.Space,
        TerminalKeyName.Up => AspireTerminalKey.Up,
        TerminalKeyName.Down => AspireTerminalKey.Down,
        TerminalKeyName.Left => AspireTerminalKey.Left,
        TerminalKeyName.Right => AspireTerminalKey.Right,
        TerminalKeyName.Home => AspireTerminalKey.Home,
        TerminalKeyName.End => AspireTerminalKey.End,
        TerminalKeyName.PageUp => AspireTerminalKey.PageUp,
        TerminalKeyName.PageDown => AspireTerminalKey.PageDown,
        TerminalKeyName.Comma => AspireTerminalKey.Comma,
        TerminalKeyName.Period => AspireTerminalKey.Period,
        TerminalKeyName.Minus => AspireTerminalKey.Minus,
        TerminalKeyName.EqualsSign => AspireTerminalKey.EqualsSign,
        TerminalKeyName.Slash => AspireTerminalKey.Slash,
        TerminalKeyName.Semicolon => AspireTerminalKey.Semicolon,
        TerminalKeyName.LeftBracket => AspireTerminalKey.LeftBracket,
        TerminalKeyName.Backslash => AspireTerminalKey.Backslash,
        TerminalKeyName.RightBracket => AspireTerminalKey.RightBracket,
        TerminalKeyName.Apostrophe => AspireTerminalKey.Apostrophe,
        TerminalKeyName.Backtick => AspireTerminalKey.Backtick,
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown terminal key.")
    };
}

/// <summary>
/// Process settings for an AppHost-owned terminal. Placement defaults to Dock, and the initial grid to 80 by 24.
/// </summary>
[AspireDto]
internal sealed class CreateTerminalOptions
{
    /// <summary>Gets the terminal title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the executable, resolved against the AppHost's PATH when not fully qualified.</summary>
    public required string Executable { get; init; }

    /// <summary>Gets the process arguments.</summary>
    public string[]? Arguments { get; init; }

    /// <summary>Gets the working directory. Defaults to the AppHost's working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets environment overrides layered over the AppHost's inherited environment.</summary>
    public Dictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>Gets the initial number of columns. Defaults to 80.</summary>
    public int? Columns { get; init; }

    /// <summary>Gets the initial number of rows. Defaults to 24.</summary>
    public int? Rows { get; init; }

    /// <summary>Gets the placement: Dock (default), Dialog, or None. ResourceView is not valid for creation.</summary>
    public TerminalPlacement? Placement { get; init; }

    internal TerminalLaunchOptions ToOptions()
    {
        var options = new TerminalLaunchOptions
        {
            Title = Title,
            Executable = Executable,
            WorkingDirectory = WorkingDirectory
        };
        if (Arguments is not null)
        {
            options.Arguments = Arguments;
        }
        if (EnvironmentVariables is not null)
        {
            foreach (var (name, value) in EnvironmentVariables)
            {
                options.EnvironmentVariables.Add(name, value);
            }
        }
        if (Columns is { } columns)
        {
            options.Columns = columns;
        }
        if (Rows is { } rows)
        {
            options.Rows = rows;
        }
        if (Placement is { } placement)
        {
            options.Placement = placement;
        }

        return options;
    }
}

/// <summary>
/// Modifiers applied to a named terminal keypress.
/// </summary>
[AspireDto]
internal sealed class TerminalKeyModifiers
{
    /// <summary>Gets whether Control is applied.</summary>
    public bool Ctrl { get; init; }

    /// <summary>Gets whether Alt is applied.</summary>
    public bool Alt { get; init; }

    /// <summary>Gets whether Shift is applied.</summary>
    public bool Shift { get; init; }
}

/// <summary>
/// Named terminal keys using the US keyboard layout. Use text input for arbitrary Unicode.
/// </summary>
internal enum TerminalKeyName
{
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Enter, Tab, Escape, Backspace, Delete, Insert, Space,
    Up, Down, Left, Right, Home, End, PageUp, PageDown,
    Comma, Period, Minus, EqualsSign, Slash, Semicolon, LeftBracket, Backslash, RightBracket, Apostrophe, Backtick
}
