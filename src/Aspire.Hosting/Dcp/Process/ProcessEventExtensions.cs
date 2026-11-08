// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;

namespace Aspire.Hosting.Dcp.Process;

/// <summary>
/// Adapts one process event stream without taking over process or pipe ownership.
/// </summary>
internal static class ProcessEventExtensions
{
    /// <summary>
    /// Observes launch without changing or removing events.
    /// </summary>
    public static IAsyncEnumerable<ProcessEvent<T>> OnStarted<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events, Action<int> observer)
        => Transform(events, item =>
        {
            if (item is ProcessEvent<T>.Started(var processId))
            {
                observer(processId);
            }
            return item;
        });

    /// <summary>
    /// Observes standard output without changing or removing events.
    /// </summary>
    public static IAsyncEnumerable<ProcessEvent<T>> OnStandardOutput<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events, Action<T> observer)
        => Transform(events, item =>
        {
            if (item is ProcessEvent<T>.StandardOutput(var content))
            {
                observer(content);
            }
            return item;
        });

    /// <summary>
    /// Observes standard error without changing or removing events.
    /// </summary>
    public static IAsyncEnumerable<ProcessEvent<T>> OnStandardError<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events, Action<T> observer)
        => Transform(events, item =>
        {
            if (item is ProcessEvent<T>.StandardError(var content))
            {
                observer(content);
            }
            return item;
        });

    /// <summary>
    /// Drains and removes standard output payloads, retaining lifecycle and EOF events.
    /// </summary>
    public static IAsyncEnumerable<ProcessEvent<T>> DiscardStandardOutput<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events)
        => Transform(events, static item => item is ProcessEvent<T>.StandardOutput ? null : item);

    /// <summary>
    /// Drains and removes standard error payloads, retaining lifecycle and EOF events.
    /// </summary>
    public static IAsyncEnumerable<ProcessEvent<T>> DiscardStandardError<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events)
        => Transform(events, static item => item is ProcessEvent<T>.StandardError ? null : item);

    private static async IAsyncEnumerable<ProcessEvent<T>> Transform<T>(
        IAsyncEnumerable<ProcessEvent<T>> events,
        Func<ProcessEvent<T>, ProcessEvent<T>?> transform,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        Exception? failure = null;
        try
        {
            while (true)
            {
                ProcessEvent<T>? item;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    item = transform(enumerator.Current);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }
                if (item is not null)
                {
                    yield return item;
                }
            }
        }
        finally
        {
            await DisposeAsync(enumerator, failure).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Decodes each output independently and flushes it at its own EOF.
    /// </summary>
    public static IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> DecodeText(
        this IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> events, Encoding encoding)
        => DecodeTextCore(events, encoding);

    private static async IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> DecodeTextCore(
        IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<byte>>> events,
        Encoding encoding,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var output = encoding.GetDecoder();
        var error = encoding.GetDecoder();
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        Exception? failure = null;
        try
        {
            while (true)
            {
                ProcessEvent<ReadOnlyMemory<char>>[] decoded;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    decoded = Decode(enumerator.Current).ToArray();
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }
                foreach (var item in decoded)
                {
                    yield return item;
                }
            }
        }
        finally
        {
            await DisposeAsync(enumerator, failure).ConfigureAwait(false);
        }

        IEnumerable<ProcessEvent<ReadOnlyMemory<char>>> Decode(ProcessEvent<ReadOnlyMemory<byte>> item)
        {
            switch (item)
            {
                case ProcessEvent<ReadOnlyMemory<byte>>.Started started:
                    yield return new ProcessEvent<ReadOnlyMemory<char>>.Started(started.ProcessId);
                    break;
                case ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput data:
                    if (GetChars(output, data.Content, flush: false) is { Length: > 0 } text)
                    {
                        yield return new ProcessEvent<ReadOnlyMemory<char>>.StandardOutput(text);
                    }
                    break;
                case ProcessEvent<ReadOnlyMemory<byte>>.StandardError data:
                    if (GetChars(error, data.Content, flush: false) is { Length: > 0 } errorText)
                    {
                        yield return new ProcessEvent<ReadOnlyMemory<char>>.StandardError(errorText);
                    }
                    break;
                case ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted:
                    if (GetChars(output, default, flush: true) is { Length: > 0 } finalText)
                    {
                        yield return new ProcessEvent<ReadOnlyMemory<char>>.StandardOutput(finalText);
                    }
                    yield return new ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted();
                    break;
                case ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted:
                    if (GetChars(error, default, flush: true) is { Length: > 0 } finalError)
                    {
                        yield return new ProcessEvent<ReadOnlyMemory<char>>.StandardError(finalError);
                    }
                    yield return new ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted();
                    break;
                case ProcessEvent<ReadOnlyMemory<byte>>.Exited exited:
                    yield return new ProcessEvent<ReadOnlyMemory<char>>.Exited(exited.ExitCode);
                    break;
            }
        }

        ReadOnlyMemory<char> GetChars(Decoder decoder, ReadOnlyMemory<byte> bytes, bool flush)
        {
            var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            var count = decoder.GetChars(bytes.Span, chars, flush);
            return chars.AsMemory(0, count);
        }
    }

    /// <summary>
    /// Reads CR, LF and CRLF lines, including empty lines and a final unterminated line at EOF.
    /// </summary>
    public static IAsyncEnumerable<ProcessEvent<string>> ReadLines(
        this IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> events)
        => ReadLinesCore(events);

    private static async IAsyncEnumerable<ProcessEvent<string>> ReadLinesCore(
        IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> events,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var output = new LineBuffer();
        var error = new LineBuffer();
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        Exception? failure = null;
        try
        {
            while (true)
            {
                ProcessEvent<string>[] lines;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }
                    lines = Read(enumerator.Current).ToArray();
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }
                foreach (var item in lines)
                {
                    yield return item;
                }
            }
        }
        finally
        {
            await DisposeAsync(enumerator, failure).ConfigureAwait(false);
        }

        IEnumerable<ProcessEvent<string>> Read(ProcessEvent<ReadOnlyMemory<char>> item)
        {
            switch (item)
            {
                case ProcessEvent<ReadOnlyMemory<char>>.Started started:
                    yield return new ProcessEvent<string>.Started(started.ProcessId);
                    break;
                case ProcessEvent<ReadOnlyMemory<char>>.StandardOutput data:
                    foreach (var line in output.Append(data.Content))
                    {
                        yield return new ProcessEvent<string>.StandardOutput(line);
                    }
                    break;
                case ProcessEvent<ReadOnlyMemory<char>>.StandardError data:
                    foreach (var line in error.Append(data.Content))
                    {
                        yield return new ProcessEvent<string>.StandardError(line);
                    }
                    break;
                case ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted:
                    if (output.Complete() is { } finalLine)
                    {
                        yield return new ProcessEvent<string>.StandardOutput(finalLine);
                    }
                    yield return new ProcessEvent<string>.StandardOutputCompleted();
                    break;
                case ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted:
                    if (error.Complete() is { } finalError)
                    {
                        yield return new ProcessEvent<string>.StandardError(finalError);
                    }
                    yield return new ProcessEvent<string>.StandardErrorCompleted();
                    break;
                case ProcessEvent<ReadOnlyMemory<char>>.Exited exited:
                    yield return new ProcessEvent<string>.Exited(exited.ExitCode);
                    break;
            }
        }
    }

    /// <summary>
    /// Drains events and awaits disposal before returning the known exit code, including nonzero exits.
    /// </summary>
    public static async Task<int> ExecuteAsync<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events,
        CancellationToken cancellationToken = default)
    {
        try
        {
            int? exitCode = null;
            var enumerator = events.GetAsyncEnumerator(cancellationToken);
            Exception? failure = null;
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    if (enumerator.Current is ProcessEvent<T>.Exited(var code))
                    {
                        exitCode = code;
                    }
                }
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                await DisposeAsync(enumerator, failure).ConfigureAwait(false);
            }
            return exitCode ?? throw new InvalidOperationException("The process event stream completed without an exit code.");
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Process execution was canceled.", exception, cancellationToken);
        }
    }

    /// <summary>
    /// Executes and captures exact decoded output, returning only after successful consumption and disposal.
    /// </summary>
    public static async Task<ProcessCaptureResult> ExecuteAndCaptureTextAsync(
        this IAsyncEnumerable<ProcessEvent<ReadOnlyMemory<char>>> events,
        CancellationToken cancellationToken = default)
    {
        var output = new StringBuilder();
        var error = new StringBuilder();
        var exitCode = await events
            .OnStandardOutput(text => output.Append(text.Span))
            .OnStandardError(text => error.Append(text.Span))
            .ExecuteAsync(cancellationToken).ConfigureAwait(false);
        return new ProcessCaptureResult(exitCode, output.ToString(), error.ToString());
    }

    // A transform owns both its projection failure and disposal. A plain await foreach
    // lets the latter replace the former before the transform can observe it.
    private static async ValueTask DisposeAsync(IAsyncDisposable enumerator, Exception? failure)
    {
        try
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure) when (failure is not null)
        {
            throw new AggregateException(failure, cleanupFailure);
        }
    }

    private sealed class LineBuffer
    {
        private readonly StringBuilder _line = new();
        private bool _afterCarriageReturn;

        public IEnumerable<string> Append(ReadOnlyMemory<char> text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                var value = text.Span[index];
                var skip = _afterCarriageReturn && value == '\n';
                _afterCarriageReturn = value == '\r';
                if (skip)
                {
                    continue;
                }
                if (value is '\r' or '\n')
                {
                    yield return _line.ToString();
                    _line.Clear();
                }
                else
                {
                    _line.Append(value);
                }
            }
        }

        public string? Complete() => _line.Length > 0 ? _line.ToString() : null;
    }
}
