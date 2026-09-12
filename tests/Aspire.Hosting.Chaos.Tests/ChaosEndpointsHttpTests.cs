// <copyright file="ChaosEndpointsHttpTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// HTTP-level integration tests for the chaos /chaos/* endpoints. Asserts status codes
/// + JSON response shapes against the same MapChaosEndpoints registration used in
/// production. Catches contract drift that helper-level unit tests would miss.
/// </summary>
public class ChaosEndpointsHttpTests
{
    private static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement;

    private static HttpContent Body(object payload) => JsonContent.Create(payload);

    [Fact]
    public async Task GetHealthz_Returns200()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.GetAsync("/chaos/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = Json(await resp.Content.ReadAsStringAsync());
        Assert.Equal("healthy", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetState_EmptyStore_ReturnsZeroes()
    {
        await using var fx = new ChaosEndpointsFixture();

        var body = Json(await fx.Client.GetStringAsync("/chaos/state"));

        Assert.False(body.GetProperty("paused").GetBoolean());
        Assert.Equal(0, body.GetProperty("policyCount").GetInt32());
        Assert.Equal(0, body.GetProperty("totalFireCount").GetInt32());
        Assert.Empty(body.GetProperty("armedFireOnceTriggers").EnumerateArray());
    }

    [Fact]
    public async Task PostPolicy_NoBody_Returns400()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/policies", new StringContent("null", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostPolicy_NoTransforms_Returns400()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/policies", Body(new { id = "p" }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = Json(await resp.Content.ReadAsStringAsync());
        Assert.Contains("at least one transform", body.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task PostPolicy_ValidLatency_Returns200WithId()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "slow", latency = new { minMs = 100, maxMs = 200 } }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = Json(await resp.Content.ReadAsStringAsync());
        Assert.Equal("slow", body.GetProperty("id").GetString());

        // Store now has the policy.
        Assert.NotNull(fx.Store.TryGet("slow"));
    }

    [Fact]
    public async Task GetPolicies_AfterInstall_ListsIt()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "a", error = new { status = 503 } }));
        await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "b", latency = new { minMs = 10, maxMs = 20 } }));

        var body = Json(await fx.Client.GetStringAsync("/chaos/policies"));

        var policies = body.GetProperty("policies");
        Assert.Equal(2, policies.GetArrayLength());
    }

    [Fact]
    public async Task GetPolicyById_KnownId_Returns200()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "found", error = new { status = 503 } }));

        var resp = await fx.Client.GetAsync("/chaos/policies/found");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = Json(await resp.Content.ReadAsStringAsync());
        Assert.Equal("found", body.GetProperty("id").GetString());
        Assert.Equal(503, body.GetProperty("error").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task GetPolicyById_UnknownId_Returns404()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.GetAsync("/chaos/policies/nope");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DeletePolicyById_RemovesIt()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies", Body(new { id = "doomed", error = new { status = 503 } }));

        var del = await fx.Client.DeleteAsync("/chaos/policies/doomed");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var get = await fx.Client.GetAsync("/chaos/policies/doomed");
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
    }

    [Fact]
    public async Task DeleteAllPolicies_WipesStateAndReturnsCount()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies", Body(new { id = "a", error = new { status = 503 } }));
        await fx.Client.PostAsync("/chaos/policies", Body(new { id = "b", latency = new { minMs = 10, maxMs = 20 } }));

        var del = await fx.Client.DeleteAsync("/chaos/policies");

        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        var body = Json(await del.Content.ReadAsStringAsync());
        Assert.Equal(2, body.GetProperty("removed").GetInt32());

        var afterList = Json(await fx.Client.GetStringAsync("/chaos/policies"));
        Assert.Empty(afterList.GetProperty("policies").EnumerateArray());
    }

    [Fact]
    public async Task PostBulk_AllValid_InstallsAll()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/policies/bulk", Body(new[]
        {
            new { id = "a", error = new { status = 503 } },
            new { id = "b", error = new { status = 504 } },
        }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = Json(await resp.Content.ReadAsStringAsync());
        Assert.Equal(2, body.GetProperty("installed").GetInt32());
        Assert.NotNull(fx.Store.TryGet("a"));
        Assert.NotNull(fx.Store.TryGet("b"));
    }

    [Fact]
    public async Task PostBulk_OneInvalid_AbortsAll()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/policies/bulk", Body(new object[]
        {
            new { id = "valid", error = new { status = 503 } },
            new { id = "invalid" /* no transform */ },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        // First entry should NOT have been added since validation failed on the second.
        Assert.Null(fx.Store.TryGet("valid"));
    }

    [Fact]
    public async Task PostPreview_ValidPolicy_ReturnsCanonicalShapeWithoutInstalling()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/preview-policy",
            Body(new { id = "preview", error = new { status = 503 } }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = Json(await resp.Content.ReadAsStringAsync());
        Assert.Equal("preview", body.GetProperty("id").GetString());
        Assert.Equal(503, body.GetProperty("error").GetProperty("status").GetInt32());

        // Critically: NOT in the store.
        Assert.Null(fx.Store.TryGet("preview"));
    }

    [Fact]
    public async Task PostPreview_InvalidPolicy_Returns400()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/preview-policy", Body(new { id = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PauseAndResume_TogglesIsPausedFlag()
    {
        await using var fx = new ChaosEndpointsFixture();

        await fx.Client.PostAsync("/chaos/pause", null);
        Assert.True(fx.Store.IsPaused);

        await fx.Client.PostAsync("/chaos/resume", null);
        Assert.False(fx.Store.IsPaused);
    }

    [Fact]
    public async Task FireOnce_UnknownTransform_Returns400()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/fire-once?transform=bogus", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task FireOnce_ValidTransform_ArmsTrigger()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/fire-once?transform=latency", null);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.True(fx.Store.ConsumeFireOnce("latency"));
    }

    [Fact]
    public async Task PerPolicyFireOnce_UnknownId_Returns404()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/policies/nope/fire-once?transform=latency", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task PerPolicyFireOnce_KnownId_ArmsScopedTrigger()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies", Body(new { id = "p", error = new { status = 503 } }));

        var resp = await fx.Client.PostAsync("/chaos/policies/p/fire-once?transform=error", null);

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.True(fx.Store.ConsumeFireOnceForPolicy("p", "error"));
    }

    [Fact]
    public async Task ExtendTtl_NegativeSeconds_Returns400()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/policies/x/extend?seconds=-1", null);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task ExtendTtl_UnknownId_Returns404()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/policies/nope/extend?seconds=60", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task ExtendTtl_KnownId_Returns200WithNewExpiry()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "p", latency = new { minMs = 1, maxMs = 2 }, ttlSeconds = 30 }));

        var resp = await fx.Client.PostAsync("/chaos/policies/p/extend?seconds=600", null);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = Json(await resp.Content.ReadAsStringAsync());
        Assert.Equal("p", body.GetProperty("id").GetString());
        Assert.True(body.TryGetProperty("expiresAt", out _));
    }

    [Fact]
    public async Task DeleteFireCounts_UnknownId_Returns404()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.DeleteAsync("/chaos/policies/nope/fire-counts");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DeleteFireCounts_KnownId_ResetsCounts()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "p", error = new { status = 503 } }));
        fx.Store.RecordFire("p", "error");
        fx.Store.RecordFire("p", "error");
        Assert.Equal(2, fx.Store.GetFireCounts("p")["error"]);

        var resp = await fx.Client.DeleteAsync("/chaos/policies/p/fire-counts");

        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Empty(fx.Store.GetFireCounts("p"));
    }

    [Fact]
    public async Task GetFireCounts_UnknownId_Returns404()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.GetAsync("/chaos/policies/nope/fire-counts");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task GetFireCounts_KnownId_ReturnsCounts()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies", Body(new { id = "p", error = new { status = 503 } }));
        fx.Store.RecordFire("p", "error");

        var body = Json(await fx.Client.GetStringAsync("/chaos/policies/p/fire-counts"));

        Assert.Equal("p", body.GetProperty("id").GetString());
        Assert.Equal(1, body.GetProperty("fireCounts").GetProperty("error").GetInt32());
    }

    [Fact]
    public async Task PostMatch_MissingPath_Returns400()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/match", Body(new { method = "GET" }));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PostMatch_NoActivePolicies_ReturnsEmptyMatches()
    {
        await using var fx = new ChaosEndpointsFixture();
        var resp = await fx.Client.PostAsync("/chaos/match", Body(new { path = "/api/x" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = Json(await resp.Content.ReadAsStringAsync());
        Assert.Empty(body.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task PostMatch_SinglePolicy_ReturnsItsTransforms()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new
            {
                id = "p",
                matcher = new { pathPrefix = "/api/" },
                latency = new { minMs = 10, maxMs = 20 },
                error = new { status = 503 },
            }));

        var resp = await fx.Client.PostAsync("/chaos/match", Body(new { path = "/api/x" }));
        var body = Json(await resp.Content.ReadAsStringAsync());
        var matches = body.GetProperty("matches");

        Assert.Equal(1, matches.GetArrayLength());
        var match = matches[0];
        Assert.Equal("p", match.GetProperty("policyId").GetString());
        var transforms = match.GetProperty("transformsThatWouldFire").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("latency", transforms);
        Assert.Contains("error", transforms);
    }

    [Fact]
    public async Task PostMatch_PathDoesNotMatch_OmitsThePolicy()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new
            {
                id = "scoped",
                matcher = new { pathPrefix = "/api/" },
                error = new { status = 503 },
            }));

        var resp = await fx.Client.PostAsync("/chaos/match", Body(new { path = "/different" }));
        var body = Json(await resp.Content.ReadAsStringAsync());

        Assert.Empty(body.GetProperty("matches").EnumerateArray());
    }

    [Fact]
    public async Task PostMatch_HeaderRequired_DoesNotMatchWithoutHeader()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new
            {
                id = "tenanted",
                matcher = new { pathPrefix = "/api/", headerEquals = new { TenantId = "flaky" } },
                error = new { status = 503 },
            }));

        // Without the header - no match.
        var without = Json(await (await fx.Client.PostAsync("/chaos/match", Body(new { path = "/api/x" }))).Content.ReadAsStringAsync());
        Assert.Empty(without.GetProperty("matches").EnumerateArray());

        // With the header - match.
        var with = Json(await (await fx.Client.PostAsync("/chaos/match", Body(new
        {
            path = "/api/x",
            headers = new Dictionary<string, string> { ["TenantId"] = "flaky" },
        }))).Content.ReadAsStringAsync());
        Assert.Equal(1, with.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task PostMatch_MethodFilter_Respected()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "post-only", matcher = new { method = "POST" }, error = new { status = 503 } }));

        var getResp = Json(await (await fx.Client.PostAsync("/chaos/match", Body(new { method = "GET", path = "/x" }))).Content.ReadAsStringAsync());
        var postResp = Json(await (await fx.Client.PostAsync("/chaos/match", Body(new { method = "POST", path = "/x" }))).Content.ReadAsStringAsync());

        Assert.Empty(getResp.GetProperty("matches").EnumerateArray());
        Assert.Equal(1, postResp.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task PostMatch_PathWithoutLeadingSlash_AutoPrefixed()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "p", matcher = new { pathPrefix = "/api/" }, error = new { status = 503 } }));

        // Path 'api/x' (no leading /) should still match /api/ prefix.
        var resp = Json(await (await fx.Client.PostAsync("/chaos/match", Body(new { path = "api/x" }))).Content.ReadAsStringAsync());
        Assert.Equal(1, resp.GetProperty("matches").GetArrayLength());
    }

    [Fact]
    public async Task PostMatch_DoesNotMutateAnyChaosState()
    {
        await using var fx = new ChaosEndpointsFixture();
        await fx.Client.PostAsync("/chaos/policies",
            Body(new { id = "p", error = new { status = 503, failFirst = 3 } }));

        // Match prediction should be pure - no failFirst counter increments, no fire-count
        // increments. Same prediction whether we hit it once or a hundred times.
        for (var i = 0; i < 10; i++)
        {
            _ = await fx.Client.PostAsync("/chaos/match", Body(new { path = "/api/x" }));
        }

        Assert.Empty(fx.Store.GetFireCounts("p"));
        // failFirst counter still intact - first 3 actual hits would still fire.
        Assert.True(fx.Store.ConsumeFailFirstSlot("error", "p", "k", 3));
        Assert.True(fx.Store.ConsumeFailFirstSlot("error", "p", "k", 3));
        Assert.True(fx.Store.ConsumeFailFirstSlot("error", "p", "k", 3));
        Assert.False(fx.Store.ConsumeFailFirstSlot("error", "p", "k", 3));
    }
}
