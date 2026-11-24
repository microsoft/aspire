// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Orchestrator;

/// <summary>What a Stop of a resource instance did.</summary>
internal enum ResourceStopResult
{
    /// <summary>DCP stopped what the instance's start launched.</summary>
    Stopped,

    /// <summary>The instance's start was canceled before it reached DCP, so the instance didn't start.</summary>
    StartCanceled,

    /// <summary>The instance wasn't running and no start of it was pending, so there was nothing to stop.</summary>
    NotRunning,
}
