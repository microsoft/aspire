// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Reflection;

namespace Stress.ApiService;

public class TraceCreator
{
    public const string ActivitySourceName = "CustomTraceSpan";

    public bool IncludeBrokenLinks { get; set; }

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    private readonly List<Activity> _allActivities = new List<Activity>();

    public Activity? CreateActivity(string name, string? spandId)
    {
        var activity = s_activitySource.StartActivity(name, ActivityKind.Client);
        if (activity != null)
        {
            if (spandId != null)
            {
                // Gross but it's the only way.
                typeof(Activity).GetField("_spanId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activity, spandId);
                typeof(Activity).GetField("_traceId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activity, activity.TraceId.ToString());
            }
        }

        return activity;
    }

    public async Task CreateTraceAsync(string traceName, int count, bool createChildren, string? rootName = null)
    {
        var activityStack = new Stack<Activity>();

        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                await Task.Delay(Random.Shared.Next(10, 50));
            }

            var name = $"{traceName}-Span-{i}";
            using var activity = s_activitySource.StartActivity(rootName ?? name, ActivityKind.Client);
            if (activity == null)
            {
                continue;
            }

            _allActivities.Add(activity);

            if (createChildren)
            {
                await CreateChildActivityAsync(name);
            }
        }

        while (activityStack.Count > 0)
        {
            activityStack.Pop().Stop();
        }
    }

    private async Task CreateChildActivityAsync(string parentName)
    {
        if (Random.Shared.NextDouble() > 0.05)
        {
            var links = CreateLinks();
            var distinctLinks = links.DistinctBy(l => l.Context.SpanId).ToArray();

            var sameTraceCount = 0;
            var crossTraceCount = 0;
            var currentTraceId = Activity.Current?.TraceId.ToString();
            foreach (var link in distinctLinks)
            {
                if (link.Context.TraceId.ToString() == currentTraceId)
                {
                    sameTraceCount++;
                }
                else
                {
                    crossTraceCount++;
                }
            }

            var name = $"{parentName}-0";
            var resolvedName = name;
            if (distinctLinks.Length > 0)
            {
                resolvedName = $"{name} (links: {distinctLinks.Length}, same-trace: {sameTraceCount}, cross-trace: {crossTraceCount})";
            }

            using var activity = s_activitySource.StartActivity(ActivityKind.Client, name: resolvedName, links: distinctLinks);
            if (activity == null)
            {
                return;
            }

            AddEvents(activity);

            _allActivities.Add(activity);

            await Task.Delay(Random.Shared.Next(10, 50));

            await CreateChildActivityAsync(name);

            await Task.Delay(Random.Shared.Next(10, 50));
        }
    }

    private static void AddEvents(Activity activity)
    {
        var eventCount = Random.Shared.Next(0, 5);
        for (var i = 0; i < eventCount; i++)
        {
            var activityTags = new ActivityTagsCollection();
            var tagsCount = Random.Shared.Next(0, 5);
            for (var j = 0; j < tagsCount; j++)
            {
                activityTags.Add($"key-{j}", "Value!");
            }

            activity.AddEvent(new ActivityEvent($"event-{i}", DateTimeOffset.UtcNow.AddMilliseconds(1), activityTags));
        }
    }

    private ActivityLink[] CreateLinks()
    {
        var activityLinkCount = Random.Shared.Next(0, Math.Min(5, _allActivities.Count));
        var links = new ActivityLink[activityLinkCount];
        for (var i = 0; i < links.Length; i++)
        {
            // Randomly create some tags.
            var activityTags = new ActivityTagsCollection();
            var tagsCount = Random.Shared.Next(0, 3);
            for (var j = 0; j < tagsCount; j++)
            {
                activityTags.Add($"key-{j}", "Value!");
            }

            ActivityContext activityContext;
            if (!IncludeBrokenLinks || Random.Shared.Next() % 2 == 0)
            {
                var a = _allActivities[Random.Shared.Next(0, _allActivities.Count)];
                activityContext = a.Context;
            }
            else
            {
                activityContext = new ActivityContext(
                    ActivityTraceId.CreateRandom(),
                    ActivitySpanId.CreateRandom(),
                    ActivityTraceFlags.None);

                if (Random.Shared.Next() % 2 == 0)
                {
                    // 50% of cross-trace links create a real new trace+span so the link is navigable.
                    CreateLinkedTrace(activityContext);
                }
            }
            links[i] = new ActivityLink(activityContext, activityTags);
        }

        return links;
    }

    private static void CreateLinkedTrace(ActivityContext activityContext)
    {
        // Temporarily clear Activity.Current so the new activity doesn't inherit the current trace.
        var previous = Activity.Current;
        Activity.Current = null;

        var activity = s_activitySource.StartActivity("linked-trace-span", ActivityKind.Internal);
        if (activity is not null)
        {
            // Force the activity to use the exact trace and span ID from activityContext
            // so the span link points to this recorded span.
            typeof(Activity).GetField("_spanId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activity, activityContext.SpanId.ToString());
            typeof(Activity).GetField("_traceId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(activity, activityContext.TraceId.ToString());
            activity.Stop();
        }

        Activity.Current = previous;
    }
}
