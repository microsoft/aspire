// <copyright file="ChaosRequestBodyBufferingMiddleware.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Text;
using ChaosProxy.Container.Policy;

namespace ChaosProxy.Container.Middleware;

/// <summary>
/// Runs first in the chaos pipeline. When any active policy has a body-content matcher
/// (<c>BodyContains</c>), this middleware enables ASP.NET Core request buffering, reads
/// the body up to the configured cap, decodes it as UTF-8 (lossy), and stashes the
/// result in <c>HttpContext.Items[RequestMatcher.BufferedBodyItemsKey]</c> for downstream
/// middlewares to consume via <see cref="RequestMatcher.Matches"/>.
/// </summary>
/// <remarks>
/// <para>
/// Buffering is opt-in per-request: if no installed policy has a body matcher, this
/// middleware is a no-op (single dictionary check + delegate to next). Keeps the cost
/// of body matching scoped to deployments that actually use it.
/// </para>
/// <para>
/// Bodies larger than <see cref="BufferLimitBytes"/> (1 MB) are skipped — the buffer is
/// drained back to the start so YARP can still forward, but <c>Items[BufferedBodyItemsKey]</c>
/// is left unset. The matcher treats "buffered body absent" as non-matching, which means
/// oversized requests are never tagged for chaos. This is the conservative choice: we'd
/// rather miss a fault than memory-DoS the proxy.
/// </para>
/// <para>
/// <c>/chaos/*</c> control-plane requests are passed through unchanged — they never need
/// chaos applied to themselves.
/// </para>
/// </remarks>
internal sealed class ChaosRequestBodyBufferingMiddleware
{
    /// <summary>1 MB cap on the buffered body. Larger requests skip buffering.</summary>
    public const int BufferLimitBytes = 1024 * 1024;

    private readonly RequestDelegate _next;
    private readonly ActivePolicyStore _store;
    private readonly ILogger<ChaosRequestBodyBufferingMiddleware> _logger;

    public ChaosRequestBodyBufferingMiddleware(RequestDelegate next, ActivePolicyStore store, ILogger<ChaosRequestBodyBufferingMiddleware> logger)
    {
        _next = next;
        _store = store;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/chaos"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!AnyPolicyNeedsBody())
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // ContentLength can be null for chunked-transfer requests; the read loop honors
        // the cap regardless. Skip empty bodies entirely — no point buffering nothing.
        if (context.Request.ContentLength.GetValueOrDefault() == 0 && !context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > BufferLimitBytes)
        {
            _logger.LogDebug(
                "Skipping body buffering for {Method} {Path}: ContentLength={ContentLength} exceeds {Limit}",
                context.Request.Method, context.Request.Path, context.Request.ContentLength.Value, BufferLimitBytes);
            await _next(context).ConfigureAwait(false);
            return;
        }

        context.Request.EnableBuffering(bufferThreshold: BufferLimitBytes, bufferLimit: BufferLimitBytes);

        try
        {
            // Read up to BufferLimitBytes; if we hit the cap, treat as oversize and skip.
            var buffer = new byte[BufferLimitBytes];
            var totalRead = 0;
            while (totalRead < BufferLimitBytes)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(totalRead, BufferLimitBytes - totalRead), context.RequestAborted).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }

            // Reset stream position so YARP / downstream middleware can read body again.
            context.Request.Body.Position = 0;

            if (totalRead == 0)
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            if (totalRead == BufferLimitBytes)
            {
                // Hit the cap — likely truncated. Skip body matching for this request.
                _logger.LogDebug(
                    "Body for {Method} {Path} hit buffer limit ({Limit} bytes); skipping body matcher",
                    context.Request.Method, context.Request.Path, BufferLimitBytes);
                await _next(context).ConfigureAwait(false);
                return;
            }

            // UTF-8 is the right default for Azure Storage queue messages (the request
            // body envelope is XML; the inner message text is base64-encoded). We stash
            // both the raw body AND a "decoded" form (base64-decoded MessageText if the
            // request is an Azure Queue Storage POST/PUT). The matcher concatenates both
            // for BodyContains lookups so the same matcher works regardless of whether
            // the protocol wraps payloads in base64.
            var rawBody = Encoding.UTF8.GetString(buffer, 0, totalRead);
            var augmented = TryDecodeAzureQueueMessage(rawBody, out var decoded)
                ? rawBody + "\n\u0001DECODED\u0001\n" + decoded
                : rawBody;
            context.Items[RequestMatcher.BufferedBodyItemsKey] = augmented;

