// <copyright file="ActivePolicy.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace ChaosProxy.Container.Policy;

/// <summary>
/// A single installed chaos policy: an optional request matcher + one or more transform
/// configurations + lifecycle metadata. Multiple policies coexist in the
/// <see cref="ActivePolicyStore"/>; per D12 first-installed-wins on matcher overlap
/// per transform type.
/// </summary>
/// <param name="Id">Unique identifier. Bootstrap policy from env vars uses <c>"bootstrap"</c>; runtime-installed policies use a GUID by default or a caller-supplied id.</param>
/// <param name="Matcher">Optional request matcher; null = match all (non-/chaos/*) requests.</param>
/// <param name="Latency">Latency injection config; null = this policy doesn't inject latency.</param>
/// <param name="Error">Error short-circuit config; null = this policy doesn't error.</param>
/// <param name="ReplayDuplicate">Replay-duplicate config; null = this policy doesn't replay.</param>
/// <param name="ExpiresAt">Absolute UTC timestamp at which the policy is auto-removed by <see cref="PolicyExpirationService"/>. Null = never expires (use for bootstrap policy).</param>
internal sealed record ActivePolicy(
    string Id,
    RequestMatcher? Matcher,
    LatencyConfig? Latency,
    ErrorConfig? Error,
    ReplayDuplicateConfig? ReplayDuplicate,
    DropResponseConfig? DropResponse,
    RateLimitConfig? RateLimit,
    HeaderTamperConfig? HeaderTamper,
    PartialResponseConfig? PartialResponse,
    IdempotencyCollisionConfig? IdempotencyCollision,
    SlowResponseConfig? SlowResponse,
    DateTimeOffset? ExpiresAt,
    ForwardThenFailConfig? ForwardThenFail = null,
    RandomFaultConfig? RandomFault = null);

/// <summary>
/// Resource-aware random chaos: instead of a single fixed transform, fires a fault
/// sampled (weighted, seeded) from a <c>FaultProfile</c> matching the target resource
/// type. Per matching request the runtime rolls <see cref="Intensity"/> against the
/// policy's seeded RNG; on a hit it samples one profile entry and materializes it into
/// an error / latency / drop effect. Seeded so a failing validation run is reproducible
/// (D21).
/// </summary>
/// <param name="ProfileId">Id of the fault profile to sample (e.g. <c>service.http</c>, <c>azure.cosmos</c>). Unknown ids fall back to the generic profile.</param>
/// <param name="Intensity">Per-request fire probability in [0, 1]. 0 = never, 1 = every matching request gets a sampled fault.</param>
/// <param name="Seed">RNG seed. A fixed seed makes the fault sequence reproducible across runs.</param>
/// <param name="MaxFires">Optional global cap on total fires across all request keys (blast-radius control). Null = no cap.</param>
/// <param name="ExcludePaths">Request path prefixes that random chaos must never fault (e.g. health/readiness). Case-insensitive prefix match.</param>
internal sealed record RandomFaultConfig(
    string ProfileId,
    double Intensity,
    int Seed,
    int? MaxFires,
    IReadOnlyList<string>? ExcludePaths);

/// <summary>
/// One fault that resource-aware random chaos actually fired, captured for
/// <c>/chaos/freeze</c> so the random run can be replayed deterministically.
/// </summary>
/// <param name="PolicyId">The random policy that fired.</param>
/// <param name="Method">Request HTTP method.</param>
/// <param name="Path">Request path the fault fired on.</param>
/// <param name="Kind"><c>error</c>, <c>latency</c>, or <c>drop</c>.</param>
/// <param name="Status">HTTP status for an <c>error</c> fault; null otherwise.</param>
/// <param name="DelayMs">Delay for a <c>latency</c> fault; null otherwise.</param>
internal sealed record FrozenFault(
    string PolicyId,
    string Method,
    string Path,
    string Kind,
    int? Status,
    int? DelayMs);

