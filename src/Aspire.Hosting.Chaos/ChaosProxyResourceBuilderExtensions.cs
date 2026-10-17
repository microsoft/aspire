// <copyright file="ChaosProxyResourceBuilderExtensions.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Chaos;
using Aspire.Chaos.Client;

namespace Aspire.Hosting;

/// <summary>
/// Extension methods for adding and configuring <see cref="ChaosProxyResource"/> instances
/// in a distributed application.
/// </summary>
[Experimental("ASPIRECHAOS001", UrlFormat = "https://aka.ms/aspire-chaos-proxy/experimental/{0}")]
public static class ChaosProxyResourceBuilderExtensions
{
    private const string RouteId = "r1";
    private const string ClusterId = "c1";
    private const string DestinationId = "d1";

    /// <summary>
    /// Adds a chaos proxy resource to the distributed application. Container is built
    /// locally from the package's container/ source via Aspire's WithDockerfile during
    /// in-house incubation (D2); switches to a published image at M4.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="name">The name of the resource. Must be unique within the application.</param>
    /// <returns>A resource builder for the chaos proxy.</returns>
    /// <remarks>
    /// The proxy is dev-only: <c>ExcludeFromManifest</c> is applied automatically so the
    /// resource never appears in the Aspire publish manifest.
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> AddChaosProxy(
        this IDistributedApplicationBuilder builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var resource = new ChaosProxyResource(name);
        var containerSourcePath = ResolveContainerSourcePath();

        // Every proxy builds its OWN image via WithDockerfile (distinct per-edge image name). This
        // is deliberate and required for correctness. Do NOT "build once" by giving the first proxy
        // the WithDockerfile and pointing the rest at a shared WithImage tag: Aspire 13.3.5's
        // WithDockerfile sets the BUILD output tag from the resource name + a random per-build hash
        // (GenerateImageName + GenerateImageTag) and IGNORES the WithImage tag, while DCP resolves a
        // dependent's run image from its ContainerImageAnnotation (the shared tag) — a tag no build
        // ever produces. On a clean image cache that's a hard 'image not found' at container create
        // for every non-owner proxy. WaitForStart only orders startup; it can't create the tag.
        //
        // Per-proxy WithDockerfile still gets the startup win from this image: the Dockerfile's
        // restore/publish/ReadyToRun/cert-bake layers are byte-identical across edges, so the
        // container layer cache serves builds 2..N cheaply — only the first edge pays the cold
        // build. (A correct true "build once" would need a real pre-provision build step that emits
        // a fixed tag before DCP runs; that's a separate follow-up, not WithImage tag-matching.)
        var resourceBuilder = builder.AddResource(resource)
            // Placeholder image identity - WithDockerfile() needs an image to "configure" but
            // docker builds the actual image from container/Dockerfile at AppHost startup.
            .WithImage($"aspire-hosting-chaosproxy-{name.ToLowerInvariant()}")
            .WithImageTag("local")
            .WithDockerfile(contextPath: containerSourcePath, dockerfilePath: "Dockerfile")
            .WithHttpEndpoint(targetPort: ChaosProxyResource.ContainerPort, name: ChaosProxyResource.HttpEndpointName)
            // HTTPS listener for clients that require https to their target (e.g. the Cosmos
            // SDK in Gateway mode). The container serves a self-signed cert on 8443; consumers
            // routed here must accept any server cert (dev-only chaos infra).
            .WithHttpsEndpoint(targetPort: ChaosProxyResource.HttpsContainerPort, name: ChaosProxyResource.HttpsEndpointName)
            // Health check MUST target the HTTP endpoint explicitly. With no endpointName,
            // Aspire 13.3.5's WithHttpHealthCheck prefers the HTTPS endpoint when both exist,
            // and its health-check HttpClient doesn't trust the proxy's self-signed 8443 cert —
            // so the TLS handshake fails and EVERY proxy sits perpetually "Running (Unhealthy)"
            // even though it forwards fine. Liveness doesn't need TLS; the plaintext /chaos/healthz
            // on the http endpoint returns 200 unconditionally.
            .WithHttpHealthCheck("/chaos/healthz", endpointName: ChaosProxyResource.HttpEndpointName)
            .WithOtlpExporter()
            .ExcludeFromManifest();

        AddDashboardUrls(resourceBuilder);
        AddPauseResumeCommands(resourceBuilder);
        AddFireOnceCommands(resourceBuilder);
        return resourceBuilder;
    }

    /// <summary>
    /// Adds one-click navigation links from the Aspire dashboard's resource view into
    /// the chaos proxy's runtime API endpoints (state probe, installed policies, etc).
    /// Lets users inspect chaos state without leaving the dashboard.
    /// </summary>
    private static void AddDashboardUrls(IResourceBuilder<ChaosProxyResource> builder)
    {
        builder.WithUrls(context =>
        {
            var endpoint = context.GetEndpoint(ChaosProxyResource.HttpEndpointName);
            if (endpoint is null || !endpoint.IsAllocated)
            {
                return;
            }

            var baseUrl = endpoint.Url.TrimEnd('/');
            context.Urls.Add(new ResourceUrlAnnotation
            {
                Url = $"{baseUrl}/chaos/state",
                DisplayText = "Chaos state",
                DisplayLocation = UrlDisplayLocation.DetailsOnly,
            });
            context.Urls.Add(new ResourceUrlAnnotation
            {
                Url = $"{baseUrl}/chaos/policies",
                DisplayText = "Installed policies",
                DisplayLocation = UrlDisplayLocation.DetailsOnly,
            });
            context.Urls.Add(new ResourceUrlAnnotation
            {
                Url = $"{baseUrl}/chaos/healthz",
                DisplayText = "Health probe",
                DisplayLocation = UrlDisplayLocation.DetailsOnly,
            });
        });
    }