            // DTFx-aware correlation: if the body looks like a DTFx queue envelope,
            // parse it and stash the result for downstream matchers (DtfxActivityName).
            // Always record TaskScheduledEvent correlations so any LATER TaskCompletedEvent
            // can be matched by activity name — regardless of whether this specific request
            // gets matched right now.
            var dtfxMsg = DtfxMessageParser.TryParse(rawBody);
            if (dtfxMsg is not null)
            {
                context.Items[RequestMatcher.DtfxParsedMessageItemsKey] = dtfxMsg;

                if (dtfxMsg.Kind == DtfxMessageParser.DtfxEventKind.TaskScheduled
                    && dtfxMsg.InstanceId is not null
                    && dtfxMsg.EventId is { } eventId
                    && dtfxMsg.ActivityName is not null)
                {
                    _store.RecordDtfxActivityName(dtfxMsg.InstanceId, eventId, dtfxMsg.ActivityName);
                    _logger.LogDebug(
                        "[CHAOS-DTFX] recorded TaskScheduled: instance={InstanceId} eventId={EventId} activity={ActivityName}",
                        dtfxMsg.InstanceId, eventId, dtfxMsg.ActivityName);
                }
                else if (dtfxMsg.Kind == DtfxMessageParser.DtfxEventKind.TaskCompleted)
                {
                    _logger.LogDebug(
                        "[CHAOS-DTFX] observed TaskCompleted: instance={InstanceId} tsid={TaskScheduledId}",
                        dtfxMsg.InstanceId, dtfxMsg.TaskScheduledId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Client aborted while we were reading; let downstream handle the cancellation.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to buffer request body for {Method} {Path}; continuing without body match", context.Request.Method, context.Request.Path);
            try
            {
                context.Request.Body.Position = 0;
            }
            catch
            {
                // Best effort — if rewind fails, downstream will see a partial body but
                // that's better than throwing 500 here.
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private bool AnyPolicyNeedsBody()
    {
        foreach (var policy in _store.GetActive())
        {
            if (policy.Matcher?.BodyContains is not null || policy.Matcher?.DtfxActivityName is not null)
            {
                return true;
            }
            // forward-then-fail re-reads the request body to forward it to upstream
            // before writing the synthesized failure to the client. Without buffering
            // the body, the middleware can't reset Body.Position and the upstream call
            // would either fail (un-seekable stream) or send an empty body.
            if (policy.ForwardThenFail is not null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// If <paramref name="rawBody"/> looks like an Azure Queue Storage message envelope
    /// (the XML <c>&lt;QueueMessage&gt;&lt;MessageText&gt;base64&lt;/MessageText&gt;&lt;/QueueMessage&gt;</c>
    /// payload that <c>Put Message</c> requires), extracts the inner text and base64-
    /// decodes it. Returns true with the decoded UTF-8 string when the shape matches and
    /// the decode succeeds; false otherwise.
    /// </summary>
    /// <remarks>
    /// This is the protocol-specific shim that lets <c>BodyContains</c> match DTFx
    /// message envelopes (the discriminator like <c>TaskCompletedEvent</c> lives inside
    /// the base64 payload, not in the outer XML). Keeping the shim in the generic
    /// buffering middleware (rather than a separate Azure-shaped middleware) means any
    /// policy installed on a proxy in front of Azure Queue Storage benefits without
    /// the author needing to know about queue-specific encoding.
    /// </remarks>
    internal static bool TryDecodeAzureQueueMessage(string rawBody, out string decoded)
    {
        decoded = string.Empty;
        if (string.IsNullOrEmpty(rawBody))
        {
            return false;
        }

        const string openTag = "<MessageText>";
        const string closeTag = "</MessageText>";

        var openIdx = rawBody.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
        if (openIdx < 0)
        {
            return false;
        }
        var startIdx = openIdx + openTag.Length;
        var closeIdx = rawBody.IndexOf(closeTag, startIdx, StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0)
        {
            return false;
        }

        var base64 = rawBody.Substring(startIdx, closeIdx - startIdx).Trim();
        if (string.IsNullOrEmpty(base64))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(base64);
            decoded = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
