// <copyright file="ChaosActivitySource.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics;

namespace ChaosProxy.Container.Telemetry;

/// <summary>
/// ActivitySource for the chaos proxy. Middleware tags the current ASP.NET Core
/// activity with <c>chaos.proxy.*</c> attributes when a transform fires - the Aspire
/// dashboard's traces tab picks these up via the OTLP exporter and surfaces them on
/// the request timeline so operators can see WHAT chaos fired on each request.
/// </summary>
/// <remarks>
/// We don't start a separate Activity per chaos fire - we annotate the parent ASP.NET
/// Core activity instead. Multiple chaos transforms on one request (e.g., latency +
/// replay-duplicate) layer their tags via separate prefixes (<c>chaos.proxy.latency.*</c>,
/// <c>chaos.proxy.replay.*</c>) so they don't collide.
/// </remarks>
internal static class ChaosActivitySource
{
    public const string Name = "Aspire.Hosting.Chaos";

    public static readonly ActivitySource Source = new(Name);
}
