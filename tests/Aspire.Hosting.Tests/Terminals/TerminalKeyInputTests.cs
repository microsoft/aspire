// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Pipelines;
using System.Reflection;
using System.Text;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Hex1b;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable ASPIRETERMINAL001 // Test consumer of the experimental AppHost terminal API.

namespace Aspire.Hosting.Tests.Terminals;

[Trait("Partition", "2")]
public class TerminalKeyInputTests : IAsyncLifetime
{
    private readonly string _socketDirectory = Directory.CreateTempSubdirectory("aspire-key-input-").FullName;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NamedAndModifiedKeysReachTheWorkload(bool resourceOwned, bool applicationCursor)
    {
        await using var service = TestTerminalService.Create();
        var output = new Pipe();
        var input = new Pipe();
        await using var outputReader = output.Reader.AsStream();
        await using var outputWriter = output.Writer.AsStream();
        await using var inputReader = input.Reader.AsStream();
        await using var inputWriter = input.Writer.AsStream();
        var workload = new StreamWorkloadAdapter(outputReader, inputWriter);
        await using var host = resourceOwned
            ? await TestResourceTerminalHost.StartAsync(Path.Combine(_socketDirectory, "keys.sock"), workload)
            : null;
        await using var terminal = host is not null
            ? new ResourceAspireTerminal("resource:keys:0", "Keys", host.SocketPath, NullLogger.Instance).Handle
            : service.CreateTerminal("Keys", TerminalPlacement.None,
                Hex1bTerminal.CreateBuilder().WithWorkload(workload), 80, 24);

        terminal.Start();
        // The marker follows DECCKM on the same output stream. For resource terminals, wait on the
        // mirror too: handshake completion is not a guarantee that initial replay has been applied.
        await outputWriter.WriteAsync(Encoding.UTF8.GetBytes(
            (applicationCursor ? "\x1b[?1h" : "\x1b[?1l") + "key-input-ready"));
        if (host is not null)
        {
            await host.WaitForTextAsync("key-input-ready").DefaultTimeout();
        }
        await terminal.WaitForTextAsync("key-input-ready").DefaultTimeout();

        foreach (var (key, expected) in GetKeyInputs(applicationCursor))
        {
            await terminal.SendKeyAsync(key).DefaultTimeout();
            var bytes = new byte[Encoding.UTF8.GetByteCount(expected)];
            await inputReader.ReadExactlyAsync(bytes).AsTask().DefaultTimeout();
            Assert.Equal(Encoding.UTF8.GetBytes(expected), bytes);
        }

        await terminal.SendTextAsync("é😀").DefaultTimeout();
        var textBytes = new byte[Encoding.UTF8.GetByteCount("é😀")];
        await inputReader.ReadExactlyAsync(textBytes).AsTask().DefaultTimeout();
        Assert.Equal(Encoding.UTF8.GetBytes("é😀"), textBytes);

        await outputWriter.WriteAsync(Encoding.UTF8.GetBytes(
            (applicationCursor ? "\x1b[?1l" : "\x1b[?1h") + "key-input-mode-changed"));
        await terminal.WaitForTextAsync("key-input-mode-changed").DefaultTimeout();
        await terminal.SendKeyAsync(AspireTerminalKey.Home).DefaultTimeout();
        var homeBytes = new byte[3];
        await inputReader.ReadExactlyAsync(homeBytes).AsTask().DefaultTimeout();
        Assert.Equal(Encoding.UTF8.GetBytes(applicationCursor ? "\x1b[H" : "\x1bOH"), homeBytes);
    }

    private static IEnumerable<(AspireTerminalKey Key, string Expected)> GetKeyInputs(bool applicationCursor)
    {
        var keys = typeof(AspireTerminalKey).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(AspireTerminalKey))
            .ToDictionary(property => property.Name, property => Assert.IsType<AspireTerminalKey>(property.GetValue(null)));
        var cursorPrefix = applicationCursor ? "\x1bO" : "\x1b[";

