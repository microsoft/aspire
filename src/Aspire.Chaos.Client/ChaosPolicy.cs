// <copyright file="ChaosPolicy.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace Aspire.Chaos.Client;

/// <summary>
/// A declarative chaos policy installed at AppHost build time via
/// <c>WithPolicy(...)</c>. Mirrors the runtime <c>POST /chaos/policies</c> payload
/// shape so the AppHost author and the harness use the same mental model.
/// </summary>
/// <remarks>
/// Each call to <c>WithPolicy</c> accumulates policies on the resource; at container
/// startup all accumulated policies are loaded into the active policy store alongside
/// the "bootstrap" policy that the existing fluent extensions (<c>WithLatency</c>,
/// <c>WithError</c>, <c>WithReplayDuplicate</c>, <c>When</c>) construct from
/// <c>CHAOS_*</c> environment variables.
/// </remarks>
public sealed record ChaosPolicy
{
    /// <summary>Optional unique identifier. If null, the server assigns a GUID.</summary>
    public string? Id { get; init; }

    /// <summary>Optional request matcher. Null = match all (non-/chaos/*) requests.</summary>
    public ChaosMatcher? Matcher { get; init; }

    /// <summary>Latency injection config. Null = this policy doesn't inject latency.</summary>
    public ChaosLatency? Latency { get; init; }

    /// <summary>Error short-circuit config. Null = this policy doesn't error.</summary>
    public ChaosError? Error { get; init; }

    /// <summary>Replay-duplicate config. Null = this policy doesn't replay.</summary>
    public ChaosReplayDuplicate? ReplayDuplicate { get; init; }

    /// <summary>Drop-response config. Null = this policy doesn't drop responses.</summary>
    public ChaosDropResponse? DropResponse { get; init; }

    /// <summary>Rate-limit config. Null = this policy doesn't rate-limit.</summary>
    public ChaosRateLimit? RateLimit { get; init; }

    /// <summary>Header tampering config. Null = this policy doesn't tamper with headers.</summary>
    public ChaosHeaderTamper? HeaderTamper { get; init; }

    /// <summary>Partial-response config. Null = this policy doesn't write partial responses.</summary>
    public ChaosPartialResponse? PartialResponse { get; init; }

    /// <summary>Idempotency-key collision config. Null = this policy doesn't simulate collisions.</summary>
    public ChaosIdempotencyKeyCollision? IdempotencyCollision { get; init; }

    /// <summary>Slow-response config. Null = this policy doesn't synthesize slow responses.</summary>
    public ChaosSlowResponse? SlowResponse { get; init; }

    /// <summary>
    /// Forward-then-fail config. Null = this policy doesn't forward-then-fail. The ONLY
    /// transform that forwards the request to upstream (so the upstream-side state change
    /// commits), discards the upstream response, then returns a configured retryable
    /// failure to the client. Reach for it to reproduce "state-guard fires on the retry"
    /// bug classes — e.g. DTFx replays a Workspaces POST whose first attempt succeeded
    /// BE-side but the client never saw the response.
    /// </summary>
    public ChaosForwardThenFail? ForwardThenFail { get; init; }

    /// <summary>
    /// Resource-aware random chaos config. Null = this policy doesn't inject random
    /// faults. Instead of a single fixed transform, samples (weighted, seeded) the faults
    /// realistic for the target resource type from a named fault profile — used for
    /// feature-resilience validation ("does my feature survive its dependencies' real
    /// failure modes?").
    /// </summary>
    public ChaosRandomFault? RandomFault { get; init; }

    /// <summary>
    /// Time-to-live for the policy. Null = no expiry (the natural default for
    /// build-time declarative policies, which should live for the AppHost's lifetime).
    /// Runtime <c>POST /chaos/policies</c> defaults to 5 minutes as a safety net per
    /// D6 - the AppHost-side default is null because there's no orphan risk.
    /// </summary>
    public int? TtlSeconds { get; init; }
}

