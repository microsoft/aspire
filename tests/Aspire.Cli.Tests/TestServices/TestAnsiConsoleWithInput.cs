// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAnsiConsoleWithInput : IAnsiConsole
{
    private readonly IAnsiConsole _inner;
    private readonly IAnsiConsoleInput _testInput;

    public TestAnsiConsoleWithInput(IAnsiConsole inner, TextReader inputReader)
    {
        _inner = inner;
        _testInput = new TextReaderInput(inputReader);
    }

    public TestAnsiConsoleWithInput(IAnsiConsole inner, IEnumerable<ConsoleKeyInfo> keys)
    {
        _inner = inner;
        _testInput = new KeyInput(keys);
    }

    public TestAnsiConsoleWithInput(IAnsiConsole inner, Func<bool, CancellationToken, Task<ConsoleKeyInfo?>> readKeyAsync)
    {
        _inner = inner;
        _testInput = new CallbackInput(readKeyAsync);
    }

    public Profile Profile => _inner.Profile;
    public IAnsiConsoleCursor Cursor => _inner.Cursor;
    public IAnsiConsoleInput Input => _testInput;
    public IExclusivityMode ExclusivityMode => _inner.ExclusivityMode;
    public RenderPipeline Pipeline => _inner.Pipeline;

    public void Clear(bool home) => _inner.Clear(home);
    public void Write(IRenderable renderable) => _inner.Write(renderable);
    public void WriteAnsi(Action<AnsiWriter> action) => _inner.WriteAnsi(action);

    private sealed class CallbackInput(Func<bool, CancellationToken, Task<ConsoleKeyInfo?>> readKeyAsync) : IAnsiConsoleInput
    {
        public bool IsKeyAvailable() => true;
        public ConsoleKeyInfo? ReadKey(bool intercept) => throw new NotSupportedException("This input supports asynchronous reads only.");
        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
            => readKeyAsync(intercept, cancellationToken);
    }

    private sealed class KeyInput(IEnumerable<ConsoleKeyInfo> keys) : IAnsiConsoleInput
    {
        private readonly Queue<ConsoleKeyInfo> _keys = new(keys);

        public bool IsKeyAvailable() => _keys.Count > 0;
        public ConsoleKeyInfo? ReadKey(bool intercept) => _keys.Dequeue();
        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ReadKey(intercept));
        }
    }

    private sealed class TextReaderInput(TextReader reader) : IAnsiConsoleInput
    {
        public bool IsKeyAvailable() => true;

        public ConsoleKeyInfo? ReadKey(bool intercept)
        {
            var read = reader.Read();
            if (read == -1)
            {
                return new ConsoleKeyInfo('\r', ConsoleKey.Enter, shift: false, alt: false, control: false);
            }

            var ch = (char)read;
            var key = ch switch
            {
                '\n' or '\r' => ConsoleKey.Enter,
                'y' or 'Y' => ConsoleKey.Y,
                'n' or 'N' => ConsoleKey.N,
                _ => ConsoleKey.Enter,
            };
            return new ConsoleKeyInfo(ch, key, shift: char.IsUpper(ch), alt: false, control: false);
        }

        public Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
            => Task.FromResult(ReadKey(intercept));
    }
}
