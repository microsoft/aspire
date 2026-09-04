// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

#pragma warning disable ASPIRETERMINAL002 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// The non-printable keys that can be sent to a terminal through <see cref="IAspireTerminal.SendKeyAsync"/>.
/// </summary>
/// <remarks>
/// This is deliberately a small, Aspire-owned enum rather than a projection of the underlying terminal
/// library's key enum. Each value maps to a raw byte sequence in <see cref="AspireTerminalKeySequences"/>,
/// which keeps the mapping under Aspire's control and avoids leaking a third-party enum through
/// <see cref="IAspireTerminal"/>.
/// </remarks>
[Experimental(TerminalDiagnostics.AppHostTerminals, UrlFormat = TerminalDiagnostics.UrlFormat)]
public enum AspireTerminalKey
{
    /// <summary>The Enter key — sends a carriage return.</summary>
    Enter,

    /// <summary>The Tab key.</summary>
    Tab,

    /// <summary>The Escape key.</summary>
    Escape,

    /// <summary>The Backspace key.</summary>
    Backspace,

    /// <summary>The Delete key.</summary>
    Delete,

    /// <summary>The Up arrow key.</summary>
    Up,

    /// <summary>The Down arrow key.</summary>
    Down,

    /// <summary>The Left arrow key.</summary>
    Left,

    /// <summary>The Right arrow key.</summary>
    Right,

    /// <summary>The Home key.</summary>
    Home,

    /// <summary>The End key.</summary>
    End,

    /// <summary>The Page Up key.</summary>
    PageUp,

    /// <summary>The Page Down key.</summary>
    PageDown,

    /// <summary>Ctrl+C — sends the interrupt control character.</summary>
    CtrlC,

    /// <summary>Ctrl+D — sends the end-of-transmission control character.</summary>
    CtrlD
}

/// <summary>
/// Maps <see cref="AspireTerminalKey"/> values to the byte sequences a terminal workload expects.
/// </summary>
internal static class AspireTerminalKeySequences
{
    /// <summary>
    /// Gets the raw sequence for <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// Cursor and editing keys use the normal-mode sequences from the xterm control sequence reference
    /// (see https://invisible-island.net/xterm/ctlseqs/ctlseqs.html, "PC-Style Function Keys"). Applications
    /// that enable DECCKM (application cursor keys) expect SS3-prefixed forms instead — <c>ESC O A</c> rather
    /// than <c>ESC [ A</c> — but normal mode is the safer default because most workloads accept both and
    /// tracking DECCKM state would mean reaching back into the emulator for every keystroke.
    ///
    /// Backspace sends DEL (0x7f) rather than BS (0x08) because that is what terminal emulators send by
    /// default on Unix, and what readline-based shells expect.
    /// </remarks>
    public static string Get(AspireTerminalKey key) => key switch
    {
        AspireTerminalKey.Enter => "\r",
        AspireTerminalKey.Tab => "\t",
        AspireTerminalKey.Escape => "\u001b",
        AspireTerminalKey.Backspace => "\u007f",
        AspireTerminalKey.Delete => "\u001b[3~",
        AspireTerminalKey.Up => "\u001b[A",
        AspireTerminalKey.Down => "\u001b[B",
        AspireTerminalKey.Right => "\u001b[C",
        AspireTerminalKey.Left => "\u001b[D",
        AspireTerminalKey.Home => "\u001b[H",
        AspireTerminalKey.End => "\u001b[F",
        AspireTerminalKey.PageUp => "\u001b[5~",
        AspireTerminalKey.PageDown => "\u001b[6~",
        AspireTerminalKey.CtrlC => "\u0003",
        AspireTerminalKey.CtrlD => "\u0004",
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown terminal key.")
    };
}
