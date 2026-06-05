// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// A minimal NuGet v3 server that proxies <c>api.nuget.org</c> and overlays
/// a directory of locally-built <c>.nupkg</c> files on top of the upstream
/// responses. Implements only the endpoints the CLI's restore /
/// <c>dotnet package search</c> code paths actually call: service index,
/// flatcontainer (versions + nupkg + nuspec), registration index, search,
/// autocomplete. Symbol packages, push, statistics, and vulnerability
/// metadata are deliberately out of scope.
/// </summary>
/// <remarks>
/// <para>
/// Overlay semantics: <b>local always wins</b>. If the local directory
/// contains <c>Aspire.Hosting/9.5.0-dogfood</c>, every endpoint serves the
/// local file/nuspec/metadata for that <c>(id, version)</c> pair regardless
/// of what nuget.org would have returned. Other versions of the same id are
/// proxied transparently.
/// </para>
/// <para>
/// Per-session usage:
/// <code>
/// await using var server = new DogfoodingNuGetServer(loggerFactory)
///     .AddUpstream(new Uri("https://api.nuget.org/v3/index.json"))
///     .AddLocalOverrides(packageDir);
/// await server.StartAsync(ct);
/// var serviceIndex = server.ServiceIndexUri!;
/// </code>
/// </para>
/// <para>
/// Observability: every incoming request fires
/// <see cref="RequestStarted"/> + <see cref="RequestCompleted"/> events from
/// a single Minimal API middleware wrapper, so the SessionNuGet traffic
/// analyzer sees uniform coverage independent of which endpoint handler
/// produced the response. A bounded ring buffer (<see cref="RecentEvents"/>)
/// lets a TUI tab opened mid-session backfill recent history.
/// </para>
/// </remarks>
internal sealed class DogfoodingNuGetServer : IAsyncDisposable
{
    public DogfoodingNuGetServer(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory;
    }

    private readonly ILoggerFactory? _loggerFactory;
    private Uri? _upstreamServiceIndex;
    private string? _localDir;

    private WebApplication? _app;
    private HttpClient? _upstreamClient;
    private UpstreamResources? _upstreamResources;
    private readonly Dictionary<string, List<LocalPackage>> _localById = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<DogfoodingNuGetTrafficEvent> _ring = new();
    private const int RingCapacity = 500;

    /// <summary>Service-index URL clients should use. Null until <see cref="StartAsync"/> completes.</summary>
    public Uri? ServiceIndexUri { get; private set; }

    /// <summary>Count of (id, version) pairs loaded from the local overrides directory.</summary>
    public int LocalPackageCount => _localById.Values.Sum(v => v.Count);

    /// <summary>Distinct local override package ids.</summary>
    public int LocalPackageIdCount => _localById.Count;

    /// <summary>Path the local overrides were loaded from (echoed back in the traffic UI summary).</summary>
    public string? LocalOverridesDirectory => _localDir;

    /// <summary>Upstream feed URL the server is proxying.</summary>
    public Uri? UpstreamServiceIndex => _upstreamServiceIndex;

    /// <summary>Fired when a new request begins. Synchronous; handlers must not block.</summary>
    public event Action<DogfoodingNuGetRequest>? RequestStarted;

    /// <summary>Fired when a response has been fully written. Synchronous; handlers must not block.</summary>
    public event Action<DogfoodingNuGetResponse>? RequestCompleted;

    /// <summary>Most recent <see cref="RingCapacity"/> events (request+response pairs), oldest first.</summary>
    public IReadOnlyList<DogfoodingNuGetTrafficEvent> RecentEvents => _ring.ToArray();

    /// <summary>
    /// Fired once per request after the response has been written AND the
    /// event has been added to the ring buffer. Preferred over
    /// <see cref="RequestCompleted"/> for UI integration because the
    /// payload is the already-paired request/response — no follow-up
    /// lookup required.
    /// </summary>
    public event Action<DogfoodingNuGetTrafficEvent>? TrafficObserved;

    /// <summary>Sets the upstream nuget v3 feed. Required.</summary>
    public DogfoodingNuGetServer AddUpstream(Uri upstreamServiceIndex)
    {
        _upstreamServiceIndex = upstreamServiceIndex;
        return this;
    }

