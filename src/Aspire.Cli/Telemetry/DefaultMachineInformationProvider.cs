// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Telemetry;

// This is copied from https://github.com/microsoft/mcp/tree/e9f5da8fa093239d42d4b867afdf5e73e291d472/core/Microsoft.Mcp.Core/src/Services/Telemetry
// Keep in sync with updates there.

/// <summary>
/// Default information provider not tied to any platform specification for DevDeviceId.
/// </summary>
internal class DefaultMachineInformationProvider(ILogger<MachineInformationProviderBase> logger)
    : MachineInformationProviderBase(logger)
{
    /// <summary>
    /// Returns null.
    /// </summary>
    /// <returns></returns>
    public override Task<string?> GetOrCreateDeviceId() => Task.FromResult<string?>(null);
}
