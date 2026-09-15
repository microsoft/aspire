// <copyright file="ChaosProxyResourceBuilderExtensionsTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Chaos.Client;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Chaos;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Tests the Aspire-side builder extensions: AddChaosProxy, WithTarget, WithLatency,
/// WithError, WithReplayDuplicate, WithDropResponse, WithRateLimit, WithHeaderTamper,
/// WithPartialResponse, WithIdempotencyKeyCollision, When.
/// </summary>
/// <remarks>
/// We do NOT call <c>builder.Build()</c> - that would attempt to actually start the
/// orchestration. Instead we inspect the resource graph (Annotations) directly to
/// confirm the right env vars and ResourceCommandAnnotations were registered.
/// </remarks>
[SuppressMessage("Reliability", "CA2007", Justification = "test")]
[SuppressMessage("AspireExperimental", "ASPIRECHAOS001", Justification = "test")]
public class ChaosProxyResourceBuilderExtensionsTests
{
    private static IDistributedApplicationBuilder CreateBuilder()
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            AssemblyName = typeof(ChaosProxyResourceBuilderExtensionsTests).Assembly.GetName().Name,
        });
    }

    private static IDictionary<string, string?> GetEnvironment(IResource resource)
    {
        var dict = new Dictionary<string, object>();
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            var ctx = new EnvironmentCallbackContext(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                resource,
                dict,
                CancellationToken.None);
            annotation.Callback(ctx);
        }
        return dict.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString());
    }

    [Fact]
    public void AddChaosProxy_CreatesResourceWithCorrectName()
    {
        var builder = CreateBuilder();

        var proxy = builder.AddChaosProxy("my-proxy");

        Assert.Equal("my-proxy", proxy.Resource.Name);
        Assert.Contains(builder.Resources, r => r.Name == "my-proxy");
    }

    [Fact]
    public void AddChaosProxy_RegistersHttpEndpoint()
    {
        var builder = CreateBuilder();

        var proxy = builder.AddChaosProxy("my-proxy");

        var endpoints = proxy.Resource.Annotations.OfType<EndpointAnnotation>().ToList();
        Assert.Contains(endpoints, e => string.Equals(e.Name, "http", StringComparison.OrdinalIgnoreCase));
    }

    // ---- per-proxy image build (correctness) ---------------------------------------------

    [Fact]
    public void AddChaosProxy_SingleProxy_BuildsItsOwnImageViaDockerfile()
    {
        var builder = CreateBuilder();

        var proxy = builder.AddChaosProxy("solo");

        // The proxy carries its own WithDockerfile build...
        Assert.True(proxy.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Any());

        // ...producing a per-proxy image identity (aspire-hosting-chaosproxy-{name}:local).
        var image = proxy.Resource.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("aspire-hosting-chaosproxy-solo", image.Image);
        Assert.Equal("local", image.Tag);
    }

    [Fact]
    public void AddChaosProxy_ManyProxies_EachBuildsItsOwnImage()
    {
        var builder = CreateBuilder();

        // Stand in for a multi-edge mesh: 16 proxies, like the live run that piled up in Created.
        var proxies = Enumerable.Range(0, 16)
            .Select(i => builder.AddChaosProxy($"mesh-p{i}"))
            .ToList();

        // CORRECTNESS: every proxy carries its OWN WithDockerfile build (one build per proxy). A
        // "build once, share via a fixed WithImage tag" scheme is broken on a clean image cache —
        // Aspire 13.3.5's WithDockerfile derives the build output tag from the resource name + a
        // random per-build hash and ignores the WithImage tag, so non-owner proxies resolve to a
        // tag no build produced and fail 'image not found' at container create. Per-proxy builds
        // are the correct shape; builds 2..N stay cheap via the container layer cache, not tag-sharing.
        Assert.Equal(
            proxies.Count,
            proxies.Count(p => p.Resource.Annotations.OfType<DockerfileBuildAnnotation>().Any()));

        // Each proxy resolves to its OWN distinct image name (no shared tag), tag local.
        foreach (var proxy in proxies)
        {
            var image = proxy.Resource.Annotations.OfType<ContainerImageAnnotation>().Single();
            Assert.Equal($"aspire-hosting-chaosproxy-{proxy.Resource.Name}", image.Image);
            Assert.Equal("local", image.Tag);
        }

        var distinctImages = proxies
            .Select(p => p.Resource.Annotations.OfType<ContainerImageAnnotation>().Single().Image)
            .Distinct(StringComparer.Ordinal)
            .Count();
        Assert.Equal(proxies.Count, distinctImages);

        // No image-SHARING wiring: no proxy is forced to ImagePullPolicy.Never, and none is made to
        // WaitForStart on another proxy (the removed owner/dependent mechanism).
        var proxyNames = proxies.Select(p => p.Resource.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var proxy in proxies)
        {
            Assert.Empty(proxy.Resource.Annotations.OfType<ContainerImagePullPolicyAnnotation>());
            Assert.DoesNotContain(
                proxy.Resource.Annotations.OfType<WaitAnnotation>(),
                w => proxyNames.Contains(w.Resource.Name));
        }
    }

    [Fact]
    public void AddChaosProxy_RegistersPauseResumeAndFireOnceCommands()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy");

        var commands = proxy.Resource.Annotations.OfType<ResourceCommandAnnotation>().Select(c => c.Name).ToList();

        Assert.Contains("pause-faults", commands);
        Assert.Contains("resume-faults", commands);
        Assert.Contains("fire-once-latency", commands);
        Assert.Contains("fire-once-error", commands);
        Assert.Contains("fire-once-replay", commands);
    }

    [Fact]
    public void AddChaosProxy_ExcludesResourceFromManifest()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy");

        // Dev-only resource - should never appear in publish manifest.
        var manifestAnnotation = proxy.Resource.Annotations.OfType<ManifestPublishingCallbackAnnotation>().FirstOrDefault();
        Assert.NotNull(manifestAnnotation);
        Assert.Null(manifestAnnotation!.Callback);
    }

    [Fact]
    public void AddChaosProxy_RegistersDashboardUrlCallback()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy");

        // The dashboard URL wiring lives in a ResourceUrlsCallbackAnnotation;
        // assert the annotation is registered. The actual URL list is populated by
        // Aspire at runtime once the endpoint is allocated.
        var urlsCallbacks = proxy.Resource.Annotations.OfType<ResourceUrlsCallbackAnnotation>().ToList();
        Assert.NotEmpty(urlsCallbacks);
    }

    [Fact]
    public void WithTarget_NestsProxyUnderTargetForDashboardGrouping()
    {
        var builder = CreateBuilder();
        var target = builder.AddContainer("cosmos", "fake-image")
            .WithHttpEndpoint(targetPort: 8080, name: "http");

        var proxy = builder.AddChaosProxy("mesh-gw-to-cosmos").WithTarget(target);

        // The proxy should declare a "Parent" relationship to the resource it fronts so the
        // Aspire dashboard nests it under that resource instead of listing it flat at top level.
        var parentRel = proxy.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .SingleOrDefault(r => string.Equals(r.Type, "Parent", StringComparison.Ordinal));

        Assert.NotNull(parentRel);
        Assert.Same(target.Resource, parentRel!.Resource);
    }

    [Fact]
    public void WithTarget_NamedEndpoint_NestsProxyUnderTarget()
    {
        var builder = CreateBuilder();
        // Mirrors the infra tier: a target exposing a named (non-"http") endpoint, e.g. a
        // storage emulator's "queue" endpoint.
        var target = builder.AddContainer("azurite", "fake-image")
            .WithHttpEndpoint(targetPort: 10001, name: "queue");

        var proxy = builder.AddChaosProxy("mesh-worker-to-queue").WithTarget(target, "queue");

        var parentRel = proxy.Resource.Annotations
            .OfType<ResourceRelationshipAnnotation>()
            .SingleOrDefault(r => string.Equals(r.Type, "Parent", StringComparison.Ordinal));

        Assert.NotNull(parentRel);
        Assert.Same(target.Resource, parentRel!.Resource);
    }

    [Fact]
    public void WithLatency_SetsChaosLatencyEnvVars()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithLatency(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300));

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("100", env["CHAOS_LATENCY_MIN_MS"]);
        Assert.Equal("300", env["CHAOS_LATENCY_MAX_MS"]);
        Assert.Equal("1", env["CHAOS_LATENCY_PROBABILITY"]);
    }

    [Fact]
    public void WithLatency_FailFirst_SetsFailFirstEnvVar()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithLatency(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), failFirst: 5);

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("5", env["CHAOS_LATENCY_FAIL_FIRST"]);
    }

    [Fact]
    public void WithLatency_BothProbabilityAndFailFirst_Throws()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddChaosProxy("my-proxy")
            .WithLatency(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), probability: 0.5, failFirst: 3));
    }

    [Fact]
    public void WithError_SetsChaosErrorEnvVars()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithError(httpStatus: 503, body: "ServerBusy");

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("503", env["CHAOS_ERROR_STATUS"]);
        Assert.Equal("ServerBusy", env["CHAOS_ERROR_BODY"]);
    }

    [Fact]
    public void WithReplayDuplicate_SetsEnabledEnvVar()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithReplayDuplicate();

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("true", env["CHAOS_REPLAY_DUPLICATE_ENABLED"]);
        Assert.Equal("1", env["CHAOS_REPLAY_DUPLICATE_PROBABILITY"]);
    }

    [Fact]
    public void WithDropResponse_SetsEnabledEnvVar()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithDropResponse();

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("true", env["CHAOS_DROP_RESPONSE_ENABLED"]);
    }

    [Fact]
    public void WithRateLimit_SetsRateLimitEnvVars()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithRateLimit(requestsPerWindow: 10, window: TimeSpan.FromSeconds(5), retryAfterSeconds: 3);

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("10", env["CHAOS_RATE_LIMIT_REQUESTS_PER_WINDOW"]);
        Assert.Equal("5000", env["CHAOS_RATE_LIMIT_WINDOW_MS"]);
        Assert.Equal("429", env["CHAOS_RATE_LIMIT_STATUS"]);
        Assert.Contains("Retry-After", env["CHAOS_RATE_LIMIT_HEADERS_JSON"]!);
        Assert.Contains("3", env["CHAOS_RATE_LIMIT_HEADERS_JSON"]!);
    }

    [Fact]
    public void WithRateLimit_ZeroRequestsPerWindow_Throws()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddChaosProxy("my-proxy")
            .WithRateLimit(requestsPerWindow: 0, window: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void WithRateLimit_ZeroWindow_Throws()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AddChaosProxy("my-proxy")
            .WithRateLimit(requestsPerWindow: 10, window: TimeSpan.Zero));
    }

    [Fact]
    public void WithHeaderTamper_SetsHeaderTamperJsonEnvVar()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithHeaderTamper(
                direction: ChaosHeaderTamperDirection.Both,
                remove: new[] { "Authorization" },
                set: new Dictionary<string, string> { ["X-Chaos"] = "true" });

        var env = GetEnvironment(proxy.Resource);

        var json = env["CHAOS_HEADER_TAMPER_JSON"];
        Assert.NotNull(json);
        Assert.Contains("\"direction\":\"Both\"", json);
        Assert.Contains("\"Authorization\"", json);
        Assert.Contains("\"X-Chaos\"", json);
    }

    [Fact]
    public void WithHeaderTamper_NoOperations_Throws()
    {
        var builder = CreateBuilder();

        Assert.Throws<ArgumentException>(() => builder.AddChaosProxy("my-proxy").WithHeaderTamper());
    }

    [Fact]
    public void WithPartialResponse_SetsPartialResponseEnvVars()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithPartialResponse(body: "truncated", status: 200, advertisedContentLength: 5000, abortAfterMs: 50);

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("true", env["CHAOS_PARTIAL_RESPONSE_ENABLED"]);
        Assert.Equal("200", env["CHAOS_PARTIAL_RESPONSE_STATUS"]);
        Assert.Equal("truncated", env["CHAOS_PARTIAL_RESPONSE_BODY"]);
        Assert.Equal("5000", env["CHAOS_PARTIAL_RESPONSE_ADVERTISED_CONTENT_LENGTH"]);
        Assert.Equal("50", env["CHAOS_PARTIAL_RESPONSE_ABORT_AFTER_MS"]);
    }

    [Fact]
    public void WithIdempotencyKeyCollision_SetsIdempotencyEnvVars()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .WithIdempotencyKeyCollision(window: TimeSpan.FromMinutes(2), status: 422, body: "duplicate");

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("true", env["CHAOS_IDEMPOTENCY_COLLISION_ENABLED"]);
        Assert.Equal("120000", env["CHAOS_IDEMPOTENCY_COLLISION_WINDOW_MS"]);
        Assert.Equal("422", env["CHAOS_IDEMPOTENCY_COLLISION_STATUS"]);
        Assert.Equal("duplicate", env["CHAOS_IDEMPOTENCY_COLLISION_BODY"]);
    }

    [Fact]
    public void When_SetsMatcherEnvVars()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .When(method: "POST", pathPrefix: "/api/v1", pathContains: "items");

        var env = GetEnvironment(proxy.Resource);

        Assert.Equal("POST", env["CHAOS_MATCH_METHOD"]);
        Assert.Equal("/api/v1", env["CHAOS_MATCH_PATH_PREFIX"]);
        Assert.Equal("items", env["CHAOS_MATCH_PATH_CONTAINS"]);
    }

    [Fact]
    public void When_NoArguments_Throws()
    {
        var builder = CreateBuilder();
        Assert.Throws<ArgumentException>(() => builder.AddChaosProxy("my-proxy").When());
    }

    [Fact]
    public void When_HeaderEquals_SerializesAsJsonEnvVar()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .When(headerEquals: new Dictionary<string, string> { ["X-Tenant-Id"] = "test-tenant" });

        var env = GetEnvironment(proxy.Resource);

        var json = env["CHAOS_MATCH_HEADER_EQUALS_JSON"];
        Assert.NotNull(json);
        Assert.Contains("X-Tenant-Id", json);
        Assert.Contains("test-tenant", json);
    }

    [Fact]
    public void When_HeaderContains_SerializesAsJsonEnvVar()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("my-proxy")
            .When(headerContains: new Dictionary<string, string> { ["User-Agent"] = "Postman" });

        var env = GetEnvironment(proxy.Resource);

        var json = env["CHAOS_MATCH_HEADER_CONTAINS_JSON"];
        Assert.NotNull(json);
        Assert.Contains("User-Agent", json);
        Assert.Contains("Postman", json);
    }
}
