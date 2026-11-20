// <copyright file="ChaosRequestBodyBufferingMiddlewareTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Text;
using ChaosProxy.Container.Middleware;
using ChaosProxy.Container.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Chaos.UnitTests;

public class ChaosRequestBodyBufferingMiddlewareTests
{
    private static (ChaosRequestBodyBufferingMiddleware middleware, ActivePolicyStore store) CreateMiddleware(out RequestDelegate next, ActivePolicy[]? policies = null)
    {
        var store = new ActivePolicyStore();
        if (policies is not null)
        {
            foreach (var p in policies)
            {
                store.Add(p);
            }
        }

        var nextCalled = false;
        next = ctx =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new ChaosRequestBodyBufferingMiddleware(next, store, NullLogger<ChaosRequestBodyBufferingMiddleware>.Instance);
        return (middleware, store);
    }

    private static DefaultHttpContext CreateRequest(string body, string method = "POST", string path = "/queue/messages", string contentType = "application/json")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = new PathString(path);
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;
        ctx.Request.ContentType = contentType;
        return ctx;
    }

    private static ActivePolicy PolicyWithBodyMatcher(string bodyContains)
    {
        return new ActivePolicy(
            Id: "test",
            Matcher: new RequestMatcher(Method: null, PathPrefix: null, PathContains: null, BodyContains: bodyContains),
            Latency: null, Error: null, ReplayDuplicate: null,
            DropResponse: new DropResponseConfig(Probability: 1.0, FailFirst: null),
            RateLimit: null, HeaderTamper: null, PartialResponse: null,
            IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null);
    }

    [Fact]
    public async Task Invoke_NoBodyMatchingPolicies_DoesNotBufferOrAlterContext()
    {
        var (middleware, _) = CreateMiddleware(out var next);
        var ctx = CreateRequest("any body");

        await middleware.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(RequestMatcher.BufferedBodyItemsKey));
        // Body stream untouched (not yet consumed since no read happened).
        Assert.Equal(0, ctx.Request.Body.Position);
    }

    [Fact]
    public async Task Invoke_PolicyWithBodyMatcher_BuffersBodyIntoItems()
    {
        var (middleware, _) = CreateMiddleware(out var next, new[] { PolicyWithBodyMatcher("TaskCompletedEvent") });
        const string body = "{\"EventType\":\"TaskCompletedEvent\",\"Result\":\"op-001\"}";
        var ctx = CreateRequest(body);

        await middleware.InvokeAsync(ctx);

        Assert.True(ctx.Items.TryGetValue(RequestMatcher.BufferedBodyItemsKey, out var stashed));
        Assert.Equal(body, stashed);
        // Stream is rewound so YARP / next middleware can read again.
        Assert.Equal(0, ctx.Request.Body.Position);
    }

    [Fact]
    public async Task Invoke_BodyIsRewoundAfterBuffering()
    {
        // Verify the body is fully re-readable after buffering by reading it via Stream.
        var (middleware, _) = CreateMiddleware(out var next, new[] { PolicyWithBodyMatcher("anything") });
        const string body = "{\"EventType\":\"TaskCompletedEvent\"}";
        var ctx = CreateRequest(body);

        await middleware.InvokeAsync(ctx);

        using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
        var rereadBody = await reader.ReadToEndAsync();
        Assert.Equal(body, rereadBody);
    }

    [Fact]
    public async Task Invoke_ChaosControlPath_BypassesBuffering()
    {
        // /chaos/* endpoints must never have their body buffered — the control-plane
        // POST /chaos/policies endpoint reads its own body and would conflict with
        // any pre-buffering.
        var (middleware, _) = CreateMiddleware(out var next, new[] { PolicyWithBodyMatcher("anything") });
        var ctx = CreateRequest("{\"policy\":\"data\"}", path: "/chaos/policies");

        await middleware.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(RequestMatcher.BufferedBodyItemsKey));
    }

    [Fact]
    public async Task Invoke_EmptyBody_SkipsBuffering()
    {
        var (middleware, _) = CreateMiddleware(out var next, new[] { PolicyWithBodyMatcher("x") });
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = new PathString("/queue/messages");
        ctx.Request.Body = new MemoryStream(Array.Empty<byte>());
        ctx.Request.ContentLength = 0;

        await middleware.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(RequestMatcher.BufferedBodyItemsKey));
    }

    [Fact]
    public async Task Invoke_OversizeBody_SkipsBufferingButForwardsToNext()
    {
        // A body larger than the buffer limit must not be stashed, but the request must
        // still flow through to the next middleware (no 413 / no truncation).
        var (middleware, _) = CreateMiddleware(out var next, new[] { PolicyWithBodyMatcher("x") });
        var bytes = new byte[ChaosRequestBodyBufferingMiddleware.BufferLimitBytes + 1];
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "POST";
        ctx.Request.Path = new PathString("/queue/messages");
        ctx.Request.Body = new MemoryStream(bytes);
        ctx.Request.ContentLength = bytes.Length;

        await middleware.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(RequestMatcher.BufferedBodyItemsKey));
    }

    [Fact]
    public async Task Invoke_MatcherWithNoBodyContains_DoesNotTriggerBuffering()
    {
        // A policy with only path/header matchers doesn't need body buffering — verify
        // the perf-relevant fast path stays fast (no buffering, no Items population).
        var policy = new ActivePolicy(
            Id: "test",
            Matcher: new RequestMatcher(Method: "POST", PathPrefix: "/api", PathContains: null),
            Latency: null, Error: null, ReplayDuplicate: null,
            DropResponse: new DropResponseConfig(Probability: 1.0, FailFirst: null),
            RateLimit: null, HeaderTamper: null, PartialResponse: null,
            IdempotencyCollision: null, SlowResponse: null,
            ExpiresAt: null);

        var (middleware, _) = CreateMiddleware(out var next, new[] { policy });
        var ctx = CreateRequest("{\"some\":\"body\"}");

        await middleware.InvokeAsync(ctx);

        Assert.False(ctx.Items.ContainsKey(RequestMatcher.BufferedBodyItemsKey));
    }

    [Fact]
    public async Task Invoke_AzureQueueMessageEnvelope_DecodesBase64MessageTextIntoSearchableBody()
    {
        // The Azure Queue Storage Put Message API wire shape: the body is XML with
        // <MessageText>base64</MessageText> wrapping the actual queue payload. DTFx
        // queue messages have "TaskCompletedEvent" inside the base64 blob — we'd
        // never find it via a substring match on the raw XML.
        //
        // Verify the middleware base64-decodes the inner MessageText and stashes the
        // decoded form (concatenated with the raw body) so BodyContains: "TaskCompletedEvent"
        // matches the activity-completion enqueue.
        const string decodedPayload = "{\"$type\":\"DurableTask.Core.History.TaskCompletedEvent\",\"TaskScheduledId\":3,\"Result\":\"op-001\"}";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(decodedPayload));
        var queueEnvelope = $"<QueueMessage><MessageText>{base64}</MessageText></QueueMessage>";

        var (middleware, _) = CreateMiddleware(out var next, new[] { PolicyWithBodyMatcher("TaskCompletedEvent") });
        var ctx = CreateRequest(queueEnvelope, contentType: "application/xml");

        await middleware.InvokeAsync(ctx);

        Assert.True(ctx.Items.TryGetValue(RequestMatcher.BufferedBodyItemsKey, out var stashed));
        var augmented = Assert.IsType<string>(stashed);
        Assert.Contains(queueEnvelope, augmented);       // raw body preserved
        Assert.Contains("TaskCompletedEvent", augmented); // decoded form now searchable
    }

    [Fact]
    public void TryDecodeAzureQueueMessage_ValidEnvelope_ReturnsTrueAndDecodedPayload()
    {
        const string payload = "{\"EventType\":\"TaskCompletedEvent\"}";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
        var envelope = $"<QueueMessage><MessageText>{base64}</MessageText></QueueMessage>";

        var ok = ChaosRequestBodyBufferingMiddleware.TryDecodeAzureQueueMessage(envelope, out var decoded);

        Assert.True(ok);
        Assert.Equal(payload, decoded);
    }

    [Fact]
    public void TryDecodeAzureQueueMessage_NoMessageTextTag_ReturnsFalse()
    {
        var ok = ChaosRequestBodyBufferingMiddleware.TryDecodeAzureQueueMessage("{\"plain\":\"json\"}", out var decoded);
        Assert.False(ok);
        Assert.Equal(string.Empty, decoded);
    }

    [Fact]
    public void TryDecodeAzureQueueMessage_InvalidBase64_ReturnsFalse()
    {
        // The XML envelope says base64 but the inner text isn't valid base64. Should
        // not throw — just return false so the middleware leaves Items empty and
        // downstream matchers treat the request as non-matching.
        var ok = ChaosRequestBodyBufferingMiddleware.TryDecodeAzureQueueMessage(
            "<QueueMessage><MessageText>!!!not-base64!!!</MessageText></QueueMessage>",
            out var decoded);

        Assert.False(ok);
    }
}
