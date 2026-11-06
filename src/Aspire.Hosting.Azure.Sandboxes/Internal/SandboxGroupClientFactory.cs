// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Microsoft.Azure.ContainerApps.Sandbox;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Identifies the sandbox group that data-plane operations are issued against.
/// </summary>
internal sealed record SandboxDataPlaneScope(string SubscriptionId, string ResourceGroupName, string SandboxGroupName, string Region);

internal static class SandboxGroupClientFactory
{
    internal static SandboxGroupClient Create(
        SandboxDataPlaneScope scope,
        TokenCredential credential,
        ILogger logger,
        Action<SandboxGroupClientOptions>? configureOptions = null)
    {
        var options = new SandboxGroupClientOptions
        {
            RetryPolicy = new SandboxRetryPolicy(logger)
        };
        options.AddPolicy(new ErrorBodyRedactionPolicy(), HttpPipelinePosition.PerRetry);
        configureOptions?.Invoke(options);

        return new SandboxGroupClient(
            scope.Region,
            scope.SubscriptionId,
            scope.ResourceGroupName,
            scope.SandboxGroupName,
            credential,
            options);
    }

    /// <summary>
    /// Determines whether a failed create call might have been committed by the service even though
    /// the caller never saw a successful response.
    /// </summary>
    internal static bool MayHaveCreatedResource(Exception exception)
    {
        return exception switch
        {
            // Status 0 means the transport failed before a response was received (the SDK wraps
            // network failures this way). 5xx responses can be returned after the service accepted the create.
            // 4xx responses mean the service rejected the request.
            RequestFailedException requestFailed => requestFailed.Status is 0 or >= 500,
            SandboxCreateResponseException or HttpRequestException or IOException or JsonException or TimeoutException or OperationCanceledException => true,
            _ => false
        };
    }
}

/// <summary>
/// Thrown when a create call succeeded but the response could not be used, so the resource may exist without the caller knowing its ID.
/// </summary>
internal sealed class SandboxCreateResponseException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

/// <summary>
/// Retry policy tuned for sandbox group data-plane calls.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The data-plane role assignment created during provisioning can take a while to propagate, so HTTP 403
/// responses are retried for a bounded period.</item>
/// <item>Creates are not idempotent. A network failure or 5xx response on anything other than GET/DELETE is not retried
/// so that the caller can reconcile by deployment labels rather than risk creating a duplicate resource.</item>
/// </list>
/// </remarks>
internal sealed class SandboxRetryPolicy(ILogger logger, TimeSpan? forbiddenRetryDelay = null, TimeSpan? retryDelay = null)
    : RetryPolicy(
        MaxRetryCount,
        new SandboxDelayStrategy(retryDelay ?? s_defaultRetryDelay, forbiddenRetryDelay ?? s_defaultForbiddenRetryDelay))
{
    private const int MaxRetryCount = 6;
    private const int MaxForbiddenRetryCount = 20;
    private static readonly TimeSpan s_defaultRetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_defaultForbiddenRetryDelay = TimeSpan.FromSeconds(30);

    private const string ForbiddenRetryCountPropertyName = "Aspire.Sandboxes.ForbiddenRetryCount";

    protected override bool ShouldRetry(HttpMessage message, Exception? exception)
    {
        if (exception is null && message.HasResponse && message.Response.Status == 403)
        {
            // HttpMessage does not expose the attempt number, so track forbidden retries on the message.
            var retryCount = message.TryGetProperty(ForbiddenRetryCountPropertyName, out var value) && value is int count ? count : 0;
            if (retryCount >= MaxForbiddenRetryCount)
            {
                return false;
            }

            message.SetProperty(ForbiddenRetryCountPropertyName, retryCount + 1);
            logger.LogWarning(
                "Sandbox request {Method} {Path} returned HTTP 403. Waiting for the Container Apps SandboxGroup Data Owner role assignment to propagate (retry {RetryAttempt} of {MaxRetryAttempts}). If this persists, verify the role assignment on the sandbox group.",
                message.Request.Method,
                message.Request.Uri.Path,
                retryCount + 1,
                MaxForbiddenRetryCount);
            return true;
        }

        var isTransientFailure = exception is not null ||
            (message.HasResponse && message.Response.Status >= 500);
        if (isTransientFailure && !IsGetOrDelete(message.Request.Method))
        {
            return false;
        }

        return base.ShouldRetry(message, exception);
    }

    // The async pipeline calls ShouldRetryAsync, whose default implementation does not delegate to ShouldRetry.
    protected override ValueTask<bool> ShouldRetryAsync(HttpMessage message, Exception? exception) =>
        new(ShouldRetry(message, exception));

    private static bool IsGetOrDelete(RequestMethod method) =>
        method == RequestMethod.Get ||
        method == RequestMethod.Delete;

    // Retry-After headers are honored by the base DelayStrategy and capped at the maximum delay.
    private sealed class SandboxDelayStrategy(TimeSpan retryDelay, TimeSpan forbiddenRetryDelay)
        : DelayStrategy(maxDelay: TimeSpan.FromSeconds(30), jitterFactor: 0)
    {
        protected override TimeSpan GetNextDelayCore(Response? response, int retryNumber) =>
            response?.Status == 403 ? forbiddenRetryDelay : retryDelay;
    }
}

/// <summary>
/// Removes error response bodies before the SDK builds a <see cref="RequestFailedException"/>.
/// </summary>
/// <remarks>
/// Service problem details can echo request content such as environment variable values, which can contain secrets.
/// Dropping the body keeps those values out of exception messages and logs; the status code and reason phrase remain.
/// </remarks>
internal sealed class ErrorBodyRedactionPolicy : HttpPipelinePolicy
{
    public override void Process(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        ProcessNext(message, pipeline);
        Redact(message);
    }

    public override async ValueTask ProcessAsync(HttpMessage message, ReadOnlyMemory<HttpPipelinePolicy> pipeline)
    {
        await ProcessNextAsync(message, pipeline).ConfigureAwait(false);
        Redact(message);
    }

    private static void Redact(HttpMessage message)
    {
        if (message.HasResponse && message.Response.Status >= 400)
        {
            message.Response.ContentStream = null;
        }
    }
}
