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
/// live traffic table sourced from <see cref="NuGetTrafficState"/>, plus a
/// summary line showing where the server is bound and how many local
/// overrides it carries.
/// </summary>
/// <remarks>
/// Widgets are built fresh each frame by Hex1b; subscription to live events
/// is set up by the preparer (which wires the server's RequestCompleted
/// event into the per-session traffic state). The content here is a pure
/// view over that state — it reads <see cref="NuGetTrafficState.Events"/>
/// each render.
/// </remarks>
internal static class SessionNuGetTrafficContent
{
    public static Hex1bWidget Build<TParent>(
        WidgetContext<TParent> ctx,
        DogfoodSession session,
        DogfoodingNuGetServerRegistry registry)
        where TParent : Hex1bWidget
    {
        if (!registry.TryGet(session.Id, out var server) || session.NuGetTraffic is not { } traffic)
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

        return ctx.VStack(v =>
        {
            var rows = new List<Hex1bWidget>
            {
                v.Text(""),
                v.Text(summary),
                v.Text("  Filter: [All] [Search] [Restore] [Errors]   (TODO: filter switches)"),
                v.Text(""),
                v.Text("  Time      Status  Kind            Source         Dur     Bytes  Path"),
                v.Text("  --------  ------  --------------  -------------  ------  -----  ----------------------------------"),
            };
            foreach (var ev in filtered)
            {
                rows.Add(v.Text(FormatRow(ev)));
            }
            return rows.ToArray();
        });
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

    private static string FormatRow(DogfoodingNuGetTrafficEvent ev)
    {
        // Compact one-line representation tuned for the 80-col window:
        //   13:48:21  200    Search          Synthesised    42ms     2148  /v3/search?q=Aspire.Hosting
        var time = ev.Request.StartedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var status = ev.Response.StatusCode.ToString(CultureInfo.InvariantCulture);
        var kind = ev.Request.Kind.ToString();
        var src = ev.Response.Source.ToString();
        var dur = $"{ev.Response.Duration.TotalMilliseconds:F0}ms";
        var bytes = ev.Response.BodyBytes?.ToString("N0", CultureInfo.InvariantCulture) ?? "-";
        var path = (ev.Request.Path + (ev.Request.Query ?? "")).Length > 40
            ? (ev.Request.Path + (ev.Request.Query ?? ""))[..40] + "…"
            : ev.Request.Path + (ev.Request.Query ?? "");
        return $"  {time}  {status,-6}  {Pad(kind, 14)}  {Pad(src, 13)}  {dur,-6}  {bytes,5}  {path}";
    }

    private static string Pad(string s, int width)
    {
        if (s.Length >= width)
        {
            return s[..width];
        }
        return s + new string(' ', width - s.Length);
    }
}
