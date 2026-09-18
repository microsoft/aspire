// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Hex1b.Input;

#pragma warning disable ASPIRETERMINAL001 // Internal consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Terminals;

/// <summary>
/// Represents one terminal keypress, optionally combined with modifiers, for <see cref="AspireTerminal.SendKeyAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use a named key property and compose <see cref="Ctrl"/>, <see cref="Shift"/>, and <see cref="Alt"/>
/// to add modifiers. Composition is order-independent, and applying the same modifier repeatedly has no
/// additional effect. A default-initialized value is invalid and cannot be sent or passed to a modifier factory.
/// </para>
/// <para>
/// Letter keys produce lowercase letters without modifiers and uppercase letters with <see cref="Shift"/>.
/// Digit and punctuation keys follow the US keyboard layout. Use <see cref="AspireTerminal.SendTextAsync"/>
/// for arbitrary text or Unicode rather than composing keypresses.
/// </para>
/// <para>
/// These values describe terminal input, not physically held keys. Terminal protocols, platforms, and
/// applications differ in how they interpret key combinations. Legacy terminal encoding cannot distinguish
/// Ctrl+I from Tab, Ctrl+M from Enter, or Ctrl+Shift+letter from Ctrl+letter.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await terminal.SendKeyAsync(AspireTerminalKey.Shift(AspireTerminalKey.E));
/// await terminal.SendKeyAsync(AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(AspireTerminalKey.Delete)));
/// </code>
/// </example>
[Experimental(TerminalDiagnostics.DiagnosticId, UrlFormat = TerminalDiagnostics.UrlFormat)]
public readonly struct AspireTerminalKey : IEquatable<AspireTerminalKey>
{
    private readonly string? _name;

    private AspireTerminalKey(Hex1bKey key, Hex1bModifiers modifiers, string name)
    {
        Key = key;
        Modifiers = modifiers;
        _name = name;
    }

    /// <summary>Gets the terminal key identity.</summary>
    internal Hex1bKey Key { get; }

    /// <summary>Gets the modifiers applied to this keypress.</summary>
    internal Hex1bModifiers Modifiers { get; }

    /// <summary>Gets the A letter key.</summary>
    public static AspireTerminalKey A => new(Hex1bKey.A, Hex1bModifiers.None, nameof(A));

    /// <summary>Gets the B letter key.</summary>
    public static AspireTerminalKey B => new(Hex1bKey.B, Hex1bModifiers.None, nameof(B));

    /// <summary>Gets the C letter key.</summary>
    public static AspireTerminalKey C => new(Hex1bKey.C, Hex1bModifiers.None, nameof(C));

    /// <summary>Gets the D letter key.</summary>
    public static AspireTerminalKey D => new(Hex1bKey.D, Hex1bModifiers.None, nameof(D));

    /// <summary>Gets the E letter key.</summary>
    public static AspireTerminalKey E => new(Hex1bKey.E, Hex1bModifiers.None, nameof(E));

    /// <summary>Gets the F letter key.</summary>
    public static AspireTerminalKey F => new(Hex1bKey.F, Hex1bModifiers.None, nameof(F));

    /// <summary>Gets the G letter key.</summary>
    public static AspireTerminalKey G => new(Hex1bKey.G, Hex1bModifiers.None, nameof(G));

    /// <summary>Gets the H letter key.</summary>
    public static AspireTerminalKey H => new(Hex1bKey.H, Hex1bModifiers.None, nameof(H));

    /// <summary>Gets the I letter key.</summary>
    public static AspireTerminalKey I => new(Hex1bKey.I, Hex1bModifiers.None, nameof(I));

    /// <summary>Gets the J letter key.</summary>
    public static AspireTerminalKey J => new(Hex1bKey.J, Hex1bModifiers.None, nameof(J));

    /// <summary>Gets the K letter key.</summary>
    public static AspireTerminalKey K => new(Hex1bKey.K, Hex1bModifiers.None, nameof(K));

    /// <summary>Gets the L letter key.</summary>
    public static AspireTerminalKey L => new(Hex1bKey.L, Hex1bModifiers.None, nameof(L));

    /// <summary>Gets the M letter key.</summary>
    public static AspireTerminalKey M => new(Hex1bKey.M, Hex1bModifiers.None, nameof(M));

    /// <summary>Gets the N letter key.</summary>
    public static AspireTerminalKey N => new(Hex1bKey.N, Hex1bModifiers.None, nameof(N));

    /// <summary>Gets the O letter key.</summary>
    public static AspireTerminalKey O => new(Hex1bKey.O, Hex1bModifiers.None, nameof(O));

    /// <summary>Gets the P letter key.</summary>
    public static AspireTerminalKey P => new(Hex1bKey.P, Hex1bModifiers.None, nameof(P));

    /// <summary>Gets the Q letter key.</summary>
    public static AspireTerminalKey Q => new(Hex1bKey.Q, Hex1bModifiers.None, nameof(Q));

    /// <summary>Gets the R letter key.</summary>
    public static AspireTerminalKey R => new(Hex1bKey.R, Hex1bModifiers.None, nameof(R));

    /// <summary>Gets the S letter key.</summary>
    public static AspireTerminalKey S => new(Hex1bKey.S, Hex1bModifiers.None, nameof(S));

    /// <summary>Gets the T letter key.</summary>
    public static AspireTerminalKey T => new(Hex1bKey.T, Hex1bModifiers.None, nameof(T));

    /// <summary>Gets the U letter key.</summary>
    public static AspireTerminalKey U => new(Hex1bKey.U, Hex1bModifiers.None, nameof(U));

    /// <summary>Gets the V letter key.</summary>
    public static AspireTerminalKey V => new(Hex1bKey.V, Hex1bModifiers.None, nameof(V));

    /// <summary>Gets the W letter key.</summary>
    public static AspireTerminalKey W => new(Hex1bKey.W, Hex1bModifiers.None, nameof(W));

    /// <summary>Gets the X letter key.</summary>
    public static AspireTerminalKey X => new(Hex1bKey.X, Hex1bModifiers.None, nameof(X));

    /// <summary>Gets the Y letter key.</summary>
    public static AspireTerminalKey Y => new(Hex1bKey.Y, Hex1bModifiers.None, nameof(Y));

    /// <summary>Gets the Z letter key.</summary>
    public static AspireTerminalKey Z => new(Hex1bKey.Z, Hex1bModifiers.None, nameof(Z));

    /// <summary>Gets the 0 digit key.</summary>
    public static AspireTerminalKey D0 => new(Hex1bKey.D0, Hex1bModifiers.None, nameof(D0));

    /// <summary>Gets the 1 digit key.</summary>
    public static AspireTerminalKey D1 => new(Hex1bKey.D1, Hex1bModifiers.None, nameof(D1));

    /// <summary>Gets the 2 digit key.</summary>
    public static AspireTerminalKey D2 => new(Hex1bKey.D2, Hex1bModifiers.None, nameof(D2));

    /// <summary>Gets the 3 digit key.</summary>
    public static AspireTerminalKey D3 => new(Hex1bKey.D3, Hex1bModifiers.None, nameof(D3));

    /// <summary>Gets the 4 digit key.</summary>
    public static AspireTerminalKey D4 => new(Hex1bKey.D4, Hex1bModifiers.None, nameof(D4));

    /// <summary>Gets the 5 digit key.</summary>
    public static AspireTerminalKey D5 => new(Hex1bKey.D5, Hex1bModifiers.None, nameof(D5));

    /// <summary>Gets the 6 digit key.</summary>
    public static AspireTerminalKey D6 => new(Hex1bKey.D6, Hex1bModifiers.None, nameof(D6));

    /// <summary>Gets the 7 digit key.</summary>
    public static AspireTerminalKey D7 => new(Hex1bKey.D7, Hex1bModifiers.None, nameof(D7));

    /// <summary>Gets the 8 digit key.</summary>
    public static AspireTerminalKey D8 => new(Hex1bKey.D8, Hex1bModifiers.None, nameof(D8));

    /// <summary>Gets the 9 digit key.</summary>
    public static AspireTerminalKey D9 => new(Hex1bKey.D9, Hex1bModifiers.None, nameof(D9));

    /// <summary>Gets the F1 function key.</summary>
    public static AspireTerminalKey F1 => new(Hex1bKey.F1, Hex1bModifiers.None, nameof(F1));

    /// <summary>Gets the F2 function key.</summary>
    public static AspireTerminalKey F2 => new(Hex1bKey.F2, Hex1bModifiers.None, nameof(F2));

    /// <summary>Gets the F3 function key.</summary>
    public static AspireTerminalKey F3 => new(Hex1bKey.F3, Hex1bModifiers.None, nameof(F3));

    /// <summary>Gets the F4 function key.</summary>
    public static AspireTerminalKey F4 => new(Hex1bKey.F4, Hex1bModifiers.None, nameof(F4));

    /// <summary>Gets the F5 function key.</summary>
    public static AspireTerminalKey F5 => new(Hex1bKey.F5, Hex1bModifiers.None, nameof(F5));

    /// <summary>Gets the F6 function key.</summary>
    public static AspireTerminalKey F6 => new(Hex1bKey.F6, Hex1bModifiers.None, nameof(F6));

    /// <summary>Gets the F7 function key.</summary>
    public static AspireTerminalKey F7 => new(Hex1bKey.F7, Hex1bModifiers.None, nameof(F7));

    /// <summary>Gets the F8 function key.</summary>
    public static AspireTerminalKey F8 => new(Hex1bKey.F8, Hex1bModifiers.None, nameof(F8));

    /// <summary>Gets the F9 function key.</summary>
    public static AspireTerminalKey F9 => new(Hex1bKey.F9, Hex1bModifiers.None, nameof(F9));

    /// <summary>Gets the F10 function key.</summary>
    public static AspireTerminalKey F10 => new(Hex1bKey.F10, Hex1bModifiers.None, nameof(F10));

    /// <summary>Gets the F11 function key.</summary>
    public static AspireTerminalKey F11 => new(Hex1bKey.F11, Hex1bModifiers.None, nameof(F11));

    /// <summary>Gets the F12 function key.</summary>
    public static AspireTerminalKey F12 => new(Hex1bKey.F12, Hex1bModifiers.None, nameof(F12));

    /// <summary>Gets the Enter key.</summary>
    public static AspireTerminalKey Enter => new(Hex1bKey.Enter, Hex1bModifiers.None, nameof(Enter));

    /// <summary>Gets the Tab key.</summary>
    public static AspireTerminalKey Tab => new(Hex1bKey.Tab, Hex1bModifiers.None, nameof(Tab));

    /// <summary>Gets the Escape key.</summary>
    public static AspireTerminalKey Escape => new(Hex1bKey.Escape, Hex1bModifiers.None, nameof(Escape));

    /// <summary>Gets the Backspace key.</summary>
    public static AspireTerminalKey Backspace => new(Hex1bKey.Backspace, Hex1bModifiers.None, nameof(Backspace));

    /// <summary>Gets the Delete key.</summary>
    public static AspireTerminalKey Delete => new(Hex1bKey.Delete, Hex1bModifiers.None, nameof(Delete));

    /// <summary>Gets the Insert key.</summary>
    public static AspireTerminalKey Insert => new(Hex1bKey.Insert, Hex1bModifiers.None, nameof(Insert));

    /// <summary>Gets the space bar key.</summary>
    public static AspireTerminalKey Space => new(Hex1bKey.Spacebar, Hex1bModifiers.None, nameof(Space));

    /// <summary>Gets the Up arrow key.</summary>
    public static AspireTerminalKey Up => new(Hex1bKey.UpArrow, Hex1bModifiers.None, nameof(Up));

    /// <summary>Gets the Down arrow key.</summary>
    public static AspireTerminalKey Down => new(Hex1bKey.DownArrow, Hex1bModifiers.None, nameof(Down));

    /// <summary>Gets the Left arrow key.</summary>
    public static AspireTerminalKey Left => new(Hex1bKey.LeftArrow, Hex1bModifiers.None, nameof(Left));

    /// <summary>Gets the Right arrow key.</summary>
    public static AspireTerminalKey Right => new(Hex1bKey.RightArrow, Hex1bModifiers.None, nameof(Right));

    /// <summary>Gets the Home key.</summary>
    public static AspireTerminalKey Home => new(Hex1bKey.Home, Hex1bModifiers.None, nameof(Home));

    /// <summary>Gets the End key.</summary>
    public static AspireTerminalKey End => new(Hex1bKey.End, Hex1bModifiers.None, nameof(End));

    /// <summary>Gets the Page Up key.</summary>
    public static AspireTerminalKey PageUp => new(Hex1bKey.PageUp, Hex1bModifiers.None, nameof(PageUp));

    /// <summary>Gets the Page Down key.</summary>
    public static AspireTerminalKey PageDown => new(Hex1bKey.PageDown, Hex1bModifiers.None, nameof(PageDown));

    /// <summary>Gets the comma (<c>,</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey Comma => new(Hex1bKey.OemComma, Hex1bModifiers.None, nameof(Comma));

    /// <summary>Gets the period (<c>.</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey Period => new(Hex1bKey.OemPeriod, Hex1bModifiers.None, nameof(Period));

    /// <summary>Gets the minus (<c>-</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey Minus => new(Hex1bKey.OemMinus, Hex1bModifiers.None, nameof(Minus));

    /// <summary>Gets the equals sign (<c>=</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey EqualsSign => new(Hex1bKey.OemPlus, Hex1bModifiers.None, nameof(EqualsSign));

    /// <summary>Gets the slash (<c>/</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey Slash => new(Hex1bKey.OemQuestion, Hex1bModifiers.None, nameof(Slash));

    /// <summary>Gets the semicolon (<c>;</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey Semicolon => new(Hex1bKey.Oem1, Hex1bModifiers.None, nameof(Semicolon));

    /// <summary>Gets the left bracket (<c>[</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey LeftBracket => new(Hex1bKey.Oem4, Hex1bModifiers.None, nameof(LeftBracket));

    /// <summary>Gets the backslash (<c>\</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey Backslash => new(Hex1bKey.Oem5, Hex1bModifiers.None, nameof(Backslash));

    /// <summary>Gets the right bracket (<c>]</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey RightBracket => new(Hex1bKey.Oem6, Hex1bModifiers.None, nameof(RightBracket));

    /// <summary>Gets the apostrophe (<c>'</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey Apostrophe => new(Hex1bKey.Oem7, Hex1bModifiers.None, nameof(Apostrophe));

    /// <summary>Gets the backtick (<c>`</c>) key on a US keyboard.</summary>
    public static AspireTerminalKey Backtick => new(Hex1bKey.OemTilde, Hex1bModifiers.None, nameof(Backtick));

    /// <summary>
    /// Adds the Control modifier to a terminal keypress.
    /// </summary>
    /// <param name="key">The initialized keypress, including any existing modifiers.</param>
    /// <returns>A keypress with Control added, preserving its key identity and other modifiers.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is default-initialized.</exception>
    /// <remarks>Repeated application has no additional effect, and composition with other modifiers is order-independent.</remarks>
    public static AspireTerminalKey Ctrl(AspireTerminalKey key)
    {
        key.Validate(nameof(key));

        return new(key.Key, key.Modifiers | Hex1bModifiers.Control, key._name!);
    }

    /// <summary>
    /// Adds the Shift modifier to a terminal keypress.
    /// </summary>
    /// <param name="key">The initialized keypress, including any existing modifiers.</param>
    /// <returns>A keypress with Shift added, preserving its key identity and other modifiers.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is default-initialized.</exception>
    /// <remarks>Repeated application has no additional effect, and composition with other modifiers is order-independent.</remarks>
    public static AspireTerminalKey Shift(AspireTerminalKey key)
    {
        key.Validate(nameof(key));

        return new(key.Key, key.Modifiers | Hex1bModifiers.Shift, key._name!);
    }

    /// <summary>
    /// Adds the Alt modifier to a terminal keypress.
    /// </summary>
    /// <param name="key">The initialized keypress, including any existing modifiers.</param>
    /// <returns>A keypress with Alt added, preserving its key identity and other modifiers.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is default-initialized.</exception>
    /// <remarks>Repeated application has no additional effect, and composition with other modifiers is order-independent.</remarks>
    public static AspireTerminalKey Alt(AspireTerminalKey key)
    {
        key.Validate(nameof(key));

        return new(key.Key, key.Modifiers | Hex1bModifiers.Alt, key._name!);
    }

    /// <summary>
    /// Determines whether another keypress has the same key identity and modifiers.
    /// </summary>
    /// <param name="other">The keypress to compare.</param>
    /// <returns><see langword="true"/> if the key identities and modifiers are equal; otherwise, <see langword="false"/>.</returns>
    public bool Equals(AspireTerminalKey other) => Key == other.Key && Modifiers == other.Modifiers;

    /// <summary>
    /// Determines whether an object is a keypress with the same key identity and modifiers.
    /// </summary>
    /// <param name="obj">The object to compare, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the object is an equal keypress; otherwise, <see langword="false"/>.</returns>
    public override bool Equals(object? obj) => obj is AspireTerminalKey other && Equals(other);

    /// <summary>
    /// Gets a hash code based on the key identity and modifiers.
    /// </summary>
    /// <returns>The hash code for this keypress.</returns>
    public override int GetHashCode() => HashCode.Combine(Key, Modifiers);

    /// <summary>
    /// Gets a readable description of this keypress.
    /// </summary>
    /// <returns>
    /// The named key prefixed by any modifiers in Ctrl, Alt, Shift order, separated by <c>+</c>,
    /// or <c>Uninitialized</c> for a default-initialized value.
    /// </returns>
    public override string ToString()
    {
        if (_name is null)
        {
            return "Uninitialized";
        }

        var control = (Modifiers & Hex1bModifiers.Control) != 0 ? "Ctrl+" : string.Empty;
        var alt = (Modifiers & Hex1bModifiers.Alt) != 0 ? "Alt+" : string.Empty;
        var shift = (Modifiers & Hex1bModifiers.Shift) != 0 ? "Shift+" : string.Empty;

        return string.Concat(control, alt, shift, _name);
    }

    /// <summary>
    /// Determines whether two keypresses have the same key identity and modifiers.
    /// </summary>
    /// <param name="left">The first keypress.</param>
    /// <param name="right">The second keypress.</param>
    /// <returns><see langword="true"/> if the keypresses are equal; otherwise, <see langword="false"/>.</returns>
    public static bool operator ==(AspireTerminalKey left, AspireTerminalKey right) => left.Equals(right);

    /// <summary>
    /// Determines whether two keypresses differ in key identity or modifiers.
    /// </summary>
    /// <param name="left">The first keypress.</param>
    /// <param name="right">The second keypress.</param>
    /// <returns><see langword="true"/> if the keypresses differ; otherwise, <see langword="false"/>.</returns>
    public static bool operator !=(AspireTerminalKey left, AspireTerminalKey right) => !left.Equals(right);

    /// <summary>
    /// Rejects a default-initialized keypress.
    /// </summary>
    internal void Validate(string paramName)
    {
        if (_name is null)
        {
            throw new ArgumentException("The terminal key must be initialized using a named key property.", paramName);
        }
    }
}
