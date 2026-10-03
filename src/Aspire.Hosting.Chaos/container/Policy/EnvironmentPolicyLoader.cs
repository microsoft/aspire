// <copyright file="EnvironmentPolicyLoader.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

namespace ChaosProxy.Container.Policy;

/// <summary>
/// Translates the CHAOS_* environment variables set by Aspire.Hosting.Chaos's
/// fluent extensions (WithLatency, WithError, WithReplayDuplicate, When) into a single
/// "bootstrap" <see cref="ActivePolicy"/> that gets added to the
/// <see cref="ActivePolicyStore"/> at container startup. Preserves the existing AppHost
/// fluent API even as runtime policy installation lands.
/// </summary>
internal static class EnvironmentPolicyLoader
{
    public const string BootstrapPolicyId = "bootstrap";

    public static ActivePolicy? LoadBootstrap(IConfiguration configuration)
    {
        var matcher = LoadMatcher(configuration);
        var latency = LoadLatency(configuration);
        var error = LoadError(configuration);
        var replay = LoadReplayDuplicate(configuration);
        var drop = LoadDropResponse(configuration);
        var rateLimit = LoadRateLimit(configuration);
        var headerTamper = LoadHeaderTamper(configuration);
        var partial = LoadPartialResponse(configuration);
        var idempotency = LoadIdempotencyCollision(configuration);
        var slow = LoadSlowResponse(configuration);

        if (matcher is null && latency is null && error is null && replay is null && drop is null && rateLimit is null && headerTamper is null && partial is null && idempotency is null && slow is null)
        {
            return null;
        }

        return new ActivePolicy(
            Id: BootstrapPolicyId,
            Matcher: matcher,
            Latency: latency,
            Error: error,
            ReplayDuplicate: replay,
            DropResponse: drop,
            RateLimit: rateLimit,
            HeaderTamper: headerTamper,
            PartialResponse: partial,
            IdempotencyCollision: idempotency,
            SlowResponse: slow,
            ExpiresAt: null);
    }

