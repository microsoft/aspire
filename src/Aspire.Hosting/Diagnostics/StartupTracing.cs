// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Diagnostics;

internal static class StartupTracing
{
    public const string ActivitySourceName = "Aspire.Hosting.Startup";

    private const string OperationIdEnvironmentVariable = "ASPIRE_STARTUP_OPERATION_ID";
    private const string TraceParentEnvironmentVariable = "ASPIRE_STARTUP_TRACEPARENT";
    private const string TraceStateEnvironmentVariable = "ASPIRE_STARTUP_TRACESTATE";

    private const string OperationIdTag = "aspire.startup.operation_id";
    private const string ResourceNameTag = "aspire.resource.name";
    private const string ResourceTypeTag = "aspire.resource.type";
    private const string DcpResourceNameTag = "aspire.dcp.resource.name";
    private const string DcpResourceKindTag = "aspire.dcp.resource.kind";

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    public static Activity? StartActivity(string name)
    {
        var activity = Activity.Current is null && TryGetStartupParentContext(out var parentContext)
            ? s_activitySource.StartActivity(name, ActivityKind.Internal, parentContext)
            : s_activitySource.StartActivity(name, ActivityKind.Internal);

        AddStartupOperationId(activity);
        return activity;
    }

    public static void SetResourceTags(Activity? activity, IResource resource)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(ResourceNameTag, resource.Name);
        activity.SetTag(ResourceTypeTag, resource.GetType().Name);
    }

    public static void SetDcpResourceTags(Activity? activity, string resourceKind, string resourceName)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetTag(DcpResourceKindTag, resourceKind);
        activity.SetTag(DcpResourceNameTag, resourceName);
    }

    public static void SetError(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = exception.GetType().FullName,
            ["exception.message"] = exception.Message
        }));
    }

    private static void AddStartupOperationId(Activity? activity)
    {
        if (activity is null)
        {
            return;
        }

        var operationId = Environment.GetEnvironmentVariable(OperationIdEnvironmentVariable);
        if (!string.IsNullOrEmpty(operationId))
        {
            activity.SetTag(OperationIdTag, operationId);
        }
    }

    private static bool TryGetStartupParentContext(out ActivityContext parentContext)
    {
        var traceParent = Environment.GetEnvironmentVariable(TraceParentEnvironmentVariable);
        var traceState = Environment.GetEnvironmentVariable(TraceStateEnvironmentVariable);
        if (string.IsNullOrEmpty(traceParent))
        {
            parentContext = default;
            return false;
        }

        return ActivityContext.TryParse(traceParent, traceState, out parentContext);
    }
}
