// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using OpenTelemetry;

namespace Aspire.Shared.Telemetry;

/// <summary>
/// Applies product-specific default metadata and privacy rules to activities before export.
/// </summary>
internal sealed class ActivityTagEnrichmentProcessor(AspireTelemetryBase telemetry) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        telemetry.SetActivityProperties(data, telemetry.GetActivityDefaultTags(data));
    }
}