    /// <summary>
    /// Snapshots <paramref name="directory"/> for <c>.nupkg</c> overrides.
    /// Called multiple times accumulates; subsequent calls replace earlier
    /// versions if the same (id, version) appears in a later directory.
    /// Missing directories are silently ignored (a build that produced no
    /// packages should still let the server start and serve nuget.org-only
    /// responses).
    /// </summary>
    public DogfoodingNuGetServer AddLocalOverrides(string? directory)
    {
        _localDir = directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return this;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*.nupkg", SearchOption.TopDirectoryOnly))
        {
            // Skip symbol packages so they don't pollute the overlay map with
            // ".symbols.nupkg" entries that would shadow the real nupkg.
            if (file.EndsWith(".symbols.nupkg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryReadLocalPackage(file, out var local))
            {
                if (!_localById.TryGetValue(local.Id, out var list))
                {
                    list = new List<LocalPackage>();
                    _localById[local.Id] = list;
                }
                // Newer call wins on duplicate (id, version) — preserves the
                // documented "AddLocalOverrides may be called multiple times,
                // last wins" contract.
                list.RemoveAll(p => string.Equals(p.Version, local.Version, StringComparison.OrdinalIgnoreCase));
                list.Add(local);
            }
        }

        return this;
    }

    private const long PayloadCaptureLimitBytes = 32 * 1024;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_upstreamServiceIndex is null)
        {
            throw new InvalidOperationException("AddUpstream must be called before StartAsync.");
        }
        if (_app is not null)
        {
            throw new InvalidOperationException("Server already started.");
        }

        // Use CreateBuilder (not CreateSlimBuilder) because the slim
        // builder omits the Kestrel HTTPS configuration loader, leading
        // to a runtime "Call UseKestrelHttpsConfiguration() to enable
        // HTTPS configuration" error when we bind to https:// below.
        // The full builder wires it up by default. The startup cost
        // difference is irrelevant for the Dogfooder TUI.
        var builder = WebApplication.CreateBuilder();
        if (_loggerFactory is not null)
        {
            builder.Services.AddSingleton(_loggerFactory);
        }
        // Quiet down request logs — the Dogfooder TUI already renders every
        // request via the events; duplicate console spam would just push the
        // hosting noise above the TUI render area.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        _app = app;
        // Bind to a 127.0.0.1 ephemeral HTTPS port. We deliberately use
        // https rather than http because dotnet 9's CLI feed plumbing
        // requires `allowInsecureConnections="true"` on any cleartext
        // package source registered via the standalone CLI config — a flag
        // the Aspire CLI's own ConfigureNuGetSourcesAsync path does not
        // currently set. Falling back to HTTPS sidesteps the whole gate.
        //
        // Kestrel's default certificate provider auto-resolves the
        // `dotnet dev-certs https` cert from the user store, so this works
        // out of the box once the user has run `dotnet dev-certs https
        // --trust` (a normal dev-loop prerequisite that the
        // EnvironmentValidationScreen checks for separately). The bound
        // port is read back from IServerAddressesFeature after StartAsync
        // since :0 doesn't surface the chosen value any other way.
        app.Urls.Add("https://127.0.0.1:0");

        _upstreamClient = new HttpClient
        {
            // The upstream registration index for a popular package can be
            // ~10MB; the default 100s timeout is fine.
            BaseAddress = null,
        };

        // Single shared middleware fires RequestStarted/Completed for every
        // request, including 404s and exceptions, so the traffic analyzer's
        // coverage is endpoint-independent. Per-endpoint handlers stash their
        // classification into HttpContext.Items so this middleware can record
        // it on the response event without needing per-route plumbing.
        app.Use(async (ctx, next) =>
        {
            var id = Guid.NewGuid();
            var startedAt = DateTimeOffset.Now;
            var kind = ClassifyEndpoint(ctx.Request.Path);
            var req = new DogfoodingNuGetRequest(id, startedAt, ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : null, kind);
            RequestStarted?.Invoke(req);

            var sw = Stopwatch.StartNew();
            var handlerState = new HandlerState();
            ctx.Items["__dogfood_state"] = handlerState;

            // Count the response body bytes by inserting a CountingStream
            // ahead of the framework's writer. The same wrapper also
            // captures the first PayloadCaptureLimitBytes of the body for
            // the inspector — only when the handler has opted in via
            // MarkCaptureOutgoing (binary endpoints like .nupkg skip the
            // capture so we don't buffer hundreds of MB on every restore).
            var originalBody = ctx.Response.Body;
            var counting = new CountingStream(originalBody, PayloadCaptureLimitBytes);
            ctx.Response.Body = counting;
            handlerState.OutgoingStream = counting;

            string? error = null;
            try
            {
                await next().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                handlerState.Source = DogfoodingNuGetSource.Error;
                throw;
            }
            finally
            {
                ctx.Response.Body = originalBody;
                sw.Stop();
                var outgoingBytes = handlerState.CaptureOutgoing ? counting.CapturedBytes : null;
                var resp = new DogfoodingNuGetResponse(
                    Id: id,
                    CompletedAt: DateTimeOffset.Now,
                    StatusCode: ctx.Response.StatusCode,
                    BodyBytes: counting.BytesWritten,
                    Duration: sw.Elapsed,
                    Source: handlerState.Source ?? DogfoodingNuGetSource.Upstream,
                    UpstreamUrl: handlerState.UpstreamUrl,
                    LocalPackagesUsed: handlerState.LocalPackagesUsed,
                    ErrorMessage: error,
                    SearchStats: handlerState.SearchStats,
                    Payloads: new DogfoodingNuGetPayloads(
                        UpstreamMethod: handlerState.UpstreamMethod,
                        UpstreamUrl: handlerState.UpstreamUrl,
                        UpstreamStatus: handlerState.UpstreamStatus,
                        UpstreamContentType: handlerState.UpstreamContentType,
                        UpstreamBody: handlerState.UpstreamBody,
                        UpstreamBodyTruncated: handlerState.UpstreamBodyTruncated,
                        OutgoingContentType: ctx.Response.ContentType,
                        OutgoingBody: outgoingBytes is null ? null : TryDecodeText(outgoingBytes),
                        OutgoingBodyTruncated: counting.Truncated,
                        Transformations: handlerState.Transformations.ToArray()));
                RequestCompleted?.Invoke(resp);
                // Fall back to an UnknownDetails envelope so the analyzer
                // panel can render *something* for endpoints that didn't
                // set their own details (e.g. exception paths or routes we
                // haven't yet enriched).
                var details = handlerState.Details ?? new UnknownDetails(ctx.Request.Path + (ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value : ""));
                var pair = new DogfoodingNuGetTrafficEvent(req, resp, details);
                EnqueueEvent(pair);
                TrafficObserved?.Invoke(pair);
            }
        });

        MapEndpoints(app);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not surface IServerAddressesFeature.");
        var bound = addresses.Addresses.First();
        ServiceIndexUri = new Uri(new Uri(bound), "/v3/index.json");

        // Resolve the upstream service index up front (fail-fast if nuget.org
        // is unreachable). The resolved resource URLs are cached for the
        // server's lifetime — nuget.org's service index is stable enough that
        // we do not re-resolve.
        _upstreamResources = await ResolveUpstreamAsync(_upstreamServiceIndex!, _upstreamClient, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try
            {
                await _app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort shutdown.
            }
            await ((IAsyncDisposable)_app).DisposeAsync().ConfigureAwait(false);
        }
        _upstreamClient?.Dispose();
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/v3/index.json", (HttpContext ctx) =>
        {
            var baseUri = new Uri(ServiceIndexUri!, "/").ToString().TrimEnd('/');
            // Resource @types follow the v3 spec — see
            // https://learn.microsoft.com/nuget/api/service-index#resources.
            // We only publish the resources the CLI's restore + search paths
            // actually consume; anything else falls through to a 404 in
            // upstream's actual service index when the client follows our URL.
            var json = $$"""
            {
              "version": "3.0.0",
              "resources": [
                { "@id": "{{baseUri}}/v3/flat/", "@type": "PackageBaseAddress/3.0.0" },
                { "@id": "{{baseUri}}/v3/reg/",  "@type": "RegistrationsBaseUrl" },
                { "@id": "{{baseUri}}/v3/reg/",  "@type": "RegistrationsBaseUrl/3.0.0-beta" },
                { "@id": "{{baseUri}}/v3/reg/",  "@type": "RegistrationsBaseUrl/3.0.0-rc" },
                { "@id": "{{baseUri}}/v3/reg/",  "@type": "RegistrationsBaseUrl/3.4.0" },
                { "@id": "{{baseUri}}/v3/reg/",  "@type": "RegistrationsBaseUrl/3.6.0" },
                { "@id": "{{baseUri}}/v3/search", "@type": "SearchQueryService" },
                { "@id": "{{baseUri}}/v3/search", "@type": "SearchQueryService/3.0.0-beta" },
                { "@id": "{{baseUri}}/v3/search", "@type": "SearchQueryService/3.0.0-rc" },
                { "@id": "{{baseUri}}/v3/autocomplete", "@type": "SearchAutocompleteService" },
                { "@id": "{{baseUri}}/v3/autocomplete", "@type": "SearchAutocompleteService/3.0.0-beta" },
                { "@id": "{{baseUri}}/v3/autocomplete", "@type": "SearchAutocompleteService/3.0.0-rc" }
            ]
            }
            """;
            MarkSource(ctx, DogfoodingNuGetSource.Synthesised);
            MarkDetails(ctx, new ServiceIndexDetails());
            Transform(ctx, "Synthesised service index with all resource @ids pointing back at the proxy.");
            MarkCaptureOutgoing(ctx);
            return Results.Text(json, "application/json");
        });

