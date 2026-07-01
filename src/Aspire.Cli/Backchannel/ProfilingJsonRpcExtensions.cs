// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Telemetry;
using CurlyRpc;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Adds profiling spans and trace-context metadata around JSON-RPC calls.
/// </summary>
/// <remarks>
/// The JSON-RPC connection carries W3C traceparent/tracestate via an explicit
/// <see cref="BackchannelTraceContext"/> on the request object. These helpers wrap client calls
/// and inject extra trace metadata, such as baggage, into request-object RPC parameters.
/// </remarks>
internal static class ProfilingJsonRpcExtensions
{
    /// <summary>
    /// Controls when the client span for a streaming RPC call ends.
    /// </summary>
    internal enum StreamingSpanLifetime
    {
        /// <summary>
        /// The span stays open for the entire enumeration and is disposed when the stream
        /// completes (or faults). Use when the span is meant to represent the full streaming
        /// lifetime - for example, a backchannel that streams resource updates for as long
        /// as the AppHost runs.
        /// </summary>
        Enumeration,

        /// <summary>
        /// The span ends when the first stream item arrives. Use for setup-style RPCs where
        /// the meaningful work is producing the first response and the rest of the stream is
        /// long-lived but not interesting for timing (otherwise it dominates duration views).
        /// </summary>
        FirstItem
    }

    public static async Task InvokeWithProfilingAsync(
        this JsonRpc rpc,
        ProfilingTelemetry? profilingTelemetry,
        string connectionName,
        string methodName,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        using var activity = profilingTelemetry?.StartJsonRpcClientCall(connectionName, methodName, streaming: false) ?? default;
        arguments = WithTraceContext(arguments, activity.CreateBackchannelTraceContext());

        try
        {
            await rpc.InvokeAsync(methodName, arguments, cancellationToken).ConfigureAwait(false);
            activity.AddJsonRpcResponseReceivedEvent();
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    public static async Task<T> InvokeWithProfilingAsync<T>(
        this JsonRpc rpc,
        ProfilingTelemetry? profilingTelemetry,
        string connectionName,
        string methodName,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        using var activity = profilingTelemetry?.StartJsonRpcClientCall(connectionName, methodName, streaming: false) ?? default;
        arguments = WithTraceContext(arguments, activity.CreateBackchannelTraceContext());

        try
        {
            var response = await rpc.InvokeAsync<T>(methodName, arguments, cancellationToken).ConfigureAwait(false);
            activity.AddJsonRpcResponseReceivedEvent();
            return response!;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    public static Task<IAsyncEnumerable<T>> InvokeStreamingWithProfilingAsync<T>(
        this JsonRpc rpc,
        ProfilingTelemetry? profilingTelemetry,
        string connectionName,
        string methodName,
        object?[] arguments,
        CancellationToken cancellationToken,
        StreamingSpanLifetime spanLifetime = StreamingSpanLifetime.Enumeration)
    {
        // Do not use `using` here: for a non-null response, activity ownership
        // transfers to the returned enumerable. If a caller obtains the enumerable
        // but never enumerates it, EnumerateWithProfiling will not dispose the activity.
        var activity = profilingTelemetry?.StartJsonRpcClientCall(connectionName, methodName, streaming: true) ?? default;
        arguments = WithTraceContext(arguments, activity.CreateBackchannelTraceContext());

        try
        {
            // CurlyRpc streams plain IAsyncEnumerable<T> lazily: InvokeAsyncEnumerable returns the
            // enumerable without a blocking round-trip, so request items are pulled during enumeration.
            // Unlike the non-streaming overloads (which await a real response before emitting it), there is
            // no response to await here, so we deliberately do NOT emit a "response received" event: it would
            // only carry the request-sent timestamp and make streaming telemetry misleading. The meaningful
            // first-response marker is AddJsonRpcStreamFirstItemEvent, emitted from EnumerateWithProfiling
            // when the first item actually arrives over the wire.
            var response = rpc.InvokeAsyncEnumerable<T>(methodName, arguments, cancellationToken);

            return Task.FromResult(EnumerateWithProfiling(response, activity, spanLifetime, cancellationToken));
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            activity.Dispose();
            throw;
        }
    }

    private static async IAsyncEnumerable<T> EnumerateWithProfiling<T>(
        IAsyncEnumerable<T> response,
        ProfilingTelemetry.ActivityScope activity,
        StreamingSpanLifetime spanLifetime,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // CurlyRpc returns the IAsyncEnumerable before any stream items are read. Long-lived
        // startup streams can outlive readiness and dominate duration views, so callers that only
        // need setup timing can stop the client span as soon as the first item arrives.
        var itemCount = 0;
        var activityDisposed = false;
        var enumerator = response.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                T item;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        break;
                    }

                    item = enumerator.Current;
                }
                catch (Exception ex)
                {
                    if (!activityDisposed)
                    {
                        activity.SetError(ex);
                    }

                    throw;
                }

                if (itemCount == 0)
                {
                    activity.AddJsonRpcStreamFirstItemEvent();
                    if (spanLifetime == StreamingSpanLifetime.FirstItem)
                    {
                        activity.Dispose();
                        activityDisposed = true;
                    }
                }

                itemCount++;
                yield return item;
            }

            if (!activityDisposed)
            {
                activity.AddJsonRpcStreamCompletedEvent();
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            if (!activityDisposed)
            {
                activity.SetJsonRpcStreamItemCount(itemCount);
                activity.Dispose();
            }
        }
    }

    private static object?[] WithTraceContext(object?[] arguments, BackchannelTraceContext? traceContext)
    {
        if (traceContext is null || arguments.Length != 1)
        {
            return arguments;
        }

        // CurlyRpc accepts RPC parameters as an object array. The auxiliary backchannel
        // contract uses a single request object parameter, so replace that one argument with
        // a copy carrying trace metadata instead of mutating the caller's instance.
        return arguments[0] is BackchannelRequest request
            ? [request.WithTraceContext(traceContext)]
            : arguments;
    }

}
