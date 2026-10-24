// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Aspire.Hosting.Dashboard;

// Tokens are stable across runs, but commands and interaction IDs belong to one execution. Fence
// requests from an old dashboard attachment before they can execute against the next AppHost.
internal sealed class AppHostRunInterceptor(DashboardRunState runState) : Interceptor
{
    private void Validate(ServerCallContext context)
    {
        var runId = context.RequestHeaders.GetValue("x-aspire-apphost-run-id");
        if (runId is not null && runId != runState.RunId)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "The AppHost run has changed."));
        }
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Validate(context);
        return continuation(request, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, ServerCallContext context, ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Validate(context);
        return continuation(requestStream, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Validate(context);
        return continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream, ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Validate(context);
        return continuation(requestStream, responseStream, context);
    }
}
