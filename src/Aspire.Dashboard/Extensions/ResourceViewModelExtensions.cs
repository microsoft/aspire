// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;

namespace Aspire.Dashboard.Extensions;

internal static class ResourceViewModelExtensions
{
    public static bool IsRunningState(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.Running;
    }

    public static bool IsFinishedState(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.Finished;
    }

    public static bool IsExitedState(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.Exited;
    }

    public static bool IsStopped(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.Exited or KnownResourceState.Finished or KnownResourceState.FailedToStart;
    }

    public static bool IsFailedToStart(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.FailedToStart;
    }

    public static bool IsUnusableTransitoryState(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.Starting or KnownResourceState.Building or KnownResourceState.Waiting or KnownResourceState.Stopping;
    }

    public static bool IsRuntimeUnhealthy(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.RuntimeUnhealthy;
    }

    public static bool IsNotStarted(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.NotStarted;
    }

    public static bool IsWaiting(this ResourceViewModel resource)
    {
        return resource.KnownState is KnownResourceState.Waiting;
    }

    public static bool HasMissingParameterValueState(this ResourceViewModel resource)
    {
        return resource.IsParameter && resource.KnownState is KnownResourceState.ValueMissing;
    }

    public static bool IsUnknownState(this ResourceViewModel resource) => resource.KnownState is KnownResourceState.Unknown;

    public static bool HasNoState(this ResourceViewModel resource) => string.IsNullOrEmpty(resource.State);
}
