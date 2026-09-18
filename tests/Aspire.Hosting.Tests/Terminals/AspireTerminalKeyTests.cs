// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.Terminals;
using Hex1b.Input;

#pragma warning disable ASPIRETERMINAL001 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

[Trait("Partition", "2")]
public class AspireTerminalKeyTests
{
    [Fact]
    public void NamedKeys_HaveCompleteUniqueCatalogAndExpectedNativeMappings()
    {
        var namedKeys = GetNamedKeys();
        var properties = typeof(AspireTerminalKey).GetProperties(BindingFlags.Public | BindingFlags.Static);

        Assert.Equal(74, namedKeys.Length);
        Assert.Equal(namedKeys.Select(key => key.Name).Order(), properties.Select(property => property.Name).Order());
        Assert.All(properties, property => Assert.Equal(typeof(AspireTerminalKey), property.PropertyType));
        Assert.Equal(namedKeys.Length, namedKeys.Select(key => key.Value).Distinct().Count());
        Assert.Equal(namedKeys.Length, namedKeys.Select(key => key.Value.Key).Distinct().Count());

        foreach (var (name, key, nativeKey) in namedKeys)
        {
            Assert.Equal(nativeKey, key.Key);
            Assert.Equal(Hex1bModifiers.None, key.Modifiers);
            Assert.Equal(name, key.ToString());
            Assert.NotEqual(default, key);
            key.Validate("key");
        }
    }