        yield return (AspireTerminalKey.Up, cursorPrefix + "A");
        foreach (var (name, suffix) in new[]
        {
            ("Down", "B"), ("Right", "C"), ("Left", "D"), ("Home", "H"), ("End", "F")
        })
        {
            yield return (keys[name], cursorPrefix + suffix);
            yield return (AspireTerminalKey.Ctrl(keys[name]), "\x1b[1;5" + suffix);
            yield return (AspireTerminalKey.Alt(AspireTerminalKey.Shift(keys[name])), "\x1b[1;4" + suffix);
        }

        foreach (var (name, text) in new[]
        {
            ("Enter", "\r"), ("Tab", "\t"), ("Escape", "\x1b"), ("Backspace", "\x7f"),
            ("Space", " "), ("Insert", "\x1b[2~"), ("Delete", "\x1b[3~"),
            ("PageUp", "\x1b[5~"), ("PageDown", "\x1b[6~"),
            ("F1", "\x1bOP"), ("F2", "\x1bOQ"), ("F3", "\x1bOR"), ("F4", "\x1bOS"),
            ("F5", "\x1b[15~"), ("F6", "\x1b[17~"), ("F7", "\x1b[18~"), ("F8", "\x1b[19~"),
            ("F9", "\x1b[20~"), ("F10", "\x1b[21~"), ("F11", "\x1b[23~"), ("F12", "\x1b[24~")
        })
        {
            yield return (keys[name], text);
        }

        for (var letter = 'A'; letter <= 'Z'; letter++)
        {
            var key = keys[letter.ToString()];
            var lower = char.ToLowerInvariant(letter).ToString();
            var upper = letter.ToString();
            var control = ((char)(letter - 'A' + 1)).ToString();
            yield return (key, lower);
            yield return (AspireTerminalKey.Shift(key), upper);
            yield return (AspireTerminalKey.Ctrl(key), control);
            yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(key)), control);
            yield return (AspireTerminalKey.Alt(key), "\x1b" + lower);
            yield return (AspireTerminalKey.Alt(AspireTerminalKey.Shift(key)), "\x1b" + upper);
            yield return (AspireTerminalKey.Alt(AspireTerminalKey.Ctrl(key)), "\x1b" + control);
            yield return (AspireTerminalKey.Alt(AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(key))), "\x1b" + control);
        }

        const string shiftedDigits = ")!@#$%^&*(";
        for (var digit = 0; digit < 10; digit++)
        {
            var key = keys[$"D{digit}"];
            yield return (key, ((char)('0' + digit)).ToString());
            yield return (AspireTerminalKey.Shift(key), shiftedDigits[digit].ToString());
        }

        foreach (var (name, plain, shifted) in new[]
        {
            ("Comma", ",", "<"), ("Period", ".", ">"), ("Minus", "-", "_"),
            ("EqualsSign", "=", "+"), ("Slash", "/", "?"), ("Semicolon", ";", ":"),
            ("LeftBracket", "[", "{"), ("Backslash", "\\", "|"), ("RightBracket", "]", "}"),
            ("Apostrophe", "'", "\""), ("Backtick", "`", "~")
        })
        {
            yield return (keys[name], plain);
            yield return (AspireTerminalKey.Shift(keys[name]), shifted);
        }

        yield return (AspireTerminalKey.Shift(AspireTerminalKey.Tab), "\x1b[Z");
        yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.Tab), "\t");
        yield return (AspireTerminalKey.Shift(AspireTerminalKey.Enter), "\r");
        yield return (AspireTerminalKey.Alt(AspireTerminalKey.Enter), "\x1b\r");
        yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.Backspace), "\b");
        yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.Space), "\0");
        yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.LeftBracket), "\x1b");
        yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(AspireTerminalKey.Left)), "\x1b[1;6D");
        yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(AspireTerminalKey.Delete)), "\x1b[3;7~");
        yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.Shift(AspireTerminalKey.F1)), "\x1b[1;6P");
        yield return (AspireTerminalKey.Ctrl(AspireTerminalKey.Alt(AspireTerminalKey.Shift(AspireTerminalKey.F12))), "\x1b[24;8~");
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_socketDirectory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
