// <copyright file="ChaosProxyAzureResourceBuilderExtensions.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// Azure-SDK-shaped fault transforms for the chaos proxy. Each extension emits a
/// response with the exact status code + headers + body shape that the corresponding
/// Azure SDK retry policy expects.
/// </summary>
[Experimental("ASPIRECHAOS001", UrlFormat = "https://aka.ms/aspire-chaos-proxy/experimental/{0}")]
public static class ChaosProxyAzureResourceBuilderExtensions
{
    /// <summary>
    /// Injects a Cosmos DB 429 RU-throttle response when the transform fires. The
    /// response carries the <c>x-ms-retry-after-ms</c> header that CosmosClient reads to
    /// determine how long to wait before retrying.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="retryAfterMs">Milliseconds the SDK should wait before retrying. Cosmos SDK default retry policy honors this exactly; without the header it falls back to ~5s default.</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting the throttle on each matching request. Defaults to <c>null</c> (use <paramref name="failFirst"/> default). Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject the throttle on the first N occurrences per logical request key, then forward subsequent occurrences. Defaults to <c>1</c> when neither parameter is specified - the canonical retry-validation shape per D13.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// Default is <c>failFirst: 1</c> because the primary use case is "validate the SDK
    /// recovers via retry". A different default would force every retry to also fail,
    /// which validates "client gives up after N retries" instead - rarely what you want.
    /// Safe failFirst max for Cosmos default retry policy is 8 (SDK retries 9 times).
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithCosmosThrottle(
        this IResourceBuilder<ChaosProxyResource> builder,
        int retryAfterMs,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (retryAfterMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterMs), retryAfterMs, "retryAfterMs must be non-negative.");
        }

        var resolvedFailFirst = probability.HasValue ? failFirst : (failFirst ?? 1);

        return builder.WithError(
            httpStatus: 429,
            body: BuildCosmosThrottleBody(retryAfterMs),
            contentType: "application/json",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-ms-retry-after-ms"] = retryAfterMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["x-ms-substatus"] = "3200",
            },
            probability: probability,
            failFirst: resolvedFailFirst);
    }

    /// <summary>
    /// Injects an Azure Storage 503 ServerBusy response when the transform fires. The
    /// response carries the <c>x-ms-error-code: ServerBusy</c> header that the Storage
    /// SDK's <c>StorageResponseClassifier</c> classifies as retriable.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting the 503 on each matching request. Defaults to <c>null</c> (use <paramref name="failFirst"/> default). Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject the 503 on the first N occurrences per logical request key, then forward subsequent occurrences. Defaults to <c>1</c> when neither parameter is specified.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// Safe failFirst max for Azure Storage default retry policy is 4 (SDK retries 5 times).
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithStorageServerBusy(
        this IResourceBuilder<ChaosProxyResource> builder,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolvedFailFirst = probability.HasValue ? failFirst : (failFirst ?? 1);

        return builder.WithError(
            httpStatus: 503,
            body: "<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>ServerBusy</Code><Message>The server is busy.</Message></Error>",
            contentType: "application/xml",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-ms-error-code"] = "ServerBusy",
            },
            probability: probability,
            failFirst: resolvedFailFirst);
    }

    /// <summary>
    /// Injects an Azure Storage 412 PreconditionFailed response when the transform
    /// fires - the response Storage returns when an ETag-conditional request (e.g.,
    /// <c>If-Match</c>) doesn't match the current resource state. Exercises the
    /// optimistic-concurrency retry/handle code path in clients.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting the 412. Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject on the first N occurrences per logical request key. Defaults to <c>1</c> when neither parameter is specified.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// 412 is NOT classified as retriable by the Storage SDK - this transform is for
    /// exercising application-level concurrency-conflict handlers (read-modify-write
    /// loops, ETag-aware update logic), not SDK retry policies.
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithStorageEtagMismatch(
        this IResourceBuilder<ChaosProxyResource> builder,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolvedFailFirst = probability.HasValue ? failFirst : (failFirst ?? 1);

        return builder.WithError(
            httpStatus: 412,
            body: "<?xml version=\"1.0\" encoding=\"utf-8\"?><Error><Code>ConditionNotMet</Code><Message>The condition specified using HTTP conditional header(s) is not met.</Message></Error>",
            contentType: "application/xml",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-ms-error-code"] = "ConditionNotMet",
            },
            probability: probability,
            failFirst: resolvedFailFirst);
    }

    /// <summary>
    /// Injects a Cosmos DB 449 Conflict response when the transform fires - the response
    /// Cosmos returns when an optimistic-concurrency conditional document update fails
    /// (item was modified since the requesting client last read it). Forces the SDK's
    /// concurrency-retry code path.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting the 449. Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject on the first N occurrences per logical request key. Defaults to <c>1</c> when neither parameter is specified.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// CosmosClient direct-mode retries 449 internally with exponential backoff
    /// (10ms x 2 + 5ms salt, max 1s per attempt, max 30s total). Gateway mode
    /// surfaces 449 to the caller as a CosmosException with SubStatusCode unset
    /// for application-level retry. Safe failFirst max 8 within the gateway retry
    /// budget; for direct-mode retry-validation use failFirst:1 only.
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithCosmosConcurrencyConflict(
        this IResourceBuilder<ChaosProxyResource> builder,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolvedFailFirst = probability.HasValue ? failFirst : (failFirst ?? 1);

        return builder.WithError(
            httpStatus: 449,
            body: "{\"code\":\"Conflict\",\"message\":\"The request failed because of a conflict with the current state of the resource.\"}",
            contentType: "application/json",
            probability: probability,
            failFirst: resolvedFailFirst);
    }

    /// <summary>
    /// Injects a Cosmos DB 412 PreconditionFailed response when the transform fires - the
    /// response Cosmos returns when an ETag-conditional write (e.g.,
    /// <c>UpsertItemAsync</c> with <c>ItemRequestOptions.IfMatchEtag</c>) loses an
    /// optimistic-concurrency race: the stored item's ETag no longer matches the
    /// <c>If-Match</c> the client sent. Exercises the application-level
    /// concurrent-modification handler (the read-modify-write loser path).
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting the 412. Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject on the first N occurrences per logical request key. Defaults to <c>1</c> when neither parameter is specified.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// 412 is NOT in CosmosClient's retriable set - unlike 429 (throttle) or 449
    /// (RetryWith), the SDK surfaces 412 straight to the caller as a
    /// <c>CosmosException</c> with <c>StatusCode = HttpStatusCode.PreconditionFailed</c>.
    /// This transform is for exercising application-level optimistic-concurrency
    /// handling (does the app translate the 412 into the right customer-facing
    /// response — e.g. ARM 409 Conflict — or leak a 500?), not SDK retry policies.
    /// </para>
    /// <para>
    /// Cosmos's 412 carries substatus <c>0</c> and the body's <c>code</c> is
    /// <c>PreconditionFailed</c>; the SDK keys off the HTTP status, not the body, so
    /// the body shape is informational. Default <c>failFirst: 1</c> reproduces the
    /// canonical "first write loses the race, retry/handle should recover" pattern.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithCosmosPreconditionFailed(
        this IResourceBuilder<ChaosProxyResource> builder,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolvedFailFirst = probability.HasValue ? failFirst : (failFirst ?? 1);

        return builder.WithError(
            httpStatus: 412,
            body: "{\"code\":\"PreconditionFailed\",\"message\":\"Operation cannot be performed because one of the specified precondition is not met.\"}",
            contentType: "application/json",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-ms-substatus"] = "0",
            },
            probability: probability,
            failFirst: resolvedFailFirst);
    }

    /// <summary>
    /// Injects a Cosmos DB 503 ServiceUnavailable response when the transform fires.
    /// On multi-region accounts the SDK will failover to the next preferred region
    /// when it sees 503; on single-region accounts the SDK retries up to its budget
    /// (~30s direct mode) before surfacing.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting the 503. Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject on the first N occurrences per logical request key. Defaults to <c>1</c> when neither parameter is specified.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    public static IResourceBuilder<ChaosProxyResource> WithCosmosServiceUnavailable(
        this IResourceBuilder<ChaosProxyResource> builder,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var resolvedFailFirst = probability.HasValue ? failFirst : (failFirst ?? 1);

        return builder.WithError(
            httpStatus: 503,
            body: "{\"code\":\"ServiceUnavailable\",\"message\":\"The service is currently unavailable. Retry the request.\"}",
            contentType: "application/json",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // 0 means generic ServiceUnavailable (not a specific known sub-state).
                // Real Cosmos uses non-zero substatus values for specific failure modes
                // (e.g., 21008 partition-key-out-of-range); we don't model those here.
                ["x-ms-substatus"] = "0",
            },
            probability: probability,
            failFirst: resolvedFailFirst);
    }

    /// <summary>
    /// Injects a Key Vault 429 throttle response when the transform fires. The response
    /// carries the standard <c>Retry-After</c> header (in seconds) that the Key Vault
    /// SDK reads to determine how long to wait before retrying.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="retryAfterSeconds">Seconds the SDK should wait before retrying. KV SDK default retry policy honors this; without the header it falls back to its exponential backoff (1s -> 2s -> 4s -> 8s -> 16s).</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting the throttle on each matching request. Defaults to <c>null</c> (use <paramref name="failFirst"/> default). Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject the throttle on the first N occurrences per logical request key, then forward subsequent occurrences. Defaults to <c>1</c> when neither parameter is specified.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// Safe failFirst max for Key Vault SDK is 4 (recommended 5 retries).
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithKeyVaultThrottle(
        this IResourceBuilder<ChaosProxyResource> builder,
        int retryAfterSeconds,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (retryAfterSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfterSeconds), retryAfterSeconds, "retryAfterSeconds must be non-negative.");
        }

        var resolvedFailFirst = probability.HasValue ? failFirst : (failFirst ?? 1);

        return builder.WithError(
            httpStatus: 429,
            body: "{\"error\":{\"code\":\"Throttled\",\"message\":\"Throttled. Please retry after the suggested delay.\"}}",
            contentType: "application/json",
            headers: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            probability: probability,
            failFirst: resolvedFailFirst);
    }

    private static string BuildCosmosThrottleBody(int retryAfterMs)
    {
        // Cosmos response body shape: SDK doesn't strictly require this but the documented
        // throttle body helps anyone inspecting the proxy traffic understand what fired.
        return $"{{\"code\":\"TooManyRequests\",\"message\":\"Request rate is large. More Request Units may be needed; retryAfterMilliseconds={retryAfterMs}\",\"retryAfterMilliseconds\":{retryAfterMs}}}";
    }
}