/// <summary>
/// Request matcher scoping the chaos transforms on a policy. All non-null fields must
/// match the incoming request for the policy's transforms to fire. PathPrefix uses plain
/// case-insensitive string prefix (so <c>/test-</c> matches <c>/test-foo</c>); PathContains
/// uses case-insensitive substring search. HeaderEquals / HeaderContains compare against
/// the FIRST value of each header (case-insensitive name lookup) and require ALL listed
/// header constraints to satisfy. BodyContains looks for a case-insensitive substring in
/// the buffered request body (see <see cref="ChaosRequestBodyBufferingMiddleware"/>).
/// </summary>
internal sealed record RequestMatcher(
    string? Method,
    string? PathPrefix,
    string? PathContains,
    IReadOnlyDictionary<string, string>? HeaderEquals = null,
    IReadOnlyDictionary<string, string>? HeaderContains = null,
    string? BodyContains = null,
    string? DtfxActivityName = null)
{
    /// <summary>HttpContext.Items key holding the buffered request body (string).</summary>
    public const string BufferedBodyItemsKey = "chaos.request.bufferedBody";

    /// <summary>
    /// HttpContext.Items key holding the parsed DTFx message (if the body was
    /// a recognizable DTFx queue envelope, else null). Populated by the buffering
    /// middleware. Consumed by DtfxActivityName matching here in Matches().
    /// </summary>
    public const string DtfxParsedMessageItemsKey = "chaos.request.dtfxMessage";

    public bool Matches(HttpRequest request)
    {
        if (Method is not null && !string.Equals(request.Method, Method, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pathValue = request.Path.Value ?? string.Empty;

        if (PathPrefix is not null && !pathValue.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (PathContains is not null && pathValue.IndexOf(PathContains, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (HeaderEquals is not null)
        {
            foreach (var kv in HeaderEquals)
            {
                if (!request.Headers.TryGetValue(kv.Key, out var values) || values.Count == 0 || !string.Equals(values[0], kv.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        if (HeaderContains is not null)
        {
            foreach (var kv in HeaderContains)
            {
                if (!request.Headers.TryGetValue(kv.Key, out var values) || values.Count == 0 || (values[0] ?? string.Empty).IndexOf(kv.Value, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return false;
                }
            }
        }

        if (BodyContains is not null)
        {
            // The body must have been pre-buffered by ChaosRequestBodyBufferingMiddleware.
            // If it isn't in Items (buffering middleware not registered, or body was over
            // the cap), treat as non-matching — never silently degrade to "match anything".
            if (!request.HttpContext.Items.TryGetValue(BufferedBodyItemsKey, out var bodyObj) || bodyObj is not string body)
            {
                return false;
            }

            if (body.IndexOf(BodyContains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }
        }

        if (DtfxActivityName is not null)
        {
            // The buffering middleware should have parsed the body and stashed the
            // DtfxMessage (if any) under DtfxParsedMessageItemsKey. We only fire on
            // TaskCompletedEvent messages whose schedule-side correlation (recorded
            // earlier) matches the target activity name. The store lookup needs an
            // ActivePolicyStore — pulled from the request services.
            if (!request.HttpContext.Items.TryGetValue(DtfxParsedMessageItemsKey, out var dtfxObj) || dtfxObj is not DtfxMessageParser.DtfxMessage dtfx)
            {
                return false;
            }

            if (dtfx.Kind != DtfxMessageParser.DtfxEventKind.TaskCompleted || dtfx.InstanceId is null || dtfx.TaskScheduledId is null)
            {
                return false;
            }

            var store = request.HttpContext.RequestServices.GetService(typeof(ActivePolicyStore)) as ActivePolicyStore;
            if (store is null)
            {
                return false;
            }

            var recordedName = store.LookupDtfxActivityName(dtfx.InstanceId, dtfx.TaskScheduledId.Value);
            // Case-sensitive comparison: DTFx activity names ARE case-sensitive in
            // the framework (the dispatcher looks them up via a CLR Type/Name table).
            // Customers should specify the exact name they registered with DTFx.
            if (!string.Equals(recordedName, DtfxActivityName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record LatencyConfig(int MinMs, int MaxMs, double Probability, int? FailFirst);

internal sealed record ErrorConfig(int Status, string? Body, string? ContentType, IReadOnlyDictionary<string, string>? Headers, double Probability, int? FailFirst);

internal sealed record ReplayDuplicateConfig(double Probability, int? FailFirst);

/// <summary>
/// Drops the response on the floor instead of writing it to the wire. Pipeline stops at
/// this middleware: upstream is never called and the client sees a connection reset / read
/// timeout (depending on its HttpClient timeout configuration). Useful for exercising
/// client-side timeout + retry policies and hung-request handling.
/// </summary>
internal sealed record DropResponseConfig(double Probability, int? FailFirst, int? MaxFires = null);

/// <summary>
/// Rate-limit a path with a sliding window. Once the per-(policy,request-key) window
/// has admitted <see cref="RequestsPerWindow"/> requests, subsequent requests within
/// the window short-circuit with the configured <see cref="Status"/> and optional
/// <see cref="Headers"/> (e.g., <c>Retry-After</c>). The window slides forward on
/// every request so behavior is steady-state token-bucket-ish.
/// </summary>
internal sealed record RateLimitConfig(
    int RequestsPerWindow,
    int WindowMs,
    int Status,
    IReadOnlyDictionary<string, string>? Headers);

/// <summary>
/// Direction the <see cref="HeaderTamperConfig"/> targets. Request modifies the
/// inbound request headers before they reach upstream; Response modifies the headers
/// flowing back to the client; Both applies to both.
/// </summary>
internal enum HeaderTamperDirection
{
    Request,
    Response,
    Both,
}

/// <summary>
/// Tampers with request and/or response headers as they flow through the proxy.
/// Use cases: simulate missing auth headers (Remove), inject malformed values
/// (Set with bad data), test header-conditional client logic (Add a new header).
///
/// <para>
/// Operation order on each direction: Remove first, then Set (overwrite existing or
/// add if absent), then Add (always append, even if header already exists). Note that
/// "Set" replaces the entire value collection for that header; "Add" appends another
/// value to whatever's already there (multi-valued headers).
/// </para>
/// </summary>
internal sealed record HeaderTamperConfig(
    HeaderTamperDirection Direction,
    IReadOnlyList<string>? Remove,
    IReadOnlyDictionary<string, string>? Set,
    IReadOnlyDictionary<string, string>? Add);

/// <summary>
/// Writes a partial response body and then aborts the connection mid-stream. Distinct
/// from <see cref="DropResponseConfig"/> (which never writes anything): partial
/// response delivers headers + part of the body, then cuts the stream off. The client
/// sees a successful status with a Content-Length header advertising more bytes than
/// actually arrive - triggering deserialization failures, truncated-stream errors, or
/// "unexpected end of stream" exceptions depending on the client library.
/// </summary>
/// <param name="Status">HTTP status to write. Defaults to 200 for a "successful but truncated" scenario.</param>
/// <param name="ContentType">Optional Content-Type. Defaults to <c>application/octet-stream</c>.</param>
/// <param name="Body">The partial bytes to write before aborting. Empty array = headers-only response then immediate abort.</param>
/// <param name="AdvertisedContentLength">Optional Content-Length to write in the response headers. Pass a value larger than <see cref="Body"/>.Length to lie about the response size and force client truncation errors. Null = no Content-Length (chunked-like behavior).</param>
/// <param name="AbortAfterMs">Optional delay between writing the partial body and aborting the connection. Lets the partial bytes drain to the client before the abort is signaled. Defaults to 0 (immediate abort).</param>
/// <param name="Probability">Probability of firing per request.</param>
/// <param name="FailFirst">Fire on the first N occurrences per request-key. Mutually exclusive with Probability.</param>
internal sealed record PartialResponseConfig(
    int Status,
    string? ContentType,
    byte[] Body,
    int? AdvertisedContentLength,
    int AbortAfterMs,
    double Probability,
    int? FailFirst);

/// <summary>
/// Simulates idempotency-key collision detection: the proxy remembers each
/// <see cref="KeyHeaderName"/> value seen within the past <see cref="WindowMs"/>
/// milliseconds; the FIRST request with a given key flows through normally; any
/// subsequent request reusing the same key within the window short-circuits with the
/// configured <see cref="Status"/> (default 409) + optional body/headers.
///
/// <para>
/// Reproduces a common backend safeguard: customers who accidentally reuse an
/// idempotency key expect a "you already submitted this" response. Lets tests
/// validate that the client surfaces this scenario correctly without needing the
/// real backend's idempotency cache.
/// </para>
/// </summary>
/// <param name="KeyHeaderName">Header to read for the idempotency key. Defaults to <c>Idempotency-Key</c>. Requests without this header are forwarded normally (no collision possible).</param>
/// <param name="Status">HTTP status returned on collision. Defaults to 409.</param>
/// <param name="Body">Optional body to write on collision.</param>
/// <param name="ContentType">Optional content type for the collision response.</param>
/// <param name="Headers">Optional response headers added on collision (e.g., custom diagnostic headers).</param>
/// <param name="WindowMs">How long a key is remembered. Sliding from each seen timestamp. Defaults to 60_000.</param>
internal sealed record IdempotencyCollisionConfig(
    string KeyHeaderName,
    int Status,
    string? Body,
    string? ContentType,
    IReadOnlyDictionary<string, string>? Headers,
    int WindowMs);

/// <summary>
/// Synthesizes a successful response and streams the body at a configurable bytes/sec
/// rate. Distinct from <see cref="LatencyConfig"/> (which delays then forwards normally
/// at full speed) and <see cref="PartialResponseConfig"/> (which writes some bytes then
/// aborts the stream): this transform delivers the FULL body but slowly, modeling a
/// "slow upstream that eventually completes". Tests streaming clients whose per-read
/// timeout is shorter than the full response time.
/// </summary>
/// <param name="Status">HTTP status to write. Defaults to 200.</param>
/// <param name="ContentType">Optional Content-Type. Defaults to <c>application/octet-stream</c>.</param>
/// <param name="Body">The full body to write, slowly.</param>
/// <param name="BytesPerSecond">Sustained throughput rate. Must be greater than 0.</param>
/// <param name="ChunkSize">Bytes written per delay period. Smaller = smoother throttling, more CPU; larger = burstier, less overhead. Defaults to 64 if not specified.</param>
/// <param name="Probability">Probability of firing per request.</param>
/// <param name="FailFirst">Fire on the first N occurrences per request-key. Mutually exclusive with Probability.</param>
internal sealed record SlowResponseConfig(
    int Status,
    string? ContentType,
    byte[] Body,
    int BytesPerSecond,
    int ChunkSize,
    double Probability,
    int? FailFirst);

/// <summary>
/// Forward-then-fail: forwards the request to the upstream destination (so the
/// upstream-side state change actually happens), discards the upstream response, and
/// returns a configured retryable failure to the client. Models the failure mode where
/// "the server committed but the client never saw the response" — exactly the
/// precondition for DTFx-replay state-guard 409 reproductions (BE writes the eval; GW
/// Worker activity sees a transient 5xx; DTFx replays the activity; replay hits the
/// real BE state guard with the real operationId).
///
/// <para>
/// Distinct from every other transform in this proxy: all other transforms short-
/// circuit BEFORE upstream. This one is the ONLY transform that lets the upstream-side
/// side-effect happen while the client sees a failure. Reach for it when reproducing
/// "state-guard fires on the retry" bugs.
/// </para>
///
/// <para>
/// The default <see cref="Status"/> of 503 is retryable per most HTTP clients' policy
/// (and per DTFx <c>RetryUtility.ShouldRetry()</c>) — pick a non-retryable status only
/// if you want to test the "single-attempt then surface" path instead of the replay
/// path.
/// </para>
/// </summary>
/// <param name="Status">HTTP status returned to the client AFTER upstream completes. Defaults to 503 (retryable).</param>
/// <param name="ContentType">Optional Content-Type for the synthesized failure body.</param>
/// <param name="Body">Optional body for the synthesized failure response. Null/empty = no body, only status.</param>
/// <param name="Headers">Optional headers added to the synthesized failure response (e.g., <c>Retry-After</c>).</param>
/// <param name="UpstreamTimeoutSeconds">How long to wait for the upstream call to complete before giving up. Defaults to 30. The upstream call is NOT propagated <c>RequestAborted</c> from the client — we want the upstream to commit even if the client cancels mid-call.</param>
/// <param name="Probability">Probability of firing per request. Defaults to 1.0.</param>
/// <param name="FailFirst">Fire on the first N matching requests per request-key. Mutually exclusive with non-1.0 probability.</param>
/// <param name="MaxFires">Optional global cap on total fires across all request keys. Once reached, the policy no-ops.</param>
internal sealed record ForwardThenFailConfig(
    int Status,
    string? ContentType,
    string? Body,
    IReadOnlyDictionary<string, string>? Headers,
    int UpstreamTimeoutSeconds,
    double Probability,
    int? FailFirst,
    int? MaxFires);