    [Fact]
    public void ModifierFactories_PreserveKeyIdentityAndAccumulateModifiers()
    {
        foreach (var (_, key, nativeKey) in GetNamedKeys())
        {
            (AspireTerminalKey Value, Hex1bModifiers Modifiers)[] modifiedKeys =
            [
                (AspireTerminalKey.Ctrl(key), Hex1bModifiers.Control),
                (AspireTerminalKey.Shift(key), Hex1bModifiers.Shift),
                (AspireTerminalKey.Alt(key), Hex1bModifiers.Alt),
                (AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(key)), Hex1bModifiers.Control | Hex1bModifiers.Shift),
                (AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(key)), Hex1bModifiers.Control | Hex1bModifiers.Alt),
                (AspireTerminalKey.Alt(AspireTerminalKey.Shift(key)), Hex1bModifiers.Alt | Hex1bModifiers.Shift),
                (AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(AspireTerminalKey.Shift(key))), Hex1bModifiers.Control | Hex1bModifiers.Alt | Hex1bModifiers.Shift)
            ];

            foreach (var (modifiedKey, modifiers) in modifiedKeys)
            {
                Assert.Equal(nativeKey, modifiedKey.Key);
                Assert.Equal(modifiers, modifiedKey.Modifiers);
                modifiedKey.Validate("key");
            }

            Assert.Equal(Hex1bModifiers.None, key.Modifiers);
        }
    }

    [Fact]
    public void ModifierFactories_RepeatedApplicationIsIdempotent()
    {
        Func<AspireTerminalKey, AspireTerminalKey>[] factories =
        [
            AspireTerminalKey.Ctrl,
            AspireTerminalKey.Shift,
            AspireTerminalKey.Alt
        ];

        foreach (var (_, key, _) in GetNamedKeys())
        {
            var allModifiers = AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(AspireTerminalKey.Shift(key)));

            foreach (var factory in factories)
            {
                Assert.Equal(factory(key), factory(factory(key)));
                Assert.Equal(allModifiers, factory(allModifiers));
            }
        }
    }

    [Fact]
    public void ModifierFactories_AllSixOrdersAreEquivalent()
    {
        foreach (var (_, key, _) in GetNamedKeys())
        {
            AspireTerminalKey[] permutations =
            [
                AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(AspireTerminalKey.Shift(key))),
                AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(AspireTerminalKey.Alt(key))),
                AspireTerminalKey.Alt(AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(key))),
                AspireTerminalKey.Alt(AspireTerminalKey.Shift(AspireTerminalKey.Ctrl(key))),
                AspireTerminalKey.Shift(AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(key))),
                AspireTerminalKey.Shift(AspireTerminalKey.Alt(AspireTerminalKey.Ctrl(key)))
            ];

            foreach (var permutation in permutations)
            {
                Assert.Equal(permutations[0], permutation);
                Assert.Equal(permutations[0].GetHashCode(), permutation.GetHashCode());
                Assert.Equal(permutations[0].ToString(), permutation.ToString());
            }
        }
    }

    [Fact]
    public void Equality_ComparesKeyIdentityAndModifiers()
    {
        var first = AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(AspireTerminalKey.E));
        var second = AspireTerminalKey.Shift(AspireTerminalKey.Ctrl(AspireTerminalKey.E));
        var differentKey = AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(AspireTerminalKey.F));
        var differentModifiers = AspireTerminalKey.Ctrl(AspireTerminalKey.E);
        IEquatable<AspireTerminalKey> equatable = first;
        object boxed = first;

        Assert.True(first.Equals(second));
        Assert.True(equatable.Equals(second));
        Assert.True(boxed.Equals(second));
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());

        foreach (var different in new[] { differentKey, differentModifiers, default })
        {
            Assert.False(first.Equals(different));
            Assert.False(boxed.Equals(different));
            Assert.False(first == different);
            Assert.True(first != different);
        }

        Assert.False(first.Equals(null));
        Assert.False(first.Equals("Ctrl+Shift+E"));
        Assert.False(first.Equals(Hex1bKey.E));
    }

    [Fact]
    public void Equality_DefaultValuesAreEqual()
    {
        var first = default(AspireTerminalKey);
        var second = new AspireTerminalKey();

        Assert.True(first.Equals(second));
        Assert.True(first.Equals((object)second));
        Assert.True(first == second);
        Assert.False(first != second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
    }

    [Fact]
    public void ToString_DescribesKeyAndModifiersInCanonicalOrder()
    {
        (AspireTerminalKey Key, string Expected)[] cases =
        [
            (AspireTerminalKey.E, "E"),
            (AspireTerminalKey.Ctrl(AspireTerminalKey.E), "Ctrl+E"),
            (AspireTerminalKey.Alt(AspireTerminalKey.E), "Alt+E"),
            (AspireTerminalKey.Shift(AspireTerminalKey.E), "Shift+E"),
            (AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(AspireTerminalKey.E)), "Ctrl+Alt+E"),
            (AspireTerminalKey.Shift(AspireTerminalKey.Ctrl(AspireTerminalKey.E)), "Ctrl+Shift+E"),
            (AspireTerminalKey.Alt(AspireTerminalKey.Shift(AspireTerminalKey.E)), "Alt+Shift+E"),
            (AspireTerminalKey.Shift(AspireTerminalKey.Alt(AspireTerminalKey.Ctrl(AspireTerminalKey.E))), "Ctrl+Alt+Shift+E"),
            (AspireTerminalKey.Ctrl(AspireTerminalKey.Delete), "Ctrl+Delete"),
            (AspireTerminalKey.Shift(AspireTerminalKey.D1), "Shift+D1"),
            (AspireTerminalKey.Alt(AspireTerminalKey.Backtick), "Alt+Backtick"),
            (default, "Uninitialized")
        ];

        foreach (var (key, expected) in cases)
        {
            Assert.Equal(expected, key.ToString());
        }
    }

    [Theory]
    [InlineData("key")]
    [InlineData("terminalKey")]
    public void Validate_DefaultValue_ThrowsWithParameterName(string paramName)
    {
        var key = default(AspireTerminalKey);

        Assert.Throws<ArgumentException>(paramName, () => key.Validate(paramName));
    }

    [Fact]
    public void ModifierFactories_DefaultValue_ThrowsWithKeyParameterName()
    {
        Assert.Throws<ArgumentException>("key", () => AspireTerminalKey.Ctrl(default));
        Assert.Throws<ArgumentException>("key", () => AspireTerminalKey.Shift(default));
        Assert.Throws<ArgumentException>("key", () => AspireTerminalKey.Alt(default));
    }

    private static (string Name, AspireTerminalKey Value, Hex1bKey NativeKey)[] GetNamedKeys() =>
    [
        ("A", AspireTerminalKey.A, Hex1bKey.A),
        ("B", AspireTerminalKey.B, Hex1bKey.B),
        ("C", AspireTerminalKey.C, Hex1bKey.C),
        ("D", AspireTerminalKey.D, Hex1bKey.D),
        ("E", AspireTerminalKey.E, Hex1bKey.E),
        ("F", AspireTerminalKey.F, Hex1bKey.F),
        ("G", AspireTerminalKey.G, Hex1bKey.G),
        ("H", AspireTerminalKey.H, Hex1bKey.H),
        ("I", AspireTerminalKey.I, Hex1bKey.I),
        ("J", AspireTerminalKey.J, Hex1bKey.J),
        ("K", AspireTerminalKey.K, Hex1bKey.K),
        ("L", AspireTerminalKey.L, Hex1bKey.L),
        ("M", AspireTerminalKey.M, Hex1bKey.M),
        ("N", AspireTerminalKey.N, Hex1bKey.N),
        ("O", AspireTerminalKey.O, Hex1bKey.O),
        ("P", AspireTerminalKey.P, Hex1bKey.P),
        ("Q", AspireTerminalKey.Q, Hex1bKey.Q),
        ("R", AspireTerminalKey.R, Hex1bKey.R),
        ("S", AspireTerminalKey.S, Hex1bKey.S),
        ("T", AspireTerminalKey.T, Hex1bKey.T),
        ("U", AspireTerminalKey.U, Hex1bKey.U),
        ("V", AspireTerminalKey.V, Hex1bKey.V),
        ("W", AspireTerminalKey.W, Hex1bKey.W),
        ("X", AspireTerminalKey.X, Hex1bKey.X),
        ("Y", AspireTerminalKey.Y, Hex1bKey.Y),
        ("Z", AspireTerminalKey.Z, Hex1bKey.Z),
        ("D0", AspireTerminalKey.D0, Hex1bKey.D0),
        ("D1", AspireTerminalKey.D1, Hex1bKey.D1),
        ("D2", AspireTerminalKey.D2, Hex1bKey.D2),
        ("D3", AspireTerminalKey.D3, Hex1bKey.D3),
        ("D4", AspireTerminalKey.D4, Hex1bKey.D4),
        ("D5", AspireTerminalKey.D5, Hex1bKey.D5),
        ("D6", AspireTerminalKey.D6, Hex1bKey.D6),
        ("D7", AspireTerminalKey.D7, Hex1bKey.D7),
        ("D8", AspireTerminalKey.D8, Hex1bKey.D8),
        ("D9", AspireTerminalKey.D9, Hex1bKey.D9),
        ("F1", AspireTerminalKey.F1, Hex1bKey.F1),
        ("F2", AspireTerminalKey.F2, Hex1bKey.F2),
        ("F3", AspireTerminalKey.F3, Hex1bKey.F3),
        ("F4", AspireTerminalKey.F4, Hex1bKey.F4),
        ("F5", AspireTerminalKey.F5, Hex1bKey.F5),
        ("F6", AspireTerminalKey.F6, Hex1bKey.F6),
        ("F7", AspireTerminalKey.F7, Hex1bKey.F7),
        ("F8", AspireTerminalKey.F8, Hex1bKey.F8),
        ("F9", AspireTerminalKey.F9, Hex1bKey.F9),
        ("F10", AspireTerminalKey.F10, Hex1bKey.F10),
        ("F11", AspireTerminalKey.F11, Hex1bKey.F11),
        ("F12", AspireTerminalKey.F12, Hex1bKey.F12),
        ("Enter", AspireTerminalKey.Enter, Hex1bKey.Enter),
        ("Tab", AspireTerminalKey.Tab, Hex1bKey.Tab),
        ("Escape", AspireTerminalKey.Escape, Hex1bKey.Escape),
        ("Backspace", AspireTerminalKey.Backspace, Hex1bKey.Backspace),
        ("Delete", AspireTerminalKey.Delete, Hex1bKey.Delete),
        ("Insert", AspireTerminalKey.Insert, Hex1bKey.Insert),
        ("Space", AspireTerminalKey.Space, Hex1bKey.Spacebar),
        ("Up", AspireTerminalKey.Up, Hex1bKey.UpArrow),
        ("Down", AspireTerminalKey.Down, Hex1bKey.DownArrow),
        ("Left", AspireTerminalKey.Left, Hex1bKey.LeftArrow),
        ("Right", AspireTerminalKey.Right, Hex1bKey.RightArrow),
        ("Home", AspireTerminalKey.Home, Hex1bKey.Home),
        ("End", AspireTerminalKey.End, Hex1bKey.End),
        ("PageUp", AspireTerminalKey.PageUp, Hex1bKey.PageUp),
        ("PageDown", AspireTerminalKey.PageDown, Hex1bKey.PageDown),
        ("Comma", AspireTerminalKey.Comma, Hex1bKey.OemComma),
        ("Period", AspireTerminalKey.Period, Hex1bKey.OemPeriod),
        ("Minus", AspireTerminalKey.Minus, Hex1bKey.OemMinus),
        ("EqualsSign", AspireTerminalKey.EqualsSign, Hex1bKey.OemPlus),
        ("Slash", AspireTerminalKey.Slash, Hex1bKey.OemQuestion),
        ("Semicolon", AspireTerminalKey.Semicolon, Hex1bKey.Oem1),
        ("LeftBracket", AspireTerminalKey.LeftBracket, Hex1bKey.Oem4),
        ("Backslash", AspireTerminalKey.Backslash, Hex1bKey.Oem5),
        ("RightBracket", AspireTerminalKey.RightBracket, Hex1bKey.Oem6),
        ("Apostrophe", AspireTerminalKey.Apostrophe, Hex1bKey.Oem7),
        ("Backtick", AspireTerminalKey.Backtick, Hex1bKey.OemTilde)
    ];
}
