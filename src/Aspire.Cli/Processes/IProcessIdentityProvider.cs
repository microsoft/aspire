// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Processes;

/// <summary>
/// Reads stable process lifetimes without using the Linux runtime start-time approximation.
/// </summary>
internal interface IProcessIdentityProvider
{
    long? GetStartTimeUnixMilliseconds(int processId);
}

internal sealed class ProcessIdentityProvider : IProcessIdentityProvider
{
    public long? GetStartTimeUnixMilliseconds(int processId)
        => ProcessStartTimeHelper.TryGetProcessStartTimeUnixMilliseconds(processId);
}