    /// <summary>
    /// Registers <c>pause-faults</c> and <c>resume-faults</c> dashboard commands on the
    /// chaos proxy resource. Each command POSTs to the container's <c>/chaos/pause</c>
    /// or <c>/chaos/resume</c> endpoint when invoked from the Aspire dashboard.
    /// </summary>
    /// <remarks>
    /// updateState shows enabled/disabled based on whether the resource is Running -
    /// avoids showing commands when the container is starting/failed. We don't query
    /// /chaos/state from updateState (which would be expensive on every dashboard tick);
    /// the commands themselves are idempotent so showing both at all times is safe.
    /// </remarks>
    private static void AddPauseResumeCommands(IResourceBuilder<ChaosProxyResource> builder)
    {
        builder.Resource.Annotations.Add(new ResourceCommandAnnotation(
            name: "pause-faults",
            displayName: "Pause faults",
            updateState: context => context.ResourceSnapshot.State?.Text == "Running" ? ResourceCommandState.Enabled : ResourceCommandState.Disabled,
            executeCommand: async _ => await InvokeChaosControlEndpointAsync(builder.Resource, "pause").ConfigureAwait(false),
            displayDescription: "Pause all chaos transforms. Proxy keeps forwarding traffic; faults stop firing until resumed.",
            parameter: null,
            confirmationMessage: null,
            iconName: "Pause",
            iconVariant: IconVariant.Regular,
            isHighlighted: true));

        builder.Resource.Annotations.Add(new ResourceCommandAnnotation(
            name: "resume-faults",
            displayName: "Resume faults",
            updateState: context => context.ResourceSnapshot.State?.Text == "Running" ? ResourceCommandState.Enabled : ResourceCommandState.Disabled,
            executeCommand: async _ => await InvokeChaosControlEndpointAsync(builder.Resource, "resume").ConfigureAwait(false),
            displayDescription: "Resume chaos transforms after a previous pause. Idempotent.",
            parameter: null,
            confirmationMessage: null,
            iconName: "Play",
            iconVariant: IconVariant.Regular,
            isHighlighted: true));
    }