    /// <summary>
    /// Reads any policies declared at AppHost build time via <c>WithPolicy(...)</c>.
    /// The library serializes the cumulative list as JSON to <c>CHAOS_POLICIES_JSON</c>;
    /// each entry becomes its own <see cref="ActivePolicy"/> alongside the singleton
    /// bootstrap policy. Returns empty list when the env var is unset or empty.
    /// </summary>
    public static IReadOnlyList<ActivePolicy> LoadDeclaredPolicies(IConfiguration configuration, ILogger? logger = null)
    {
        var json = configuration.GetValue<string?>("CHAOS_POLICIES_JSON");
        if (string.IsNullOrEmpty(json))
        {
            return Array.Empty<ActivePolicy>();
        }

        List<InstallPolicyRequest>? requests;
        try
        {
            requests = System.Text.Json.JsonSerializer.Deserialize<List<InstallPolicyRequest>>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to deserialize CHAOS_POLICIES_JSON; skipping declared policies");
            return Array.Empty<ActivePolicy>();
        }

        if (requests is null)
        {
            return Array.Empty<ActivePolicy>();
        }

        var result = new List<ActivePolicy>(requests.Count);
        foreach (var req in requests)
        {
            if (req.Latency is null && req.Error is null && req.ReplayDuplicate is null && req.DropResponse is null && req.RateLimit is null && req.HeaderTamper is null && req.PartialResponse is null && req.IdempotencyCollision is null && req.SlowResponse is null && req.ForwardThenFail is null && req.RandomFault is null)
            {
                logger?.LogWarning("Skipping declared chaos policy with no transforms");
                continue;
            }

            // Declared policies default to no expiry (live for the AppHost lifetime).
            // Runtime POST /chaos/policies defaults to 5min TTL per D6; this path is
            // different because there's no orphan risk with build-time installs.
            var expiresAt = req.TtlSeconds.HasValue && req.TtlSeconds.Value > 0
                ? DateTimeOffset.UtcNow.AddSeconds(req.TtlSeconds.Value)
                : (DateTimeOffset?)null;

            var id = string.IsNullOrEmpty(req.Id) ? $"declared-{Guid.NewGuid():n}" : req.Id;

            result.Add(new ActivePolicy(
                Id: id,
                Matcher: req.Matcher is null ? null : new RequestMatcher(req.Matcher.Method, req.Matcher.PathPrefix, req.Matcher.PathContains, req.Matcher.HeaderEquals, req.Matcher.HeaderContains, req.Matcher.BodyContains, req.Matcher.DtfxActivityName),
                Latency: req.Latency is null ? null : new LatencyConfig(req.Latency.MinMs, req.Latency.MaxMs, req.Latency.Probability ?? 1.0, req.Latency.FailFirst),
                Error: req.Error is null ? null : new ErrorConfig(req.Error.Status, req.Error.Body, req.Error.ContentType, req.Error.Headers, req.Error.Probability ?? 1.0, req.Error.FailFirst),
                ReplayDuplicate: req.ReplayDuplicate is null ? null : new ReplayDuplicateConfig(req.ReplayDuplicate.Probability ?? 1.0, req.ReplayDuplicate.FailFirst),
                DropResponse: req.DropResponse is null ? null : new DropResponseConfig(req.DropResponse.Probability ?? 1.0, req.DropResponse.FailFirst, req.DropResponse.MaxFires),
                RateLimit: req.RateLimit is null ? null : new RateLimitConfig(req.RateLimit.RequestsPerWindow, req.RateLimit.WindowMs, req.RateLimit.Status ?? 429, req.RateLimit.Headers),
                HeaderTamper: req.HeaderTamper is null ? null : new HeaderTamperConfig(ParseDirection(req.HeaderTamper.Direction), req.HeaderTamper.Remove, req.HeaderTamper.Set, req.HeaderTamper.Add),
                PartialResponse: req.PartialResponse is null ? null : BuildPartialResponse(req.PartialResponse),
                IdempotencyCollision: req.IdempotencyCollision is null ? null : BuildIdempotencyCollision(req.IdempotencyCollision),
                SlowResponse: req.SlowResponse is null ? null : BuildSlowResponse(req.SlowResponse),
                ExpiresAt: expiresAt,
                ForwardThenFail: req.ForwardThenFail is null ? null : BuildForwardThenFail(req.ForwardThenFail),
                RandomFault: req.RandomFault is null ? null : new RandomFaultConfig(
                    ProfileId: string.IsNullOrEmpty(req.RandomFault.ProfileId) ? "service.http" : req.RandomFault.ProfileId,
                    Intensity: req.RandomFault.Intensity ?? 0.1,
                    Seed: req.RandomFault.Seed ?? Random.Shared.Next(),
                    MaxFires: req.RandomFault.MaxFires,
                    ExcludePaths: req.RandomFault.ExcludePaths)));
        }

        return result;
    }

    private static ForwardThenFailConfig BuildForwardThenFail(ForwardThenFailDto dto)
    {
        return new ForwardThenFailConfig(
            Status: dto.Status ?? 503,
            ContentType: dto.ContentType,
            Body: dto.Body,
            Headers: dto.Headers,
            UpstreamTimeoutSeconds: dto.UpstreamTimeoutSeconds ?? 30,
            Probability: dto.Probability ?? 1.0,
            FailFirst: dto.FailFirst,
            MaxFires: dto.MaxFires);
    }

    private static SlowResponseConfig BuildSlowResponse(SlowResponseDto dto)
    {
        var body = string.IsNullOrEmpty(dto.Body) ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(dto.Body);
        return new SlowResponseConfig(
            Status: dto.Status ?? 200,
            ContentType: dto.ContentType,
            Body: body,
            BytesPerSecond: dto.BytesPerSecond ?? 1024,
            ChunkSize: dto.ChunkSize ?? 64,
            Probability: dto.Probability ?? 1.0,
            FailFirst: dto.FailFirst);
    }

    private static IdempotencyCollisionConfig BuildIdempotencyCollision(IdempotencyCollisionDto dto)
    {
        return new IdempotencyCollisionConfig(
            KeyHeaderName: string.IsNullOrEmpty(dto.KeyHeaderName) ? "Idempotency-Key" : dto.KeyHeaderName,
            Status: dto.Status ?? 409,
            Body: dto.Body,
            ContentType: dto.ContentType,
            Headers: dto.Headers,
            WindowMs: dto.WindowMs ?? 60_000);
    }

