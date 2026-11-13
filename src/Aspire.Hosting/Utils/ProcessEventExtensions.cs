// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text;

namespace Aspire.Hosting.Utils;

/// <summary>
/// Adapts one process event stream without taking over process or pipe ownership.
/// </summary>
internal static class ProcessEventExtensions
{
    /// <summary>
    /// Observes standard output without changing or removing events.
    /// </summary>
    /// <typeparam name="T">The event payload type.</typeparam>
    /// <param name="events">The deferred process events to observe.</param>
    /// <param name="observer">The callback invoked for each stdout payload during consumption.</param>
    /// <returns>A deferred stream that forwards the original events after observation.</returns>
    public static IAsyncEnumerable<ProcessEvent<T>> OnStandardOutput<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events, Action<T> observer)
        => Observe(events, observer, static (item, callback) =>
        {
            if (item is ProcessEvent<T>.StandardOutput(var content))
            {
                callback(content);
            }
        });

    /// <summary>
    /// Observes standard error without changing or removing events.
    /// </summary>
    /// <typeparam name="T">The event payload type.</typeparam>
    /// <param name="events">The deferred process events to observe.</param>
    /// <param name="observer">The callback invoked for each stderr payload during consumption.</param>
    /// <returns>A deferred stream that forwards the original events after observation.</returns>
    public static IAsyncEnumerable<ProcessEvent<T>> OnStandardError<T>(
        this IAsyncEnumerable<ProcessEvent<T>> events, Action<T> observer)
        => Observe(events, observer, static (item, callback) =>
        {
            if (item is ProcessEvent<T>.StandardError(var content))
            {
                callback(content);
            }
        });

    private static async IAsyncEnumerable<ProcessEvent<T>> Observe<T>(
        IAsyncEnumerable<ProcessEvent<T>> events,
        Action<T> observer,
        Action<ProcessEvent<T>, Action<T>> observe,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var enumerator = events.GetAsyncEnumerator(cancellationToken);
        Exception? failure = null;
        try
        {
            while (true)
            {
                ProcessEvent<T> item;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    item = enumerator.Current;
                    observe(item, observer);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                yield return item;
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
    /// <param name="events">The byte events to decode during consumption.</param>
    /// <param name="encoding">The encoding and fallback policy used by each stream's decoder.</param>
    /// <returns>A deferred stream of owned decoded character buffers and lifecycle events.</returns>
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
                (ProcessEvent<ReadOnlyMemory<char>>? Event, ProcessEvent<ReadOnlyMemory<char>>? Completion) decoded;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    // An input event produces at most a payload and its EOF. Decode eagerly
                    // inside the guard so a decoder failure survives upstream disposal.
                    decoded = DecodeEvent(enumerator.Current, output, error, encoding);
                }
                catch (Exception exception)
                {
                    failure = exception;
                    throw;
                }

                if (decoded.Event is { } content)
                {
                    yield return content;
                }

                if (decoded.Completion is { } completion)
                {
                    yield return completion;
                }
            }
        }
        finally
        {
            await DisposeAsync(enumerator, failure).ConfigureAwait(false);
        }
    }

    private static (ProcessEvent<ReadOnlyMemory<char>>? Event, ProcessEvent<ReadOnlyMemory<char>>? Completion) DecodeEvent(
        ProcessEvent<ReadOnlyMemory<byte>> item, Decoder output, Decoder error, Encoding encoding)
    {
        switch (item)
        {
            case ProcessEvent<ReadOnlyMemory<byte>>.Started started:
                return (new ProcessEvent<ReadOnlyMemory<char>>.Started(started.ProcessId), null);
            case ProcessEvent<ReadOnlyMemory<byte>>.StandardOutput data:
                var text = GetChars(output, encoding, data.Content, flush: false);
                return (text.IsEmpty ? null : new ProcessEvent<ReadOnlyMemory<char>>.StandardOutput(text), null);
            case ProcessEvent<ReadOnlyMemory<byte>>.StandardError data:
                var errorText = GetChars(error, encoding, data.Content, flush: false);
                return (errorText.IsEmpty ? null : new ProcessEvent<ReadOnlyMemory<char>>.StandardError(errorText), null);
            case ProcessEvent<ReadOnlyMemory<byte>>.StandardOutputCompleted:
                var finalText = GetChars(output, encoding, default, flush: true);
                return (finalText.IsEmpty ? null : new ProcessEvent<ReadOnlyMemory<char>>.StandardOutput(finalText),
                    new ProcessEvent<ReadOnlyMemory<char>>.StandardOutputCompleted());
            case ProcessEvent<ReadOnlyMemory<byte>>.StandardErrorCompleted:
                var finalError = GetChars(error, encoding, default, flush: true);
                return (finalError.IsEmpty ? null : new ProcessEvent<ReadOnlyMemory<char>>.StandardError(finalError),
                    new ProcessEvent<ReadOnlyMemory<char>>.StandardErrorCompleted());
            case ProcessEvent<ReadOnlyMemory<byte>>.Exited exited:
                return (new ProcessEvent<ReadOnlyMemory<char>>.Exited(exited.ExitCode), null);
            default:
                return (null, null);
        }
    }

    private static ReadOnlyMemory<char> GetChars(Decoder decoder, Encoding encoding, ReadOnlyMemory<byte> bytes, bool flush)
    {
        var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
        var count = decoder.GetChars(bytes.Span, chars, flush);
        return chars.AsMemory(0, count);
    }

    /// <summary>
    /// Drains events and awaits disposal before returning the known exit code, including nonzero exits.
    /// </summary>
    /// <typeparam name="T">The event payload type.</typeparam>
    /// <param name="events">The process events to consume through completion and disposal.</param>
    /// <param name="cancellationToken">The token used to cancel consumption and process execution.</param>
    /// <returns>The observed exit code after consumption and cleanup complete.</returns>
    /// <exception cref="InvalidOperationException">The stream completes without an exit code.</exception>
    /// <exception cref="OperationCanceledException">Consumption or execution is canceled.</exception>
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
    /// Captures exact decoded stdout and stderr, returning after consumption and disposal complete.
    /// </summary>
    /// <param name="events">The decoded process events to consume and capture.</param>
    /// <param name="cancellationToken">The token used to cancel consumption and process execution.</param>
    /// <returns>The known exit code and both complete output strings; nonzero exits are returned normally.</returns>
    /// <remarks>
    /// Buffers all decoded output without a size limit. Large output can require substantial memory.
    /// Failed or canceled consumption and failed cleanup throw instead of returning partial capture.
    /// </remarks>
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
}