        app.MapGet("/v3/flat/{id}/index.json", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            MarkCaptureOutgoing(ctx);
            var lower = id.ToLowerInvariant();
            // Versions list: union upstream versions with our local override
            // versions. Local-only ids still get a valid response so the CLI
            // can restore a package that has never been published.
            JsonObject? upstreamJson = null;
            var upstream = $"{_upstreamResources!.FlatContainerBase}{lower}/index.json";
            try
            {
                using var upstreamResp = await _upstreamClient!.GetAsync(upstream, ct).ConfigureAwait(false);
                MarkUpstream(ctx, upstream);
                var (text, truncated) = await ReadBodyCappedAsync(upstreamResp, ct).ConfigureAwait(false);
                MarkUpstreamResponse(ctx, (int)upstreamResp.StatusCode, upstreamResp.Content.Headers.ContentType?.ToString(), text, truncated);
                if (upstreamResp.IsSuccessStatusCode && text is not null)
                {
                    upstreamJson = JsonNode.Parse(text) as JsonObject;
                }
            }
            catch
            {
                // Upstream failure for a known-local-only id is still success.
            }

            var versions = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            if (upstreamJson?["versions"] is JsonArray arr)
            {
                foreach (var v in arr)
                {
                    if (v?.GetValue<string>() is { } s)
                    {
                        versions.Add(s);
                    }
                }
            }

            if (_localById.TryGetValue(lower, out var locals))
            {
                foreach (var p in locals)
                {
                    versions.Add(p.Version);
                }
                Transform(ctx, $"Unioned {locals.Count} local version(s) with {(upstreamJson?["versions"] as JsonArray)?.Count ?? 0} upstream version(s) for '{lower}'.");
                MarkSource(ctx, upstreamJson is null ? DogfoodingNuGetSource.LocalOverride : DogfoodingNuGetSource.Synthesised);
            }
            else if (upstreamJson is null)
            {
                Transform(ctx, $"Upstream {upstream} returned non-success and no local override found — replying 404.");
                return Results.NotFound();
            }
            else
            {
                MarkSource(ctx, DogfoodingNuGetSource.Upstream);
            }

            var versionList = versions.ToArray();
            MarkDetails(ctx, new FlatVersionsDetails(
                PackageId: lower,
                Versions: versionList,
                UpstreamVersionCount: upstreamJson?["versions"] is JsonArray ua ? ua.Count : 0,
                LocalVersionCount: _localById.TryGetValue(lower, out var lc) ? lc.Count : 0));

            return Results.Json(new { versions = versionList });
        });

        app.MapGet("/v3/flat/{id}/{version}/{filename}", async (string id, string version, string filename, HttpContext ctx, CancellationToken ct) =>
        {
            var lower = id.ToLowerInvariant();
            var verLower = version.ToLowerInvariant();
            // Local match? Stream the bytes directly. The nuspec sub-path is
            // the same physical file (a .nupkg is a ZIP containing the .nuspec)
            // — we extract on demand rather than caching the extracted bytes.
            if (_localById.TryGetValue(lower, out var locals)
                && locals.FirstOrDefault(p => string.Equals(p.Version, verLower, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                MarkSource(ctx, DogfoodingNuGetSource.LocalOverride);
                MarkLocalPackagesUsed(ctx, [$"{match.Id}/{match.Version}"]);

                if (filename.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                {
                    MarkDetails(ctx, new NuspecDetails(match.Id, match.Version, match.FilePath, UpstreamUrl: null));
                    Transform(ctx, $"Served local .nuspec from {match.FilePath} (no upstream fetch).");
                    MarkCaptureOutgoing(ctx);
                    return Results.Text(match.NuspecXml, "application/xml");
                }
                if (filename.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    MarkDetails(ctx, new NupkgDetails(match.Id, match.Version, match.FilePath, UpstreamUrl: null));
                    Transform(ctx, $"Served local .nupkg from {match.FilePath} (binary; body capture skipped).");
                    return Results.File(match.FilePath, "application/octet-stream", enableRangeProcessing: true);
                }
                return Results.NotFound();
            }

            // Proxy upstream.
            var upstream = $"{_upstreamResources!.FlatContainerBase}{lower}/{verLower}/{filename}";
            MarkUpstream(ctx, upstream);
            if (filename.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            {
                MarkDetails(ctx, new NuspecDetails(lower, verLower, LocalFilePath: null, UpstreamUrl: upstream));
                MarkCaptureOutgoing(ctx);
            }
            else if (filename.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
            {
                MarkDetails(ctx, new NupkgDetails(lower, verLower, LocalFilePath: null, UpstreamUrl: upstream));
                Transform(ctx, $"Streaming upstream .nupkg bytes from {upstream} (binary; body capture skipped).");
            }
            using var resp = await _upstreamClient!.GetAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            MarkUpstreamResponse(ctx, (int)resp.StatusCode, resp.Content.Headers.ContentType?.ToString(), body: null, truncated: false);
            if (!resp.IsSuccessStatusCode)
            {
                Transform(ctx, $"Upstream returned {(int)resp.StatusCode} — forwarded status to client.");
                return Results.StatusCode((int)resp.StatusCode);
            }
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return Results.Stream(stream, contentType);
        });

        app.MapGet("/v3/reg/{id}/index.json", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            MarkCaptureOutgoing(ctx);
            var lower = id.ToLowerInvariant();
            var upstream = $"{_upstreamResources!.RegistrationBase}{lower}/index.json";
            MarkUpstream(ctx, upstream);

            JsonObject? upstreamJson = null;
            try
            {
                using var resp = await _upstreamClient!.GetAsync(upstream, ct).ConfigureAwait(false);
                var (text, truncated) = await ReadBodyCappedAsync(resp, ct).ConfigureAwait(false);
                MarkUpstreamResponse(ctx, (int)resp.StatusCode, resp.Content.Headers.ContentType?.ToString(), text, truncated);
                if (resp.IsSuccessStatusCode && text is not null)
                {
                    upstreamJson = JsonNode.Parse(text) as JsonObject;
                }
            }
            catch
            {
                // Local-only fallback below.
            }

            _localById.TryGetValue(lower, out var locals);
            if (upstreamJson is null && (locals is null || locals.Count == 0))
            {
                Transform(ctx, $"Upstream registration for '{lower}' failed and no local overrides — replying 404.");
                return Results.NotFound();
            }

            // Splice local entries into upstream's inline items page when
            // present, else synthesise an entirely-local registration. We do
            // NOT follow paginated upstream sub-pages — for our overlay
            // semantics (local always wins) the resulting JSON is sufficient
            // for the CLI's restore code path: it consumes inline pages first
            // and only fetches sub-pages when it needs versions outside the
            // inline set. If a future scenario needs full pagination support
            // we'd implement on-demand sub-page fetching here.
            var merged = MergeRegistration(lower, ctx.Request.Scheme + "://" + ctx.Request.Host.Value, upstreamJson, locals);
            if (locals is not null && locals.Count > 0)
            {
                MarkSource(ctx, upstreamJson is null ? DogfoodingNuGetSource.LocalOverride : DogfoodingNuGetSource.Synthesised);
                MarkLocalPackagesUsed(ctx, locals.Select(p => $"{p.Id}/{p.Version}").ToArray());
                Transform(ctx, $"Spliced {locals.Count} local override(s) into registration for '{lower}'.");
            }
            // Sweep any straggling upstream URLs hiding inside the cloned
            // catalogEntry payloads (MergeRegistration only rebuilds the
            // outer envelope; the inner @id/registration/packageContent
            // strings come from upstream untouched). Rewrites here keep the
            // CLI from following catalog links direct to nuget.org for the
            // overlapping versions.
            var regBaseUri = ctx.Request.Scheme + "://" + ctx.Request.Host.Value;
            var rewriteCount = RewriteUpstreamUrls(merged, regBaseUri);
            if (rewriteCount > 0)
            {
                Transform(ctx, $"Rewrote {rewriteCount} upstream URL(s) inside registration payload to point at proxy.");
            }
            var itemCount = (merged["items"] as JsonArray)?.Sum(p => (p?["items"] as JsonArray)?.Count ?? 0) ?? 0;
            MarkDetails(ctx, new RegistrationDetails(
                PackageId: lower,
                MergedItemCount: itemCount,
                LocalOverrideCount: locals?.Count ?? 0,
                UpstreamFetched: upstreamJson is not null));
            return Results.Text(merged.ToJsonString(), "application/json");
        });

        app.MapGet("/v3/search", async (HttpContext ctx, CancellationToken ct) =>
        {
            MarkCaptureOutgoing(ctx);
            var query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";
            var q = ctx.Request.Query["q"].ToString();
            var prereleaseParam = ctx.Request.Query["prerelease"].ToString();
            var prerelease = string.Equals(prereleaseParam, "true", StringComparison.OrdinalIgnoreCase);

            var take = ParseIntOrDefault(ctx.Request.Query["take"], 20);
            var skip = ParseIntOrDefault(ctx.Request.Query["skip"], 0);

            // Forward verbatim to upstream first.
            var upstreamUrl = $"{_upstreamResources!.SearchBase}{query}";
            MarkUpstream(ctx, upstreamUrl);
            JsonObject upstreamJson;
            try
            {
                using var resp = await _upstreamClient!.GetAsync(upstreamUrl, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var (text, truncated) = await ReadBodyCappedAsync(resp, ct).ConfigureAwait(false);
                MarkUpstreamResponse(ctx, (int)resp.StatusCode, resp.Content.Headers.ContentType?.ToString(), text, truncated);
                upstreamJson = text is null ? new JsonObject { ["totalHits"] = 0, ["data"] = new JsonArray() } : (JsonObject)JsonNode.Parse(text)!;
            }
            catch
            {
                // Upstream search is best-effort; we can still return local matches.
                upstreamJson = new JsonObject { ["totalHits"] = 0, ["data"] = new JsonArray() };
                Transform(ctx, $"Upstream search at {upstreamUrl} failed — falling back to local-only results.");
            }

            var upstreamData = upstreamJson["data"] as JsonArray ?? new JsonArray();
            // Build a set of upstream ids to dedupe local entries against
            // (local always wins → strip the upstream entry).
            var upstreamIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in upstreamData)
            {
                if (item?["id"]?.GetValue<string>() is { } sid)
                {
                    upstreamIds.Add(sid);
                }
            }

            // Find local matches: substring on id (case-insensitive),
            // honouring prerelease=false by stripping suffix versions.
            var localMatches = new List<LocalPackage>();
            foreach (var (id, versions) in _localById)
            {
                if (!string.IsNullOrEmpty(q) && id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var pick = versions
                    .Where(v => prerelease || !v.Version.Contains('-', StringComparison.Ordinal))
                    .OrderByDescending(v => v.Version, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (pick is not null)
                {
                    localMatches.Add(pick);
                }
            }

            // Local-first merge with dedupe by id.
            var mergedData = new JsonArray();
            foreach (var local in localMatches)
            {
                mergedData.Add(BuildSearchEntry(local, ctx.Request.Scheme + "://" + ctx.Request.Host.Value));
            }
            foreach (var item in upstreamData)
            {
                var sid = item?["id"]?.GetValue<string>();
                if (sid is null || localMatches.Any(l => string.Equals(l.Id, sid, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                // Re-parent: deep-clone so we don't mutate upstreamJson.
                mergedData.Add(JsonNode.Parse(item!.ToJsonString())!);
            }

            // Apply skip / take after merge so local entries always surface
            // even when the user's "take" is small.
            var paged = mergedData.Skip(skip).Take(take).ToArray();
            var pagedArr = new JsonArray();
            foreach (var n in paged)
            {
                pagedArr.Add(n!.DeepClone());
            }

            MarkSource(ctx, localMatches.Count > 0 ? DogfoodingNuGetSource.Synthesised : DogfoodingNuGetSource.Upstream);
            if (localMatches.Count > 0)
            {
                MarkLocalPackagesUsed(ctx, localMatches.Select(l => $"{l.Id}/{l.Version}").ToArray());
            }
            MarkSearchStats(ctx, upstreamHits: upstreamData.Count, localHits: localMatches.Count, mergedCount: mergedData.Count);

            // Rewrite upstream URLs in the merged page so the CLI follows
            // proxy links rather than going direct to nuget.org (which would
            // bypass any local-override semantics entirely for downstream
            // registration/package fetches).
            var baseUri = ctx.Request.Scheme + "://" + ctx.Request.Host.Value;
            var rewrites = 0;
            foreach (var n in pagedArr)
            {
                rewrites += RewriteUpstreamUrls(n, baseUri);
            }
            if (localMatches.Count > 0)
            {
                Transform(ctx, $"Merged {localMatches.Count} local override(s) with {upstreamData.Count} upstream hit(s), deduped to {mergedData.Count} result(s).");
            }
            if (rewrites > 0)
            {
                Transform(ctx, $"Rewrote {rewrites} @id / registration / packageContent URL(s) in search payload to proxy endpoints.");
            }

            MarkDetails(ctx, new SearchDetails(
                Query: q,
                IncludePrerelease: prerelease,
                Skip: skip,
                Take: take,
                UpstreamHits: upstreamData.Count,
                LocalHits: localMatches.Count,
                MergedCount: mergedData.Count,
                UrlRewrites: rewrites));

            var output = new JsonObject
            {
                ["totalHits"] = mergedData.Count,
                ["data"] = pagedArr,
            };
            return Results.Text(output.ToJsonString(), "application/json");
        });

        app.MapGet("/v3/autocomplete", async (HttpContext ctx, CancellationToken ct) =>
        {
            MarkCaptureOutgoing(ctx);
            var q = ctx.Request.Query["q"].ToString();
            var take = ParseIntOrDefault(ctx.Request.Query["take"], 20);
            var prereleaseParam = ctx.Request.Query["prerelease"].ToString();
            var prerelease = string.Equals(prereleaseParam, "true", StringComparison.OrdinalIgnoreCase);
            _ = prerelease;

            var upstreamUrl = $"{_upstreamResources!.AutocompleteBase}{ctx.Request.QueryString.Value}";
            MarkUpstream(ctx, upstreamUrl);
            var dataSet = new List<string>();
            try
            {
                using var resp = await _upstreamClient!.GetAsync(upstreamUrl, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                var (text, truncated) = await ReadBodyCappedAsync(resp, ct).ConfigureAwait(false);
                MarkUpstreamResponse(ctx, (int)resp.StatusCode, resp.Content.Headers.ContentType?.ToString(), text, truncated);
                if (text is not null && JsonNode.Parse(text) is JsonObject obj && obj["data"] is JsonArray arr)
                {
                    foreach (var n in arr)
                    {
                        if (n?.GetValue<string>() is { } s)
                        {
                            dataSet.Add(s);
                        }
                    }
                }
            }
            catch
            {
                // Local-only fallback below.
            }

            var localMatches = _localById.Keys
                .Where(id => string.IsNullOrEmpty(q) || id.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // Local first, then upstream filtered to non-duplicates.
            var seen = new HashSet<string>(localMatches, StringComparer.OrdinalIgnoreCase);
            var merged = new List<string>(localMatches);
            foreach (var s in dataSet)
            {
                if (seen.Add(s))
                {
                    merged.Add(s);
                }
            }
            if (merged.Count > take)
            {
                merged = merged.Take(take).ToList();
            }

            if (localMatches.Count > 0)
            {
                MarkSource(ctx, DogfoodingNuGetSource.Synthesised);
            }

            MarkDetails(ctx, new AutocompleteDetails(
                Query: q,
                UpstreamHits: dataSet.Count,
                LocalHits: localMatches.Count,
                MergedCount: merged.Count));

            return Results.Json(new { totalHits = merged.Count, data = merged });
        });
    }

    private static int ParseIntOrDefault(Microsoft.Extensions.Primitives.StringValues s, int def)
        => int.TryParse(s.ToString(), out var v) ? v : def;

    /// <summary>
    /// Reads at most <see cref="PayloadCaptureLimitBytes"/> bytes from the
    /// HTTP response into a string for the inspector, returning a flag
    /// indicating whether the body was longer than the cap. Callers still
    /// receive a valid string when truncated — it's not a partial JSON
    /// document on the consumer side because the consumer reads the
    /// returned string, not the original stream. Returns (null, false) on
    /// a non-readable body.
    /// </summary>
    private static async Task<(string? Body, bool Truncated)> ReadBodyCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            var truncated = false;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                var remaining = PayloadCaptureLimitBytes - ms.Length;
                if (remaining <= 0)
                {
                    truncated = true;
                    // Drain the rest so disposing the stream doesn't abort
                    // an unread connection (HttpClient pools the socket).
                    while (await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) > 0) { }
                    break;
                }
                if (read > remaining)
                {
                    ms.Write(buffer, 0, (int)remaining);
                    truncated = true;
                    while (await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) > 0) { }
                    break;
                }
                ms.Write(buffer, 0, read);
            }
            return (System.Text.Encoding.UTF8.GetString(ms.ToArray()), truncated);
        }
        catch
        {
            return (null, false);
        }
    }

    private static DogfoodingNuGetEndpointKind ClassifyEndpoint(PathString path)
    {
        var p = path.HasValue ? path.Value! : string.Empty;
        if (p.Equals("/v3/index.json", StringComparison.OrdinalIgnoreCase))
        {
            return DogfoodingNuGetEndpointKind.ServiceIndex;
        }
        if (p.StartsWith("/v3/flat/", StringComparison.OrdinalIgnoreCase))
        {
            return p.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) ? DogfoodingNuGetEndpointKind.Nupkg
                : p.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ? DogfoodingNuGetEndpointKind.Nuspec
                : DogfoodingNuGetEndpointKind.Flatcontainer;
        }
        if (p.StartsWith("/v3/reg/", StringComparison.OrdinalIgnoreCase))
        {
            return DogfoodingNuGetEndpointKind.Registration;
        }
        if (p.StartsWith("/v3/search", StringComparison.OrdinalIgnoreCase))
        {
            return DogfoodingNuGetEndpointKind.Search;
        }
        if (p.StartsWith("/v3/autocomplete", StringComparison.OrdinalIgnoreCase))
        {
            return DogfoodingNuGetEndpointKind.Autocomplete;
        }
        return DogfoodingNuGetEndpointKind.Unknown;
    }

    private static void MarkSource(HttpContext ctx, DogfoodingNuGetSource source)
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.Source = source;
    }
    private static void MarkUpstream(HttpContext ctx, string url, string method = "GET")
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.UpstreamUrl = url;
        state.UpstreamMethod = method;
        state.Source ??= DogfoodingNuGetSource.Upstream;
    }
    private static void MarkUpstreamResponse(HttpContext ctx, int status, string? contentType, string? body, bool truncated)
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.UpstreamStatus = status;
        state.UpstreamContentType = contentType;
        state.UpstreamBody = body;
        state.UpstreamBodyTruncated = truncated;
    }
    private static void MarkCaptureOutgoing(HttpContext ctx)
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.CaptureOutgoing = true;
        if (state.OutgoingStream is { } stream)
        {
            stream.EnableCapture();
        }
    }
    private static void Transform(HttpContext ctx, string message)
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.Transformations.Add(message);
    }
    private static void MarkLocalPackagesUsed(HttpContext ctx, IReadOnlyList<string> packages)
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.LocalPackagesUsed = packages;
    }
    private static void MarkSearchStats(HttpContext ctx, int upstreamHits, int localHits, int mergedCount)
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.SearchStats = new DogfoodingNuGetSearchStats(upstreamHits, localHits, mergedCount);
    }
    private static void MarkDetails(HttpContext ctx, NuGetTrafficDetails details)
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.Details = details;
    }

    /// <summary>
    /// Replace any URL that sits under one of the resolved upstream base
    /// URIs with the equivalent path under the proxy's <paramref name="baseUri"/>,
    /// but only when the URL appears in a NuGet structural field we
    /// recognise (<c>@id</c>, <c>registration</c>, <c>packageContent</c>). This deliberately avoids
    /// blanket find/replace across the whole JSON: free-form text fields
    /// like <c>description</c> or <c>summary</c> can contain unrelated URLs
    /// that we shouldn't rewrite. Returns the number of rewrites performed
    /// so the details panel can surface "n URLs rewritten" as evidence the
    /// proxy is fully terminating package traffic.
    /// </summary>
    /// <remarks>
    /// Path mapping is endpoint-by-endpoint: anything under the upstream's
    /// flat-container base maps to <c>/v3/flat/...</c>; anything under the
    /// registration base maps to <c>/v3/reg/...</c>. The mapping uses the
    /// upstream base URIs resolved at startup (see
    /// <see cref="ResolveUpstreamAsync"/>) so we don't hard-code
    /// <c>api.nuget.org</c> — alternate mirrors work the same way. URLs that
    /// don't sit under any known upstream base are left alone (a future
    /// upstream-of-upstream redirect could break this, but the dogfood
    /// scenarios all point at canonical NuGet endpoints).
    /// </remarks>
    private int RewriteUpstreamUrls(JsonNode? node, string baseUri)
    {
        if (node is null || _upstreamResources is null)
        {
            return 0;
        }
        var count = 0;
        RewriteWalk(node, baseUri, _upstreamResources, ref count);
        return count;
    }

    private static void RewriteWalk(JsonNode node, string baseUri, UpstreamResources upstream, ref int count)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToArray())
                {
                    if (value is JsonValue && IsRewriteTargetKey(key) && value.GetValue<object>() is string s)
                    {
                        var rewritten = MapUpstreamToProxy(s, baseUri, upstream);
                        if (!ReferenceEquals(rewritten, s))
                        {
                            obj[key] = rewritten;
                            count++;
                        }
                    }
                    else if (value is JsonObject or JsonArray)
                    {
                        RewriteWalk(value, baseUri, upstream, ref count);
                    }
                }
                break;
            case JsonArray arr:
                foreach (var item in arr)
                {
                    if (item is not null)
                    {
                        RewriteWalk(item, baseUri, upstream, ref count);
                    }
                }
                break;
        }
    }

    private static bool IsRewriteTargetKey(string key)
    {
        // The NuGet v3 schema reuses these property names for any URL that
        // a client may follow. Restricting rewrites to these keys keeps us
        // from touching free-form text like description/summary that might
        // happen to contain http(s) URLs.
        return key is "@id" or "registration" or "packageContent" or "catalogEntry" or "parent" or "items";
    }

    private static string MapUpstreamToProxy(string url, string baseUri, UpstreamResources upstream)
    {
        if (TryMapPrefix(url, upstream.FlatContainerBase, baseUri + "/v3/flat/", out var flat))
        {
            return flat;
        }
        if (TryMapPrefix(url, upstream.RegistrationBase, baseUri + "/v3/reg/", out var reg))
        {
            return reg;
        }
        return url;
    }

    private static bool TryMapPrefix(string url, string upstreamBase, string proxyBase, out string mapped)
    {
        if (url.StartsWith(upstreamBase, StringComparison.OrdinalIgnoreCase))
        {
            mapped = proxyBase + url[upstreamBase.Length..];
            return true;
        }
        mapped = url;
        return false;
    }

    private void EnqueueEvent(DogfoodingNuGetTrafficEvent ev)
    {
        _ring.Enqueue(ev);
        while (_ring.Count > RingCapacity && _ring.TryDequeue(out _)) { }
    }

    private static async Task<UpstreamResources> ResolveUpstreamAsync(Uri serviceIndex, HttpClient http, CancellationToken ct)
    {
        using var resp = await http.GetAsync(serviceIndex, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var doc = JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidOperationException("Upstream service index was not a JSON object.");
        var resources = doc["resources"] as JsonArray
            ?? throw new InvalidOperationException("Upstream service index has no 'resources' array.");

        string? flat = null, reg = null, search = null, autocomplete = null;
        foreach (var r in resources)
        {
            var type = r?["@type"]?.GetValue<string>() ?? "";
            var id = r?["@id"]?.GetValue<string>() ?? "";
            // Prefer the newer versioned @types when present (latest wins in
            // iteration order — nuget.org lists them in chronological order).
            if (type.StartsWith("PackageBaseAddress", StringComparison.Ordinal))
            {
                flat = id;
            }
            else if (type.StartsWith("RegistrationsBaseUrl", StringComparison.Ordinal))
            {
                reg = id;
            }
            else if (type.StartsWith("SearchQueryService", StringComparison.Ordinal))
            {
                search = id;
            }
            else if (type.StartsWith("SearchAutocompleteService", StringComparison.Ordinal))
            {
                autocomplete = id;
            }
        }
        if (flat is null || reg is null || search is null || autocomplete is null)
        {
            throw new InvalidOperationException("Upstream service index missing one of: flatcontainer, registration, search, autocomplete.");
        }
        return new UpstreamResources(
            EnsureSlash(flat),
            EnsureSlash(reg),
            EnsureQuestionMark(search),
            EnsureQuestionMark(autocomplete));

        static string EnsureSlash(string s) => s.EndsWith('/') ? s : s + "/";
        static string EnsureQuestionMark(string s) => s.Contains('?') ? s : s + (s.EndsWith('/') ? "" : "/") + "?";
    }

    private static bool TryReadLocalPackage(string filePath, out LocalPackage package)
    {
        // .nupkg files are ZIPs containing a single .nuspec at the root.
        // Example name shape: Aspire.Hosting.9.5.0-dogfood.20251110.nupkg
        // We don't try to parse id/version from the filename — too many edge
        // cases with versioned ids — and read the embedded .nuspec instead.
        package = null!;
        try
        {
            using var stream = File.OpenRead(filePath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            var nuspecEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry is null)
            {
                return false;
            }
            using var nuspecStream = nuspecEntry.Open();
            var xml = XDocument.Load(nuspecStream);
            var ns = xml.Root?.Name.Namespace ?? XNamespace.None;
            var metadata = xml.Root?.Element(ns + "metadata");
            if (metadata is null)
            {
                return false;
            }
            var id = (string?)metadata.Element(ns + "id");
            var version = (string?)metadata.Element(ns + "version");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
            {
                return false;
            }
            // Read the raw nuspec XML so the flatcontainer .nuspec endpoint
            // can return it without re-opening the ZIP. Strings are small
            // (typically < 10KB) so caching them is cheap.
            string nuspecXml;
            using (var copy = nuspecEntry.Open())
            using (var sr = new StreamReader(copy))
            {
                nuspecXml = sr.ReadToEnd();
            }
            package = new LocalPackage(id.ToLowerInvariant(), version.ToLowerInvariant(), filePath, nuspecXml, metadata);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static JsonObject MergeRegistration(string id, string baseUri, JsonObject? upstream, IReadOnlyList<LocalPackage>? locals)
    {
        // Build a working set keyed by version → catalogEntry JsonObject.
        var byVersion = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

        // Seed with upstream catalog entries from the inline first page.
        if (upstream?["items"] is JsonArray pages)
        {
            foreach (var page in pages)
            {
                if (page?["items"] is JsonArray innerItems)
                {
                    foreach (var item in innerItems)
                    {
                        var entry = item?["catalogEntry"] as JsonObject;
                        var v = entry?["version"]?.GetValue<string>();
                        if (entry is not null && v is not null)
                        {
                            byVersion[v] = (JsonObject)entry.DeepClone();
                        }
                    }
                }
            }
        }

        // Overlay local entries.
        if (locals is not null)
        {
            foreach (var local in locals)
            {
                byVersion[local.Version] = BuildCatalogEntry(local, baseUri);
            }
        }

        // Sort keeps responses stable; OrdinalIgnoreCase is good enough for
        // dogfood scenarios (the CLI doesn't depend on strict SemVer order in
        // the registration response).
        var ordered = byVersion.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();

        var items = new JsonArray();
        foreach (var (ver, entry) in ordered)
        {
            items.Add(new JsonObject
            {
                ["@id"] = $"{baseUri}/v3/reg/{id}/{ver}.json",
                ["@type"] = "Package",
                ["catalogEntry"] = entry,
                ["packageContent"] = $"{baseUri}/v3/flat/{id}/{ver}/{id}.{ver}.nupkg",
                ["registration"] = $"{baseUri}/v3/reg/{id}/index.json",
            });
        }

        var pagesArr = new JsonArray
        {
            new JsonObject
            {
                ["@id"] = $"{baseUri}/v3/reg/{id}/index.json#page",
                ["@type"] = "catalog:CatalogPage",
                ["count"] = items.Count,
                ["items"] = items,
                ["lower"] = ordered.Count > 0 ? ordered[0].Key : "",
                ["upper"] = ordered.Count > 0 ? ordered[^1].Key : "",
                ["parent"] = $"{baseUri}/v3/reg/{id}/index.json",
            },
        };

        return new JsonObject
        {
            ["@id"] = $"{baseUri}/v3/reg/{id}/index.json",
            ["@type"] = new JsonArray("catalog:CatalogRoot", "PackageRegistration", "catalog:Permalink"),
            ["count"] = 1,
            ["items"] = pagesArr,
        };
    }

    private static JsonObject BuildCatalogEntry(LocalPackage local, string baseUri)
    {
        var entry = new JsonObject
        {
            ["@id"] = $"{baseUri}/v3/reg/{local.Id}/{local.Version}.json#catalog",
            ["@type"] = "PackageDetails",
            ["id"] = local.Id,
            ["version"] = local.Version,
            ["listed"] = true,
            ["packageContent"] = $"{baseUri}/v3/flat/{local.Id}/{local.Version}/{local.Id}.{local.Version}.nupkg",
        };

        // Pull a few human-visible fields from the .nuspec metadata; missing
        // values are simply omitted (the restore code path tolerates absent
        // optional metadata).
        var meta = local.Metadata;
        var ns = meta.Name.Namespace;
        AddIfPresent(entry, "description", (string?)meta.Element(ns + "description"));
        AddIfPresent(entry, "authors", (string?)meta.Element(ns + "authors"));
        AddIfPresent(entry, "summary", (string?)meta.Element(ns + "summary"));
        AddIfPresent(entry, "title", (string?)meta.Element(ns + "title"));
        AddIfPresent(entry, "licenseExpression", (string?)meta.Element(ns + "license"));
        AddIfPresent(entry, "projectUrl", (string?)meta.Element(ns + "projectUrl"));
        AddIfPresent(entry, "tags", (string?)meta.Element(ns + "tags"));

        var deps = meta.Element(ns + "dependencies");
        if (deps is not null)
        {
            var groups = new JsonArray();
            // Two shapes per nuspec spec: <group targetFramework="..."> with
            // child <dependency>, or flat <dependency> children. Handle both
            // because both occur in our shipping nuspecs.
            var groupElems = deps.Elements(ns + "group").ToList();
            if (groupElems.Count > 0)
            {
                foreach (var g in groupElems)
                {
                    groups.Add(BuildDependencyGroup(g, ns, $"{baseUri}/v3/reg/{local.Id}/{local.Version}.json"));
                }
            }
            else
            {
                groups.Add(BuildDependencyGroup(deps, ns, $"{baseUri}/v3/reg/{local.Id}/{local.Version}.json"));
            }
            entry["dependencyGroups"] = groups;
        }

        return entry;

        static void AddIfPresent(JsonObject o, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                o[key] = value;
            }
        }
    }

    private static JsonObject BuildDependencyGroup(XElement groupOrDeps, XNamespace ns, string idBase)
    {
        var tfm = (string?)groupOrDeps.Attribute("targetFramework");
        var deps = new JsonArray();
        foreach (var d in groupOrDeps.Elements(ns + "dependency"))
        {
            var depId = (string?)d.Attribute("id");
            var depRange = (string?)d.Attribute("version");
            if (string.IsNullOrEmpty(depId))
            {
                continue;
            }
            deps.Add(new JsonObject
            {
                ["@id"] = $"{idBase}#dependencygroup/{tfm ?? "any"}/{depId.ToLowerInvariant()}",
                ["@type"] = "PackageDependency",
                ["id"] = depId,
                ["range"] = depRange ?? "(, )",
            });
        }
        var groupObj = new JsonObject
        {
            ["@id"] = $"{idBase}#dependencygroup/{tfm ?? "any"}",
            ["@type"] = "PackageDependencyGroup",
            ["dependencies"] = deps,
        };
        if (!string.IsNullOrEmpty(tfm))
        {
            groupObj["targetFramework"] = tfm;
        }
        return groupObj;
    }

    private static JsonObject BuildSearchEntry(LocalPackage local, string baseUri)
    {
        var versions = new JsonArray
        {
            new JsonObject
            {
                ["version"] = local.Version,
                ["downloads"] = 0,
                ["@id"] = $"{baseUri}/v3/reg/{local.Id}/{local.Version}.json",
            },
        };
        var meta = local.Metadata;
        var ns = meta.Name.Namespace;
        return new JsonObject
        {
            ["@id"] = $"{baseUri}/v3/reg/{local.Id}/index.json",
            ["@type"] = "Package",
            ["registration"] = $"{baseUri}/v3/reg/{local.Id}/index.json",
            ["id"] = local.Id,
            ["version"] = local.Version,
            ["description"] = (string?)meta.Element(ns + "description") ?? "",
            ["summary"] = (string?)meta.Element(ns + "summary") ?? "",
            ["title"] = (string?)meta.Element(ns + "title") ?? local.Id,
            ["authors"] = (string?)meta.Element(ns + "authors") ?? "",
            ["totalDownloads"] = 0,
            ["verified"] = false,
            ["versions"] = versions,
        };
    }

    private sealed class HandlerState
    {
        public DogfoodingNuGetSource? Source { get; set; }
        public string? UpstreamMethod { get; set; }
        public string? UpstreamUrl { get; set; }
        public int? UpstreamStatus { get; set; }
        public string? UpstreamContentType { get; set; }
        public string? UpstreamBody { get; set; }
        public bool UpstreamBodyTruncated { get; set; }
        public IReadOnlyList<string> LocalPackagesUsed { get; set; } = Array.Empty<string>();
        public DogfoodingNuGetSearchStats? SearchStats { get; set; }
        public NuGetTrafficDetails? Details { get; set; }
        public List<string> Transformations { get; } = new();
        public bool CaptureOutgoing { get; set; }
        public CountingStream? OutgoingStream { get; set; }
    }

    private sealed class CountingStream : Stream
    {
        public CountingStream(Stream inner, long captureLimitBytes)
        {
            _inner = inner;
            _captureLimit = captureLimitBytes;
        }
        private readonly Stream _inner;
        private readonly long _captureLimit;
        private MemoryStream? _capture;
        public long BytesWritten { get; private set; }
        public bool Truncated { get; private set; }

        public byte[]? CapturedBytes => _capture?.ToArray();

        // The wrapper is always installed (BytesWritten / Source plumbing
        // would otherwise lose data) but capture is opt-in per handler so
        // a 200 MB nupkg stream doesn't get tee'd into memory. Handlers
        // call this from inside their endpoint code as soon as they know
        // the response is going to be a small text/JSON payload.
        public void EnableCapture()
        {
            _capture ??= new MemoryStream();
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            BytesWritten += count;
            TeeIfCapturing(buffer.AsSpan(offset, count));
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            BytesWritten += count;
            TeeIfCapturing(buffer.AsSpan(offset, count));
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BytesWritten += buffer.Length;
            TeeIfCapturing(buffer.Span);
            return _inner.WriteAsync(buffer, cancellationToken);
        }

        private void TeeIfCapturing(ReadOnlySpan<byte> span)
        {
            if (_capture is null)
            {
                return;
            }
            var remaining = _captureLimit - _capture.Length;
            if (remaining <= 0)
            {
                Truncated = true;
                return;
            }
            if (span.Length > remaining)
            {
                _capture.Write(span[..(int)remaining]);
                Truncated = true;
            }
            else
            {
                _capture.Write(span);
            }
        }
    }

    private static string? TryDecodeText(byte[] bytes)
    {
        // NuGet v3 endpoints emit UTF-8 JSON/XML; if a future endpoint
        // returns something else we still want a best-effort hex/ascii
        // fallback rather than a raw NRE in the renderer.
        try
        {
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return null;
        }
    }

    private sealed record UpstreamResources(string FlatContainerBase, string RegistrationBase, string SearchBase, string AutocompleteBase);

    private sealed record LocalPackage(string Id, string Version, string FilePath, string NuspecXml, XElement Metadata);
}

internal enum DogfoodingNuGetEndpointKind
{
    Unknown,
    ServiceIndex,
    Flatcontainer,
    Nupkg,
    Nuspec,
    Registration,
    Search,
    Autocomplete,
}

internal enum DogfoodingNuGetSource
{
    Upstream,
    LocalOverride,
    Synthesised,
    Cache,
    Error,
}

internal sealed record DogfoodingNuGetRequest(
    Guid Id,
    DateTimeOffset StartedAt,
    string Method,
    string Path,
    string? Query,
    DogfoodingNuGetEndpointKind Kind);

internal sealed record DogfoodingNuGetResponse(
    Guid Id,
    DateTimeOffset CompletedAt,
    int StatusCode,
    long? BodyBytes,
    TimeSpan Duration,
    DogfoodingNuGetSource Source,
    string? UpstreamUrl,
    IReadOnlyList<string> LocalPackagesUsed,
    string? ErrorMessage,
    DogfoodingNuGetSearchStats? SearchStats,
    DogfoodingNuGetPayloads Payloads);

/// <summary>
/// Captured request/response payloads for a single proxy round-trip. Drives
/// the NuGet inspector's "show me exactly what we sent and received" view
/// so the user can verify that (a) the CLI actually reaches the proxy
/// (downstream request), (b) the proxy fetched the right upstream URL
/// (upstream request/response), and (c) the proxy's outgoing response
/// rewrote URLs to point at itself rather than leaking nuget.org links
/// (outgoing response + transformation log).
/// </summary>
/// <remarks>
/// <para>
/// Bodies are capped at 32KB to keep memory bounded; the rest of the
/// stream still reaches the client. <see cref="UpstreamBodyTruncated"/>
/// and <see cref="OutgoingBodyTruncated"/> surface that fact so the
/// renderer can show a <c>(truncated)</c> badge.
/// </para>
/// <para>
/// Binary endpoints (.nupkg, .nuspec when proxied byte-for-byte) DO NOT
/// opt into outgoing capture: their handlers call <c>MarkCaptureOutgoing</c>
/// only for JSON responses, so <see cref="OutgoingBody"/> will be null on
/// package downloads. The inspector renders "&lt;binary stream, N bytes&gt;"
/// in that case.
/// </para>
/// </remarks>
internal sealed record DogfoodingNuGetPayloads(
    string? UpstreamMethod,
    string? UpstreamUrl,
    int? UpstreamStatus,
    string? UpstreamContentType,
    string? UpstreamBody,
    bool UpstreamBodyTruncated,
    string? OutgoingContentType,
    string? OutgoingBody,
    bool OutgoingBodyTruncated,
    IReadOnlyList<string> Transformations);

/// <summary>
/// For search responses only: how many results came from each side and the
/// final merged count after dedupe. Powers the analyzer's
/// "10 results (8 nuget.org + 2 local)" label.
/// </summary>
internal sealed record DogfoodingNuGetSearchStats(int UpstreamHits, int LocalHits, int MergedCount);

internal sealed record DogfoodingNuGetTrafficEvent(
    DogfoodingNuGetRequest Request,
    DogfoodingNuGetResponse Response,
    NuGetTrafficDetails Details);

/// <summary>
/// Endpoint-specific details captured for a single proxy request, attached
/// to <see cref="DogfoodingNuGetTrafficEvent"/> so the analyzer panel can
/// render an explanation tailored to the kind of NuGet call. Subtypes
/// expose only fields that don't duplicate <see cref="DogfoodingNuGetResponse.Source"/>
/// — the canonical "where did the bytes come from" lives on the response.
/// </summary>
/// <remarks>
/// Constructed by per-endpoint handlers in <see cref="DogfoodingNuGetServer"/>
/// via <c>MarkDetails</c> and copied onto the event in the request
/// middleware so the polymorphism survives across the request/response
/// boundary. The view layer pattern-matches subtypes to render a tailored
/// details pane.
/// </remarks>
internal abstract record NuGetTrafficDetails(DogfoodingNuGetEndpointKind Kind);

internal sealed record ServiceIndexDetails() : NuGetTrafficDetails(DogfoodingNuGetEndpointKind.ServiceIndex);

internal sealed record FlatVersionsDetails(
    string PackageId,
    IReadOnlyList<string> Versions,
    int UpstreamVersionCount,
    int LocalVersionCount) : NuGetTrafficDetails(DogfoodingNuGetEndpointKind.Flatcontainer);

internal sealed record NupkgDetails(
    string PackageId,
    string Version,
    string? LocalFilePath,
    string? UpstreamUrl) : NuGetTrafficDetails(DogfoodingNuGetEndpointKind.Nupkg);

internal sealed record NuspecDetails(
    string PackageId,
    string Version,
    string? LocalFilePath,
    string? UpstreamUrl) : NuGetTrafficDetails(DogfoodingNuGetEndpointKind.Nuspec);

internal sealed record RegistrationDetails(
    string PackageId,
    int MergedItemCount,
    int LocalOverrideCount,
    bool UpstreamFetched) : NuGetTrafficDetails(DogfoodingNuGetEndpointKind.Registration);

internal sealed record SearchDetails(
    string Query,
    bool IncludePrerelease,
    int Skip,
    int Take,
    int UpstreamHits,
    int LocalHits,
    int MergedCount,
    int UrlRewrites) : NuGetTrafficDetails(DogfoodingNuGetEndpointKind.Search);

internal sealed record AutocompleteDetails(
    string Query,
    int UpstreamHits,
    int LocalHits,
    int MergedCount) : NuGetTrafficDetails(DogfoodingNuGetEndpointKind.Autocomplete);

internal sealed record UnknownDetails(string RawPath) : NuGetTrafficDetails(DogfoodingNuGetEndpointKind.Unknown);
