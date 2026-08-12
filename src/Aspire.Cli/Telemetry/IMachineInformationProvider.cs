// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

// This is copied from https://github.com/microsoft/mcp/tree/e9f5da8fa093239d42d4b867afdf5e73e291d472/core/Microsoft.Mcp.Core/src/Services/Telemetry
// Keep in sync with updates there.

internal interface IMachineInformationProvider
{
    /// <summary>
    /// Gets existing or creates the device id.  In case the cached id cannot be retrieved, or the
    /// newly generated id cannot be cached, a value of null is returned.
    /// </summary>
    Task<string?> GetOrCreateDeviceId();

    /// <summary>
    /// Gets a hash of the machine's MAC address.
    /// </summary>
    Task<string> GetMacAddressHash();
}
