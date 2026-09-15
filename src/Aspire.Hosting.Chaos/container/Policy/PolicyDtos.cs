// <copyright file="PolicyDtos.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace ChaosProxy.Container.Policy;

/// <summary>
/// JSON request body for <c>POST /chaos/policies</c>. Caller may supply <see cref="Id"/>
/// to make installs idempotent (re-POSTing the same id replaces the existing policy);
/// omit to have the server assign a GUID.
/// </summary>
internal sealed record InstallPolicyRequest(
    string? Id,
    MatcherDto? Matcher,
    LatencyDto? Latency,
    ErrorDto? Error,
    ReplayDuplicateDto? ReplayDuplicate,
    DropResponseDto? DropResponse,
    RateLimitDto? RateLimit,
    HeaderTamperDto? HeaderTamper,
    PartialResponseDto? PartialResponse,
    IdempotencyCollisionDto? IdempotencyCollision,
    SlowResponseDto? SlowResponse,
    int? TtlSeconds,
    ForwardThenFailDto? ForwardThenFail = null,
    RandomFaultDto? RandomFault = null);

internal sealed record InstallPolicyResponse(string Id, string Status = "installed");

internal sealed record PolicyListResponse(IReadOnlyList<PolicySummaryDto> Policies);

internal sealed record PolicySummaryDto(
    string Id,
    MatcherDto? Matcher,
    LatencyDto? Latency,
    ErrorDto? Error,
    ReplayDuplicateDto? ReplayDuplicate,
    DropResponseDto? DropResponse,
    RateLimitDto? RateLimit,
    HeaderTamperDto? HeaderTamper,
    PartialResponseDto? PartialResponse,
    IdempotencyCollisionDto? IdempotencyCollision,
    SlowResponseDto? SlowResponse,
    DateTimeOffset? ExpiresAt,
    IReadOnlyDictionary<string, long>? FireCounts,
    ForwardThenFailDto? ForwardThenFail = null,
    RandomFaultDto? RandomFault = null);

internal sealed record MatcherDto(string? Method, string? PathPrefix, string? PathContains, IReadOnlyDictionary<string, string>? HeaderEquals, IReadOnlyDictionary<string, string>? HeaderContains, string? BodyContains = null, string? DtfxActivityName = null);

internal sealed record LatencyDto(int MinMs, int MaxMs, double? Probability, int? FailFirst);

internal sealed record ErrorDto(int Status, string? Body, string? ContentType, IReadOnlyDictionary<string, string>? Headers, double? Probability, int? FailFirst);

internal sealed record ReplayDuplicateDto(double? Probability, int? FailFirst);

internal sealed record DropResponseDto(double? Probability, int? FailFirst, int? MaxFires = null);

internal sealed record RateLimitDto(int RequestsPerWindow, int WindowMs, int? Status, IReadOnlyDictionary<string, string>? Headers);

internal sealed record HeaderTamperDto(string? Direction, IReadOnlyList<string>? Remove, IReadOnlyDictionary<string, string>? Set, IReadOnlyDictionary<string, string>? Add);

internal sealed record PartialResponseDto(
    int? Status,
    string? ContentType,
    string? Body,
    int? AdvertisedContentLength,
    int? AbortAfterMs,
    double? Probability,
    int? FailFirst);

internal sealed record IdempotencyCollisionDto(
    string? KeyHeaderName,
    int? Status,
    string? Body,
    string? ContentType,
    IReadOnlyDictionary<string, string>? Headers,
    int? WindowMs);

/// <summary>
/// Wire shape for the slow-response transform.
/// </summary>
internal sealed record SlowResponseDto(
    int? Status,
    string? ContentType,
    string? Body,
    int? BytesPerSecond,
    int? ChunkSize,
    double? Probability,
    int? FailFirst);

/// <summary>
/// Wire shape for the forward-then-fail transform. Forwards the request to upstream
/// (so the side-effect commits), discards the upstream response, then returns a
/// configured retryable failure to the client. The ONLY transform that lets upstream
/// commit while the client sees a failure — required for E2E reproductions of
/// state-guard-on-retry bugs (e.g., DTFx replays a Workspaces POST whose first
/// attempt succeeded BE-side but failed client-side).
/// </summary>
internal sealed record ForwardThenFailDto(
    int? Status,
    string? ContentType,
    string? Body,
    IReadOnlyDictionary<string, string>? Headers,
    int? UpstreamTimeoutSeconds,
    double? Probability,
    int? FailFirst,
    int? MaxFires);

/// <summary>
/// Wire shape for resource-aware random chaos. References a fault profile by id and a
/// per-request <see cref="Intensity"/>; the runtime samples (weighted, seeded by
/// <see cref="Seed"/>) the faults realistic for the target resource type. Used for
/// feature-resilience validation — "does my feature survive the failures its
/// dependencies actually produce?".
/// </summary>
internal sealed record RandomFaultDto(
    string? ProfileId,
    double? Intensity,
    int? Seed,
    int? MaxFires,
    IReadOnlyList<string>? ExcludePaths);

/// <summary>Response body for <c>POST /chaos/freeze</c>: the deterministic policy block.</summary>
internal sealed record FreezeResponse(IReadOnlyList<InstallPolicyRequest> Policies);

/// <summary>
/// Request body for <c>POST /chaos/match</c> - asks the proxy to predict which
/// policies would fire on a hypothetical request without actually sending it.
/// </summary>
internal sealed record MatchPredictionRequest(
    string? Method,
    string Path,
    IReadOnlyDictionary<string, string>? Headers);

/// <summary>Response body for <c>POST /chaos/match</c>.</summary>
internal sealed record MatchPredictionResponse(IReadOnlyList<MatchPredictionEntry> Matches);

/// <summary>
/// One matching policy + the transforms it would fire on the hypothetical request.
/// Per D12 first-installed-wins per transform type, the harness can filter for the
/// FIRST entry containing a given transform to predict actual middleware behavior.
/// </summary>
internal sealed record MatchPredictionEntry(string PolicyId, IReadOnlyList<string> TransformsThatWouldFire);