    private static async Task<ExecuteCommandResult> InvokeChaosControlEndpointAsync(ChaosProxyResource resource, string path)
    {
        // Resolve the proxy's HTTP endpoint URL from the resource snapshot. EndpointReference
        // is the runtime-resolved URL the container is actually bound to.
        var endpoint = resource.GetEndpoint(ChaosProxyResource.HttpEndpointName);
        if (!endpoint.IsAllocated)
        {
            return new ExecuteCommandResult { Success = false, Message = "Chaos proxy HTTP endpoint not yet allocated." };
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.PostAsync(new Uri($"{endpoint.Url.TrimEnd('/')}/chaos/{path}"), content: null).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ExecuteCommandResult { Success = false, Message = $"Chaos {path} returned {(int)response.StatusCode} {response.ReasonPhrase}" };
            }
            return new ExecuteCommandResult { Success = true };
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResult { Success = false, Message = $"Chaos {path} failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Registers three dashboard commands - <c>fire-once-latency</c>, <c>fire-once-error</c>,
    /// <c>fire-once-replay</c> - that arm a single-shot trigger on the container. The next
    /// matching request fires the transform regardless of probability / failFirst gates,
    /// then the trigger clears automatically.
    /// </summary>
    private static void AddFireOnceCommands(IResourceBuilder<ChaosProxyResource> builder)
    {
        AddFireOnceCommand(builder, transformBucket: "latency", commandName: "fire-once-latency", displayName: "Fire latency once", icon: "Clock");
        AddFireOnceCommand(builder, transformBucket: "error", commandName: "fire-once-error", displayName: "Fire error once", icon: "ErrorCircle");
        AddFireOnceCommand(builder, transformBucket: "replay-duplicate", commandName: "fire-once-replay", displayName: "Fire replay-duplicate once", icon: "ArrowRepeatAll");
    }

    private static void AddFireOnceCommand(IResourceBuilder<ChaosProxyResource> builder, string transformBucket, string commandName, string displayName, string icon)
    {
        builder.Resource.Annotations.Add(new ResourceCommandAnnotation(
            name: commandName,
            displayName: displayName,
            updateState: context => context.ResourceSnapshot.State?.Text == "Running" ? ResourceCommandState.Enabled : ResourceCommandState.Disabled,
            executeCommand: async _ => await InvokeChaosFireOnceAsync(builder.Resource, transformBucket).ConfigureAwait(false),
            displayDescription: $"Arm a fire-once trigger for the {transformBucket} transform. The next matching request fires it regardless of probability/failFirst, then the trigger clears.",
            parameter: null,
            confirmationMessage: null,
            iconName: icon,
            iconVariant: IconVariant.Regular,
            isHighlighted: false));
    }

    private static async Task<ExecuteCommandResult> InvokeChaosFireOnceAsync(ChaosProxyResource resource, string transformBucket)
    {
        var endpoint = resource.GetEndpoint(ChaosProxyResource.HttpEndpointName);
        if (!endpoint.IsAllocated)
        {
            return new ExecuteCommandResult { Success = false, Message = "Chaos proxy HTTP endpoint not yet allocated." };
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var url = new Uri($"{endpoint.Url.TrimEnd('/')}/chaos/fire-once?transform={Uri.EscapeDataString(transformBucket)}");
            using var response = await client.PostAsync(url, content: null).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new ExecuteCommandResult { Success = false, Message = $"fire-once {transformBucket} returned {(int)response.StatusCode} {response.ReasonPhrase}" };
            }
            return new ExecuteCommandResult { Success = true };
        }
        catch (Exception ex)
        {
            return new ExecuteCommandResult { Success = false, Message = $"fire-once {transformBucket} failed: {ex.Message}" };
        }
    }

    /// <summary>
    /// Configures the chaos proxy to forward incoming traffic to the specified target
    /// resource. Wires YARP via ReverseProxy__* environment variables; target HTTP endpoint
    /// URL resolved through Aspire's service discovery at runtime.
    /// </summary>
    /// <typeparam name="TTarget">The target resource type (must expose endpoints).</typeparam>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="target">The resource the proxy will forward traffic to.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// Mode 2 (explicit target) from the design doc. The target must expose an HTTP
    /// endpoint named "http". Mode 1 (InterceptCallsFrom().To() auto-rewrite) lands in
    /// a later slice.
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithTarget<TTarget>(
        this IResourceBuilder<ChaosProxyResource> builder,
        IResourceBuilder<TTarget> target)
        where TTarget : IResourceWithEndpoints
        => WithTarget(builder, target, endpointName: "http");

    /// <summary>
    /// Like <see cref="WithTarget{TTarget}(IResourceBuilder{ChaosProxyResource}, IResourceBuilder{TTarget})"/>
    /// but routes to a named endpoint on the target. Use for targets that expose multiple
    /// named endpoints (e.g. Azurite exposes <c>"blob"</c>, <c>"queue"</c>, <c>"table"</c>)
    /// where the default <c>"http"</c> name doesn't exist.
    /// </summary>
    /// <typeparam name="TTarget">The target resource type (must expose endpoints).</typeparam>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="target">The resource the proxy will forward traffic to.</param>
    /// <param name="endpointName">Name of the endpoint to forward to on the target.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    public static IResourceBuilder<ChaosProxyResource> WithTarget<TTarget>(
        this IResourceBuilder<ChaosProxyResource> builder,
        IResourceBuilder<TTarget> target,
        string endpointName)
        where TTarget : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(endpointName);

        var targetEndpoint = target.GetEndpoint(endpointName);