    private static PartialResponseConfig BuildPartialResponse(PartialResponseDto dto)
    {
        var body = string.IsNullOrEmpty(dto.Body)
            ? Array.Empty<byte>()
            : System.Text.Encoding.UTF8.GetBytes(dto.Body);

        return new PartialResponseConfig(
            Status: dto.Status ?? 200,
            ContentType: dto.ContentType,
            Body: body,
            AdvertisedContentLength: dto.AdvertisedContentLength,
            AbortAfterMs: dto.AbortAfterMs ?? 0,
            Probability: dto.Probability ?? 1.0,
            FailFirst: dto.FailFirst);
    }

    private static HeaderTamperDirection ParseDirection(string? direction)
    {
        if (string.IsNullOrEmpty(direction))
        {
            return HeaderTamperDirection.Both;
        }
        return Enum.TryParse<HeaderTamperDirection>(direction, ignoreCase: true, out var parsed)
            ? parsed
            : HeaderTamperDirection.Both;
    }

    private static RequestMatcher? LoadMatcher(IConfiguration configuration)
    {
        var matchMethod = configuration.GetValue<string?>("CHAOS_MATCH_METHOD");
        var matchPathPrefix = configuration.GetValue<string?>("CHAOS_MATCH_PATH_PREFIX");
        var matchPathContains = configuration.GetValue<string?>("CHAOS_MATCH_PATH_CONTAINS");
        var matchBodyContains = configuration.GetValue<string?>("CHAOS_MATCH_BODY_CONTAINS");
        var matchDtfxActivityName = configuration.GetValue<string?>("CHAOS_MATCH_DTFX_ACTIVITY_NAME");
        var headerEquals = DeserializeOptional<Dictionary<string, string>>(configuration.GetValue<string?>("CHAOS_MATCH_HEADER_EQUALS_JSON"));
        var headerContains = DeserializeOptional<Dictionary<string, string>>(configuration.GetValue<string?>("CHAOS_MATCH_HEADER_CONTAINS_JSON"));

        if (string.IsNullOrEmpty(matchMethod) && string.IsNullOrEmpty(matchPathPrefix) && string.IsNullOrEmpty(matchPathContains) && string.IsNullOrEmpty(matchBodyContains) && string.IsNullOrEmpty(matchDtfxActivityName) && headerEquals is null && headerContains is null)
        {
            return null;
        }

        return new RequestMatcher(
            Method: string.IsNullOrEmpty(matchMethod) ? null : matchMethod,
            PathPrefix: string.IsNullOrEmpty(matchPathPrefix) ? null : matchPathPrefix,
            PathContains: string.IsNullOrEmpty(matchPathContains) ? null : matchPathContains,
            HeaderEquals: headerEquals,
            HeaderContains: headerContains,
            BodyContains: string.IsNullOrEmpty(matchBodyContains) ? null : matchBodyContains,
            DtfxActivityName: string.IsNullOrEmpty(matchDtfxActivityName) ? null : matchDtfxActivityName);
    }

    private static T? DeserializeOptional<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static LatencyConfig? LoadLatency(IConfiguration configuration)
    {
        var minMs = configuration.GetValue<int?>("CHAOS_LATENCY_MIN_MS");
        var maxMs = configuration.GetValue<int?>("CHAOS_LATENCY_MAX_MS");
        if (!minMs.HasValue || !maxMs.HasValue)
        {
            return null;
        }

        return new LatencyConfig(
            MinMs: minMs.Value,
            MaxMs: maxMs.Value,
            Probability: configuration.GetValue<double?>("CHAOS_LATENCY_PROBABILITY") ?? 1.0,
            FailFirst: configuration.GetValue<int?>("CHAOS_LATENCY_FAIL_FIRST"));
    }

