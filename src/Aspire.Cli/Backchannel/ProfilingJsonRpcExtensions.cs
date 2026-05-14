// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Aspire.Cli.Telemetry;
using StreamJsonRpc;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Adds profiling spans and trace-context metadata around StreamJsonRpc calls.
/// </summary>
/// <remarks>
/// The JSON-RPC connection carries W3C traceparent/tracestate via StreamJsonRpc's request
/// envelope support. These helpers wrap client calls and inject extra trace metadata, such as
/// baggage, into request-object RPC parameters.
/// </remarks>
internal static class ProfilingJsonRpcExtensions
{
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
            await rpc.InvokeWithCancellationAsync(methodName, arguments, cancellationToken).ConfigureAwait(false);
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
            var response = await rpc.InvokeWithCancellationAsync<T>(methodName, arguments, cancellationToken).ConfigureAwait(false);
            activity.AddJsonRpcResponseReceivedEvent();
            return response;
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    public static async Task<IAsyncEnumerable<T>?> InvokeStreamingWithProfilingAsync<T>(
        this JsonRpc rpc,
        ProfilingTelemetry? profilingTelemetry,
        string connectionName,
        string methodName,
        object?[] arguments,
        CancellationToken cancellationToken)
    {
        // Do not use `using` here: for a non-null response, activity ownership
        // transfers to the returned enumerable. If a caller obtains the enumerable
        // but never enumerates it, EnumerateWithProfiling will not dispose the activity.
        var activity = profilingTelemetry?.StartJsonRpcClientCall(connectionName, methodName, streaming: true) ?? default;
        arguments = WithTraceContext(arguments, activity.CreateBackchannelTraceContext());

        try
        {
            var response = await rpc.InvokeWithCancellationAsync<IAsyncEnumerable<T>>(methodName, arguments, cancellationToken).ConfigureAwait(false);
            activity.AddJsonRpcResponseReceivedEvent();
            if (response is null)
            {
                activity.Dispose();
                return null;
            }

            return EnumerateWithProfiling(response, activity, cancellationToken);
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // StreamJsonRpc returns the IAsyncEnumerable before any stream items are read.
        // Keep the client span alive through enumeration so the measured duration includes
        // the server producing items, transport time, and caller-side consumption.
        var itemCount = 0;
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
                    activity.SetError(ex);
                    throw;
                }

                if (itemCount == 0)
                {
                    activity.AddJsonRpcStreamFirstItemEvent();
                }

                itemCount++;
                yield return item;
            }

            activity.AddJsonRpcStreamCompletedEvent();
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            activity.SetJsonRpcStreamItemCount(itemCount);
            activity.Dispose();
        }
    }

    private static object?[] WithTraceContext(object?[] arguments, BackchannelTraceContext? traceContext)
    {
        if (traceContext is null || arguments.Length != 1)
        {
            return arguments;
        }

        // StreamJsonRpc accepts RPC parameters as an object array. The auxiliary backchannel
        // contract uses a single request object parameter, so replace that one argument with
        // a copy carrying trace metadata instead of mutating the caller's instance.
        return arguments[0] is BackchannelRequest request
            ? [request.WithTraceContext(traceContext)]
            : arguments;
    }

}
