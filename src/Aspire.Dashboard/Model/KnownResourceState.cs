// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

public enum KnownResourceState
{
    Finished,
    Exited,
    FailedToStart,
    Starting,
    Running,
    Building,
    Hidden,
    Waiting,
    Stopping,
    ValueMissing,
    Unknown,
    RuntimeUnhealthy,
    NotStarted
}