/// <summary>
/// Request matcher scoping a chaos policy. All non-null fields must match for the
/// policy's transforms to fire. <see cref="PathPrefix"/> uses plain case-insensitive
/// string prefix (so <c>/test-</c> matches <c>/test-foo</c>); <see cref="PathContains"/>
/// uses case-insensitive substring search. <see cref="HeaderEquals"/> requires every
/// listed header to exist with the exact case-insensitive value (first value if multi-
/// valued); <see cref="HeaderContains"/> requires every listed header to exist with a
/// case-insensitive substring match.
/// <see cref="BodyContains"/> requires the request body to contain the given substring
/// (case-insensitive). When set, the proxy buffers the request body up to 1MB before
/// chaos middleware runs; requests with bodies larger than the buffer limit are treated
/// as non-matching to bound memory.
/// <see cref="DtfxActivityName"/> is a higher-level matcher for DurableTask Framework
/// workloads: matches DTFx <c>TaskCompletedEvent</c> messages whose corresponding
/// <c>TaskScheduledEvent</c> (observed earlier on the same proxy) had the given
/// activity name. This is the easy way to say "drop the completion event for activity
/// X" without needing to know DTFx's wire shape, partitioning, or correlation scheme.
/// </summary>
public sealed record ChaosMatcher
{
    public string? Method { get; init; }

    public string? PathPrefix { get; init; }

    public string? PathContains { get; init; }

    /// <summary>Header name -> exact expected value. ALL listed headers must match.</summary>
    public IDictionary<string, string>? HeaderEquals { get; init; }

    /// <summary>Header name -> substring to look for. ALL listed headers must match.</summary>
    public IDictionary<string, string>? HeaderContains { get; init; }

    /// <summary>
    /// Substring to look for in the request body (case-insensitive). The proxy enables
    /// request buffering up to 1MB for any matcher with this set. Use for protocols
    /// that encode the discriminator in the body — e.g. DurableTask Framework writes
    /// activity-completion messages with the literal string <c>TaskCompletedEvent</c>
    /// in the serialized payload, so <c>BodyContains = "TaskCompletedEvent"</c>
    /// selectively targets activity completions on a shared control queue while leaving
    /// orchestrator scheduling messages alone.
    /// </summary>
    public string? BodyContains { get; init; }

    /// <summary>
    /// DurableTask Framework activity name (case-sensitive). When set, the matcher
    /// fires on <c>TaskCompletedEvent</c> DTFx queue messages whose corresponding
    /// <c>TaskScheduledEvent</c> (observed earlier by the proxy on the same queue)
    /// recorded this activity name. The proxy correlates schedule + completion events
    /// by their <c>(InstanceId, TaskScheduledId)</c> pair, so the matcher works even
    /// across multiple in-flight orchestrations.
    /// </summary>
    /// <remarks>
    /// Setting this implies body buffering (the proxy reads each request body to look
    /// for DTFx envelopes regardless of whether the matcher with <see cref="DtfxActivityName"/>
    /// would otherwise want it buffered). When the proxy is in front of a non-DTFx
    /// queue, this matcher is silently a no-op (no DTFx envelopes ever observed →
    /// nothing to correlate → no fires).
    /// </remarks>
    public string? DtfxActivityName { get; init; }
}

/// <summary>Latency injection configuration. Probability and FailFirst are mutually exclusive.</summary>
public sealed record ChaosLatency
{
    [System.Text.Json.Serialization.JsonPropertyName("minMs")]
    public required TimeSpan Min { get; init; }

    [System.Text.Json.Serialization.JsonPropertyName("maxMs")]
    public required TimeSpan Max { get; init; }

    public double? Probability { get; init; }

    public int? FailFirst { get; init; }
}

/// <summary>Error-response injection configuration. Probability and FailFirst are mutually exclusive.</summary>
public sealed record ChaosError
{
    public required int Status { get; init; }

    public string? Body { get; init; }