    private static ErrorConfig? LoadError(IConfiguration configuration)
    {
        var errorStatus = configuration.GetValue<int?>("CHAOS_ERROR_STATUS");
        if (!errorStatus.HasValue)
        {
            return null;
        }

        IReadOnlyDictionary<string, string>? headers = null;
        var headersJson = configuration.GetValue<string?>("CHAOS_ERROR_HEADERS_JSON");
        if (!string.IsNullOrEmpty(headersJson))
        {
            try
            {
                headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            }
            catch (System.Text.Json.JsonException)
            {
                // Malformed JSON in env var - fall through with no headers. Container logs
                // would show this as a startup warning if we wire a logger here; for now
                // tolerate silently to avoid blocking proxy startup.
                headers = null;
            }
        }

        return new ErrorConfig(
            Status: errorStatus.Value,
            Body: configuration.GetValue<string?>("CHAOS_ERROR_BODY"),
            ContentType: configuration.GetValue<string?>("CHAOS_ERROR_CONTENT_TYPE"),
            Headers: headers,
            Probability: configuration.GetValue<double?>("CHAOS_ERROR_PROBABILITY") ?? 1.0,
            FailFirst: configuration.GetValue<int?>("CHAOS_ERROR_FAIL_FIRST"));
    }

    private static ReplayDuplicateConfig? LoadReplayDuplicate(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool?>("CHAOS_REPLAY_DUPLICATE_ENABLED");
        if (enabled != true)
        {
            return null;
        }

        return new ReplayDuplicateConfig(
            Probability: configuration.GetValue<double?>("CHAOS_REPLAY_DUPLICATE_PROBABILITY") ?? 1.0,
            FailFirst: configuration.GetValue<int?>("CHAOS_REPLAY_DUPLICATE_FAIL_FIRST"));
    }

    private static DropResponseConfig? LoadDropResponse(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool?>("CHAOS_DROP_RESPONSE_ENABLED");
        if (enabled != true)
        {
            return null;
        }

        return new DropResponseConfig(
            Probability: configuration.GetValue<double?>("CHAOS_DROP_RESPONSE_PROBABILITY") ?? 1.0,
            FailFirst: configuration.GetValue<int?>("CHAOS_DROP_RESPONSE_FAIL_FIRST"),
            MaxFires: configuration.GetValue<int?>("CHAOS_DROP_RESPONSE_MAX_FIRES"));
    }

    private static RateLimitConfig? LoadRateLimit(IConfiguration configuration)
    {
        var requestsPerWindow = configuration.GetValue<int?>("CHAOS_RATE_LIMIT_REQUESTS_PER_WINDOW");
        var windowMs = configuration.GetValue<int?>("CHAOS_RATE_LIMIT_WINDOW_MS");
        if (!requestsPerWindow.HasValue || !windowMs.HasValue)
        {
            return null;
        }

        IReadOnlyDictionary<string, string>? headers = null;
        var headersJson = configuration.GetValue<string?>("CHAOS_RATE_LIMIT_HEADERS_JSON");
        if (!string.IsNullOrEmpty(headersJson))
        {
            try
            {
                headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            }
            catch (System.Text.Json.JsonException)
            {
                headers = null;
            }
        }

        return new RateLimitConfig(
            RequestsPerWindow: requestsPerWindow.Value,
            WindowMs: windowMs.Value,
            Status: configuration.GetValue<int?>("CHAOS_RATE_LIMIT_STATUS") ?? 429,
            Headers: headers);
    }

    /// <summary>
    /// Reads <c>CHAOS_HEADER_TAMPER_JSON</c> (single env var, since the shape is too rich
    /// for a flat name=value scheme - it has 4 sub-fields, two of which are dicts and one
    /// is a list). Format mirrors <see cref="HeaderTamperDto"/>: <c>{"direction":"Both",
    /// "remove":["X-Foo"],"set":{"X-Bar":"new"},"add":{"X-Baz":"extra"}}</c>.
    /// </summary>
    private static HeaderTamperConfig? LoadHeaderTamper(IConfiguration configuration)
    {
        var json = configuration.GetValue<string?>("CHAOS_HEADER_TAMPER_JSON");
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        HeaderTamperDto? dto;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<HeaderTamperDto>(
                json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }

        if (dto is null)
        {
            return null;
        }

        return new HeaderTamperConfig(
            Direction: ParseDirection(dto.Direction),
            Remove: dto.Remove,
            Set: dto.Set,
            Add: dto.Add);
    }

