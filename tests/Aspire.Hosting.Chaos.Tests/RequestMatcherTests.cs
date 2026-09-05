// <copyright file="RequestMatcherTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Chaos.UnitTests;

public class RequestMatcherTests
{
    private static HttpRequest MakeRequest(string method = "GET", string path = "/")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = new PathString(path);
        return ctx.Request;
    }

    [Fact]
    public void Matches_NoConstraints_MatchesAnyRequest()
    {
        var matcher = new RequestMatcher(Method: null, PathPrefix: null, PathContains: null);
        Assert.True(matcher.Matches(MakeRequest("GET", "/anything")));
        Assert.True(matcher.Matches(MakeRequest("POST", "/other")));
    }

    [Theory]
    [InlineData("GET", "GET", true)]
    [InlineData("get", "GET", true)]
    [InlineData("GET", "POST", false)]
    [InlineData("POST", "GET", false)]
    public void Matches_MethodConstraint_FiltersByMethodCaseInsensitive(string actualMethod, string matcherMethod, bool expected)
    {
        var matcher = new RequestMatcher(Method: matcherMethod, PathPrefix: null, PathContains: null);
        Assert.Equal(expected, matcher.Matches(MakeRequest(actualMethod, "/any")));
    }

    [Theory]
    [InlineData("/api/foo", "/api", true)]
    [InlineData("/api", "/api", true)]
    [InlineData("/API/foo", "/api", true)]   // case-insensitive
    [InlineData("/different", "/api", false)]
    [InlineData("/", "/api", false)]
    public void Matches_PathPrefix_FiltersByPrefixCaseInsensitive(string requestPath, string prefix, bool expected)
    {
        var matcher = new RequestMatcher(Method: null, PathPrefix: prefix, PathContains: null);
        Assert.Equal(expected, matcher.Matches(MakeRequest(path: requestPath)));
    }

    [Fact]
    public void Matches_PathPrefix_NonSegmentBoundary_StillMatches()
    {
        // Per design notes: PathPrefix uses plain StartsWith, NOT segment-aware matching.
        // This means "/test-" matches "/test-foo" (the "-foo" continues the segment).
        var matcher = new RequestMatcher(Method: null, PathPrefix: "/test-", PathContains: null);
        Assert.True(matcher.Matches(MakeRequest(path: "/test-foo")));
        Assert.True(matcher.Matches(MakeRequest(path: "/test-bar/baz")));
    }

    [Theory]
    [InlineData("/api/foo/bar", "foo", true)]
    [InlineData("/api/foo/bar", "FOO", true)] // case-insensitive
    [InlineData("/api/bar", "foo", false)]
    public void Matches_PathContains_FiltersBySubstring(string requestPath, string contains, bool expected)
    {
        var matcher = new RequestMatcher(Method: null, PathPrefix: null, PathContains: contains);
        Assert.Equal(expected, matcher.Matches(MakeRequest(path: requestPath)));
    }

    [Fact]
    public void Matches_AllConstraints_AllMustMatch()
    {
        var matcher = new RequestMatcher(Method: "POST", PathPrefix: "/api/v1", PathContains: "/things");

        Assert.True(matcher.Matches(MakeRequest("POST", "/api/v1/things/123")));
        Assert.False(matcher.Matches(MakeRequest("GET", "/api/v1/things/123")));     // wrong method
        Assert.False(matcher.Matches(MakeRequest("POST", "/api/v2/things/123")));    // wrong prefix
        Assert.False(matcher.Matches(MakeRequest("POST", "/api/v1/widgets/123")));   // missing contains
    }

    private static HttpRequest MakeRequestWithHeader(string headerName, string headerValue)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = new PathString("/");
        ctx.Request.Headers[headerName] = headerValue;
        return ctx.Request;
    }

    [Fact]
    public void Matches_HeaderEquals_RequiresExactCaseInsensitiveMatch()
    {
        var matcher = new RequestMatcher(
            Method: null, PathPrefix: null, PathContains: null,
            HeaderEquals: new Dictionary<string, string> { ["X-Tenant-Id"] = "test-tenant" });

        Assert.True(matcher.Matches(MakeRequestWithHeader("X-Tenant-Id", "test-tenant")));
        Assert.True(matcher.Matches(MakeRequestWithHeader("X-Tenant-Id", "TEST-TENANT"))); // case-insensitive value
        Assert.True(matcher.Matches(MakeRequestWithHeader("x-tenant-id", "test-tenant"))); // case-insensitive name
        Assert.False(matcher.Matches(MakeRequestWithHeader("X-Tenant-Id", "different")));
        Assert.False(matcher.Matches(MakeRequest()));  // header missing entirely
    }

    [Fact]
    public void Matches_HeaderEquals_AllHeadersMustMatch()
    {
        var matcher = new RequestMatcher(
            Method: null, PathPrefix: null, PathContains: null,
            HeaderEquals: new Dictionary<string, string>
            {
                ["X-Tenant-Id"] = "test-tenant",
                ["X-Environment"] = "staging",
            });

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = new PathString("/");
        ctx.Request.Headers["X-Tenant-Id"] = "test-tenant";
        ctx.Request.Headers["X-Environment"] = "staging";
        Assert.True(matcher.Matches(ctx.Request));

        var partial = new DefaultHttpContext();
        partial.Request.Method = "GET";
        partial.Request.Path = new PathString("/");
        partial.Request.Headers["X-Tenant-Id"] = "test-tenant";
        // Missing X-Environment
        Assert.False(matcher.Matches(partial.Request));
    }

    [Fact]
    public void Matches_HeaderContains_RequiresCaseInsensitiveSubstring()
    {
        var matcher = new RequestMatcher(
            Method: null, PathPrefix: null, PathContains: null,
            HeaderContains: new Dictionary<string, string> { ["User-Agent"] = "Postman" });

        Assert.True(matcher.Matches(MakeRequestWithHeader("User-Agent", "PostmanRuntime/7.32.0")));
        Assert.True(matcher.Matches(MakeRequestWithHeader("User-Agent", "POSTMANCANARY/1.0"))); // case-insensitive
        Assert.False(matcher.Matches(MakeRequestWithHeader("User-Agent", "curl/8.0.0")));
        Assert.False(matcher.Matches(MakeRequest())); // header missing
    }

    [Fact]
    public void Matches_HeaderEqualsCombinedWithPathPrefix_BothMustMatch()
    {
        var matcher = new RequestMatcher(
            Method: null, PathPrefix: "/api", PathContains: null,
            HeaderEquals: new Dictionary<string, string> { ["X-Tenant-Id"] = "test-tenant" });

        var matched = new DefaultHttpContext();
        matched.Request.Method = "GET";
        matched.Request.Path = new PathString("/api/foo");
        matched.Request.Headers["X-Tenant-Id"] = "test-tenant";
        Assert.True(matcher.Matches(matched.Request));

        var wrongPath = new DefaultHttpContext();
        wrongPath.Request.Method = "GET";
        wrongPath.Request.Path = new PathString("/other");
        wrongPath.Request.Headers["X-Tenant-Id"] = "test-tenant";
        Assert.False(matcher.Matches(wrongPath.Request));

        var wrongHeader = new DefaultHttpContext();
        wrongHeader.Request.Method = "GET";
        wrongHeader.Request.Path = new PathString("/api/foo");
        wrongHeader.Request.Headers["X-Tenant-Id"] = "other";
        Assert.False(matcher.Matches(wrongHeader.Request));
    }

    // ---------------- BodyContains matcher ----------------

    private static HttpRequest MakeRequestWithBufferedBody(string body, string method = "POST", string path = "/")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = new PathString(path);
        ctx.Items[RequestMatcher.BufferedBodyItemsKey] = body;
        return ctx.Request;
    }

    [Fact]
    public void Matches_BodyContains_FindsCaseInsensitiveSubstringInBufferedBody()
    {
        var matcher = new RequestMatcher(
            Method: null, PathPrefix: null, PathContains: null,
            BodyContains: "TaskCompletedEvent");

        Assert.True(matcher.Matches(MakeRequestWithBufferedBody("{\"EventType\":\"TaskCompletedEvent\",\"TaskScheduledId\":3}")));
        Assert.True(matcher.Matches(MakeRequestWithBufferedBody("{\"eventtype\":\"taskcompletedevent\"}"))); // case-insensitive
        Assert.False(matcher.Matches(MakeRequestWithBufferedBody("{\"EventType\":\"TaskScheduledEvent\"}"))); // wrong event type
    }

    [Fact]
    public void Matches_BodyContains_WithNoBufferedBody_DoesNotMatch()
    {
        // The buffering middleware didn't run (oversize body, or middleware not registered).
        // Treat as non-matching — never silently degrade to "match anything".
        var matcher = new RequestMatcher(
            Method: null, PathPrefix: null, PathContains: null,
            BodyContains: "TaskCompletedEvent");

        Assert.False(matcher.Matches(MakeRequest("POST", "/queue")));
    }

    [Fact]
    public void Matches_BodyContains_WithNonStringItem_DoesNotMatch()
    {
        // Defensive: if HttpContext.Items has the key but the value is some other type
        // (corrupt state), don't crash and don't match.
        var matcher = new RequestMatcher(
            Method: null, PathPrefix: null, PathContains: null,
            BodyContains: "TaskCompletedEvent");

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Items[RequestMatcher.BufferedBodyItemsKey] = new byte[] { 1, 2, 3 };
        Assert.False(matcher.Matches(ctx.Request));
    }

    [Fact]
    public void Matches_BodyContainsCombinedWithMethodAndPath_AllMustMatch()
    {
        // The realistic AB#37560122 matcher shape: only drop POSTs to the control queue
        // that contain a TaskCompletedEvent. This combination excludes orchestrator
        // scheduling messages (which also POST to the same queue but have
        // TaskScheduledEvent in the body).
        var matcher = new RequestMatcher(
            Method: "POST",
            PathPrefix: null,
            PathContains: "armgatewayserviceworkerhub-control-",
            BodyContains: "TaskCompletedEvent");

        var activityCompleted = MakeRequestWithBufferedBody(
            body: "{\"EventType\":\"TaskCompletedEvent\",\"Result\":\"op-001\"}",
            method: "POST",
            path: "/devstoreaccount1/armgatewayserviceworkerhub-control-00/messages");
        Assert.True(matcher.Matches(activityCompleted));

        var orchScheduling = MakeRequestWithBufferedBody(
            body: "{\"EventType\":\"ExecutionStartedEvent\"}",
            method: "POST",
            path: "/devstoreaccount1/armgatewayserviceworkerhub-control-00/messages");
        Assert.False(matcher.Matches(orchScheduling)); // right path, wrong body

        var unrelatedDelete = MakeRequestWithBufferedBody(
            body: "{\"EventType\":\"TaskCompletedEvent\"}",
            method: "DELETE",
            path: "/devstoreaccount1/armgatewayserviceworkerhub-control-00/messages/abc");
        Assert.False(matcher.Matches(unrelatedDelete)); // wrong method
    }

    // ---------------- DtfxActivityName matcher ----------------

    private static HttpRequest MakeRequestWithDtfxParsed(DtfxMessageParser.DtfxMessage parsed, ActivePolicyStore store, string method = "POST", string path = "/queue")
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddSingleton(store)
            .BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = method;
        ctx.Request.Path = new PathString(path);
        ctx.Items[RequestMatcher.DtfxParsedMessageItemsKey] = parsed;
        return ctx.Request;
    }

    [Fact]
    public void Matches_DtfxActivityName_FiresWhenCorrelationFound()
    {
        var store = new ActivePolicyStore();
        store.RecordDtfxActivityName("instance-1", 3, "TriggerScenarioEvaluation");

        var matcher = new RequestMatcher(Method: "POST", PathPrefix: null, PathContains: null,
            DtfxActivityName: "TriggerScenarioEvaluation");

        var completion = new DtfxMessageParser.DtfxMessage(
            Kind: DtfxMessageParser.DtfxEventKind.TaskCompleted,
            InstanceId: "instance-1",
            ExecutionId: "exec-1",
            EventId: -1,
            TaskScheduledId: 3,
            ActivityName: null);

        Assert.True(matcher.Matches(MakeRequestWithDtfxParsed(completion, store)));
    }

    [Fact]
    public void Matches_DtfxActivityName_DoesNotFireForDifferentActivity()
    {
        var store = new ActivePolicyStore();
        store.RecordDtfxActivityName("instance-1", 3, "UpdateManagedIdentity");

        var matcher = new RequestMatcher(Method: "POST", PathPrefix: null, PathContains: null,
            DtfxActivityName: "TriggerScenarioEvaluation");

        var completion = new DtfxMessageParser.DtfxMessage(
            Kind: DtfxMessageParser.DtfxEventKind.TaskCompleted,
            InstanceId: "instance-1",
            ExecutionId: "exec-1",
            EventId: -1,
            TaskScheduledId: 3,
            ActivityName: null);

        Assert.False(matcher.Matches(MakeRequestWithDtfxParsed(completion, store)));
    }

    [Fact]
    public void Matches_DtfxActivityName_DoesNotFireOnTaskScheduledEvents()
    {
        // Schedule events don't carry TaskScheduledId (they ARE the schedule); the
        // matcher only fires on TaskCompletedEvent (the back-reference).
        var store = new ActivePolicyStore();
        store.RecordDtfxActivityName("instance-1", 3, "TriggerScenarioEvaluation");

        var matcher = new RequestMatcher(Method: "POST", PathPrefix: null, PathContains: null,
            DtfxActivityName: "TriggerScenarioEvaluation");

        var scheduled = new DtfxMessageParser.DtfxMessage(
            Kind: DtfxMessageParser.DtfxEventKind.TaskScheduled,
            InstanceId: "instance-1",
            ExecutionId: "exec-1",
            EventId: 3,
            TaskScheduledId: null,
            ActivityName: "TriggerScenarioEvaluation");

        Assert.False(matcher.Matches(MakeRequestWithDtfxParsed(scheduled, store)));
    }

    [Fact]
    public void Matches_DtfxActivityName_DoesNotFireWhenNoCorrelationRecorded()
    {
        // No prior RecordDtfxActivityName call → lookup misses → no fire. This is the
        // common case before chaos is armed and is the correct behavior.
        var store = new ActivePolicyStore();

        var matcher = new RequestMatcher(Method: "POST", PathPrefix: null, PathContains: null,
            DtfxActivityName: "TriggerScenarioEvaluation");

        var completion = new DtfxMessageParser.DtfxMessage(
            Kind: DtfxMessageParser.DtfxEventKind.TaskCompleted,
            InstanceId: "instance-1",
            ExecutionId: "exec-1",
            EventId: -1,
            TaskScheduledId: 3,
            ActivityName: null);

        Assert.False(matcher.Matches(MakeRequestWithDtfxParsed(completion, store)));
    }

    [Fact]
    public void Matches_DtfxActivityName_CrossOrchestrationsCorrelateIndependently()
    {
        // (instance-1, 3) -> "A" and (instance-2, 3) -> "B". A completion event with
        // taskScheduledId=3 from instance-1 matches "A"; from instance-2 matches "B".
        var store = new ActivePolicyStore();
        store.RecordDtfxActivityName("instance-1", 3, "ActivityA");
        store.RecordDtfxActivityName("instance-2", 3, "ActivityB");

        var matcherA = new RequestMatcher(Method: "POST", PathPrefix: null, PathContains: null,
            DtfxActivityName: "ActivityA");
        var matcherB = new RequestMatcher(Method: "POST", PathPrefix: null, PathContains: null,
            DtfxActivityName: "ActivityB");

        var completionFromI1 = new DtfxMessageParser.DtfxMessage(
            Kind: DtfxMessageParser.DtfxEventKind.TaskCompleted,
            InstanceId: "instance-1",
            ExecutionId: "exec-1",
            EventId: -1,
            TaskScheduledId: 3,
            ActivityName: null);
        var completionFromI2 = completionFromI1 with { InstanceId = "instance-2" };

        Assert.True(matcherA.Matches(MakeRequestWithDtfxParsed(completionFromI1, store)));
        Assert.False(matcherB.Matches(MakeRequestWithDtfxParsed(completionFromI1, store)));
        Assert.False(matcherA.Matches(MakeRequestWithDtfxParsed(completionFromI2, store)));
        Assert.True(matcherB.Matches(MakeRequestWithDtfxParsed(completionFromI2, store)));
    }
}
