// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.TestServices;

/// <summary>
/// A test <see cref="IAnsiConsole"/> wrapper that redirects input reads to a <see cref="TextReader"/>,
/// allowing prompts to be answered in unit tests without blocking on real console input.
/// </summary>
internal sealed class TestAnsiConsoleWithInput : IAnsiConsole
{
    private readonly IAnsiConsole _inner;
    private readonly IAnsiConsoleInput _testInput;

    public TestAnsiConsoleWithInput(IAnsiConsole inner, TextReader inputReader)
    {
        _inner = inner;
        _testInput = new TextReaderInput(inputReader);
    }

    public Profile Profile => _inner.Profile;
    public IAnsiConsoleCursor Cursor => _inner.Cursor;
    public IAnsiConsoleInput Input => _testInput;
    public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;
    public RenderPipeline Pipeline => _inner.Pipeline;

    public void Clear(bool home) => _inner.Clear(home);
    public void Write(IRenderable renderable) => _inner.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => _inner.WriteAnsi(action);

    private sealed class TextReaderInput : IAnsiConsoleInput
    {
        private readonly TextReader _reader;

        public TextReaderInput(TextReader reader) => _reader = reader;

        public bool IsKeyAvailable() => true;

        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            var read = _reader.Read();
            if (read == -1)
            {
                // End of stream - return Enter as a safe fallback
                return new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
            }

            var ch = (char)read;
            var key = ch switch
            {
                '\n' or '\r' => ConsoleKey.Enter,
                'y' or 'Y'   => ConsoleKey.Y,
                'n' or 'N'   => ConsoleKey.N,
                _            => ConsoleKey.Enter,
            };
            return new ConsoleKeyInfo(ch, key, shift: char.IsUpper(ch), alt: false, control: false);
        }

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
            => Task.FromResult(ReadKey(intercept));
    }
}