    public string? ContentType { get; init; }

    public IDictionary<string, string>? Headers { get; init; }

    public double? Probability { get; init; }

    public int? FailFirst { get; init; }
}

/// <summary>Replay-duplicate configuration. Probability and FailFirst are mutually exclusive.</summary>
public sealed record ChaosReplayDuplicate
{
    public double? Probability { get; init; }

    public int? FailFirst { get; init; }
}

/// <summary>
/// Drop-response configuration. When firing, the proxy stops processing the request
/// without contacting upstream or writing a response - the client experiences a hang
/// that resolves only when its own HttpClient.Timeout fires. Probability and FailFirst
/// are mutually exclusive. <see cref="MaxFires"/> is a global ceiling that complements
/// either firing mode — once the policy has fired this many times across ALL request
/// keys, further matches no-op even if FailFirst slots remain or Probability would
/// roll true.
/// </summary>
public sealed record ChaosDropResponse
{
    public double? Probability { get; init; }

    public int? FailFirst { get; init; }

    /// <summary>
    /// Optional global cap on total fires for this policy. Once <c>RecordFire</c> has
    /// been called this many times (across all request keys / probability rolls), the
    /// middleware refuses to fire again. Use to make per-request-key FailFirst behave
    /// as a true once-per-policy cap when the protocol fans out across many request
    /// keys (e.g., DTFx Azure Queue Storage POSTs across multiple control-queue
    /// partitions, where each partition would otherwise get its own FailFirst slot).
    /// Null = no cap.
    /// </summary>
    public int? MaxFires { get; init; }
}

/// <summary>
/// Sliding-window rate-limit configuration. Once <see cref="RequestsPerWindow"/>
/// requests within <see cref="Window"/> have flowed through, subsequent requests
/// short-circuit with <see cref="Status"/> (default 429) and optional <see cref="Headers"/>
/// (e.g., <c>Retry-After</c>) until the window slides past them.
/// </summary>
public sealed record ChaosRateLimit
{
    /// <summary>Maximum admitted requests per sliding window before rate-limiting kicks in.</summary>
    public required int RequestsPerWindow { get; init; }

    /// <summary>Length of the sliding window. Serialized to milliseconds for the container.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("windowMs")]
    public required TimeSpan Window { get; init; }

    /// <summary>HTTP status returned when over limit. Defaults to 429 if not specified.</summary>
    public int? Status { get; init; }

    /// <summary>Optional headers added to the rate-limited response (e.g., Retry-After).</summary>
    public IDictionary<string, string>? Headers { get; init; }
}

/// <summary>
/// Direction the <see cref="ChaosHeaderTamper"/> targets. <c>Request</c> modifies the
/// inbound request headers before they reach upstream; <c>Response</c> modifies the
/// headers returned to the client; <c>Both</c> applies to both directions.
/// </summary>
public enum ChaosHeaderTamperDirection
{
    Request,
    Response,
    Both,
}

/// <summary>
/// Tampers with request and/or response headers as they flow through the proxy.
/// Operation order on each direction: <see cref="Remove"/> first, then <see cref="Set"/>
/// (overwrite-or-add), then <see cref="Add"/> (append). Useful for simulating missing
/// auth headers (Remove), injecting malformed values (Set with bad data), or testing
/// header-conditional client logic (Add).
/// </summary>
public sealed record ChaosHeaderTamper
{
    /// <summary>Direction the tamper applies to. Defaults to <see cref="ChaosHeaderTamperDirection.Both"/>.</summary>
    public ChaosHeaderTamperDirection Direction { get; init; } = ChaosHeaderTamperDirection.Both;

    /// <summary>Header names to remove entirely before any other operation.</summary>
    public IList<string>? Remove { get; init; }

    /// <summary>Headers to set (overwrites any existing values).</summary>
    public IDictionary<string, string>? Set { get; init; }

    /// <summary>Headers to append (always adds another value, preserving any existing ones).</summary>
    public IDictionary<string, string>? Add { get; init; }
}

