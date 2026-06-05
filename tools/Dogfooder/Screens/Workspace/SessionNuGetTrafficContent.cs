// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dogfooder.Services;
using Aspire.Dogfooder.State;
using Hex1b;
using Hex1b.Widgets;

namespace Aspire.Dogfooder.Screens.Workspace;

/// <summary>
/// Window-body widget for the "NuGet" tab of a running session. Renders a
/// live traffic table at the top + a request-kind-specific details pane
/// below, split by a vertical splitter so the user can resize the panes.
/// </summary>
/// <remarks>
/// Widgets are built fresh each frame by Hex1b; the per-request typed
/// envelopes come from <see cref="DogfoodingNuGetTrafficEvent.Details"/>
/// and drive a pattern-matched details factory so each kind of NuGet API
/// call gets a tailored explanation (search hits + URL-rewrite count,
/// nupkg local file path vs upstream URL, registration merged item count,
/// etc.). Selection lives on <see cref="NuGetTrafficState.SelectedEventId"/>
/// with a "first visible row" fallback so the details pane stays
/// meaningful even when the selected event scrolls off the ring buffer
/// or the active filter excludes it.
/// </remarks>
internal static class SessionNuGetTrafficContent
{
    public static Hex1bWidget Build<TParent>(
        WidgetContext<TParent> ctx,
        DogfoodSession session,
        DogfoodingNuGetServer? server)
        where TParent : Hex1bWidget
    {
        if (server is null || session.NuGetTraffic is not { } traffic)
        {
            return ctx.VStack(v => new Hex1bWidget[]
            {
                v.Text(""),
                v.Text("  (NuGet proxy not running for this session.)"),
            });
        }

        var allEvents = traffic.Events;
        var filtered = ApplyFilter(allEvents, traffic.Filter);

        var summary = string.Format(
            CultureInfo.InvariantCulture,
            "  Listening on {0}  ·  Upstream: {1}  ·  Local overrides: {2} ids / {3} versions  ·  {4} req",
            server.ServiceIndexUri,
            server.UpstreamServiceIndex,
            server.LocalPackageIdCount,
            server.LocalPackageCount,
            allEvents.Count);

        // Resolve the selected event with fallback to the first visible row
        // — required because the user-visible selection can be evicted by
        // ring-buffer trim or filter changes, and a stale id would leave
        // the details pane permanently blank.
        DogfoodingNuGetTrafficEvent? selected = null;
        if (traffic.SelectedEventId is { } selId)
        {
            foreach (var e in filtered)
            {
                if (e.Request.Id == selId)
                {
                    selected = e;
                    break;
                }
            }
        }
        if (selected is null && filtered.Count > 0)
        {
            selected = filtered[0];
        }

        var table = ctx.Table(filtered)
            .RowKey(e => (object)e.Request.Id)
            .Header(h => new[]
            {
                h.Cell("Time").Fixed(10),
                h.Cell("Status").Fixed(7),
                h.Cell("Kind").Fixed(15),
                h.Cell("Source").Fixed(14),
                h.Cell("Dur").Fixed(7),
                h.Cell("Bytes").Fixed(9).AlignRight(),
                h.Cell("Path").Fill(),
            })
            .Row((r, ev, state) => new[]
            {
                r.Cell(ev.Request.StartedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
                r.Cell(ev.Response.StatusCode.ToString(CultureInfo.InvariantCulture)),
                r.Cell(ev.Request.Kind.ToString()),
                r.Cell(ev.Response.Source.ToString()),
                r.Cell($"{ev.Response.Duration.TotalMilliseconds:F0}ms"),
                r.Cell(ev.Response.BodyBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"),
                r.Cell(ev.Request.Path + (ev.Request.Query ?? "")),
            })
            .Focus(selected?.Request.Id)
            .OnFocusChanged(key =>
            {
                traffic.SelectedEventId = key as Guid?;
                traffic.NotifyChanged();
                return Task.CompletedTask;
            })
            .Empty(e => e.Text("  (No NuGet traffic yet — issue a CLI command to populate.)"))
            .FillHeight();

        var details = BuildDetailsPane(ctx, selected, traffic);

        // Top pane = summary banner + table; bottom pane = details. Keep
        // the splitter at ~16 rows by default so the table dominates but
        // the details pane is still readable (CLI users prefer scanning
        // the table on the path/status columns).
        return ctx.VSplitter(
            top => new Hex1bWidget[]
            {
                top.Text(""),
                top.Text(summary),
                top.Text(""),
                table,
            },
            bottom => new Hex1bWidget[]
            {
                details,
            },
            topHeight: 16);
    }

    private static Hex1bWidget BuildDetailsPane<TParent>(WidgetContext<TParent> ctx, DogfoodingNuGetTrafficEvent? ev, NuGetTrafficState traffic)
        where TParent : Hex1bWidget
    {
        if (ev is null)
        {
            return ctx.VStack(v => new[]
            {
                v.Text(""),
                v.Text("  (Select a row to see request details.)"),
            }).Fill();
        }

        // TabPanel for the bottom pane lets us put the structured summary
        // alongside the two raw response bodies (each in a navigable
        // read-only Editor) without overflowing the splitter. Editors give
        // the user copy/scroll/search over the captured JSON — the prior
        // text-only dump was capped to 60 lines and hostile to inspection.
        return ctx.TabPanel(tp =>
        {
            var upstreamLabel = ev.Response.Payloads.UpstreamStatus is int us
                ? $"Upstream Response ({us})"
                : "Upstream Response";
            var outgoingLabel = $"Proxy Response ({ev.Response.StatusCode})";
            return new List<TabItemWidget>
            {
                tp.Tab("Summary", t => new[] { BuildSummaryTab(t, ev) }),
                tp.Tab(upstreamLabel, t => new[] { BuildEditorTab(t, traffic.GetUpstreamEditor(ev)) }),
                tp.Tab(outgoingLabel, t => new[] { BuildEditorTab(t, traffic.GetOutgoingEditor(ev)) }),
            };
        }).Fill();
    }

    private static Hex1bWidget BuildSummaryTab<TParent>(WidgetContext<TParent> ctx, DogfoodingNuGetTrafficEvent ev)
        where TParent : Hex1bWidget
    {
        var lines = new List<string>
        {
            "",
            "  === DOWNSTREAM REQUEST (from CLI) ===",
            $"    {ev.Request.Method}  {ev.Request.Path}{ev.Request.Query ?? ""}",
            "",
            "  === SUMMARY ===",
            $"    → {ev.Response.StatusCode}  in {ev.Response.Duration.TotalMilliseconds:F0}ms  ·  {ev.Response.BodyBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "-"} bytes  ·  source={ev.Response.Source}",
        };
        if (ev.Response.ErrorMessage is { Length: > 0 } err)
        {
            lines.Add($"    ERROR: {err}");
        }

        var kindLines = BuildKindLines(ev);
        if (kindLines.Length > 0)
        {
            lines.Add("");
            lines.Add("  === KIND-SPECIFIC ===");
            foreach (var l in kindLines)
            {
                lines.Add($"  {l}");
            }
        }

        if (ev.Response.LocalPackagesUsed.Count > 0)
        {
            lines.Add("");
            lines.Add($"  local packages used: {string.Join(", ", ev.Response.LocalPackagesUsed)}");
        }

        var p = ev.Response.Payloads;
        if (!string.IsNullOrEmpty(p.UpstreamUrl))
        {
            lines.Add("");
            lines.Add("  === UPSTREAM REQUEST (proxy → nuget.org) ===");
            lines.Add($"    {p.UpstreamMethod ?? "GET"}  {p.UpstreamUrl}");
            if (p.UpstreamStatus is int us)
            {
                var ct = string.IsNullOrEmpty(p.UpstreamContentType) ? "" : $"  {p.UpstreamContentType}";
                lines.Add($"    → {us}{ct}  ·  see the “Upstream Response” tab for the body");
            }
        }

        lines.Add("");
        var outCt = string.IsNullOrEmpty(p.OutgoingContentType) ? "" : $"  {p.OutgoingContentType}";
        lines.Add($"  === OUTGOING RESPONSE (proxy → CLI){outCt} ===");
        lines.Add("    see the “Proxy Response” tab for the body");

        if (p.Transformations.Count > 0)
        {
            lines.Add("");
            lines.Add("  === TRANSFORMATIONS ===");
            foreach (var t in p.Transformations)
            {
                lines.Add($"    • {t}");
            }
        }

        return ctx.VStack(v => lines.Select(l => (Hex1bWidget)v.Text(l)).ToArray()).Fill();
    }

    private static Hex1bWidget BuildEditorTab<TParent>(WidgetContext<TParent> ctx, EditorState state)
        where TParent : Hex1bWidget
    {
        // The Editor extension already sets HeightHint=Fill; nothing
        // additional needed for layout. ShowLineNumbers makes scanning
        // big JSON payloads much easier.
        return ctx.Editor(state).LineNumbers().WordWrap();
    }

    private static string[] BuildKindLines(DogfoodingNuGetTrafficEvent ev)
    {
        return ev.Details switch
        {
            ServiceIndexDetails => new[]
            {
                "  Synthesised service index (proxy endpoints only — no upstream fetch).",
            },
            FlatVersionsDetails f => new[]
            {
                $"  package: {f.PackageId}",
                $"  versions returned: {f.Versions.Count}  (upstream={f.UpstreamVersionCount}, local={f.LocalVersionCount})",
                f.Versions.Count > 0 ? $"  versions: {string.Join(", ", f.Versions.Take(20))}{(f.Versions.Count > 20 ? " …" : "")}" : "  versions: (none)",
            },
            NupkgDetails n => new[]
            {
                $"  package: {n.PackageId} {n.Version}",
                n.LocalFilePath is { Length: > 0 } lp
                    ? $"  served from LOCAL file: {lp}"
                    : $"  proxied from UPSTREAM: {n.UpstreamUrl ?? "(unknown)"}",
            },
            NuspecDetails nu => new[]
            {
                $"  package: {nu.PackageId} {nu.Version}",
                nu.LocalFilePath is { Length: > 0 } lp
                    ? $"  served from LOCAL .nuspec inside {lp}"
                    : $"  proxied from UPSTREAM: {nu.UpstreamUrl ?? "(unknown)"}",
            },
            RegistrationDetails r => new[]
            {
                $"  package: {r.PackageId}",
                $"  merged items: {r.MergedItemCount}  ·  local overrides: {r.LocalOverrideCount}  ·  upstream fetched: {(r.UpstreamFetched ? "yes" : "no")}",
            },
            SearchDetails s => new[]
            {
                $"  query: {(string.IsNullOrEmpty(s.Query) ? "(empty)" : s.Query)}  ·  prerelease={s.IncludePrerelease}  ·  skip={s.Skip} take={s.Take}",
                $"  hits: upstream={s.UpstreamHits}  local={s.LocalHits}  merged={s.MergedCount}",
                $"  URLs rewritten to proxy: {s.UrlRewrites}",
            },
            AutocompleteDetails a => new[]
            {
                $"  query: {(string.IsNullOrEmpty(a.Query) ? "(empty)" : a.Query)}",
                $"  hits: upstream={a.UpstreamHits}  local={a.LocalHits}  merged={a.MergedCount}",
            },
            UnknownDetails un => new[]
            {
                $"  raw path: {un.RawPath}",
                "  (No typed details captured for this endpoint kind.)",
            },
            _ => Array.Empty<string>(),
        };
    }

    private static IReadOnlyList<DogfoodingNuGetTrafficEvent> ApplyFilter(IReadOnlyList<DogfoodingNuGetTrafficEvent> events, TrafficFilter filter)
    {
        return filter switch
        {
            TrafficFilter.Search => events.Where(e => e.Request.Kind == DogfoodingNuGetEndpointKind.Search || e.Request.Kind == DogfoodingNuGetEndpointKind.Autocomplete).ToList(),
            TrafficFilter.Restore => events.Where(e =>
                e.Request.Kind == DogfoodingNuGetEndpointKind.Flatcontainer
                || e.Request.Kind == DogfoodingNuGetEndpointKind.Nupkg
                || e.Request.Kind == DogfoodingNuGetEndpointKind.Nuspec
                || e.Request.Kind == DogfoodingNuGetEndpointKind.Registration).ToList(),
            TrafficFilter.Errors => events.Where(e => e.Response.StatusCode >= 400 || e.Response.Source == DogfoodingNuGetSource.Error).ToList(),
            _ => events,
        };
    }
}
