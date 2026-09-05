// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Terminals;

#pragma warning disable ASPIRETERMINAL002 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

/// <summary>
/// Guards the <see cref="AspireTerminalKey"/> to control-sequence mapping. The sequences are a wire contract with
/// whatever workload the terminal is running, so a wrong byte here does not fail loudly — it silently sends the
/// wrong key, which surfaces as automation that hangs waiting for output that will never come.
/// </summary>
[Trait("Partition", "2")]
public class AspireTerminalKeySequencesTests
{
    [Theory]
    // Control characters.
    [InlineData(AspireTerminalKey.Enter, "\r")]
    [InlineData(AspireTerminalKey.Tab, "\t")]
    [InlineData(AspireTerminalKey.Escape, "\u001b")]
    [InlineData(AspireTerminalKey.CtrlC, "\u0003")]
    [InlineData(AspireTerminalKey.CtrlD, "\u0004")]
    // DEL rather than BS, which is what emulators send on Unix and what readline-based shells expect.
    [InlineData(AspireTerminalKey.Backspace, "\u007f")]
    // Normal-mode (CSI) cursor keys rather than the SS3 forms an application-cursor-keys workload would use.
    [InlineData(AspireTerminalKey.Up, "\u001b[A")]
    [InlineData(AspireTerminalKey.Down, "\u001b[B")]
    [InlineData(AspireTerminalKey.Right, "\u001b[C")]
    [InlineData(AspireTerminalKey.Left, "\u001b[D")]
    [InlineData(AspireTerminalKey.Home, "\u001b[H")]
    [InlineData(AspireTerminalKey.End, "\u001b[F")]
    // PC-style editing keys, which are tilde-terminated and numbered.
    [InlineData(AspireTerminalKey.Delete, "\u001b[3~")]
    [InlineData(AspireTerminalKey.PageUp, "\u001b[5~")]
    [InlineData(AspireTerminalKey.PageDown, "\u001b[6~")]
    public void Get_ReturnsExpectedSequence(AspireTerminalKey key, string expected)
    {
        Assert.Equal(expected, AspireTerminalKeySequences.Get(key));
    }

    [Fact]
    public void Get_CoversEveryDeclaredKey()
    {
        // A key added to the enum without a switch arm compiles cleanly and only fails when someone presses it,
        // so assert the mapping is total rather than relying on the arms enumerated above staying in sync.
        foreach (var key in Enum.GetValues<AspireTerminalKey>())
        {
            Assert.NotEmpty(AspireTerminalKeySequences.Get(key));
        }
    }

    [Fact]
    public void Get_UndefinedKey_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => AspireTerminalKeySequences.Get((AspireTerminalKey)int.MaxValue));
        Assert.Equal("key", ex.ParamName);
    }
}
