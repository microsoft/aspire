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

        var builder = WebApplication.CreateSlimBuilder();
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
        // Bind to a 127.0.0.1 ephemeral port via WebApplication.Urls (the
        // CreateSlimBuilder's IWebHostBuilder shape doesn't expose the legacy
        // UseUrls extension). The bound port is read back from
        // IServerAddressesFeature after StartAsync since :0 doesn't surface
        // the chosen value any other way.
        app.Urls.Add("http://127.0.0.1:0");

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
            // ahead of the framework's writer. We intentionally do not buffer
            // — nupkg downloads can be hundreds of MB.
            var originalBody = ctx.Response.Body;
            var counting = new CountingStream(originalBody);
            ctx.Response.Body = counting;

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
                    SearchStats: handlerState.SearchStats);
                RequestCompleted?.Invoke(resp);
                var pair = new DogfoodingNuGetTrafficEvent(req, resp);
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
            return Results.Text(json, "application/json");
        });

        app.MapGet("/v3/flat/{id}/index.json", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
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
                if (upstreamResp.IsSuccessStatusCode)
                {
                    var stream = await upstreamResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    upstreamJson = (JsonObject?)JsonNode.Parse(stream);
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
                MarkSource(ctx, upstreamJson is null ? DogfoodingNuGetSource.LocalOverride : DogfoodingNuGetSource.Synthesised);
            }
            else if (upstreamJson is null)
            {
                return Results.NotFound();
            }
            else
            {
                MarkSource(ctx, DogfoodingNuGetSource.Upstream);
            }

            return Results.Json(new { versions = versions.ToArray() });
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
                    return Results.Text(match.NuspecXml, "application/xml");
                }
                if (filename.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase))
                {
                    return Results.File(match.FilePath, "application/octet-stream", enableRangeProcessing: true);
                }
                return Results.NotFound();
            }

            // Proxy upstream.
            var upstream = $"{_upstreamResources!.FlatContainerBase}{lower}/{verLower}/{filename}";
            MarkUpstream(ctx, upstream);
            using var resp = await _upstreamClient!.GetAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return Results.StatusCode((int)resp.StatusCode);
            }
            var contentType = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return Results.Stream(stream, contentType);
        });

        app.MapGet("/v3/reg/{id}/index.json", async (string id, HttpContext ctx, CancellationToken ct) =>
        {
            var lower = id.ToLowerInvariant();
            var upstream = $"{_upstreamResources!.RegistrationBase}{lower}/index.json";
            MarkUpstream(ctx, upstream);

            JsonObject? upstreamJson = null;
            try
            {
                using var resp = await _upstreamClient!.GetAsync(upstream, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    upstreamJson = (JsonObject?)JsonNode.Parse(stream);
                }
            }
            catch
            {
                // Local-only fallback below.
            }

            _localById.TryGetValue(lower, out var locals);
            if (upstreamJson is null && (locals is null || locals.Count == 0))
            {
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
            }
            return Results.Text(merged.ToJsonString(), "application/json");
        });

        app.MapGet("/v3/search", async (HttpContext ctx, CancellationToken ct) =>
        {
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
                var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                upstreamJson = (JsonObject)JsonNode.Parse(stream)!;
            }
            catch
            {
                // Upstream search is best-effort; we can still return local matches.
                upstreamJson = new JsonObject { ["totalHits"] = 0, ["data"] = new JsonArray() };
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

            var output = new JsonObject
            {
                ["totalHits"] = mergedData.Count,
                ["data"] = pagedArr,
            };
            return Results.Text(output.ToJsonString(), "application/json");
        });

        app.MapGet("/v3/autocomplete", async (HttpContext ctx, CancellationToken ct) =>
        {
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
                var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                if (JsonNode.Parse(stream) is JsonObject obj && obj["data"] is JsonArray arr)
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

            return Results.Json(new { totalHits = merged.Count, data = merged });
        });
    }

    private static int ParseIntOrDefault(Microsoft.Extensions.Primitives.StringValues s, int def)
        => int.TryParse(s.ToString(), out var v) ? v : def;

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
    private static void MarkUpstream(HttpContext ctx, string url)
    {
        var state = (HandlerState)ctx.Items["__dogfood_state"]!;
        state.UpstreamUrl = url;
        state.Source ??= DogfoodingNuGetSource.Upstream;
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
        public string? UpstreamUrl { get; set; }
        public IReadOnlyList<string> LocalPackagesUsed { get; set; } = Array.Empty<string>();
        public DogfoodingNuGetSearchStats? SearchStats { get; set; }
    }

    private sealed class CountingStream : Stream
    {
        public CountingStream(Stream inner) { _inner = inner; }
        private readonly Stream _inner;
        public long BytesWritten { get; private set; }

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
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            BytesWritten += count;
            return _inner.WriteAsync(buffer, offset, count, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            BytesWritten += buffer.Length;
            return _inner.WriteAsync(buffer, cancellationToken);
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
    DogfoodingNuGetSearchStats? SearchStats);

/// <summary>
/// For search responses only: how many results came from each side and the
/// final merged count after dedupe. Powers the analyzer's
/// "10 results (8 nuget.org + 2 local)" label.
/// </summary>
internal sealed record DogfoodingNuGetSearchStats(int UpstreamHits, int LocalHits, int MergedCount);

internal sealed record DogfoodingNuGetTrafficEvent(DogfoodingNuGetRequest Request, DogfoodingNuGetResponse Response);
