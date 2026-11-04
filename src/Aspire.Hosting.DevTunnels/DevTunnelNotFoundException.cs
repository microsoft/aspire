// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.DevTunnels;

/// <summary>
/// Identifies an authoritative missing-tunnel response, rather than a failed status query.
/// </summary>
internal sealed class DevTunnelNotFoundException(string tunnelId, string? error)
    : DistributedApplicationException($"Dev tunnel '{tunnelId}' was not found. {error}");