        return builder
            // Nest the proxy under the resource it fronts so the Aspire dashboard groups each
            // mesh-{client}-to-{target} proxy beneath its target (collapsible) instead of
            // cluttering the flat top-level list. The proxy forwards to `target`, so that's the
            // resource it logically belongs to. WithTarget is the single chokepoint every meshed
            // and manually-wired proxy funnels through, and it runs once per proxy (inside the
            // create-only configure lambda), so this is the right place to establish the grouping.
            .WithParentRelationship(target.Resource)
            .WithEnvironment($"ReverseProxy__Routes__{RouteId}__ClusterId", ClusterId)
            .WithEnvironment($"ReverseProxy__Routes__{RouteId}__Match__Path", "/{**catch-all}")
            .WithEnvironment($"ReverseProxy__Clusters__{ClusterId}__Destinations__{DestinationId}__Address", targetEndpoint);
    }

    /// <summary>
    /// Injects latency on requests forwarded through the proxy. Delay is uniformly
    /// random between <paramref name="min"/> and <paramref name="max"/> per matching request.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="min">Minimum delay per request.</param>
    /// <param name="max">Maximum delay per request. Must be &gt;= <paramref name="min"/>.</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting latency on each request. Defaults to 1.0 (every request). Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject latency on the first N occurrences per logical request key, then forward subsequent occurrences unmodified. Mutually exclusive with <paramref name="probability"/>. See D13 in the design doc for rationale.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// M2 first slice supports only static configuration via environment variables -
    /// runtime policy installation via POST /chaos/policies arrives in a later slice.
    /// Per-request-key state (for failFirst) is per-AppHost-session (in-memory, per D6).
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithLatency(
        this IResourceBuilder<ChaosProxyResource> builder,
        TimeSpan min,
        TimeSpan max,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (max < min)
        {
            throw new ArgumentException($"max ({max}) must be >= min ({min}).", nameof(max));
        }

        if (probability.HasValue && failFirst.HasValue)
        {
            throw new ArgumentException("probability and failFirst are mutually exclusive; specify one or neither (defaults to probability: 1.0).", nameof(failFirst));
        }

        var resolvedProbability = probability ?? (failFirst.HasValue ? 1.0 : 1.0);

        builder = builder
            .WithEnvironment("CHAOS_LATENCY_MIN_MS", ((int)min.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_LATENCY_MAX_MS", ((int)max.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_LATENCY_PROBABILITY", resolvedProbability.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        if (failFirst.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_LATENCY_FAIL_FIRST", failFirst.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder;
    }

    /// <summary>
    /// Injects an HTTP error response on requests forwarded through the proxy. When the
    /// transform fires, the proxy short-circuits with <paramref name="httpStatus"/>
    /// (and optional <paramref name="body"/> + <paramref name="headers"/>) instead of
    /// forwarding to the upstream.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="httpStatus">HTTP status code to return (e.g., 500, 503, 429).</param>
    /// <param name="body">Optional response body. If null/empty, the proxy returns the status with no body.</param>
    /// <param name="contentType">Content-Type header for the body. Defaults to <c>text/plain; charset=utf-8</c> when body is set.</param>
    /// <param name="headers">Optional additional response headers. Used by the Azure-shaped transforms in the <c>Aspire.Hosting.Chaos.Azure</c> companion (e.g., <c>x-ms-retry-after-ms</c> for Cosmos throttling, <c>Retry-After</c> for Key Vault).</param>
    /// <param name="probability">Probability (0.0-1.0) of injecting the error on each request. Defaults to 1.0 (every request). Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Inject the error on the first N occurrences per logical request key, then forward subsequent occurrences unmodified. Mutually exclusive with <paramref name="probability"/>. See D13 in the design doc for rationale.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// M2 supports static configuration via environment variables; runtime policy
    /// installation via POST /chaos/policies (M3 prep) supports the same surface
    /// natively. Error fires AFTER latency injection in the container pipeline.
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithError(
        this IResourceBuilder<ChaosProxyResource> builder,
        int httpStatus,
        string? body = null,
        string? contentType = null,
        IDictionary<string, string>? headers = null,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (httpStatus < 100 || httpStatus > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(httpStatus), httpStatus, "httpStatus must be a valid HTTP status code (100-599).");
        }

        if (probability.HasValue && failFirst.HasValue)
        {
            throw new ArgumentException("probability and failFirst are mutually exclusive; specify one or neither (defaults to probability: 1.0).", nameof(failFirst));
        }

        var resolvedProbability = probability ?? 1.0;

        builder = builder
            .WithEnvironment("CHAOS_ERROR_STATUS", httpStatus.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_ERROR_PROBABILITY", resolvedProbability.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(body))
        {
            builder = builder.WithEnvironment("CHAOS_ERROR_BODY", body);
        }
        if (!string.IsNullOrEmpty(contentType))
        {
            builder = builder.WithEnvironment("CHAOS_ERROR_CONTENT_TYPE", contentType);
        }
        if (headers is not null && headers.Count > 0)
        {
            // Serialize as JSON for env-var transport; container deserializes in EnvironmentPolicyLoader.
            var headersJson = System.Text.Json.JsonSerializer.Serialize(headers);
            builder = builder.WithEnvironment("CHAOS_ERROR_HEADERS_JSON", headersJson);
        }
        if (failFirst.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_ERROR_FAIL_FIRST", failFirst.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder;
    }

    /// <summary>
    /// Forwards the request normally AND, after the primary response is returned to the
    /// client, issues a fire-and-forget background HTTP call duplicating the same request
    /// to the upstream. Simulates DTFx-style activity replay where an orchestration retries
    /// an activity whose response was lost - the upstream sees two identical requests,
    /// the client sees one.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="probability">Probability (0.0-1.0) of firing the replay on each request. Defaults to 1.0 (every request). Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Fire the replay on the first N occurrences per logical request key, then forward subsequent occurrences without replay. Mutually exclusive with <paramref name="probability"/>. See D13 in the design doc for rationale.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The duplicate goes directly to the upstream URL via an HttpClient inside the
    /// container - it does NOT re-traverse YARP or the chaos middleware pipeline, so the
    /// duplicate is not faulted a second time (avoids recursive replay).
    /// </para>
    /// <para>
    /// Replay-duplicate runs AFTER ChaosErrorMiddleware in the container pipeline, so
    /// errored requests (which short-circuit without reaching the upstream) do not get
    /// replayed - the proxy only duplicates requests that actually hit the upstream.
    /// </para>
    /// <para>
    /// M2 third slice supports only static configuration via environment variables;
    /// runtime policy installation via POST /chaos/policies arrives in a later slice.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithReplayDuplicate(
        this IResourceBuilder<ChaosProxyResource> builder,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (probability.HasValue && failFirst.HasValue)
        {
            throw new ArgumentException("probability and failFirst are mutually exclusive; specify one or neither (defaults to probability: 1.0).", nameof(failFirst));
        }

        var resolvedProbability = probability ?? 1.0;

        builder = builder
            .WithEnvironment("CHAOS_REPLAY_DUPLICATE_ENABLED", "true")
            .WithEnvironment("CHAOS_REPLAY_DUPLICATE_PROBABILITY", resolvedProbability.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        if (failFirst.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_REPLAY_DUPLICATE_FAIL_FIRST", failFirst.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder;
    }

    /// <summary>
    /// Drops the response on the floor without forwarding to upstream. The client sees
    /// a hung request that terminates only when its <see cref="System.Net.Http.HttpClient.Timeout"/>
    /// fires (or its <see cref="CancellationToken"/> is signaled). Useful for exercising
    /// client-side timeout + retry behavior and hung-request handling.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="probability">Probability (0.0-1.0) of dropping the response on each request. Defaults to 1.0 (every request). Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Drop the response on the first N occurrences per logical request key, then forward subsequent occurrences. Mutually exclusive with <paramref name="probability"/>. See D13 in the design doc for rationale.</param>
    /// <param name="maxFires">Optional global cap on total fires for this policy across all request keys. Once the policy has dropped this many requests, no further matches fire even if <paramref name="failFirst"/> slots remain or <paramref name="probability"/> would roll true. Useful for protocols that fan a logical operation across many request keys (e.g., DTFx Azure Queue Storage POSTs spread across multiple control-queue partitions).</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// Drop runs AFTER ChaosLatencyMiddleware in the container pipeline, so a slow-then-dropped
    /// policy waits the latency window before hanging - reproduces "slow server eventually
    /// stops responding" failure modes precisely.
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithDropResponse(
        this IResourceBuilder<ChaosProxyResource> builder,
        double? probability = null,
        int? failFirst = null,
        int? maxFires = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (probability.HasValue && failFirst.HasValue)
        {
            throw new ArgumentException("probability and failFirst are mutually exclusive; specify one or neither (defaults to probability: 1.0).", nameof(failFirst));
        }

        var resolvedProbability = probability ?? 1.0;

        builder = builder
            .WithEnvironment("CHAOS_DROP_RESPONSE_ENABLED", "true")
            .WithEnvironment("CHAOS_DROP_RESPONSE_PROBABILITY", resolvedProbability.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        if (failFirst.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_DROP_RESPONSE_FAIL_FIRST", failFirst.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (maxFires.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_DROP_RESPONSE_MAX_FIRES", maxFires.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder;
    }

    /// <summary>
    /// Rate-limits requests through the proxy using a sliding window. Once
    /// <paramref name="requestsPerWindow"/> requests within <paramref name="window"/>
    /// have been admitted, subsequent requests short-circuit with
    /// <paramref name="status"/> (default 429) until the window slides past them.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="requestsPerWindow">Maximum admitted requests per sliding window.</param>
    /// <param name="window">Length of the sliding window.</param>
    /// <param name="status">HTTP status returned when rate-limited. Defaults to 429.</param>
    /// <param name="retryAfterSeconds">Optional value for the <c>Retry-After</c> response header (seconds). Common pattern for clients that respect HTTP rate-limit conventions.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// Rate-limit runs AFTER ChaosErrorMiddleware in the container pipeline (so errored
    /// requests don't count against the rate budget - they didn't reach the upstream)
    /// and BEFORE ChaosDropResponseMiddleware (so rate-limited requests get a proper
    /// HTTP response rather than hanging).
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithRateLimit(
        this IResourceBuilder<ChaosProxyResource> builder,
        int requestsPerWindow,
        TimeSpan window,
        int? status = null,
        int? retryAfterSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestsPerWindow);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "window must be greater than zero.");
        }

        builder = builder
            .WithEnvironment("CHAOS_RATE_LIMIT_REQUESTS_PER_WINDOW", requestsPerWindow.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_RATE_LIMIT_WINDOW_MS", ((long)window.TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_RATE_LIMIT_STATUS", (status ?? 429).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (retryAfterSeconds.HasValue)
        {
            var headersJson = System.Text.Json.JsonSerializer.Serialize(
                new Dictionary<string, string> { ["Retry-After"] = retryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            builder = builder.WithEnvironment("CHAOS_RATE_LIMIT_HEADERS_JSON", headersJson);
        }

        return builder;
    }

    /// <summary>
    /// Tampers with request and/or response headers flowing through the proxy. Useful for
    /// simulating missing auth headers, injecting malformed values, or testing header-
    /// conditional client logic without modifying the upstream service.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="direction">Which side of the proxy to tamper. Defaults to <see cref="ChaosHeaderTamperDirection.Both"/>.</param>
    /// <param name="remove">Header names to remove entirely (applied first).</param>
    /// <param name="set">Headers to set, overwriting any existing values (applied after remove).</param>
    /// <param name="add">Headers to append, preserving existing values (applied last).</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// Header tampering is deterministic - it always applies on matching requests. There's
    /// no probability/failFirst gate (use <see cref="When"/>
    /// to scope which requests get tampered).
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithHeaderTamper(
        this IResourceBuilder<ChaosProxyResource> builder,
        ChaosHeaderTamperDirection direction = ChaosHeaderTamperDirection.Both,
        IEnumerable<string>? remove = null,
        IDictionary<string, string>? set = null,
        IDictionary<string, string>? add = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if ((remove is null || !remove.Any()) && (set is null || set.Count == 0) && (add is null || add.Count == 0))
        {
            throw new ArgumentException("WithHeaderTamper requires at least one of remove, set, or add to specify what to change.");
        }

        // Single env var since the shape is too rich for flat name=value plumbing.
        var json = System.Text.Json.JsonSerializer.Serialize(
            new
            {
                direction = direction.ToString(),
                remove = remove?.ToArray(),
                set,
                add,
            },
            new System.Text.Json.JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });

        return builder.WithEnvironment("CHAOS_HEADER_TAMPER_JSON", json);
    }

    /// <summary>
    /// Writes a partial response body then aborts the connection mid-stream. Distinct
    /// from <see cref="WithDropResponse"/> (which never writes anything): partial
    /// response delivers headers + part of the body, then cuts the stream off. Combined
    /// with <paramref name="advertisedContentLength"/> larger than <paramref name="body"/>.Length,
    /// the client sees a truncated stream and raises a deserialization or "unexpected
    /// end of stream" error.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="body">The partial UTF-8 string to write before aborting. Empty = headers-only response then immediate abort.</param>
    /// <param name="status">HTTP status returned. Defaults to 200.</param>
    /// <param name="contentType">Optional Content-Type. Defaults to <c>application/octet-stream</c>.</param>
    /// <param name="advertisedContentLength">Optional Content-Length to advertise. Set larger than <paramref name="body"/>.Length to lie about the response size.</param>
    /// <param name="abortAfterMs">Optional drain window between the partial write and the abort. Defaults to 0 (immediate abort).</param>
    /// <param name="probability">Probability (0.0-1.0) of firing per request. Defaults to 1.0. Mutually exclusive with <paramref name="failFirst"/>.</param>
    /// <param name="failFirst">Fire on the first N occurrences per request-key. Mutually exclusive with <paramref name="probability"/>.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    public static IResourceBuilder<ChaosProxyResource> WithPartialResponse(
        this IResourceBuilder<ChaosProxyResource> builder,
        string body = "",
        int status = 200,
        string? contentType = null,
        int? advertisedContentLength = null,
        int? abortAfterMs = null,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (probability.HasValue && failFirst.HasValue)
        {
            throw new ArgumentException("probability and failFirst are mutually exclusive; specify one or neither (defaults to probability: 1.0).", nameof(failFirst));
        }

        var resolvedProbability = probability ?? 1.0;

        builder = builder
            .WithEnvironment("CHAOS_PARTIAL_RESPONSE_ENABLED", "true")
            .WithEnvironment("CHAOS_PARTIAL_RESPONSE_STATUS", status.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_PARTIAL_RESPONSE_BODY", body ?? string.Empty)
            .WithEnvironment("CHAOS_PARTIAL_RESPONSE_PROBABILITY", resolvedProbability.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(contentType))
        {
            builder = builder.WithEnvironment("CHAOS_PARTIAL_RESPONSE_CONTENT_TYPE", contentType);
        }
        if (advertisedContentLength.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_PARTIAL_RESPONSE_ADVERTISED_CONTENT_LENGTH", advertisedContentLength.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (abortAfterMs.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_PARTIAL_RESPONSE_ABORT_AFTER_MS", abortAfterMs.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (failFirst.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_PARTIAL_RESPONSE_FAIL_FIRST", failFirst.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder;
    }

    /// <summary>
    /// Simulates an idempotency-key collision. The first request carrying the
    /// configured key header value passes through; any subsequent request reusing the
    /// same key within <paramref name="window"/> short-circuits with the configured
    /// status (default 409). Requests without the key header always pass through.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="window">How long the key is remembered. Defaults to 60 seconds.</param>
    /// <param name="keyHeaderName">Header read for the idempotency key. Defaults to <c>Idempotency-Key</c>.</param>
    /// <param name="status">HTTP status returned on collision. Defaults to 409.</param>
    /// <param name="body">Optional response body on collision.</param>
    /// <param name="contentType">Optional content type for the collision response body.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    public static IResourceBuilder<ChaosProxyResource> WithIdempotencyKeyCollision(
        this IResourceBuilder<ChaosProxyResource> builder,
        TimeSpan? window = null,
        string? keyHeaderName = null,
        int? status = null,
        string? body = null,
        string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var windowMs = window.HasValue ? (long)window.Value.TotalMilliseconds : 60_000L;

        builder = builder
            .WithEnvironment("CHAOS_IDEMPOTENCY_COLLISION_ENABLED", "true")
            .WithEnvironment("CHAOS_IDEMPOTENCY_COLLISION_WINDOW_MS", windowMs.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_IDEMPOTENCY_COLLISION_STATUS", (status ?? 409).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(keyHeaderName))
        {
            builder = builder.WithEnvironment("CHAOS_IDEMPOTENCY_COLLISION_KEY_HEADER_NAME", keyHeaderName);
        }
        if (!string.IsNullOrEmpty(body))
        {
            builder = builder.WithEnvironment("CHAOS_IDEMPOTENCY_COLLISION_BODY", body);
        }
        if (!string.IsNullOrEmpty(contentType))
        {
            builder = builder.WithEnvironment("CHAOS_IDEMPOTENCY_COLLISION_CONTENT_TYPE", contentType);
        }

        return builder;
    }

    /// <summary>
    /// Synthesizes a successful response and streams the body at a configurable
    /// bytes/sec rate. Tests streaming clients whose per-read timeout is shorter than
    /// the full response time. Distinct from <see cref="WithLatency"/> (delays once
    /// then forwards at full speed) and <see cref="WithPartialResponse"/> (aborts
    /// mid-stream): slow-response delivers the FULL body but slowly.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="body">The full body to stream slowly.</param>
    /// <param name="bytesPerSecond">Sustained throughput rate. Defaults to 1024 (1 KB/s).</param>
    /// <param name="status">HTTP status returned. Defaults to 200.</param>
    /// <param name="contentType">Optional Content-Type. Defaults to <c>application/octet-stream</c>.</param>
    /// <param name="chunkSize">Bytes written per delay period. Defaults to 64.</param>
    /// <param name="probability">Probability of firing per request. Defaults to 1.0.</param>
    /// <param name="failFirst">Fire on the first N occurrences per request-key. Mutually exclusive with probability.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    public static IResourceBuilder<ChaosProxyResource> WithSlowResponse(
        this IResourceBuilder<ChaosProxyResource> builder,
        string body,
        int bytesPerSecond = 1024,
        int status = 200,
        string? contentType = null,
        int? chunkSize = null,
        double? probability = null,
        int? failFirst = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytesPerSecond);

        if (probability.HasValue && failFirst.HasValue)
        {
            throw new ArgumentException("probability and failFirst are mutually exclusive; specify one or neither (defaults to probability: 1.0).", nameof(failFirst));
        }

        var resolvedProbability = probability ?? 1.0;

        builder = builder
            .WithEnvironment("CHAOS_SLOW_RESPONSE_ENABLED", "true")
            .WithEnvironment("CHAOS_SLOW_RESPONSE_STATUS", status.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_SLOW_RESPONSE_BODY", body ?? string.Empty)
            .WithEnvironment("CHAOS_SLOW_RESPONSE_BYTES_PER_SECOND", bytesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironment("CHAOS_SLOW_RESPONSE_PROBABILITY", resolvedProbability.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        if (!string.IsNullOrEmpty(contentType))
        {
            builder = builder.WithEnvironment("CHAOS_SLOW_RESPONSE_CONTENT_TYPE", contentType);
        }
        if (chunkSize.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_SLOW_RESPONSE_CHUNK_SIZE", chunkSize.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (failFirst.HasValue)
        {
            builder = builder.WithEnvironment("CHAOS_SLOW_RESPONSE_FAIL_FIRST", failFirst.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder;
    }

    /// <summary>
    /// Scopes all chaos transforms on this proxy to requests matching the specified
    /// criteria. Subsequent <c>WithLatency</c> / <c>WithError</c> / <c>WithReplayDuplicate</c>
    /// only fire for requests that match. Without <c>.When()</c>, transforms fire on every
    /// non-<c>/chaos/*</c> request.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="method">Optional HTTP method to match (case-insensitive). Null = match any method.</param>
    /// <param name="pathPrefix">Optional path prefix to match (case-insensitive plain string prefix). <c>/test-</c> matches <c>/test-foo</c> and <c>/test-anything</c>. Null = match any path prefix.</param>
    /// <param name="pathContains">Optional substring that must appear in the request path (case-insensitive). Null = no substring constraint.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// At least one matcher field must be non-null. All non-null fields must match
    /// (logical AND). Matcher semantics are evaluated server-side in the container -
    /// the AppHost-side parameters are serialized to CHAOS_MATCH_* env vars and the
    /// container reconstructs the matcher at startup.
    /// </para>
    /// <para>
    /// Header-equals and header-contains matching arrived alongside the v2 transforms
    /// and enable per-tenant / per-feature-flag / per-client scoping (e.g.,
    /// <c>headerEquals: new Dictionary&lt;string,string&gt; { ["X-Tenant-Id"] = "test-tenant" }</c>).
    /// All listed header constraints must match - mix with method/path for AND semantics.
    /// </para>
    /// </remarks>
    /// <param name="headerEquals">Optional header name -> exact expected value (case-insensitive). All listed headers must match.</param>
    /// <param name="headerContains">Optional header name -> case-insensitive substring to find. All listed headers must match.</param>
    /// <param name="bodyContains">Optional case-insensitive substring to find in the request body. Triggers per-request body buffering (capped at 1 MB). Useful when the protocol encodes its discriminator in the body — e.g. DurableTask Framework queue messages contain the literal <c>TaskCompletedEvent</c> in activity-completion payloads.</param>
    /// <param name="dtfxActivityName">Optional DurableTask Framework activity name. When set, the matcher fires on <c>TaskCompletedEvent</c> DTFx queue messages whose corresponding <c>TaskScheduledEvent</c> (observed earlier on the same proxy) recorded this activity name. The proxy correlates schedule + completion events by their (InstanceId, TaskScheduledId) pair, so the matcher works across multiple in-flight orchestrations and is the right primitive for DTFx-replay race repros.</param>
    public static IResourceBuilder<ChaosProxyResource> When(
        this IResourceBuilder<ChaosProxyResource> builder,
        string? method = null,
        string? pathPrefix = null,
        string? pathContains = null,
        IDictionary<string, string>? headerEquals = null,
        IDictionary<string, string>? headerContains = null,
        string? bodyContains = null,
        string? dtfxActivityName = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var hasHeaderEquals = headerEquals is not null && headerEquals.Count > 0;
        var hasHeaderContains = headerContains is not null && headerContains.Count > 0;
        if (string.IsNullOrEmpty(method) && string.IsNullOrEmpty(pathPrefix) && string.IsNullOrEmpty(pathContains) && !hasHeaderEquals && !hasHeaderContains && string.IsNullOrEmpty(bodyContains) && string.IsNullOrEmpty(dtfxActivityName))
        {
            throw new ArgumentException("At least one of method, pathPrefix, pathContains, headerEquals, headerContains, bodyContains, or dtfxActivityName must be specified.", nameof(builder));
        }

        if (!string.IsNullOrEmpty(method))
        {
            builder = builder.WithEnvironment("CHAOS_MATCH_METHOD", method);
        }
        if (!string.IsNullOrEmpty(pathPrefix))
        {
            builder = builder.WithEnvironment("CHAOS_MATCH_PATH_PREFIX", pathPrefix);
        }
        if (!string.IsNullOrEmpty(pathContains))
        {
            builder = builder.WithEnvironment("CHAOS_MATCH_PATH_CONTAINS", pathContains);
        }
        if (hasHeaderEquals)
        {
            builder = builder.WithEnvironment("CHAOS_MATCH_HEADER_EQUALS_JSON", System.Text.Json.JsonSerializer.Serialize(headerEquals));
        }
        if (hasHeaderContains)
        {
            builder = builder.WithEnvironment("CHAOS_MATCH_HEADER_CONTAINS_JSON", System.Text.Json.JsonSerializer.Serialize(headerContains));
        }
        if (!string.IsNullOrEmpty(bodyContains))
        {
            builder = builder.WithEnvironment("CHAOS_MATCH_BODY_CONTAINS", bodyContains);
        }
        if (!string.IsNullOrEmpty(dtfxActivityName))
        {
            builder = builder.WithEnvironment("CHAOS_MATCH_DTFX_ACTIVITY_NAME", dtfxActivityName);
        }

        return builder;
    }

    /// <summary>
    /// Adds a declarative chaos policy to the proxy. Multiple <c>WithPolicy</c> calls
    /// accumulate; at container startup all accumulated policies load into the active
    /// policy store alongside the bootstrap policy constructed from <c>WithLatency</c> /
    /// <c>WithError</c> / etc. fluent calls.
    /// </summary>
    /// <param name="builder">The chaos proxy resource builder.</param>
    /// <param name="policy">The policy to install at container startup.</param>
    /// <returns>The same chaos proxy resource builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This is the Aspire-conventional declarative path for multi-policy scenarios -
    /// e.g., one policy throttling Cosmos for some path-prefix and another erroring
    /// Storage for a different path-prefix on the same proxy. Single-policy scenarios
    /// can keep using the simpler fluent chain (<c>.When().WithLatency()</c>...).
    /// </para>
    /// <para>
    /// Policies installed via WithPolicy have <see cref="ChaosPolicy.TtlSeconds"/>
    /// null by default (live for the AppHost's lifetime). Runtime <c>POST /chaos/policies</c>
    /// from a harness defaults to 5 minutes as a safety net per D6 - the orphan risk
    /// only applies to externally-driven installs.
    /// </para>
    /// </remarks>
    public static IResourceBuilder<ChaosProxyResource> WithPolicy(
        this IResourceBuilder<ChaosProxyResource> builder,
        ChaosPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(policy);

        if (policy.Latency is null && policy.Error is null && policy.ReplayDuplicate is null && policy.DropResponse is null && policy.RateLimit is null && policy.HeaderTamper is null && policy.PartialResponse is null && policy.IdempotencyCollision is null && policy.SlowResponse is null && policy.ForwardThenFail is null && policy.RandomFault is null)
        {
            throw new ArgumentException("ChaosPolicy must specify at least one transform (Latency, Error, ReplayDuplicate, DropResponse, RateLimit, HeaderTamper, PartialResponse, IdempotencyCollision, SlowResponse, ForwardThenFail, or RandomFault).", nameof(policy));
        }

        // Find or create the annotation that accumulates policies across multiple
        // WithPolicy calls on the same resource. Each call appends and re-serializes the
        // whole list so the env var always contains the current full set.
        if (!builder.Resource.TryGetLastAnnotation<ChaosPolicyCollectionAnnotation>(out var annotation))
        {
            annotation = new ChaosPolicyCollectionAnnotation();
            builder.Resource.Annotations.Add(annotation);
        }

        annotation.Policies.Add(policy);

        // Reserialize the full list to JSON; container's EnvironmentPolicyLoader reads
        // CHAOS_POLICIES_JSON at startup and adds each policy to the store.
        var json = System.Text.Json.JsonSerializer.Serialize(annotation.Policies, ChaosPolicyJsonOptions.CamelCase);
        return builder.WithEnvironment("CHAOS_POLICIES_JSON", json);
    }

    /// <summary>
    /// Resolves the container source directory shipped alongside the package assembly.
    /// In-house incubation pattern: container/ is copied to bin/Debug/net8.0/container/
    /// via the csproj's None CopyToOutputDirectory, so we find it relative to the .dll.
    /// </summary>
    private static string ResolveContainerSourcePath()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(ChaosProxyResource).Assembly.Location)
            ?? throw new InvalidOperationException("Unable to determine Aspire.Hosting.Chaos assembly location.");
        var containerDir = Path.Combine(assemblyDir, "container");
        if (!Directory.Exists(containerDir))
        {
            throw new DirectoryNotFoundException(
                $"Chaos proxy container source not found at '{containerDir}'. Expected the package's container/ directory to be copied alongside the assembly (verify the csproj's <None Include=\"container\\**\\*\" CopyToOutputDirectory=\"PreserveNewest\" /> item).");
        }

        return containerDir;
    }
}