    private static PartialResponseConfig? LoadPartialResponse(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool?>("CHAOS_PARTIAL_RESPONSE_ENABLED");
        if (enabled != true)
        {
            return null;
        }

        var bodyString = configuration.GetValue<string?>("CHAOS_PARTIAL_RESPONSE_BODY") ?? string.Empty;
        return new PartialResponseConfig(
            Status: configuration.GetValue<int?>("CHAOS_PARTIAL_RESPONSE_STATUS") ?? 200,
            ContentType: configuration.GetValue<string?>("CHAOS_PARTIAL_RESPONSE_CONTENT_TYPE"),
            Body: System.Text.Encoding.UTF8.GetBytes(bodyString),
            AdvertisedContentLength: configuration.GetValue<int?>("CHAOS_PARTIAL_RESPONSE_ADVERTISED_CONTENT_LENGTH"),
            AbortAfterMs: configuration.GetValue<int?>("CHAOS_PARTIAL_RESPONSE_ABORT_AFTER_MS") ?? 0,
            Probability: configuration.GetValue<double?>("CHAOS_PARTIAL_RESPONSE_PROBABILITY") ?? 1.0,
            FailFirst: configuration.GetValue<int?>("CHAOS_PARTIAL_RESPONSE_FAIL_FIRST"));
    }

    private static IdempotencyCollisionConfig? LoadIdempotencyCollision(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool?>("CHAOS_IDEMPOTENCY_COLLISION_ENABLED");
        if (enabled != true)
        {
            return null;
        }

        IReadOnlyDictionary<string, string>? headers = null;
        var headersJson = configuration.GetValue<string?>("CHAOS_IDEMPOTENCY_COLLISION_HEADERS_JSON");
        if (!string.IsNullOrEmpty(headersJson))
        {
            try
            {
                headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
            }
            catch (System.Text.Json.JsonException)
            {
                headers = null;
            }
        }

        return new IdempotencyCollisionConfig(
            KeyHeaderName: configuration.GetValue<string?>("CHAOS_IDEMPOTENCY_COLLISION_KEY_HEADER_NAME") ?? "Idempotency-Key",
            Status: configuration.GetValue<int?>("CHAOS_IDEMPOTENCY_COLLISION_STATUS") ?? 409,
            Body: configuration.GetValue<string?>("CHAOS_IDEMPOTENCY_COLLISION_BODY"),
            ContentType: configuration.GetValue<string?>("CHAOS_IDEMPOTENCY_COLLISION_CONTENT_TYPE"),
            Headers: headers,
            WindowMs: configuration.GetValue<int?>("CHAOS_IDEMPOTENCY_COLLISION_WINDOW_MS") ?? 60_000);
    }

    private static SlowResponseConfig? LoadSlowResponse(IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool?>("CHAOS_SLOW_RESPONSE_ENABLED");
        if (enabled != true)
        {
            return null;
        }

        var bodyString = configuration.GetValue<string?>("CHAOS_SLOW_RESPONSE_BODY") ?? string.Empty;
        return new SlowResponseConfig(
            Status: configuration.GetValue<int?>("CHAOS_SLOW_RESPONSE_STATUS") ?? 200,
            ContentType: configuration.GetValue<string?>("CHAOS_SLOW_RESPONSE_CONTENT_TYPE"),
            Body: System.Text.Encoding.UTF8.GetBytes(bodyString),
            BytesPerSecond: configuration.GetValue<int?>("CHAOS_SLOW_RESPONSE_BYTES_PER_SECOND") ?? 1024,
            ChunkSize: configuration.GetValue<int?>("CHAOS_SLOW_RESPONSE_CHUNK_SIZE") ?? 64,
            Probability: configuration.GetValue<double?>("CHAOS_SLOW_RESPONSE_PROBABILITY") ?? 1.0,
            FailFirst: configuration.GetValue<int?>("CHAOS_SLOW_RESPONSE_FAIL_FIRST"));
    }
}
