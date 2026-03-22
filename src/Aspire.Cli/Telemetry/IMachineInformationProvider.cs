// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Telemetry;

// This is copied from https://github.com/microsoft/mcp/tree/6bb4d76a63d24854efe0fa0bd96f5ab6f699ed3a/core/Azure.Mcp.Core/src/Services/Telemetry
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