/// <summary>
/// Writes a partial response body and then aborts the connection mid-stream. Distinct
/// from <see cref="ChaosDropResponse"/> (which never writes anything): partial response
/// delivers a status + headers + part of the body, then cuts the stream off. Combined
/// with <see cref="AdvertisedContentLength"/> larger than the body, the client sees a
/// truncated stream and raises a deserialization or "unexpected end of stream" error.
/// </summary>
public sealed record ChaosPartialResponse
{
    /// <summary>HTTP status returned. Defaults to 200 (the "successful but truncated" scenario most clients aren't defensive against).</summary>
    public int? Status { get; init; }

    /// <summary>Optional Content-Type. Defaults to <c>application/octet-stream</c> in the container.</summary>
    public string? ContentType { get; init; }

    /// <summary>The partial body to write before aborting. Empty / null = headers-only response then immediate abort.</summary>
    public string? Body { get; init; }

    /// <summary>Optional Content-Length to advertise. Set larger than <see cref="Body"/>.Length to lie about the response size and force client truncation errors. Null = chunked-like behavior with no advertised length.</summary>
    public int? AdvertisedContentLength { get; init; }

    /// <summary>Optional drain window between writing the partial body and aborting. Defaults to 0 (immediate abort). Set to 50-100ms to give buffered writes a chance to reach the kernel.</summary>
    public int? AbortAfterMs { get; init; }

    /// <summary>Probability of firing per request. Mutually exclusive with FailFirst.</summary>
    public double? Probability { get; init; }

    /// <summary>Fire on the first N occurrences per request-key. Mutually exclusive with Probability.</summary>
    public int? FailFirst { get; init; }
}

/// <summary>
/// Simulates an idempotency-key collision response. The proxy remembers each value of
/// <see cref="KeyHeaderName"/> seen within the past <see cref="Window"/>; the FIRST
/// request flows through normally, any subsequent request reusing the same key within
/// the window short-circuits with <see cref="Status"/> (default 409) and the optional
/// body/headers. Requests without the key header always forward (no collision possible).
/// </summary>
public sealed record ChaosIdempotencyKeyCollision
{
    /// <summary>Header to read for the idempotency key. Defaults to <c>Idempotency-Key</c>.</summary>
    public string? KeyHeaderName { get; init; }

    /// <summary>HTTP status returned on collision. Defaults to 409 (Conflict).</summary>
    public int? Status { get; init; }

    /// <summary>Optional body to write on collision.</summary>
    public string? Body { get; init; }

    /// <summary>Optional content type for the collision response body.</summary>
    public string? ContentType { get; init; }

    /// <summary>Optional response headers added on collision (e.g., custom diagnostic markers).</summary>
    public IDictionary<string, string>? Headers { get; init; }

    /// <summary>How long the key is remembered. Serialized to milliseconds for the container. Defaults to 60 seconds.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("windowMs")]
    public TimeSpan? Window { get; init; }
}

/// <summary>
/// Synthesizes a successful response and streams the body at a configurable
/// bytes/sec rate. Distinct from <see cref="ChaosLatency"/> (delays then forwards at
/// full speed) and <see cref="ChaosPartialResponse"/> (writes some bytes then aborts):
/// this transform delivers the FULL body but slowly, modeling "slow upstream that
/// eventually completes". Tests streaming clients whose per-read timeout is shorter
/// than the full response time.
/// </summary>
public sealed record ChaosSlowResponse
{
    /// <summary>HTTP status returned. Defaults to 200.</summary>
    public int? Status { get; init; }

    /// <summary>Optional Content-Type. Defaults to <c>application/octet-stream</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>The full body to stream slowly.</summary>
    public string? Body { get; init; }

    /// <summary>Sustained throughput rate. Defaults to 1024 (1 KB/s).</summary>
    public int? BytesPerSecond { get; init; }

