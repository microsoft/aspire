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
                await Task.Delay(Random.Shared.Next(5, 20));
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
            var name = parentName + "-0";

            var links = CreateLinks();

            using var activity = s_activitySource.StartActivity(ActivityKind.Client, name: name, links: links.DistinctBy(l => l.Context.SpanId));
            if (activity == null)
            {
                return;
            }

            AddEvents(activity);

            _allActivities.Add(activity);

            await Task.Delay(Random.Shared.Next(5, 20));

            await CreateChildActivityAsync(name);

            await Task.Delay(Random.Shared.Next(5, 20));
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

            // Create the activity link. There is a 50% chance the activity link goes to an activity
            // that doesn't exist. This logic is here to ensure incomplete links are handled correctly.
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
            }
            links[i] = new ActivityLink(activityContext, activityTags);
        }

        return links;
    }
}