    /// <summary>Bytes written per delay period. Smaller = smoother throttling, more CPU; larger = burstier, less overhead. Defaults to 64.</summary>
    public int? ChunkSize { get; init; }

    /// <summary>Probability (0.0-1.0) of firing per request. Mutually exclusive with FailFirst.</summary>
    public double? Probability { get; init; }

    /// <summary>Fire on the first N occurrences per request-key. Mutually exclusive with Probability.</summary>
    public int? FailFirst { get; init; }
}

/// <summary>
/// Forward-then-fail configuration. Forwards the request to the upstream destination
/// (so the upstream-side state change actually happens), discards the upstream response,
/// and returns a configured retryable failure to the client. The ONLY transform that
/// lets upstream commit while the client sees a failure — the exact precondition for
/// E2E reproductions of DTFx-replay / state-guard-409 bug classes.
///
/// <para>
/// The default <see cref="Status"/> of 503 is retryable by most HTTP clients (and by
/// DTFx's <c>RetryUtility.ShouldRetry()</c>) so the activity throws → DTFx replays →
/// replay hits the real upstream state guard. Pick a non-retryable status to test the
/// "single-attempt then surface" path instead.
/// </para>
/// </summary>
public sealed record ChaosForwardThenFail
{
    /// <summary>HTTP status returned to the client AFTER upstream completes. Defaults to 503 (retryable).</summary>
    public int? Status { get; init; }

    /// <summary>Optional Content-Type for the synthesized failure body.</summary>
    public string? ContentType { get; init; }

    /// <summary>Optional body for the synthesized failure response. Null/empty = no body, only status.</summary>
    public string? Body { get; init; }

    /// <summary>Optional headers added to the synthesized failure response (e.g., <c>Retry-After</c>).</summary>
    public IDictionary<string, string>? Headers { get; init; }

    /// <summary>How long to wait for the upstream call to complete before giving up. Defaults to 30s. NOT propagated <c>RequestAborted</c> — the whole point is to let upstream commit even if the client gives up.</summary>
    public int? UpstreamTimeoutSeconds { get; init; }

    /// <summary>Probability (0.0-1.0) of firing per request. Defaults to 1.0. Mutually exclusive with non-null <see cref="FailFirst"/>.</summary>
    public double? Probability { get; init; }

    /// <summary>Fire on the first N matching requests per request-key. Mutually exclusive with non-1.0 probability.</summary>
    public int? FailFirst { get; init; }

    /// <summary>Optional global cap on total fires across all request keys. Once reached, the policy no-ops.</summary>
    public int? MaxFires { get; init; }
}

/// <summary>
/// Resource-aware random chaos configuration. References a fault profile by id and a
/// per-request <see cref="Intensity"/>; the runtime samples (weighted, seeded by
/// <see cref="Seed"/>) the faults realistic for the target resource type and applies one
/// per firing request. Seeded so a failing validation run is reproducible. Typically
/// installed mesh-wide via <c>WithRandomChaos(...)</c> rather than authored by hand.
/// </summary>
public sealed record ChaosRandomFault
{
    /// <summary>Id of the fault profile to sample (e.g. <c>service.http</c>, <c>azure.cosmos</c>). Defaults to the generic service profile when unset/unknown.</summary>
    public string? ProfileId { get; init; }

    /// <summary>Per-request fire probability in [0, 1]. 0 = never, 1 = every matching request gets a sampled fault.</summary>
    public double? Intensity { get; init; }

    /// <summary>RNG seed. A fixed seed makes the fault sequence reproducible across runs; omit to have the server generate (and log) one.</summary>
    public int? Seed { get; init; }

    /// <summary>Optional global cap on total fires across all request keys (blast-radius control). Null = no cap.</summary>
    public int? MaxFires { get; init; }

    /// <summary>Request path prefixes random chaos must never fault (e.g. health/readiness). Case-insensitive prefix match.</summary>
    public IList<string>? ExcludePaths { get; init; }
}
